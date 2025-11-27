using Microsoft.Identity.Client;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace PennieBot.Services;

/// <summary>
/// Service for managing Teams meeting audio via Microsoft Graph Communications SDK.
/// Note: Full media functionality requires Windows Server deployment.
/// This implementation compiles cross-platform but audio capture only works on Windows.
/// </summary>
public class GraphCallService : IGraphCallService, IDisposable
{
    private readonly ILogger<GraphCallService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, string> _activeCalls = new(); // meetingId -> callId
    private readonly ConcurrentDictionary<string, Func<byte[], Task>> _audioCallbacks = new();
    private readonly ConcurrentDictionary<string, string> _callIdToMeetingId = new();
    private bool _disposed;
    private bool _initialized;
    private IConfidentialClientApplication? _msalClient;

    public GraphCallService(
        ILogger<GraphCallService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Initialize the Graph Communications client with bot credentials.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            _logger.LogWarning("GraphCallService already initialized");
            return;
        }

        try
        {
            _logger.LogInformation("Initializing Graph Communications SDK...");

            // Get configuration
            var appId = _configuration["MicrosoftAppId"];
            var appSecret = _configuration["MicrosoftAppPassword"];
            var tenantId = _configuration["AzureTenantId"];

            if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(appSecret) || string.IsNullOrEmpty(tenantId))
            {
                _logger.LogWarning(
                    "Graph Communications SDK not fully configured. " +
                    "MicrosoftAppId, MicrosoftAppPassword, and AzureTenantId required.");
                _initialized = true; // Mark as initialized but in limited mode
                return;
            }

            // MediaPlatform configuration for Graph Communications SDK
            // Port requirements (from Microsoft Graph Communications SDK documentation):
            // - InstancePublicPort (8445): External TCP port for media traffic (must be open in firewall/NSG)
            // - InstanceInternalPort (8445): Internal port the bot listens on (usually same as public)
            // - CallSignalingPort (9441): TCP port for call signaling/SIP traffic
            // See: https://learn.microsoft.com/en-us/graph/cloud-communications-media
            var mediaPlatformConfig = _configuration.GetSection("MediaPlatform");
            var serviceFqdn = mediaPlatformConfig["ServiceFqdn"];
            var callNotificationUrl = mediaPlatformConfig["CallNotificationUrl"];

            if (string.IsNullOrEmpty(serviceFqdn))
            {
                _logger.LogWarning("MediaPlatform:ServiceFqdn not configured. Media features disabled.");
                _initialized = true;
                return;
            }

            // Build MSAL confidential client for authentication
            _msalClient = ConfidentialClientApplicationBuilder
                .Create(appId)
                .WithClientSecret(appSecret)
                .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
                .Build();

            _logger.LogInformation(
                "Graph Communications SDK initialized. NotificationUrl={Url}, ServiceFqdn={Fqdn}. " +
                "Note: Full audio functionality requires Windows Server deployment.",
                callNotificationUrl ?? "(not set)", serviceFqdn);

