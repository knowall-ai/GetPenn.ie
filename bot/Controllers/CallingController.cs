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
