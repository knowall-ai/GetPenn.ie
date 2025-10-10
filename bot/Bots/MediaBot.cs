using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using PennieBot.Services;

namespace PennieBot.Bots;

/// <summary>
/// Teams Media Bot that joins meetings, captures real-time audio,
/// and sends transcripts to Pennie AI agent.
/// </summary>
public class MediaBot : ActivityHandler
{
    private readonly ILogger<MediaBot> _logger;
    private readonly ISpeechTranscriptionService _speechService;
    private readonly IPennieAgentClient _agentClient;
    private readonly IConfiguration _configuration;

    public MediaBot(
        ILogger<MediaBot> logger,
        ISpeechTranscriptionService speechService,
        IPennieAgentClient agentClient,
        IConfiguration configuration)
    {
        _logger = logger;
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
    /// NOTE: This is a simplified implementation. Full Graph Communications SDK integration required.
    /// </summary>
    private async Task JoinMeetingAudioAsync(
        ITurnContext turnContext,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Attempting to join meeting audio...");

            // TODO: Implement Graph Communications Call Media Bot
            // This requires:
            // 1. Create call using Graph Communications SDK
            // 2. Subscribe to audio streams (RTP)
            // 3. Process audio frames (50 frames/sec)
            // 4. Send audio to Azure Speech Services
            // 5. Receive transcription with speaker diarization
            // 6. Forward transcripts to Pennie AI agent

            // For now, log that this would be implemented
            _logger.LogInformation("Media bot audio joining logic to be implemented with Graph Communications SDK");

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error joining meeting audio");
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
            _logger.LogInformation("Stopping transcription...");

            // TODO: Implement
            // 1. Stop audio streaming
            // 2. Finalize transcription
            // 3. Request summary from Pennie AI agent
            // 4. Post summary in chat or send email

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping transcription");
        }
    }
}
