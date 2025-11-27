using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using PennieBot.Services;

namespace PennieBot.Bots;

/// <summary>
/// Teams Media Bot that joins meetings, captures real-time audio,
/// and sends transcripts to Pennie AI agent.
/// </summary>
public class MediaBot : ActivityHandler
{
    private readonly ILogger<MediaBot> _logger;
    private readonly IGraphCallService _callService;
    private readonly ISpeechTranscriptionService _speechService;
    private readonly IPennieAgentClient _agentClient;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, string> _conversationToMeetingMap = new(); // conversation ID -> meeting ID

    public MediaBot(
        ILogger<MediaBot> logger,
        IGraphCallService callService,
        ISpeechTranscriptionService speechService,
        IPennieAgentClient agentClient,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _callService = callService;
        _speechService = speechService;
        _agentClient = agentClient;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Called when the bot receives a message activity.
    /// </summary>
    protected override async Task OnMessageActivityAsync(
        ITurnContext<IMessageActivity> turnContext,
        CancellationToken cancellationToken)
    {
        var text = turnContext.Activity.Text?.ToLowerInvariant() ?? "";
        _logger.LogInformation("Received message: {Text}", turnContext.Activity.Text);

        // Check for project-related queries
        if (text.Contains("what projects") || text.Contains("devops projects") ||
            text.Contains("list projects") || text.Contains("show projects"))
        {
            await HandleProjectQueryAsync(turnContext, cancellationToken);
            return;
        }

        // Check for help command
        if (text.Contains("help") || text == "?")
        {
            var helpText = "Hi! I'm Pennie the Prepper. Here's what I can do:\n\n" +
                          "- Ask me: **\"What projects do we have in DevOps?\"**\n" +
                          "- I'll list all your Azure DevOps projects\n\n" +
                          "More features coming soon!";
            await turnContext.SendActivityAsync(MessageFactory.Text(helpText), cancellationToken);
            return;
        }

        // Default response
        var responseText = "I didn't understand that. Try asking:\n" +
                          "- \"What projects do we have in DevOps?\"\n" +
                          "- \"Help\"";
        await turnContext.SendActivityAsync(MessageFactory.Text(responseText), cancellationToken);
    }

    /// <summary>
    /// Handle queries about Azure DevOps projects.
    /// </summary>
    private async Task HandleProjectQueryAsync(
        ITurnContext turnContext,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Handling project query");

            // Get backend URL from configuration
            var backendUrl = _configuration["AZURE_FUNCTIONS_BACKEND_URL"]
                ?? "https://pennie-backend-prod.azurewebsites.net";

            var client = _httpClientFactory.CreateClient();
            var response = await client.GetStringAsync($"{backendUrl}/api/read_projects", cancellationToken);

            _logger.LogInformation("Backend response: {Response}", response);

            // Parse the JSON response
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (!root.TryGetProperty("success", out var successProp) || !successProp.GetBoolean())
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text("Sorry, I couldn't retrieve the projects. Please try again later."),
                    cancellationToken);
                return;
            }

            var count = root.GetProperty("count").GetInt32();
            var projects = root.GetProperty("projects").EnumerateArray();

