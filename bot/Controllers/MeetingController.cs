using Microsoft.AspNetCore.Mvc;
using PennieBot.Services;

namespace PennieBot.Controllers;

/// <summary>
/// Controller for meeting operations - allows triggering Pennie to join meetings
/// via HTTP request without needing to @mention from Teams.
/// </summary>
[Route("api/meeting")]
[ApiController]
public class MeetingController : ControllerBase
{
    private readonly IGraphCallService _callService;
    private readonly IOnlineMeetingService _meetingService;
    private readonly ISpeechTranscriptionService _transcriptionService;
    private readonly ILogger<MeetingController> _logger;

    public MeetingController(
        IGraphCallService callService,
        IOnlineMeetingService meetingService,
        ISpeechTranscriptionService transcriptionService,
        ILogger<MeetingController> logger)
    {
        _callService = callService;
        _meetingService = meetingService;
        _transcriptionService = transcriptionService;
        _logger = logger;
    }

    /// <summary>
    /// Join a meeting by providing either the join URL or meeting ID.
    /// POST /api/meeting/join
    /// Body: { "joinUrl": "https://teams.microsoft.com/l/meetup-join/..." }
    ///   or: { "meetingId": "...", "chatId": "...", "tenantId": "..." }
    /// </summary>
    [HttpPost("join")]
    public async Task<IActionResult> JoinMeeting([FromBody] JoinMeetingRequest request)
    {
        // Accept either joinUrl or meetingId
        var joinUrl = request.JoinUrl;

        if (string.IsNullOrWhiteSpace(joinUrl) && !string.IsNullOrWhiteSpace(request.MeetingId))
        {
            // Convert meeting ID to join URL using the OnlineMeetingService
            // The meeting panel provides meetingId from Teams SDK context (base64-encoded)
            _logger.LogInformation("Received meeting join request via meetingId: {MeetingId}", request.MeetingId);

            try
            {
                joinUrl = await _meetingService.GetMeetingJoinUrlAsync(
                    request.MeetingId,
                    request.ChatId,
                    request.TenantId,
                    HttpContext.RequestAborted);

                if (string.IsNullOrEmpty(joinUrl))
                {
                    _logger.LogWarning("Could not resolve join URL from meetingId: {MeetingId}", request.MeetingId);
                    return BadRequest(new
                    {
                        error = "Could not resolve meeting join URL from the provided meeting ID",
                        meetingId = request.MeetingId
                    });
                }

                _logger.LogInformation("Resolved join URL from meetingId: {JoinUrl}", joinUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving meeting join URL from meetingId: {MeetingId}", request.MeetingId);
                return StatusCode(500, new
                {
                    success = false,
                    error = "Failed to resolve meeting join URL",
                    details = ex.Message
                });
            }
        }

        if (string.IsNullOrWhiteSpace(joinUrl))
        {
            return BadRequest(new { error = "Either joinUrl or meetingId is required" });
        }

        _logger.LogInformation("Received meeting join request for URL: {JoinUrl}", joinUrl);

        try
        {
            // Generate internal meeting ID
            var internalMeetingId = $"api-join-{Guid.NewGuid():N}";

            // Try to start transcription for this meeting (optional - continue without if Speech not configured)
            var transcriptionEnabled = false;
            try
            {
                _logger.LogInformation("Starting transcription service for meeting {MeetingId}", internalMeetingId);
                await _transcriptionService.StartTranscriptionAsync(
                    internalMeetingId,
                    async transcriptionResult =>
                    {
                        // Transcription callback - log the results and eventually send to Pennie AI
                        _logger.LogInformation(
                            "TRANSCRIPT [{MeetingId}] {Speaker} @ {Timestamp}: {Text}",
                            transcriptionResult.MeetingId,
                            transcriptionResult.Speaker,
                            transcriptionResult.Timestamp.ToString("HH:mm:ss"),
                            transcriptionResult.Text);

                        // TODO: Send transcription to Pennie AI agent for processing
                        await Task.CompletedTask;
                    },
                    HttpContext.RequestAborted);
                transcriptionEnabled = true;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("AZURE-SPEECH-KEY"))
            {
                _logger.LogWarning("Transcription disabled: AZURE-SPEECH-KEY not configured. Meeting will join without transcription.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start transcription service, continuing without: {Message}", ex.Message);
            }

            // Join the meeting and wire up audio to transcription service (if enabled)
            var audioForwardCount = 0;
            await _callService.JoinMeetingAsync(
                joinUrl,
                internalMeetingId,
                async (audioData, speakerId, speakerName) =>
                {
                    if (transcriptionEnabled)
                    {
                        // Forward audio to Speech Services for transcription with speaker ID and name
                        audioForwardCount++;
                        if (audioForwardCount == 1 || audioForwardCount % 250 == 0) // Log first and every 5 seconds at 50fps
                        {
                            _logger.LogInformation("AUDIO->SPEECH: Forwarded {Count} audio packets (speaker {SpeakerId}: {SpeakerName}) to Speech Services for meeting {MeetingId}",
                                audioForwardCount, speakerId, speakerName ?? "Unknown", internalMeetingId);
                        }
                        await _transcriptionService.ProcessAudioAsync(internalMeetingId, audioData, speakerId, speakerName);
                    }
                },
                HttpContext.RequestAborted);

            _logger.LogInformation("Successfully initiated meeting join for: {MeetingId} (transcription={Transcription})",
                internalMeetingId, transcriptionEnabled ? "enabled" : "disabled");

            return Ok(new
            {
                success = true,
                message = transcriptionEnabled
                    ? "Pennie is joining the meeting with transcription"
                    : "Pennie is joining the meeting",
                internalMeetingId = internalMeetingId,
                transcriptionEnabled = transcriptionEnabled
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join meeting: {JoinUrl}", joinUrl);

            // Extract a clean, user-friendly error message
            var errorMessage = ExtractCleanErrorMessage(ex);

            return StatusCode(500, new
            {
                success = false,
                error = errorMessage
            });
        }
    }

    /// <summary>
    /// Extract a clean, user-friendly error message from an exception.
    /// </summary>
    private static string ExtractCleanErrorMessage(Exception ex)
    {
        // If it's a wrapped InvalidOperationException, get the inner details
        if (ex is InvalidOperationException && ex.Message.StartsWith("Failed to join meeting:"))
        {
            // Already formatted from GraphCallService
            return ex.Message.Replace("Failed to join meeting: ", "");
        }

        // For other exceptions, provide a generic but helpful message
        var message = ex.Message;

        // If there's an inner exception with more details, append it
        if (ex.InnerException != null && !string.IsNullOrEmpty(ex.InnerException.Message))
        {
            // Only append if it adds new information
            if (!message.Contains(ex.InnerException.Message))
            {
                message += $" - {ex.InnerException.Message}";
            }
        }

        return message;
    }

    /// <summary>
    /// Join a meeting using meeting number and passcode.
    /// POST /api/meeting/join-by-id
    /// Body: { "meetingNumber": "396 240 783 591", "passcode": "abc123" }
    /// This is more reliable than join URL for same-tenant meetings.
    /// </summary>
    [HttpPost("join-by-id")]
    public async Task<IActionResult> JoinMeetingById([FromBody] JoinMeetingByIdRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MeetingNumber))
        {
            return BadRequest(new { error = "meetingNumber is required" });
        }

        _logger.LogInformation("Received meeting join-by-id request for meeting number: {MeetingNumber}", request.MeetingNumber);

        try
        {
            // Generate internal meeting ID
            var internalMeetingId = $"api-join-{Guid.NewGuid():N}";

            // Try to start transcription for this meeting (optional - continue without if Speech not configured)
            var transcriptionEnabled = false;
            try
            {
                _logger.LogInformation("Starting transcription service for meeting {MeetingId}", internalMeetingId);
                await _transcriptionService.StartTranscriptionAsync(
                    internalMeetingId,
                    async transcriptionResult =>
                    {
                        // Transcription callback - log the results and eventually send to Pennie AI
                        _logger.LogInformation(
                            "TRANSCRIPT [{MeetingId}] {Speaker} @ {Timestamp}: {Text}",
                            transcriptionResult.MeetingId,
                            transcriptionResult.Speaker,
                            transcriptionResult.Timestamp.ToString("HH:mm:ss"),
                            transcriptionResult.Text);

                        // TODO: Send transcription to Pennie AI agent for processing
                        await Task.CompletedTask;
                    },
                    HttpContext.RequestAborted);
                transcriptionEnabled = true;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("AZURE-SPEECH-KEY"))
            {
                _logger.LogWarning("Transcription disabled: AZURE-SPEECH-KEY not configured. Meeting will join without transcription.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start transcription service, continuing without: {Message}", ex.Message);
            }

            // Join the meeting using meeting ID and passcode
            var audioForwardCount = 0;
            await _callService.JoinMeetingByIdAsync(
                request.MeetingNumber,
                request.Passcode ?? "",
                internalMeetingId,
                async (audioData, speakerId, speakerName) =>
                {
                    if (transcriptionEnabled)
                    {
                        // Forward audio to Speech Services for transcription with speaker ID and name
                        audioForwardCount++;
                        if (audioForwardCount == 1 || audioForwardCount % 250 == 0)
                        {
                            _logger.LogInformation("AUDIO->SPEECH: Forwarded {Count} audio packets (speaker {SpeakerId}: {SpeakerName}) to Speech Services for meeting {MeetingId}",
                                audioForwardCount, speakerId, speakerName ?? "Unknown", internalMeetingId);
                        }
                        await _transcriptionService.ProcessAudioAsync(internalMeetingId, audioData, speakerId, speakerName);
                    }
                },
                HttpContext.RequestAborted);

            _logger.LogInformation("Successfully initiated meeting join by ID for: {MeetingId} (transcription={Transcription})",
                internalMeetingId, transcriptionEnabled ? "enabled" : "disabled");

            return Ok(new
            {
                success = true,
                message = transcriptionEnabled
                    ? "Pennie is joining the meeting with transcription"
                    : "Pennie is joining the meeting",
                internalMeetingId = internalMeetingId,
                transcriptionEnabled = transcriptionEnabled
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join meeting by ID: {MeetingNumber}", request.MeetingNumber);

            var errorMessage = ExtractCleanErrorMessage(ex);

            return StatusCode(500, new
            {
                success = false,
                error = errorMessage
            });
        }
    }

    /// <summary>
    /// Leave a meeting.
    /// POST /api/meeting/leave
    /// Body: { "meetingId": "..." }
    /// </summary>
    [HttpPost("leave")]
    public async Task<IActionResult> LeaveMeeting([FromBody] LeaveMeetingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MeetingId))
        {
            _logger.LogWarning("Leave request received with empty meetingId");
            return BadRequest(new { error = "meetingId is required" });
        }

        _logger.LogInformation("Received meeting leave request for: {MeetingId}", request.MeetingId);

        try
        {
            // Check if meeting exists before trying to leave
            var wasInMeeting = _callService.IsInMeeting(request.MeetingId);
            _logger.LogInformation("Meeting {MeetingId} IsInMeeting={IsInMeeting}", request.MeetingId, wasInMeeting);

            // Stop transcription for this meeting
            try
            {
                await _transcriptionService.StopTranscriptionAsync(request.MeetingId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping transcription for meeting {MeetingId}", request.MeetingId);
            }

            await _callService.LeaveMeetingAsync(request.MeetingId);

            return Ok(new
            {
                success = true,
                message = wasInMeeting ? "Pennie has left the meeting" : "Meeting not found (may have already left)",
                meetingId = request.MeetingId,
                wasInMeeting = wasInMeeting
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to leave meeting: {MeetingId}", request.MeetingId);
            return StatusCode(500, new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Leave a meeting via GET (for sendBeacon fallback).
    /// GET /api/meeting/leave?meetingId=xxx
    /// </summary>
    [HttpGet("leave")]
    public async Task<IActionResult> LeaveMeetingGet([FromQuery] string meetingId)
    {
        if (string.IsNullOrWhiteSpace(meetingId))
        {
            _logger.LogWarning("Leave GET request received with empty meetingId");
            return BadRequest(new { error = "meetingId query parameter is required" });
        }

        _logger.LogInformation("Received meeting leave GET request for: {MeetingId}", meetingId);

        try
        {
            var wasInMeeting = _callService.IsInMeeting(meetingId);

            try
            {
                await _transcriptionService.StopTranscriptionAsync(meetingId);
            }
            catch { /* ignore */ }

            await _callService.LeaveMeetingAsync(meetingId);

            return Ok(new { success = true, meetingId, wasInMeeting });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to leave meeting via GET: {MeetingId}", meetingId);
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Get status of current calls.
    /// GET /api/meeting/status
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        // TODO: Add call tracking to GraphCallService
        return Ok(new
        {
            status = "Pennie is running",
            mediaEnabled = true,
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Get transcripts for a meeting.
    /// GET /api/meeting/transcripts?meetingId=xxx&sinceIndex=0
    /// </summary>
    [HttpGet("transcripts")]
    public IActionResult GetTranscripts([FromQuery] string meetingId, [FromQuery] int sinceIndex = 0)
    {
        if (string.IsNullOrWhiteSpace(meetingId))
        {
            return BadRequest(new { error = "meetingId is required" });
        }

        var transcripts = _transcriptionService.GetTranscripts(meetingId, sinceIndex);

        return Ok(new
        {
            success = true,
            meetingId = meetingId,
            count = transcripts.Count,
            lastIndex = transcripts.Count > 0 ? transcripts[^1].Index : sinceIndex,
            transcripts = transcripts.Select(t => new
            {
                index = t.Index,
                speaker = t.Result.Speaker,
                text = t.Result.Text,
                timestamp = t.Result.Timestamp.ToString("HH:mm:ss"),
                confidence = t.Result.Confidence,
                isFinal = t.Result.IsFinal
            })
        });
    }

    /// <summary>
    /// Get meeting coordinates (meeting number and passcode) for pre-populating the join form.
    /// POST /api/meeting/coordinates
    /// Body: { "meetingId": "...", "chatId": "...", "tenantId": "..." }
    /// </summary>
    [HttpPost("coordinates")]
    public async Task<IActionResult> GetMeetingCoordinates([FromBody] MeetingCoordinatesRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MeetingId))
        {
            return BadRequest(new { error = "meetingId is required" });
        }

        _logger.LogInformation(
            "Looking up meeting coordinates for meetingId: {MeetingId}",
            request.MeetingId);

        try
        {
            var coordinates = await _meetingService.GetMeetingCoordinatesAsync(
                request.MeetingId,
                request.ChatId,
                request.TenantId,
                HttpContext.RequestAborted);

            if (coordinates == null)
            {
                return Ok(new
                {
                    success = false,
                    error = "Could not retrieve meeting coordinates"
                });
            }

            return Ok(new
            {
                success = true,
                meetingNumber = coordinates.JoinMeetingId,
                passcode = coordinates.Passcode
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get meeting coordinates: {MeetingId}", request.MeetingId);
            return Ok(new
            {
                success = false,
                error = ex.Message
            });
        }
    }
}

public class JoinMeetingRequest
{
    /// <summary>Full Teams meeting join URL (preferred)</summary>
    public string? JoinUrl { get; set; }

    /// <summary>Teams meeting ID from SDK context (alternative to JoinUrl)</summary>
    public string? MeetingId { get; set; }

    /// <summary>Teams chat ID from SDK context</summary>
    public string? ChatId { get; set; }

    /// <summary>Tenant ID from SDK context</summary>
    public string? TenantId { get; set; }

    /// <summary>Azure DevOps project name to create work items in</summary>
    public string? DevOpsProject { get; set; }

    /// <summary>Azure DevOps Epic ID to link work items to</summary>
    public string? DevOpsEpicId { get; set; }
}

public class LeaveMeetingRequest
{
    /// <summary>Meeting ID to leave</summary>
    public string MeetingId { get; set; } = string.Empty;
}

public class JoinMeetingByIdRequest
{
    /// <summary>The meeting number shown in Teams (e.g., "396 240 783 591 15")</summary>
    public string MeetingNumber { get; set; } = string.Empty;

    /// <summary>The meeting passcode (e.g., "abc123")</summary>
    public string? Passcode { get; set; }

    /// <summary>Azure DevOps project name to create work items in</summary>
    public string? DevOpsProject { get; set; }

    /// <summary>Azure DevOps Epic ID to link work items to</summary>
    public string? DevOpsEpicId { get; set; }
}

public class MeetingCoordinatesRequest
{
    /// <summary>Teams meeting ID from SDK context (base64 encoded)</summary>
    public string MeetingId { get; set; } = string.Empty;

    /// <summary>Teams chat ID from SDK context</summary>
    public string? ChatId { get; set; }

    /// <summary>Tenant ID from SDK context</summary>
    public string? TenantId { get; set; }
}
