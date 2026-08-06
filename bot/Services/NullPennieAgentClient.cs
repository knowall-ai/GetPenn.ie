namespace PennieBot.Services;

/// <summary>
/// Null implementation of IPennieAgentClient for when Azure OpenAI is not configured.
/// The bot can still function for simple queries via HTTP client.
/// </summary>
public class NullPennieAgentClient : IPennieAgentClient
{
    private readonly ILogger<NullPennieAgentClient> _logger;

    public NullPennieAgentClient(ILogger<NullPennieAgentClient> logger)
    {
        _logger = logger;
        _logger.LogWarning("PennieAgentClient is disabled - AZURE_OPENAI_ENDPOINT not configured");
    }

    public Task SendTranscriptAsync(TranscriptionResult result, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("SendTranscriptAsync called but Azure OpenAI is not configured");
        return Task.CompletedTask;
    }

    public Task<string> SendMessageAndGetResponseAsync(TranscriptionResult result, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("SendMessageAndGetResponseAsync called but Azure OpenAI is not configured");
        return Task.FromResult("I'm sorry, I can't process your message right now. Azure OpenAI is not configured.");
    }

    public Task<string> GetMeetingSummaryAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("GetMeetingSummaryAsync called but Azure OpenAI is not configured");
        return Task.FromResult("Azure OpenAI is not configured - meeting summaries unavailable.");
    }

    public Task NotifyWorkItemCreatedAsync(int workItemId, string workItemType, string title)
    {
        _logger.LogDebug("NotifyWorkItemCreatedAsync called but Azure OpenAI is not configured");
        return Task.CompletedTask;
    }

    public Task CleanupMeetingAsync(string meetingId)
    {
        _logger.LogDebug("CleanupMeetingAsync called but Azure OpenAI is not configured");
        return Task.CompletedTask;
    }
}
