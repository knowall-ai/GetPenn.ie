namespace PennieBot.Services;

/// <summary>
/// Interface for communicating with Pennie AI Foundry Agent.
/// </summary>
public interface IPennieAgentClient
{
    /// <summary>
    /// Send transcribed text to Pennie for processing.
    /// </summary>
    /// <param name="result">Transcription result with speaker attribution</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SendTranscriptAsync(TranscriptionResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Request a meeting summary from Pennie.
    /// </summary>
    /// <param name="meetingId">Meeting identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Summary text</returns>
    Task<string> GetMeetingSummaryAsync(string meetingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Notify Pennie that a work item was created (for chat notification).
    /// </summary>
    /// <param name="workItemId">Work item ID in Azure DevOps</param>
    /// <param name="workItemType">Type (Epic, Feature, Story, Question)</param>
    /// <param name="title">Work item title</param>
    Task NotifyWorkItemCreatedAsync(int workItemId, string workItemType, string title);
}
