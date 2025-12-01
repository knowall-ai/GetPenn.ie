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

        // Log channel data for debugging meeting context
        LogChannelDataForDebugging(turnContext);

        // Check for project-related queries
        if (text.Contains("what projects") || text.Contains("devops projects") ||
            text.Contains("list projects") || text.Contains("show projects"))
        {
            await HandleProjectQueryAsync(turnContext, cancellationToken);
            return;
        }

        // Check for simple join commands (like "join", "come join", "join us")
        // These can auto-join if we're in a meeting context
        if (IsSimpleJoinCommand(text))
        {
            await HandleSimpleJoinCommandAsync(turnContext, cancellationToken);
            return;
        }

        // Check for join meeting command with ID/passcode
        if (text.Contains("join meeting") || text.Contains("join my meeting"))
        {
            await HandleJoinMeetingRequestAsync(turnContext, turnContext.Activity.Text ?? "", cancellationToken);
            return;
        }

        // Check for help command
        if (text.Contains("help") || text == "?")
        {
            var helpText = "Hi! I'm Pennie the Prepper. Here's what I can do:\n\n" +
                          "- Ask me: **\"What projects do we have in DevOps?\"**\n" +
                          "- I'll list all your Azure DevOps projects\n" +
                          "- Say: **\"Join meeting ID: xxx passcode: yyy\"** to join a Teams meeting\n\n" +
                          "More features coming soon!";
            await turnContext.SendActivityAsync(MessageFactory.Text(helpText), cancellationToken);
            return;
        }

        // Default response
        var responseText = "I didn't understand that. Try asking:\n" +
                          "- \"What projects do we have in DevOps?\"\n" +
                          "- \"Join meeting ID: xxx passcode: yyy\"\n" +
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
    /// Handle a request to join a Teams meeting by ID and passcode.
    /// </summary>
    private async Task HandleJoinMeetingRequestAsync(
        ITurnContext turnContext,
        string originalText,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Processing join meeting request: {Text}", originalText);

            // Parse meeting ID and passcode from the message
            // Expected formats:
            // - "join meeting ID: 396 240 783 591 15 passcode: tj3HN9jw"
            // - "join meeting 396 240 783 591 15 tj3HN9jw"
            // - "join meeting id 396240783591 passcode tj3HN9jw"

            var meetingId = ExtractMeetingId(originalText);
            var passcode = ExtractPasscode(originalText);

            if (string.IsNullOrEmpty(meetingId))
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text("I couldn't find a meeting ID. Please provide it like:\n" +
                                       "\"Join meeting ID: 396 240 783 591 15 passcode: tj3HN9jw\""),
                    cancellationToken);
                return;
            }

            if (string.IsNullOrEmpty(passcode))
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text($"Found meeting ID: {meetingId}\n" +
                                       "Please also provide the passcode:\n" +
                                       "\"Join meeting ID: xxx passcode: yyy\""),
                    cancellationToken);
                return;
            }

            _logger.LogInformation("Attempting to join meeting. ID={MeetingId}, Passcode={Passcode}",
                meetingId, passcode);

            await turnContext.SendActivityAsync(
                MessageFactory.Text($"🎯 Attempting to join meeting...\n" +
                                   $"Meeting ID: {meetingId}\n" +
                                   $"Passcode: {passcode}"),
                cancellationToken);

            // Generate internal meeting tracking ID
            var internalMeetingId = $"meeting_{Guid.NewGuid():N}";
            var conversationId = turnContext.Activity.Conversation.Id;

            // Store the mapping
            _conversationToMeetingMap[conversationId] = internalMeetingId;

            // Join the meeting
            await _callService.JoinMeetingByIdAsync(
                meetingId,
                passcode,
                internalMeetingId,
                async (audioData, speakerId, speakerName) => await OnAudioReceivedAsync(internalMeetingId, audioData, speakerId, speakerName),
                cancellationToken);

            // Start transcription after successfully joining
            await _speechService.StartTranscriptionAsync(
                internalMeetingId,
                async result => await OnTranscriptReceivedAsync(result, turnContext),
                cancellationToken);

            await turnContext.SendActivityAsync(
                MessageFactory.Text("✅ Successfully joined the meeting! I'm now listening and will transcribe the conversation."),
                cancellationToken);

            _logger.LogInformation("Successfully joined meeting {MeetingId}", meetingId);
        }
        catch (NotImplementedException)
        {
            _logger.LogWarning("Meeting join not available - requires Windows Server deployment with Graph Communications SDK");
            await turnContext.SendActivityAsync(
                MessageFactory.Text("❌ Meeting join is not available. This feature requires the bot to be running on Windows Server with Graph Communications SDK."),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join meeting");
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"❌ Failed to join meeting: {ex.Message}"),
                cancellationToken);
        }
    }

    /// <summary>
    /// Extract meeting ID from a message. Handles formats like "396 240 783 591 15" or "39624078359115".
    /// </summary>
    private static string? ExtractMeetingId(string text)
    {
        // Pattern 1: "id:" or "id :" followed by digits and spaces
        var idPattern = new System.Text.RegularExpressions.Regex(
            @"id\s*:?\s*([\d\s]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var match = idPattern.Match(text);
        if (match.Success)
        {
            var id = match.Groups[1].Value.Trim();
            // Stop at "passcode" or end of digits
            var passcodeIndex = id.IndexOf("passcode", StringComparison.OrdinalIgnoreCase);
            if (passcodeIndex > 0)
            {
                id = id.Substring(0, passcodeIndex).Trim();
            }
            // Remove any non-digit/space chars at the end
            id = System.Text.RegularExpressions.Regex.Replace(id, @"[^\d\s]+$", "").Trim();
            if (!string.IsNullOrEmpty(id))
            {
                return id;
            }
        }

        // Pattern 2: Look for a sequence of numbers that could be a meeting ID (10+ digits)
        var numberPattern = new System.Text.RegularExpressions.Regex(@"(\d[\d\s]{9,})");
        match = numberPattern.Match(text);
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        return null;
    }

    /// <summary>
    /// Extract passcode from a message.
    /// </summary>
    private static string? ExtractPasscode(string text)
    {
        // Pattern 1: "passcode:" or "passcode :" followed by alphanumeric
        var passcodePattern = new System.Text.RegularExpressions.Regex(
            @"passcode\s*:?\s*([a-zA-Z0-9]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var match = passcodePattern.Match(text);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        // Pattern 2: Look for alphanumeric string after the meeting ID (8+ chars)
        var lastWordPattern = new System.Text.RegularExpressions.Regex(@"\s([a-zA-Z][a-zA-Z0-9]{5,})$");
        match = lastWordPattern.Match(text.Trim());
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return null;
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
                    async (audioData, speakerId, speakerName) => await OnAudioReceivedAsync(meetingId, audioData, speakerId, speakerName),
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
    private async Task OnAudioReceivedAsync(string meetingId, byte[] audioData, uint speakerId = 0, string? speakerName = null)
    {
        try
        {
            // Send audio to speech transcription service with speaker ID and name
            await _speechService.ProcessAudioAsync(meetingId, audioData, speakerId, speakerName);
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

    /// <summary>
    /// Check if the text is a simple join command (without explicit meeting ID).
    /// </summary>
    private static bool IsSimpleJoinCommand(string text)
    {
        // Remove bot mention from text for cleaner matching
        var cleanText = System.Text.RegularExpressions.Regex.Replace(text, @"<at>.*?</at>", "").Trim();

        // Check for simple join patterns
        var joinPatterns = new[]
        {
            "join",
            "come join",
            "join us",
            "join the meeting",
            "join the call",
            "join this meeting",
            "join this call",
            "please join",
            "can you join"
        };

        foreach (var pattern in joinPatterns)
        {
            if (cleanText.Contains(pattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Handle a simple join command by detecting meeting context and auto-joining.
    /// </summary>
    private async Task HandleSimpleJoinCommandAsync(
        ITurnContext turnContext,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing simple join command");

        // Try to extract meeting context from channel data
        var meetingContext = ExtractMeetingContext(turnContext);

        if (meetingContext == null)
        {
            _logger.LogInformation("No meeting context found - asking user for meeting details");
            await turnContext.SendActivityAsync(
                MessageFactory.Text("I'd love to join, but I can't detect the meeting context from here.\n\n" +
                                   "To join a meeting, please provide the meeting details like:\n" +
                                   "\"Join meeting ID: 396 240 783 591 15 passcode: tj3HN9jw\"\n\n" +
                                   "Or add me as a participant through the meeting's People panel."),
                cancellationToken);
            return;
        }

        _logger.LogInformation("Meeting context found: {MeetingId}, JoinUrl: {JoinUrl}",
            meetingContext.MeetingId, meetingContext.JoinUrl ?? "(not available)");

        await turnContext.SendActivityAsync(
            MessageFactory.Text("I detected this meeting! Let me join..."),
            cancellationToken);

        // Try to join the meeting
        try
        {
            var internalMeetingId = $"meeting_{Guid.NewGuid():N}";
            var conversationId = turnContext.Activity.Conversation.Id;

            // Store the mapping
            if (!_conversationToMeetingMap.TryAdd(conversationId, internalMeetingId))
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text("I'm already in this meeting!"),
                    cancellationToken);
                return;
            }

            if (!string.IsNullOrEmpty(meetingContext.JoinUrl))
            {
                // Join via URL
                await _callService.JoinMeetingAsync(
                    meetingContext.JoinUrl,
                    internalMeetingId,
                    async (audioData, speakerId, speakerName) => await OnAudioReceivedAsync(internalMeetingId, audioData, speakerId, speakerName),
                    cancellationToken);
            }
            else if (!string.IsNullOrEmpty(meetingContext.MeetingId))
            {
                // Join via meeting ID (need passcode too, but may not have it)
                await turnContext.SendActivityAsync(
                    MessageFactory.Text($"I found the meeting ID ({meetingContext.MeetingId}), but I need the passcode to join.\n" +
                                       "Please say: \"Join meeting ID: {meetingId} passcode: xxx\""),
                    cancellationToken);
                _conversationToMeetingMap.TryRemove(conversationId, out _);
                return;
            }
            else
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text("I can see we're in a meeting, but I couldn't find a way to join.\n" +
                                       "Please add me as a participant through the meeting's People panel."),
                    cancellationToken);
                _conversationToMeetingMap.TryRemove(conversationId, out _);
                return;
            }

            // Start transcription after successfully joining
            await _speechService.StartTranscriptionAsync(
                internalMeetingId,
                async result => await OnTranscriptReceivedAsync(result, turnContext),
                cancellationToken);

            await turnContext.SendActivityAsync(
                MessageFactory.Text("I've joined the meeting! I'm now listening and will transcribe the conversation."),
                cancellationToken);
        }
        catch (NotImplementedException)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Meeting join is not available. This feature requires the bot to be running on Windows Server."),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join meeting from simple command");
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"Failed to join the meeting: {ex.Message}"),
                cancellationToken);
        }
    }

    /// <summary>
    /// Extract meeting context from Teams channel data.
    /// </summary>
    private MeetingContext? ExtractMeetingContext(ITurnContext turnContext)
    {
        try
        {
            var channelData = turnContext.Activity.ChannelData;
            if (channelData == null)
            {
                return null;
            }

            var channelDataJson = JsonSerializer.Serialize(channelData);
            _logger.LogDebug("Extracting meeting context from: {ChannelData}", channelDataJson);

            using var doc = JsonDocument.Parse(channelDataJson);
            var root = doc.RootElement;

            var context = new MeetingContext();

            // Check for meeting object (contains meeting info when in a meeting)
            if (root.TryGetProperty("meeting", out var meeting))
            {
                if (meeting.TryGetProperty("id", out var meetingIdProp))
                {
                    context.MeetingId = meetingIdProp.GetString();
                }
                if (meeting.TryGetProperty("joinUrl", out var joinUrlProp))
                {
                    context.JoinUrl = joinUrlProp.GetString();
                }
            }

            // Check for meetingInfo (alternative location)
            if (root.TryGetProperty("meetingInfo", out var meetingInfo))
            {
                if (meetingInfo.TryGetProperty("id", out var meetingIdProp))
                {
                    context.MeetingId ??= meetingIdProp.GetString();
                }
            }

            // Check conversation type - meeting chats have specific types
            if (root.TryGetProperty("channel", out var channel))
            {
                if (channel.TryGetProperty("id", out var channelIdProp))
                {
                    context.ChannelId = channelIdProp.GetString();
                }
            }

            // If we have any meeting info, return the context
            if (!string.IsNullOrEmpty(context.MeetingId) || !string.IsNullOrEmpty(context.JoinUrl))
            {
                return context;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting meeting context");
            return null;
        }
    }

    /// <summary>
    /// Log channel data for debugging purposes.
    /// </summary>
    private void LogChannelDataForDebugging(ITurnContext turnContext)
    {
        try
        {
            var channelData = turnContext.Activity.ChannelData;
            if (channelData == null)
            {
                _logger.LogDebug("No channel data available");
                return;
            }

            var channelDataJson = JsonSerializer.Serialize(channelData, new JsonSerializerOptions { WriteIndented = false });
            _logger.LogInformation("Channel data: {ChannelData}", channelDataJson);

            // Also log conversation info
            var conversation = turnContext.Activity.Conversation;
            _logger.LogInformation("Conversation: Id={Id}, Type={Type}, IsGroup={IsGroup}",
                conversation?.Id, conversation?.ConversationType, conversation?.IsGroup);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error logging channel data");
        }
    }

    /// <summary>
    /// Helper class to hold meeting context information.
    /// </summary>
    private class MeetingContext
    {
        public string? MeetingId { get; set; }
        public string? JoinUrl { get; set; }
        public string? ChannelId { get; set; }
    }
}
