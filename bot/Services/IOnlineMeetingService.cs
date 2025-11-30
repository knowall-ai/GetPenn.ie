namespace PennieBot.Services;

/// <summary>
/// Service for looking up online meeting details from Teams SDK context.
/// Converts Teams SDK meetingId to a joinable meeting URL via Graph API.
/// </summary>
public interface IOnlineMeetingService
{
    /// <summary>
    /// Get the Teams meeting join URL from the meeting ID provided by Teams SDK.
    /// </summary>
    /// <param name="meetingId">Base64-encoded meeting ID from Teams SDK context</param>
    /// <param name="chatId">Chat ID from Teams SDK context (optional)</param>
    /// <param name="tenantId">Tenant ID from Teams SDK context (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Meeting join URL or null if not found</returns>
    Task<string?> GetMeetingJoinUrlAsync(
        string meetingId,
        string? chatId = null,
        string? tenantId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get online meeting details from the meeting ID.
    /// </summary>
    /// <param name="meetingId">Base64-encoded meeting ID from Teams SDK context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Meeting details or null if not found</returns>
    Task<OnlineMeetingInfo?> GetMeetingInfoAsync(
        string meetingId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the meeting coordinates (join meeting ID and passcode) from the Teams SDK meeting ID.
    /// These can be used with JoinMeetingIdMeetingInfo to join meetings.
    /// </summary>
    /// <param name="meetingId">Base64-encoded meeting ID from Teams SDK context</param>
    /// <param name="chatId">Chat ID from Teams SDK context (optional)</param>
    /// <param name="tenantId">Tenant ID from Teams SDK context (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Meeting coordinates or null if not found</returns>
    Task<MeetingCoordinates?> GetMeetingCoordinatesAsync(
        string meetingId,
        string? chatId = null,
        string? tenantId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Information about an online meeting retrieved from Graph API.
/// </summary>
public class OnlineMeetingInfo
{
    public string? JoinUrl { get; set; }
    public string? Subject { get; set; }
    public string? MeetingId { get; set; }
    public string? ThreadId { get; set; }
    public DateTimeOffset? StartDateTime { get; set; }
    public DateTimeOffset? EndDateTime { get; set; }
}

/// <summary>
/// Meeting coordinates that can be used with JoinMeetingIdMeetingInfo to join meetings.
/// </summary>
public class MeetingCoordinates
{
    /// <summary>
    /// The meeting number shown in Teams (e.g., "396 240 783 591 15").
    /// This is the joinMeetingId from Graph API.
    /// </summary>
    public string? JoinMeetingId { get; set; }

    /// <summary>
    /// The meeting passcode (e.g., "tj3HN9jw").
    /// </summary>
    public string? Passcode { get; set; }
}
