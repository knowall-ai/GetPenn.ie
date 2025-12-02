using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Assistants;
using Azure.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace PennieBot.Services;

/// <summary>
/// Client for communicating with Pennie AI Foundry Agent.
/// Implements OpenAI Assistants function calling pattern with Azure Functions backend.
/// </summary>
public class PennieAgentClient : IPennieAgentClient, IDisposable
{
    private readonly ILogger<PennieAgentClient> _logger;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly AssistantsClient _assistantsClient;
    private readonly string _assistantId;
    private readonly string _backendUrl;
    private readonly IMemoryCache _meetingThreadCache;
    private readonly SemaphoreSlim _threadCreationLock = new(1, 1);
    private readonly Timer _cleanupTimer;

    /// <summary>
    /// Allowlist of valid backend function names to prevent URL injection.
    /// </summary>
    private static readonly HashSet<string> AllowedFunctions = new()
    {
        "read_projects", "read_teams", "read_work_item", "read_work_items",
        "read_work_item_types", "read_link_types", "search_work_items",
        "create_work_item", "link_work_items"
    };

    /// <summary>
    /// Functions that use GET method (no body).
    /// </summary>
    private static readonly HashSet<string> GetFunctions = new() { "read_projects", "read_link_types" };

    private bool _disposed;

    public PennieAgentClient(
        ILogger<PennieAgentClient> logger,
        IConfiguration configuration,
        HttpClient httpClient,
        IMemoryCache memoryCache)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = httpClient;
        _meetingThreadCache = memoryCache;

        // Get configuration
        // IMPORTANT: The Azure.AI.OpenAI.Assistants SDK requires an Azure OpenAI endpoint
        // in the format: https://{resource-name}.openai.azure.com
        // This is different from AI Foundry project URLs which use .services.ai.azure.com
        // Note: Config keys try dashes first (Azure Key Vault convention), then underscores for backward compatibility
        var endpoint = _configuration["AZURE-OPENAI-ENDPOINT"]
            ?? _configuration["AZURE_OPENAI_ENDPOINT"]
            ?? throw new InvalidOperationException("AZURE-OPENAI-ENDPOINT not configured. " +
                "Expected format: https://{resource}.openai.azure.com");

        _assistantId = _configuration["AZURE-OPENAI-ASSISTANT-ID"]
            ?? _configuration["AZURE_OPENAI_ASSISTANT_ID"]
            ?? throw new InvalidOperationException("AZURE-OPENAI-ASSISTANT-ID not configured");

        _backendUrl = _configuration["AZURE_FUNCTIONS_BACKEND_URL"]
            ?? "https://pennie-backend-prod.azurewebsites.net"; // Default to production backend

        // Create Assistants client using DefaultAzureCredential
        // This will use managed identity in production or developer credentials locally
        _assistantsClient = new AssistantsClient(
            new Uri(endpoint),
            new DefaultAzureCredential());

        // Start cleanup timer - runs every 5 minutes to log cache statistics
        _cleanupTimer = new Timer(LogCacheStatistics, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        _logger.LogInformation(
            "Pennie Agent Client initialized with assistant {AssistantId} at {Endpoint}",
            _assistantId, endpoint);
    }

    /// <summary>
    /// Log cache statistics periodically.
    /// </summary>
    private void LogCacheStatistics(object? state)
    {
        if (_meetingThreadCache is MemoryCache mc)
        {
            _logger.LogDebug("Meeting thread cache entries: {Count}", mc.Count);
        }
    }

