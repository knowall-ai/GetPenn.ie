using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Assistants;
using Azure.Identity;

namespace PennieBot.Services;

/// <summary>
/// Client for communicating with Pennie AI Foundry Agent.
/// Implements OpenAI Assistants function calling pattern with Azure Functions backend.
/// </summary>
public class PennieAgentClient : IPennieAgentClient
{
    private readonly ILogger<PennieAgentClient> _logger;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly AssistantsClient _assistantsClient;
    private readonly string _assistantId;
    private readonly string _backendUrl;
    private readonly Dictionary<string, string> _meetingThreads = new();
    private static readonly HashSet<string> GetFunctions = new() { "read_projects", "read_link_types" };

    public PennieAgentClient(
        ILogger<PennieAgentClient> logger,
        IConfiguration configuration,
        HttpClient httpClient)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = httpClient;

        // Get configuration
        var endpoint = _configuration["AZURE_AI_FOUNDRY_ENDPOINT"]
            ?? throw new InvalidOperationException("AZURE_AI_FOUNDRY_ENDPOINT not configured");

        _assistantId = _configuration["AZURE_AI_FOUNDRY_AGENT_ID"]
            ?? throw new InvalidOperationException("AZURE_AI_FOUNDRY_AGENT_ID not configured");

        _backendUrl = _configuration["AZURE_FUNCTIONS_BACKEND_URL"]
            ?? "https://pennie-backend-prod.azurewebsites.net"; // Default to production backend

        // Create Assistants client using DefaultAzureCredential
        // This will use managed identity in production or developer credentials locally
        _assistantsClient = new AssistantsClient(
            new Uri(endpoint),
            new DefaultAzureCredential());

        _logger.LogInformation(
            "Pennie Agent Client initialized with assistant {AssistantId} at {Endpoint}",
            _assistantId, endpoint);
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

    /// <summary>
    /// Get or create a thread for a meeting.
    /// </summary>
    private async Task<string> GetOrCreateThreadAsync(string meetingId, CancellationToken cancellationToken)
    {
        if (_meetingThreads.TryGetValue(meetingId, out var existingThreadId))
        {
            return existingThreadId;
        }

        var thread = await _assistantsClient.CreateThreadAsync(cancellationToken);

        var threadId = thread.Value.Id;
        _meetingThreads[meetingId] = threadId;

        _logger.LogInformation("Created new thread {ThreadId} for meeting {MeetingId}", threadId, meetingId);

        return threadId;
    }

    /// <summary>
    /// Monitor run status and handle function calls.
    /// This is the CRITICAL function call handler that makes Pennie's backend integration work.
    /// </summary>
    private async Task ProcessRunAsync(string threadId, string runId, CancellationToken cancellationToken)
    {
        var timeoutSeconds = _configuration.GetValue<int>("PennieAgent:RunTimeoutSeconds", 60);
        var maxAttempts = timeoutSeconds; // Default 60 seconds (60 attempts * 1 second delay)
        var attempt = 0;

        while (attempt < maxAttempts)
        {
            attempt++;

            // Get current run status
            var run = await _assistantsClient.GetRunAsync(threadId, runId, cancellationToken);
            var status = run.Value.Status;

            _logger.LogDebug("Run {RunId} status: {Status} (attempt {Attempt}/{MaxAttempts})",
                runId, status, attempt, maxAttempts);

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
                // CRITICAL: Handle function calls
                _logger.LogInformation("Run requires action - processing function calls");

                var requiredAction = run.Value.RequiredAction;
                if (requiredAction is SubmitToolOutputsAction submitToolOutputsAction)
                {
                    var toolOutputs = new List<ToolOutput>();

                    foreach (var toolCall in submitToolOutputsAction.ToolCalls)
                    {
                        if (toolCall is RequiredFunctionToolCall functionCall)
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

            // Wait before next status check
            await Task.Delay(1000, cancellationToken);
        }

        _logger.LogWarning("Run {RunId} did not complete within timeout", runId);
    }

    /// <summary>
    /// Call a backend function and return the result.
    /// </summary>
    private async Task<string> CallBackendFunctionAsync(
        string functionName,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        try
        {
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
            if (!_meetingThreads.TryGetValue(meetingId, out var threadId))
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

            // TODO: This would typically be handled by Pennie posting to Teams chat
            // via the Bot Framework messaging API
            // For now, just log

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error notifying work item creation");
            return Task.CompletedTask;
        }
    }
}