            // Build response message
            var projectList = new List<string>();
            foreach (var project in projects.Take(15))
            {
                if (project.TryGetProperty("name", out var nameProp))
                {
                    var name = nameProp.GetString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        projectList.Add($"- {name}");
                    }
                }
            }

            var reply = $"**Azure DevOps Projects**\n\n" +
                       $"Found {count} projects:\n\n" +
                       string.Join("\n", projectList);

            if (count > 15)
            {
                reply += $"\n\n_(Showing first 15 of {count})_";
            }

            await turnContext.SendActivityAsync(MessageFactory.Text(reply), cancellationToken);
            _logger.LogInformation("Successfully returned {Count} projects", count);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error calling backend API");
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Sorry, I couldn't connect to the backend service. Please try again later."),
                cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Error parsing backend response");
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Sorry, I received an unexpected response from the backend. Please try again later."),
                cancellationToken);
        }
    }

    /// <summary>
    /// Called when the bot is added to a conversation (meeting invite).
    /// </summary>
    protected override async Task OnConversationUpdateActivityAsync(
        ITurnContext<IConversationUpdateActivity> turnContext,
        CancellationToken cancellationToken)
    {
        foreach (var member in turnContext.Activity.MembersAdded ?? Array.Empty<ChannelAccount>())
        {
            if (member.Id != turnContext.Activity.Recipient.Id)
            {
                continue;
            }

            _logger.LogInformation("Bot was added to conversation: {ConversationId}",
                turnContext.Activity.Conversation.Id);

            // Announce presence
            var welcomeMessage = "Hi! I'm Pennie the Prepper. " +
                                "Ask me about your Azure DevOps projects - try \"What projects do we have in DevOps?\"";

            await turnContext.SendActivityAsync(
                MessageFactory.Text(welcomeMessage),
                cancellationToken);

            // Check if this is a meeting and attempt to join for audio capture
            await TryJoinMeetingForAudioAsync(turnContext, cancellationToken);
        }
    }

    /// <summary>
    /// Called when the bot receives members added to the conversation.
    /// </summary>
    protected override async Task OnMembersAddedAsync(
        IList<ChannelAccount> membersAdded,
        ITurnContext<IConversationUpdateActivity> turnContext,
        CancellationToken cancellationToken)
    {
        foreach (var member in membersAdded)
        {
            if (member.Id == turnContext.Activity.Recipient.Id)
            {
                continue;
            }

            _logger.LogInformation("Member joined: {Name} ({Id})", member.Name, member.Id);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Called when the bot is removed from a conversation (meeting ends or bot removed).
    /// </summary>
    protected override async Task OnMembersRemovedAsync(
        IList<ChannelAccount> membersRemoved,
        ITurnContext<IConversationUpdateActivity> turnContext,
        CancellationToken cancellationToken)
    {
        foreach (var member in membersRemoved)
        {
            if (member.Id == turnContext.Activity.Recipient.Id)
            {
                _logger.LogInformation("Bot was removed from conversation");

                // Clean up any meeting resources
                var conversationId = turnContext.Activity.Conversation.Id;
                if (_conversationToMeetingMap.TryRemove(conversationId, out var meetingId))
                {
                    // Stop transcription
                    await _speechService.StopTranscriptionAsync(meetingId);

                    // Leave the meeting call
                    await _callService.LeaveMeetingAsync(meetingId);

                    // Cleanup agent session
                    await _agentClient.CleanupMeetingAsync(meetingId);

                    _logger.LogInformation("Cleaned up meeting {MeetingId}", meetingId);
                }
                break;
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Attempt to join a Teams meeting for audio capture if this is a meeting conversation.
    /// </summary>
    private async Task TryJoinMeetingForAudioAsync(
        ITurnContext turnContext,
        CancellationToken cancellationToken)
    {
        try
        {
            // Extract meeting information from channel data
            var channelData = turnContext.Activity.ChannelData;
            if (channelData == null)
            {
                _logger.LogDebug("No channel data available, not a meeting context");
                return;
            }

            // Parse channel data to extract meeting join URL
            var channelDataJson = JsonSerializer.Serialize(channelData);
            _logger.LogDebug("Channel data: {ChannelData}", channelDataJson);

            using var doc = JsonDocument.Parse(channelDataJson);
            var root = doc.RootElement;

            // Check if this is a meeting
            string? meetingJoinUrl = null;

            // Try to get meeting info from different possible locations
            if (root.TryGetProperty("meeting", out var meeting))
            {
                if (meeting.TryGetProperty("joinUrl", out var joinUrl))
                {
                    meetingJoinUrl = joinUrl.GetString();
                }
            }
            else if (root.TryGetProperty("teamsChannelId", out _) &&
                     root.TryGetProperty("tenant", out _))
            {
                // This might be a meeting context, but we need the join URL
                _logger.LogInformation("Teams context detected but no meeting join URL available");
            }

            if (string.IsNullOrEmpty(meetingJoinUrl))
            {
                _logger.LogDebug("No meeting join URL found, skipping audio join");
                return;
            }

            // Generate meeting ID and store mapping
            var conversationId = turnContext.Activity.Conversation.Id;
            var meetingId = $"meeting_{Guid.NewGuid():N}";

            // Use TryAdd for thread-safe operation - prevents duplicate entries if same conversation processed concurrently
            if (!_conversationToMeetingMap.TryAdd(conversationId, meetingId))
            {
                _logger.LogWarning("Conversation {ConversationId} already has a meeting mapped, skipping duplicate join",
                    conversationId);
                return;
            }

            _logger.LogInformation(
                "Detected meeting context. MeetingId={MeetingId}, JoinUrl={JoinUrl}",
                meetingId, meetingJoinUrl);

            // First, try to join the meeting. Only start transcription if join succeeds.
            // This prevents orphaned transcription sessions when join fails.
            try
            {
                await _callService.JoinMeetingAsync(
                    meetingJoinUrl,
                    meetingId,
                    async audioData => await OnAudioReceivedAsync(meetingId, audioData),
                    cancellationToken);

                // Meeting join succeeded - now start transcription
                await _speechService.StartTranscriptionAsync(
                    meetingId,
                    async result => await OnTranscriptReceivedAsync(result, turnContext),
                    cancellationToken);

                _logger.LogInformation("Successfully joined meeting {MeetingId} for audio capture", meetingId);
            }
            catch
            {
                // Clean up mapping on any failure to prevent orphaned entries
                _conversationToMeetingMap.TryRemove(conversationId, out _);
                throw;
            }
        }
        catch (NotImplementedException)
        {
            // Expected when running outside Windows VM
            _logger.LogWarning("Meeting audio join not available - Graph Communications SDK requires Windows Server deployment");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join meeting for audio capture");
            // Don't throw - bot should continue to work for chat even if audio fails
        }
    }

    /// <summary>
    /// Handle incoming audio data from the meeting.
    /// </summary>
    private async Task OnAudioReceivedAsync(string meetingId, byte[] audioData)
    {
        try
        {
            // Send audio to speech transcription service
            await _speechService.ProcessAudioAsync(meetingId, audioData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing audio for meeting {MeetingId}", meetingId);
        }
    }

    /// <summary>
    /// Handle transcription results from speech service.
    /// </summary>
    private async Task OnTranscriptReceivedAsync(
        TranscriptionResult result,
        ITurnContext turnContext)
    {
        try
        {
            _logger.LogInformation(
                "Transcript received: {Speaker} @ {Timestamp}: {Text}",
                result.Speaker, result.Timestamp, result.Text);

            // Send transcript to Pennie agent for processing
            await _agentClient.SendTranscriptAsync(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing transcript for meeting {MeetingId}", result.MeetingId);
        }
    }
}