    /// <inheritdoc/>
    public async Task SendTranscriptAsync(
        TranscriptionResult result,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation(
                "Sending transcript to Pennie: {Speaker} - {Text}",
                result.Speaker, result.Text);

            // Get or create thread for this meeting
            var threadId = await GetOrCreateThreadAsync(result.MeetingId, cancellationToken);

            // Add transcript message to thread
            var message = $"[{result.Timestamp:HH:mm:ss}] {result.Speaker}: {result.Text}";
            await _assistantsClient.CreateMessageAsync(
                threadId,
                MessageRole.User,
                message,
                cancellationToken: cancellationToken);

            _logger.LogInformation("Added message to thread {ThreadId}", threadId);

            // Create run to process the transcript
            var run = await _assistantsClient.CreateRunAsync(
                threadId,
                new CreateRunOptions(_assistantId),
                cancellationToken);

            _logger.LogInformation("Created run {RunId} with status {Status}", run.Value.Id, run.Value.Status);

            // Monitor run and handle function calls
            await ProcessRunAsync(threadId, run.Value.Id, cancellationToken);

            _logger.LogInformation("Transcript processed successfully by Pennie");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending transcript to Pennie");
            // Don't throw - we don't want transcription failures to break the bot
        }
    }

    /// <inheritdoc/>
    public async Task<string> SendMessageAndGetResponseAsync(
        TranscriptionResult result,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation(
                "Sending message to Pennie and awaiting response: {Speaker} - {Text}",
                result.Speaker, result.Text);

            // Get or create thread for this conversation
            var threadId = await GetOrCreateThreadAsync(result.MeetingId, cancellationToken);

            // Add user message to thread (no timestamp prefix for chat mode)
            var message = result.Text;
            await _assistantsClient.CreateMessageAsync(
                threadId,
                MessageRole.User,
                message,
                cancellationToken: cancellationToken);

            _logger.LogInformation("Added message to thread {ThreadId}", threadId);

            // Create run to process the message
            var run = await _assistantsClient.CreateRunAsync(
                threadId,
                new CreateRunOptions(_assistantId),
                cancellationToken);

            _logger.LogInformation("Created run {RunId} with status {Status}", run.Value.Id, run.Value.Status);

            // Monitor run and handle function calls
            await ProcessRunAsync(threadId, run.Value.Id, cancellationToken);

            // Get Pennie's response
            var messages = await _assistantsClient.GetMessagesAsync(threadId, cancellationToken: cancellationToken);
            var latestMessage = messages.Value.Data.FirstOrDefault(m => m.Role == MessageRole.Assistant);

            if (latestMessage != null)
            {
                var responseText = latestMessage.ContentItems.OfType<MessageTextContent>().FirstOrDefault()?.Text ?? "";
                _logger.LogInformation("Received response from Pennie: {Response}",
                    responseText.Length > 100 ? responseText[..100] + "..." : responseText);
                return responseText;
            }

            _logger.LogWarning("No response received from Pennie");
            return "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting response from Pennie");
            return "";
        }
    }

    /// <summary>
    /// Get or create a thread for a meeting.
    /// Uses MemoryCache with 2-hour expiration to prevent unbounded memory growth.
    /// </summary>
    private async Task<string> GetOrCreateThreadAsync(string meetingId, CancellationToken cancellationToken)
    {
        var cacheKey = $"meeting_thread:{meetingId}";

        // Fast path: check if thread already exists in cache
        if (_meetingThreadCache.TryGetValue(cacheKey, out string? existingThreadId) && existingThreadId != null)
        {
            return existingThreadId;
        }

        // Slow path: acquire lock and create thread if needed
        await _threadCreationLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (_meetingThreadCache.TryGetValue(cacheKey, out existingThreadId) && existingThreadId != null)
            {
                return existingThreadId;
            }

            var thread = await _assistantsClient.CreateThreadAsync(cancellationToken);
            var threadId = thread.Value.Id;

            // Cache with sliding expiration of 2 hours (typical max meeting length)
            var cacheOptions = new MemoryCacheEntryOptions()
                .SetSlidingExpiration(TimeSpan.FromHours(2))
                .SetAbsoluteExpiration(TimeSpan.FromHours(4));

            _meetingThreadCache.Set(cacheKey, threadId, cacheOptions);

            _logger.LogInformation("Created new thread {ThreadId} for meeting {MeetingId}", threadId, meetingId);

            return threadId;
        }
        finally
        {
            _threadCreationLock.Release();
        }
    }

    /// <summary>
    /// Monitor run status and handle function calls.
    /// This is the CRITICAL function call handler that makes Pennie's backend integration work.
    /// Uses exponential backoff to reduce load while waiting.
    /// </summary>
    private async Task ProcessRunAsync(string threadId, string runId, CancellationToken cancellationToken)
    {
        var timeoutSeconds = _configuration.GetValue<int>("PennieAgent:RunTimeoutSeconds", 60);
        var maxIterations = _configuration.GetValue<int>("PennieAgent:MaxRunIterations", 120);
        var startTime = DateTime.UtcNow;
        var baseDelayMs = 500;
        var maxDelayMs = 5000;
        var currentDelayMs = baseDelayMs;
        var iteration = 0;

        while ((DateTime.UtcNow - startTime).TotalSeconds < timeoutSeconds && iteration < maxIterations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iteration++;
            // Get current run status
            var run = await _assistantsClient.GetRunAsync(threadId, runId, cancellationToken);
            var status = run.Value.Status;

            var elapsedSeconds = (int)(DateTime.UtcNow - startTime).TotalSeconds;
            _logger.LogDebug("Run {RunId} status: {Status} (iteration {Iteration}, elapsed {Elapsed}s/{Timeout}s)",
                runId, status, iteration, elapsedSeconds, timeoutSeconds);

            // Warn if run is taking too long
            if (iteration == 10)
            {
                _logger.LogWarning("Run {RunId} still processing after {Iteration} iterations", runId, iteration);
            }

            if (status == RunStatus.Completed)
            {
                // Run completed successfully - extract Pennie's response
                var messages = await _assistantsClient.GetMessagesAsync(threadId, cancellationToken: cancellationToken);
                var latestMessage = messages.Value.Data.FirstOrDefault(m => m.Role == MessageRole.Assistant);

                if (latestMessage != null)
                {
                    var responseText = latestMessage.ContentItems.OfType<MessageTextContent>().FirstOrDefault()?.Text ?? "";
                    _logger.LogInformation("Pennie response: {Response}", responseText);
                }

                return;
            }
            else if (status == RunStatus.RequiresAction)
            {
                // CRITICAL: Handle function calls - reset backoff since we're actively processing
                currentDelayMs = baseDelayMs;

                _logger.LogInformation("Run requires action - processing function calls");

                var requiredAction = run.Value.RequiredAction;
                if (requiredAction is SubmitToolOutputsAction submitToolOutputsAction)
                {
                    var toolOutputs = new List<ToolOutput>();

                    foreach (var functionCall in submitToolOutputsAction.ToolCalls.OfType<RequiredFunctionToolCall>())
                    {
                        _logger.LogInformation(
                            "Processing function call: {FunctionName} with arguments: {Arguments}",
                            functionCall.Name, functionCall.Arguments);

                        // Call the backend function
                        var output = await CallBackendFunctionAsync(
                            functionCall.Name,
                            functionCall.Arguments,
                            cancellationToken);

                        toolOutputs.Add(new ToolOutput(functionCall.Id, output));
                    }

                    // Submit tool outputs back to Pennie
                    await _assistantsClient.SubmitToolOutputsToRunAsync(
                        threadId,
                        runId,
                        toolOutputs,
                        cancellationToken);

                    _logger.LogInformation("Submitted {Count} tool outputs to run {RunId}", toolOutputs.Count, runId);
                }
            }
            else if (status == RunStatus.Failed || status == RunStatus.Cancelled || status == RunStatus.Expired)
            {
                _logger.LogError("Run {RunId} ended with status: {Status}", runId, status);
                return;
            }

            // Exponential backoff: wait before next status check
            await Task.Delay(currentDelayMs, cancellationToken);
            currentDelayMs = Math.Min(currentDelayMs * 2, maxDelayMs);
        }

        _logger.LogWarning(
            "Run {RunId} did not complete: timeout={Timeout}s, iterations={Iteration}/{MaxIterations}",
            runId, timeoutSeconds, iteration, maxIterations);
    }

    /// <summary>
    /// Call a backend function and return the result.
    /// Validates function name against allowlist to prevent URL injection.
    /// </summary>
    private async Task<string> CallBackendFunctionAsync(
        string functionName,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        try
        {
            // Validate function name against allowlist to prevent URL injection
            if (!AllowedFunctions.Contains(functionName))
            {
                _logger.LogError("Rejected unknown function: {FunctionName}", functionName);
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"Unknown function: {functionName}"
                });
            }

            _logger.LogInformation("Calling backend function: {FunctionName}", functionName);

            // Build URL for backend function
            var url = $"{_backendUrl}/api/{functionName}";

            // Parse arguments
            var arguments = JsonSerializer.Deserialize<JsonElement>(argumentsJson);

            // Call backend based on HTTP method (GET for parameterless functions, POST for others)
            HttpResponseMessage response;
            if (GetFunctions.Contains(functionName))
            {
                // GET request (no body)
                response = await _httpClient.GetAsync(url, cancellationToken);
            }
            else
            {
                // POST request with JSON body
                var content = new StringContent(argumentsJson, Encoding.UTF8, "application/json");
                response = await _httpClient.PostAsync(url, content, cancellationToken);
            }

            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogInformation(
                "Backend function {FunctionName} returned: {Response}",
                functionName, responseBody.Length > 200 ? responseBody[..200] + "..." : responseBody);

            return responseBody;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling backend function: {FunctionName}", functionName);

            // Return error as JSON for Pennie to handle
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    /// <inheritdoc/>
    public async Task<string> GetMeetingSummaryAsync(
        string meetingId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Requesting meeting summary for {MeetingId}", meetingId);

            // Get thread for this meeting
            var cacheKey = $"meeting_thread:{meetingId}";
            if (!_meetingThreadCache.TryGetValue(cacheKey, out string? threadId) || threadId == null)
            {
                return "No conversation found for this meeting.";
            }

            // Add summary request message
            await _assistantsClient.CreateMessageAsync(
                threadId,
                MessageRole.User,
                "Please provide a summary of this meeting including: " +
                "1. All work items created with their IDs and links, " +
                "2. Key decisions made, " +
                "3. Outstanding questions or ambiguities.",
                cancellationToken: cancellationToken);

            // Create run to generate summary
            var run = await _assistantsClient.CreateRunAsync(
                threadId,
                new CreateRunOptions(_assistantId),
                cancellationToken);

            // Process run (handle any function calls)
            await ProcessRunAsync(threadId, run.Value.Id, cancellationToken);

            // Get Pennie's summary response
            var messages = await _assistantsClient.GetMessagesAsync(threadId, cancellationToken: cancellationToken);
            var summaryMessage = messages.Value.Data.FirstOrDefault(m => m.Role == MessageRole.Assistant);

            if (summaryMessage != null)
            {
                var summary = summaryMessage.ContentItems.OfType<MessageTextContent>().FirstOrDefault()?.Text ?? "";
                _logger.LogInformation("Generated meeting summary ({Length} chars)", summary.Length);
                return summary;
            }

            return "Unable to generate meeting summary.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting meeting summary from Pennie");
            return "Error generating meeting summary.";
        }
    }

    /// <inheritdoc/>
    public Task NotifyWorkItemCreatedAsync(int workItemId, string workItemType, string title)
    {
        try
        {
            _logger.LogInformation(
                "Work item created notification: {Type} #{Id} - {Title}",
                workItemType, workItemId, title);

            // TODO(#30): Post work item link to Teams chat via Bot Framework messaging API

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error notifying work item creation");
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public Task CleanupMeetingAsync(string meetingId)
    {
        try
        {
            var cacheKey = $"meeting_thread:{meetingId}";
            if (_meetingThreadCache.TryGetValue(cacheKey, out string? threadId))
            {
                _meetingThreadCache.Remove(cacheKey);
                _logger.LogInformation(
                    "Cleaned up meeting {MeetingId} (thread {ThreadId})",
                    meetingId, threadId);
            }
            else
            {
                _logger.LogDebug("No thread found for meeting {MeetingId} during cleanup", meetingId);
            }

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up meeting {MeetingId}", meetingId);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Dispose of managed resources.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Dispose pattern implementation.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _cleanupTimer?.Dispose();
            _threadCreationLock.Dispose();
            _logger.LogInformation("PennieAgentClient disposed");
        }

        _disposed = true;
    }
}
