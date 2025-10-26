using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Resources;
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
    private readonly Dictionary<string, string> _conversationToMeetingMap = new(); // conversation ID -> meeting ID

    public MediaBot(
        ILogger<MediaBot> logger,
        IGraphCallService callService,
        ISpeechTranscriptionService speechService,
        IPennieAgentClient agentClient,
        IConfiguration configuration)
    {
        _logger = logger;
        _callService = callService;
        _speechService = speechService;
        _agentClient = agentClient;
        _configuration = configuration;
    }

    /// <summary>
    /// Called when the bot receives a message activity.
    /// </summary>
    protected override async Task OnMessageActivityAsync(
        ITurnContext<IMessageActivity> turnContext,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Received message: {Text}", turnContext.Activity.Text);

        var responseText = $"Echo: {turnContext.Activity.Text}";
        await turnContext.SendActivityAsync(
            MessageFactory.Text(responseText),
            cancellationToken);
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
            var welcomeMessage = "👋 Hi, I'm Pennie the Prepper! " +
                                "I'll be listening to this meeting and creating backlog items in Azure DevOps. " +
                                "All participants consent to transcription by continuing in this meeting.";

            await turnContext.SendActivityAsync(
                MessageFactory.Text(welcomeMessage),
                cancellationToken);

            // Join the meeting audio (this is where Graph Communications SDK integration would go)
            await JoinMeetingAudioAsync(turnContext, cancellationToken);
        }
    }

    /// <summary>
    /// Join meeting audio and start transcription.
    /// </summary>
    private async Task JoinMeetingAudioAsync(
        ITurnContext turnContext,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Attempting to join meeting audio for conversation {ConversationId}",
                turnContext.Activity.Conversation.Id);

            // Extract meeting join URL from Teams channel data
            // The join URL is in channelData.meeting.joinUrl for Teams meetings
            var channelData = turnContext.Activity.ChannelData as Newtonsoft.Json.Linq.JObject;
            var meetingJoinUrl = channelData?.SelectToken("meeting.joinUrl")?.ToString();

            if (string.IsNullOrEmpty(meetingJoinUrl))
            {
                _logger.LogError("Could not extract meeting join URL from Teams activity");
                await turnContext.SendActivityAsync(
                    MessageFactory.Text("⚠️ Unable to join meeting - meeting join URL not found in activity."),
                    cancellationToken);
                return;
            }

            var meetingId = turnContext.Activity.Conversation.Id; // Use conversation ID as meeting ID for tracking

            // Track conversation to meeting mapping
            _conversationToMeetingMap[turnContext.Activity.Conversation.Id] = meetingId;

            // Initialize Graph Call Service if not already initialized
            if (!_callService.IsInMeeting(meetingId))
            {
                await _callService.InitializeAsync(cancellationToken);
            }

            // Start speech transcription with speaker diarization
            await _speechService.StartTranscriptionAsync(
                meetingId,
                async (transcriptionResult) =>
                {
                    // Forward transcript to Pennie AI agent for analysis
                    _logger.LogInformation("Transcript from {Speaker}: {Text}",
                        transcriptionResult.Speaker, transcriptionResult.Text);

                    await _agentClient.SendTranscriptAsync(transcriptionResult, cancellationToken);
                },
                cancellationToken);

            // Join the meeting via Graph Communications SDK
            await _callService.JoinMeetingAsync(
                meetingJoinUrl,
                meetingId,
                async (audioData) =>
                {
                    // Audio callback: Send RTP audio frames to Speech Services
                    // Audio format: 16kHz, mono, 16-bit PCM
                    await _speechService.ProcessAudioAsync(meetingId, audioData);
                },
                cancellationToken);

            _logger.LogInformation("Successfully joined meeting audio for {MeetingId}", meetingId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error joining meeting audio");

            // Notify user in chat
            await turnContext.SendActivityAsync(
                MessageFactory.Text("⚠️ Sorry, I encountered an error joining the meeting audio. Please check the logs."),
                cancellationToken);

            throw;
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

                // Stop transcription and generate summary
                await StopTranscriptionAsync(cancellationToken);
                break;
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Stop transcription and generate post-meeting summary.
    /// </summary>
    private async Task StopTranscriptionAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Stopping transcription and leaving meetings...");

            // Leave all active meetings and stop transcriptions
            foreach (var (conversationId, meetingId) in _conversationToMeetingMap.ToList())
            {
                try
                {
                    _logger.LogInformation("Leaving meeting {MeetingId}", meetingId);

                    // Stop transcription
                    await _speechService.StopTranscriptionAsync(meetingId);

                    // Leave the meeting via Graph Call Service
                    await _callService.LeaveMeetingAsync(meetingId);

                    // Remove from tracking
                    _conversationToMeetingMap.Remove(conversationId);

                    _logger.LogInformation("Successfully left meeting {MeetingId}", meetingId);

                    // TODO: Future enhancement
                    // 1. Request summary from Pennie AI agent
                    // 2. Post summary in chat or send email
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error leaving meeting {MeetingId}", meetingId);
                }
            }

            _logger.LogInformation("All transcriptions stopped and meetings left");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping transcription");
        }
    }
}