            _initialized = true;
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Graph Communications SDK");
            throw;
        }
    }

    /// <summary>
    /// Join a Teams meeting and start audio capture.
    /// Note: Full implementation requires Windows Server with Graph Communications Media SDK.
    /// </summary>
    public async Task JoinMeetingAsync(
        string meetingJoinUrl,
        string meetingId,
        Func<byte[], Task> audioDataCallback,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        try
        {
            _logger.LogInformation("Joining meeting {MeetingId} with URL {JoinUrl}", meetingId, meetingJoinUrl);

            // Parse the meeting join URL to validate it
            var joinInfo = ParseMeetingJoinUrl(meetingJoinUrl);
            _logger.LogInformation(
                "Parsed meeting info: ThreadId={ThreadId}, TenantId={TenantId}",
                joinInfo.ThreadId, joinInfo.TenantId);

            // Store the callback for when audio arrives
            _audioCallbacks[meetingId] = audioDataCallback;

            // Check if we're on Windows with full SDK support
            if (!OperatingSystem.IsWindows())
            {
                _logger.LogWarning(
                    "Meeting join requested on non-Windows platform. " +
                    "Audio capture requires Windows Server with Graph Communications Media SDK. " +
                    "Meeting chat functionality will still work.");

                throw new NotImplementedException(
                    "Graph Communications Media SDK requires Windows Server. " +
                    "Deploy the bot to pennie-vm-prod for full audio functionality.");
            }

            // On Windows, we would initialize the full Graph Communications SDK here.
            // The SDK initialization requires Windows-specific types:
            // - MediaPlatformSettings
            // - AudioSocketSettings
            // - ICommunicationsClient
            // These types only resolve on Windows where the SDK native DLLs are available.

            _logger.LogWarning(
                "Graph Communications SDK Windows implementation pending. " +
                "Full media SDK integration will be completed on Windows VM deployment.");

            throw new NotImplementedException(
                "Graph Communications SDK Windows implementation pending. " +
                "The SDK requires Windows Server with native media DLLs.");
        }
        catch (NotImplementedException)
        {
            throw; // Re-throw NotImplementedException as-is
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join meeting {MeetingId}", meetingId);
            throw;
        }
    }

    /// <summary>
    /// Leave the current meeting and stop audio capture.
    /// </summary>
    public async Task LeaveMeetingAsync(string meetingId)
    {
        try
        {
            _logger.LogInformation("Leaving meeting {MeetingId}", meetingId);

            // Remove callbacks
            _audioCallbacks.TryRemove(meetingId, out _);

            if (_activeCalls.TryRemove(meetingId, out var callId))
            {
                _callIdToMeetingId.TryRemove(callId, out _);
                _logger.LogInformation("Cleaned up call {CallId} for meeting {MeetingId}", callId, meetingId);
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error leaving meeting {MeetingId}", meetingId);
            throw;
        }
    }

    /// <summary>
    /// Check if the bot is currently in a meeting.
    /// </summary>
    /// <remarks>
    /// This is a point-in-time check. The result may become stale immediately after return
    /// as the meeting state can change asynchronously. Do not use for critical decisions
    /// without additional synchronization.
    /// </remarks>
    public bool IsInMeeting(string meetingId)
    {
        return _activeCalls.ContainsKey(meetingId);
    }

    /// <summary>
    /// Get the current call state for a meeting.
    /// </summary>
    public string GetCallState(string meetingId)
    {
        if (!_activeCalls.ContainsKey(meetingId))
        {
            return "NotInMeeting";
        }

        return "Unknown"; // Would query actual call state on Windows
    }

    /// <summary>
    /// Process incoming call state notification from Graph.
    /// </summary>
    public async Task ProcessNotificationAsync(string notificationBody)
    {
        try
        {
            _logger.LogDebug("Processing call notification");

            // Parse notification to extract call information
            using var doc = JsonDocument.Parse(notificationBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("value", out var notifications))
            {
                foreach (var notification in notifications.EnumerateArray())
                {
                    if (notification.TryGetProperty("resourceUrl", out var resourceUrl))
                    {
                        _logger.LogInformation(
                            "Processing notification for resource: {ResourceUrl}",
                            resourceUrl.GetString());
                    }

                    // Handle call state changes
                    if (notification.TryGetProperty("changeType", out var changeType))
                    {
                        _logger.LogInformation("Call change type: {ChangeType}", changeType.GetString());
                    }
                }
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing notification");
            throw;
        }
    }

    /// <summary>
    /// Process incoming media notification from Graph.
    /// </summary>
    public async Task ProcessMediaNotificationAsync(string notificationBody)
    {
        try
        {
            _logger.LogDebug("Processing media notification, length={Length}", notificationBody.Length);

            // Media notifications are typically handled via the Media SDK callback pattern
            // This method handles any metadata/signaling that comes via HTTP

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing media notification");
            throw;
        }
    }

    /// <summary>
    /// Process audio data from the media pipeline.
    /// Called by the Graph Communications SDK on Windows when audio is received.
    /// </summary>
    /// <param name="meetingId">The meeting ID for context</param>
    /// <param name="audioData">Raw audio data (16kHz, mono, 16-bit PCM)</param>
    /// <param name="activeSpeakerCount">Number of active speakers</param>
    internal void ProcessAudioData(string meetingId, byte[] audioData, int activeSpeakerCount)
    {
        try
        {
            _logger.LogDebug(
                "Received audio frame for meeting {MeetingId}: Length={Length}, ActiveSpeakers={Speakers}",
                meetingId, audioData.Length, activeSpeakerCount);

            // Invoke the callback to send audio to speech transcription
            if (_audioCallbacks.TryGetValue(meetingId, out var callback))
            {
                // Fire and forget - don't block the media pipeline
                Task.Run(async () =>
                {
                    try
                    {
                        await callback(audioData);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in audio callback for meeting {MeetingId}", meetingId);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing audio media for meeting {MeetingId}", meetingId);
        }
    }

    /// <summary>
    /// Parse Teams meeting join URL to extract meeting info.
    /// </summary>
    private MeetingJoinInfo ParseMeetingJoinUrl(string joinUrl)
    {
        // Teams meeting URLs have format:
        // https://teams.microsoft.com/l/meetup-join/19%3ameeting_xxx%40thread.v2/0?context=%7B%22Tid%22%3A%22xxx%22%2C%22Oid%22%3A%22xxx%22%7D

        try
        {
            var uri = new Uri(joinUrl);

            // Validate domain to prevent URL injection attacks
            if (uri.Host != "teams.microsoft.com" && !uri.Host.EndsWith(".teams.microsoft.com"))
            {
                throw new ArgumentException(
                    $"Invalid meeting URL domain: {uri.Host}. Expected teams.microsoft.com",
                    nameof(joinUrl));
            }

            var path = WebUtility.UrlDecode(uri.AbsolutePath);
            var query = WebUtility.UrlDecode(uri.Query);

            // Extract thread ID from path
            var segments = path.Split('/');
            var threadId = segments.FirstOrDefault(s => s.Contains("@thread")) ?? "";

            // Parse context JSON from query string
            var contextStart = query.IndexOf("{");
            var contextEnd = query.LastIndexOf("}");
            string? tenantId = null;
            string? organizerId = null;

            if (contextStart >= 0 && contextEnd > contextStart)
            {
                var contextJson = query.Substring(contextStart, contextEnd - contextStart + 1);
                using var doc = JsonDocument.Parse(contextJson);

                if (doc.RootElement.TryGetProperty("Tid", out var tid))
                    tenantId = tid.GetString();
                if (doc.RootElement.TryGetProperty("Oid", out var oid))
                    organizerId = oid.GetString();
            }

            return new MeetingJoinInfo
            {
                ThreadId = threadId,
                MessageId = "0",
                TenantId = tenantId ?? _configuration["AzureTenantId"] ?? "",
                OrganizerId = organizerId ?? "",
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse context JSON from meeting join URL: {Url}", joinUrl);
            // Return partial info even if context JSON is malformed
            return new MeetingJoinInfo
            {
                ThreadId = "",
                MessageId = "0",
                TenantId = _configuration["AzureTenantId"] ?? "",
                OrganizerId = "",
            };
        }
        catch (UriFormatException ex)
        {
            _logger.LogError(ex, "Invalid URI format for meeting join URL: {Url}", joinUrl);
            throw new ArgumentException($"Invalid meeting join URL format: {joinUrl}", nameof(joinUrl), ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse meeting join URL: {Url}", joinUrl);
            throw new ArgumentException($"Invalid meeting join URL: {joinUrl}", nameof(joinUrl), ex);
        }
    }

    /// <summary>
    /// Ensure the service is initialized before use.
    /// </summary>
    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException(
                "GraphCallService not initialized. Call InitializeAsync first.");
        }
    }

    /// <summary>
    /// Dispose of managed resources.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Dispose pattern implementation.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // Leave all active meetings using fire-and-forget pattern
            // Avoids blocking the dispose call which can cause deadlocks
            foreach (var meetingId in _activeCalls.Keys.ToList())
            {
                var id = meetingId; // Capture for closure
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await LeaveMeetingAsync(id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error leaving meeting {MeetingId} during dispose", id);
                    }
                });
            }

            _logger.LogInformation("GraphCallService disposed");
        }

        _disposed = true;
    }

    /// <summary>
    /// Meeting join information extracted from URL.
    /// </summary>
    private record MeetingJoinInfo
    {
        public string ThreadId { get; init; } = "";
        public string MessageId { get; init; } = "";
        public string TenantId { get; init; } = "";
        public string OrganizerId { get; init; } = "";
    }
}
