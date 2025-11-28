using Microsoft.AspNetCore.Mvc;
using PennieBot.Services;

namespace PennieBot.Controllers;

/// <summary>
/// Controller for receiving Graph Communications SDK call state notifications.
/// Graph sends POST requests to this endpoint when call state changes.
/// </summary>
[Route("api/calling")]
[ApiController]
public class CallingController : ControllerBase
{
    private readonly IGraphCallService _callService;
    private readonly ILogger<CallingController> _logger;

    public CallingController(
        IGraphCallService callService,
        ILogger<CallingController> logger)
    {
        _callService = callService;
        _logger = logger;
    }

    /// <summary>
    /// Health check endpoint for Azure Load Balancer.
    /// </summary>
    [HttpGet]
    public IActionResult HealthCheck()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Join a Teams meeting by Meeting ID and Passcode.
    /// POST /api/calling/join
    /// </summary>
    [HttpPost("join")]
    public async Task<IActionResult> JoinMeeting([FromBody] JoinMeetingRequest request)
    {
        if (string.IsNullOrEmpty(request.MeetingId) || string.IsNullOrEmpty(request.Passcode))
        {
            return BadRequest(new { error = "MeetingId and Passcode are required" });
        }

        try
        {
            _logger.LogInformation(
                "Received join request for meeting {MeetingId}",
                request.MeetingId);

            // Generate a unique internal ID for this meeting session
            var internalMeetingId = $"meeting-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";

            // Audio callback for transcription (placeholder for now)
            Func<byte[], Task> audioCallback = async (audioData) =>
            {
                _logger.LogDebug("Received audio frame, length={Length}", audioData.Length);
                await Task.CompletedTask;
            };

            await _callService.JoinMeetingByIdAsync(
                request.MeetingId,
                request.Passcode,
                internalMeetingId,
                audioCallback);

            return Ok(new
            {
                success = true,
                internalMeetingId,
                message = $"Joining meeting {request.MeetingId}",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join meeting {MeetingId}", request.MeetingId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Leave a Teams meeting.
    /// POST /api/calling/leave
    /// </summary>
    [HttpPost("leave")]
    public async Task<IActionResult> LeaveMeeting([FromBody] LeaveMeetingRequest request)
    {
        if (string.IsNullOrEmpty(request.InternalMeetingId))
        {
            return BadRequest(new { error = "InternalMeetingId is required" });
        }

        try
        {
            _logger.LogInformation("Leaving meeting {InternalMeetingId}", request.InternalMeetingId);
            await _callService.LeaveMeetingAsync(request.InternalMeetingId);
            return Ok(new { success = true, message = "Left meeting" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to leave meeting");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    public record JoinMeetingRequest(string MeetingId, string Passcode);
    public record LeaveMeetingRequest(string InternalMeetingId);

    /// <summary>
    /// Receives call state notifications from Graph Communications SDK.
    /// POST /api/calling
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(10_485_760)] // 10MB limit to prevent DoS attacks
    public async Task<IActionResult> HandleNotification()
    {
        try
        {
            // Validate content type
            if (Request.ContentType != null &&
                !Request.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Invalid content type: {ContentType}", Request.ContentType);
                return BadRequest(new { error = "Content-Type must be application/json" });
            }

            // Read the request body
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();

            _logger.LogInformation(
                "Received call notification: ContentType={ContentType}, Length={Length}",
                Request.ContentType,
                body.Length);

            _logger.LogDebug("Notification body: {Body}", body);

            // Process the notification through GraphCallService
            await _callService.ProcessNotificationAsync(body);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing call notification");
            return StatusCode(500, new { error = "Failed to process notification" });
        }
    }

    /// <summary>
    /// Receives media notifications (audio/video frames) from Graph.
    /// POST /api/calling/media
    /// </summary>
    [HttpPost("media")]
    [RequestSizeLimit(10_485_760)] // 10MB limit to prevent DoS attacks
    public async Task<IActionResult> HandleMediaNotification()
    {
        try
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();

            _logger.LogDebug("Received media notification, length={Length}", body.Length);

            // Media notifications are handled via the Media SDK callback pattern
            // This endpoint is primarily for signaling/metadata
            await _callService.ProcessMediaNotificationAsync(body);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing media notification");
            return StatusCode(500, new { error = "Failed to process media notification" });
        }
    }
}
