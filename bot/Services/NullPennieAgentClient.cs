namespace PennieBot.Services;

/// <summary>
/// Null implementation of IPennieAgentClient for when AI Foundry is not configured.
/// The bot can still function for simple queries via HTTP client.
/// </summary>
public class NullPennieAgentClient : IPennieAgentClient
{
    private readonly ILogger<NullPennieAgentClient> _logger;

    public NullPennieAgentClient(ILogger<NullPennieAgentClient> logger)
    {
        _logger = logger;
        _logger.LogWarning("PennieAgentClient is disabled - AZURE_AI_FOUNDRY_ENDPOINT not configured");
    }

    public Task SendTranscriptAsync(TranscriptionResult result, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("SendTranscriptAsync called but AI Foundry is not configured");
        return Task.CompletedTask;
    }

    public Task<string> GetMeetingSummaryAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("GetMeetingSummaryAsync called but AI Foundry is not configured");
        return Task.FromResult("AI Foundry is not configured - meeting summaries unavailable.");
    }

    public Task NotifyWorkItemCreatedAsync(int workItemId, string workItemType, string title)
    {
        _logger.LogDebug("NotifyWorkItemCreatedAsync called but AI Foundry is not configured");
        return Task.CompletedTask;
    }

    public Task CleanupMeetingAsync(string meetingId)
    {
        _logger.LogDebug("CleanupMeetingAsync called but AI Foundry is not configured");
        return Task.CompletedTask;
    }
}
