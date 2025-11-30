namespace PennieBot.Services;

/// <summary>
/// Interface for Microsoft Graph Communications SDK integration.
/// Manages joining Teams meetings and capturing audio streams.
/// </summary>
public interface IGraphCallService
{
    /// <summary>
    /// Initialize the Graph Communications client with bot credentials.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Join a Teams meeting and start audio capture.
    /// </summary>
    /// <param name="meetingJoinUrl">Teams meeting join URL</param>
    /// <param name="meetingId">Unique meeting identifier for tracking</param>
    /// <param name="audioDataCallback">Callback to receive audio frames (16kHz, mono, 16-bit PCM)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task JoinMeetingAsync(
        string meetingJoinUrl,
        string meetingId,
        Func<byte[], Task> audioDataCallback,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Join a Teams meeting using meeting ID and passcode.
    /// </summary>
    /// <param name="meetingIdNumber">Meeting ID number (e.g., "396 240 783 591 15")</param>
    /// <param name="passcode">Meeting passcode</param>
    /// <param name="meetingId">Unique meeting identifier for tracking</param>
    /// <param name="audioDataCallback">Callback to receive audio frames</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task JoinMeetingByIdAsync(
        string meetingIdNumber,
        string passcode,
        string meetingId,
        Func<byte[], Task> audioDataCallback,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Leave the current meeting and stop audio capture.
    /// </summary>
    /// <param name="meetingId">Meeting identifier</param>
    Task LeaveMeetingAsync(string meetingId);

    /// <summary>
    /// Check if the bot is currently in a meeting.
    /// </summary>
    /// <param name="meetingId">Meeting identifier</param>
    /// <returns>True if in meeting, false otherwise</returns>
    bool IsInMeeting(string meetingId);

    /// <summary>
    /// Get the current call state for a meeting.
    /// </summary>
    /// <param name="meetingId">Meeting identifier</param>
    /// <returns>Call state (Establishing, Established, Terminated, etc.)</returns>
    string GetCallState(string meetingId);

    /// <summary>
    /// Process incoming call state notification from Graph.
    /// </summary>
    /// <param name="notificationBody">Raw JSON notification body</param>
    Task ProcessNotificationAsync(string notificationBody);

    /// <summary>
    /// Process incoming media notification from Graph.
    /// </summary>
    /// <param name="notificationBody">Raw JSON notification body</param>
    Task ProcessMediaNotificationAsync(string notificationBody);

    /// <summary>
    /// Send a message to the meeting chat.
    /// </summary>
    /// <param name="threadId">The chat thread ID (e.g., 19:meeting_xxx@thread.v2)</param>
    /// <param name="message">The message text to send</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SendChatMessageAsync(string threadId, string message, CancellationToken cancellationToken = default);
}
