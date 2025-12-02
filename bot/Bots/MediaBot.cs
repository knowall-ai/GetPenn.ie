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
    private readonly SemaphoreSlim _joinMeetingSemaphore = new(1, 1); // Prevents race condition in meeting join

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

        // For all other messages (including "what projects", "help", general questions, etc.),
        // forward to Pennie for conversational handling
        await HandleGeneralConversationAsync(turnContext, cancellationToken);
    }

    /// <summary>
    /// Handle general conversation by forwarding to Pennie AI agent.
    /// This enables Pennie to answer questions about Agile, methodologies, DevOps projects, etc.
    /// </summary>
    private async Task HandleGeneralConversationAsync(
        ITurnContext turnContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var userMessage = turnContext.Activity.Text ?? "";

            // Strip @mention markup (e.g., "<at>Pennie</at>") from user messages
            // Teams adds this XML when users @mention the bot in group chats
            userMessage = StripAtMentions(userMessage);

            _logger.LogInformation("Forwarding message to Pennie: {Message}", userMessage);

            // Create a "chat" meeting ID for non-meeting conversations
            // This allows Pennie to maintain conversation context per Teams conversation
            var conversationId = turnContext.Activity.Conversation.Id;
            var chatMeetingId = $"chat_{conversationId}";

            // Create a transcription result to send to Pennie
            // In chat mode, there's no speaker diarization, so use the user's name
            var transcriptionResult = new TranscriptionResult
            {
                MeetingId = chatMeetingId,
                Speaker = turnContext.Activity.From?.Name ?? "User",
                Timestamp = DateTime.UtcNow,
                Text = userMessage
            };

            // Send to Pennie agent and get response
            var response = await _agentClient.SendMessageAndGetResponseAsync(transcriptionResult, cancellationToken);

            if (!string.IsNullOrEmpty(response))
            {
                await turnContext.SendActivityAsync(MessageFactory.Text(response), cancellationToken);
                _logger.LogInformation("Sent Pennie's response to user");
            }
            else
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text("I'm having trouble thinking right now. Could you try again?"),
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling general conversation");
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Sorry, I encountered an error. Please try again."),
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
        var regexTimeout = TimeSpan.FromMilliseconds(100);
        var idPattern = new System.Text.RegularExpressions.Regex(
            @"id\s*:?\s*([\d\s]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            regexTimeout);
        System.Text.RegularExpressions.Match match;
        try
        {
            match = idPattern.Match(text);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return null; // Input too complex, reject
        }
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
            if (IsValidMeetingIdFormat(id))
            {
                return id;
            }
        }

        // Pattern 2: Look for a sequence of numbers that could be a meeting ID (10-30 digits)
        var numberPattern = new System.Text.RegularExpressions.Regex(
            @"(\d[\d\s]{9,30})",
            System.Text.RegularExpressions.RegexOptions.None,
            regexTimeout);
        try
        {
            match = numberPattern.Match(text);
            if (match.Success)
            {
                var id = match.Groups[1].Value.Trim();
                if (IsValidMeetingIdFormat(id))
                {
                    return id;
                }
            }
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return null; // Input too complex, reject
        }

        return null;
    }

    /// <summary>
    /// Validate that a meeting ID has the correct format (10-15 digits when spaces are removed).
    /// </summary>
    private static bool IsValidMeetingIdFormat(string? meetingId)
    {
        if (string.IsNullOrWhiteSpace(meetingId))
        {
            return false;
        }

        // Remove spaces and validate digit count
        var digitsOnly = meetingId.Replace(" ", "");

        // Teams meeting IDs are typically 10-15 digits
        if (digitsOnly.Length < 10 || digitsOnly.Length > 15)
        {
            return false;
        }

        // Ensure all characters are digits
        return digitsOnly.All(char.IsDigit);
    }

    /// <summary>
    /// Extract passcode from a message.
    /// </summary>
    private static string? ExtractPasscode(string text)
    {
        var regexTimeout = TimeSpan.FromMilliseconds(100);

        // Pattern 1: "passcode:" or "passcode :" followed by alphanumeric
        var passcodePattern = new System.Text.RegularExpressions.Regex(
            @"passcode\s*:?\s*([a-zA-Z0-9]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            regexTimeout);
        try
        {
            var match = passcodePattern.Match(text);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return null; // Input too complex, reject
        }

        // Pattern 2: Look for alphanumeric string after the meeting ID (8+ chars)
        var lastWordPattern = new System.Text.RegularExpressions.Regex(
            @"\s([a-zA-Z][a-zA-Z0-9]{5,})$",
            System.Text.RegularExpressions.RegexOptions.None,
            regexTimeout);
        try
        {
            var match = lastWordPattern.Match(text.Trim());
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return null; // Input too complex, reject
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
        // Use semaphore to prevent race condition when multiple events trigger join simultaneously
        await _joinMeetingSemaphore.WaitAsync(cancellationToken);
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
            // Expected when running outside Windows VM - notify user
            _logger.LogWarning("Meeting audio join not available - Graph Communications SDK requires Windows Server deployment");
            await turnContext.SendActivityAsync(
                MessageFactory.Text("I'm unable to capture meeting audio. This feature requires the bot to be running on Windows Server with Graph Communications SDK."),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join meeting for audio capture");
            // Notify user of audio join failure - don't throw so bot continues for chat
            await turnContext.SendActivityAsync(
                MessageFactory.Text("I wasn't able to join for audio capture, but I can still chat! To try again, say \"join\"."),
                cancellationToken);
        }
        finally
        {
            _joinMeetingSemaphore.Release();
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
        var cleanText = StripAtMentions(text);

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
    /// Strip @mention markup from Teams messages.
    /// Teams wraps @mentions in XML like: "<at>Pennie</at> what projects do we have?"
    /// This strips the markup so Pennie receives clean text.
    /// </summary>
    private static string StripAtMentions(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Remove <at>...</at> tags (Teams @mention markup)
        // Uses timeout to prevent ReDoS attacks
        try
        {
            var cleanText = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"<at>.*?</at>",
                "",
                System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromMilliseconds(100));

            return cleanText.Trim();
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            // If regex times out, return original text
            return text.Trim();
        }
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
            if (root.TryGetProperty("meetingInfo", out var meetingInfo) &&
                meetingInfo.TryGetProperty("id", out var meetingInfoIdProp))
            {
                context.MeetingId ??= meetingInfoIdProp.GetString();
            }

            // Check conversation type - meeting chats have specific types
            if (root.TryGetProperty("channel", out var channel) &&
                channel.TryGetProperty("id", out var channelIdProp))
            {
                context.ChannelId = channelIdProp.GetString();
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
