using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Identity.Client;
using Microsoft.Skype.Bots.Media;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace PennieBot.Services;

/// <summary>
/// Service for managing Teams meeting calls via Microsoft Graph API.
/// Joins meetings and coordinates with Speech Services for transcription.
/// </summary>
public class GraphCallService : IGraphCallService, IDisposable
{
    private readonly ILogger<GraphCallService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IMediaPlatformService _mediaPlatformService;
    private readonly ConcurrentDictionary<string, string> _activeCalls = new(); // meetingId -> callId
    private readonly ConcurrentDictionary<string, Func<byte[], Task>> _audioCallbacks = new();
    private readonly ConcurrentDictionary<string, string> _callIdToMeetingId = new();
    private readonly ConcurrentDictionary<string, AudioSocket> _audioSockets = new(); // callId -> AudioSocket
    private bool _disposed;
    private bool _initialized;
    private bool _useApplicationHostedMedia;
    private GraphServiceClient? _graphClient;
    private IConfidentialClientApplication? _msalClient;
    private string? _tenantId;
    private string? _appId;
    private string? _certificateThumbprint;
    private string? _serviceFqdn;
    private int _mediaInstanceExternalPort;

    public GraphCallService(
        ILogger<GraphCallService> logger,
        IConfiguration configuration,
        IMediaPlatformService mediaPlatformService)
    {
        _logger = logger;
        _configuration = configuration;
        _mediaPlatformService = mediaPlatformService;
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
            _appId = _configuration["MicrosoftAppId"];
            var appSecret = _configuration["MicrosoftAppPassword"];
            _tenantId = _configuration["MicrosoftAppTenantId"];

            if (string.IsNullOrEmpty(_appId) || string.IsNullOrEmpty(appSecret) || string.IsNullOrEmpty(_tenantId))
            {
                _logger.LogWarning(
                    "Graph Communications SDK not fully configured. " +
                    "MicrosoftAppId, MicrosoftAppPassword, and MicrosoftAppTenantId required.");
                _initialized = true; // Mark as initialized but in limited mode
                return;
            }

            // Build MSAL confidential client for authentication
            _msalClient = ConfidentialClientApplicationBuilder
                .Create(_appId)
                .WithClientSecret(appSecret)
                .WithAuthority($"https://login.microsoftonline.com/{_tenantId}")
                .Build();

            // Build Graph client with client credentials
            var credential = new ClientSecretCredential(_tenantId, _appId, appSecret);
            _graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });

            // MediaPlatform configuration for callbacks and ApplicationHostedMedia
            var mediaPlatformConfig = _configuration.GetSection("MediaPlatform");
            _serviceFqdn = mediaPlatformConfig["ServiceFqdn"];
            var callNotificationUrl = mediaPlatformConfig["CallNotificationUrl"];
            _certificateThumbprint = mediaPlatformConfig["CertificateThumbprint"];
            _useApplicationHostedMedia = bool.TryParse(mediaPlatformConfig["UseApplicationHostedMedia"], out var useAppHosted) && useAppHosted;
            _mediaInstanceExternalPort = int.TryParse(mediaPlatformConfig["MediaInstanceExternalPort"], out var port) ? port : 20000;

            if (_useApplicationHostedMedia)
            {
                _logger.LogInformation(
                    "ApplicationHostedMedia ENABLED. Certificate={CertThumbprint}, MediaPort={Port}",
                    _certificateThumbprint ?? "(not set)", _mediaInstanceExternalPort);

                if (string.IsNullOrEmpty(_certificateThumbprint))
                {
                    _logger.LogWarning("CertificateThumbprint not configured. ApplicationHostedMedia may fail.");
                }
            }
            else
            {
                _logger.LogInformation("ServiceHostedMedia mode (no audio capture)");
            }

            _logger.LogInformation(
                "Graph SDK initialized. AppId={AppId}, NotificationUrl={Url}, ServiceFqdn={Fqdn}, AppHostedMedia={AppHosted}",
                _appId, callNotificationUrl ?? "(not set)", _serviceFqdn ?? "(not set)", _useApplicationHostedMedia);

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
    /// Uses Graph API /communications/calls to join as an application.
    /// </summary>
    public async Task JoinMeetingAsync(
        string meetingJoinUrl,
        string meetingId,
        Func<byte[], Task> audioDataCallback,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (_graphClient == null)
        {
            throw new InvalidOperationException("Graph client not initialized. Check credentials.");
        }

        try
        {
            _logger.LogInformation("Joining meeting {MeetingId} with URL {JoinUrl}", meetingId, meetingJoinUrl);

            // Parse the meeting join URL to extract meeting info
            var joinInfo = ParseMeetingJoinUrl(meetingJoinUrl);
            _logger.LogInformation(
                "Parsed meeting info: ThreadId={ThreadId}, TenantId={TenantId}",
                joinInfo.ThreadId, joinInfo.TenantId);

            // Store the callback for when audio arrives
            _audioCallbacks[meetingId] = audioDataCallback;

            // Get notification URL from configuration
            var callbackUrl = _configuration["MediaPlatform:CallNotificationUrl"]
                ?? $"https://{_configuration["MediaPlatform:ServiceFqdn"]}/api/calling";

            _logger.LogInformation("Using callback URL: {CallbackUrl}", callbackUrl);

            // Create the call to join the meeting
            // Using Graph API: POST /communications/calls
            // Use the new method that returns both config and socket
            var (mediaConfig, audioSocket) = CreateMediaConfigWithSocket();

            var call = new Call
            {
                Direction = CallDirection.Outgoing,
                CallbackUri = callbackUrl,
                TenantId = joinInfo.TenantId ?? _tenantId,
                MediaConfig = mediaConfig,
                RequestedModalities = new List<Modality?>
                {
                    Modality.Audio
                },
                ChatInfo = new ChatInfo
                {
                    ThreadId = joinInfo.ThreadId,
                    MessageId = joinInfo.MessageId
                },
                MeetingInfo = new OrganizerMeetingInfo
                {
                    OdataType = "#microsoft.graph.organizerMeetingInfo",
                    Organizer = new IdentitySet
                    {
                        User = new Identity
                        {
                            Id = joinInfo.OrganizerId,
                            AdditionalData = new Dictionary<string, object>
                            {
                                { "tenantId", joinInfo.TenantId ?? _tenantId ?? "" }
                            }
                        }
                    }
                }
            };

            _logger.LogInformation("Creating call to join meeting...");

            // Make the Graph API call to join
            var createdCall = await _graphClient.Communications.Calls
                .PostAsync(call, cancellationToken: cancellationToken);

            if (createdCall == null || string.IsNullOrEmpty(createdCall.Id))
            {
                // Clean up socket on failure
                audioSocket?.Dispose();
                throw new InvalidOperationException("Failed to create call - no call ID returned");
            }

            var callId = createdCall.Id;
            _activeCalls[meetingId] = callId;
            _callIdToMeetingId[callId] = meetingId;

            // Store the AudioSocket for this call - it must remain alive!
            if (audioSocket != null)
            {
                _audioSockets[callId] = audioSocket;
                _logger.LogInformation("Stored AudioSocket for call {CallId} - socket will remain alive", callId);
            }

            _logger.LogInformation(
                "Successfully joined meeting {MeetingId} with call ID {CallId}. State: {State}",
                meetingId, callId, createdCall.State);
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError odataEx)
        {
            _logger.LogError(odataEx,
                "Graph API error joining meeting {MeetingId}: {Code} - {Message}",
                meetingId, odataEx.Error?.Code, odataEx.Error?.Message);
            throw new InvalidOperationException(
                $"Failed to join meeting: {odataEx.Error?.Code} - {odataEx.Error?.Message}", odataEx);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join meeting {MeetingId}", meetingId);
            throw;
        }
    }

    /// <summary>
    /// Join a meeting using meeting ID and passcode (for meetings created via Teams UI).
    /// </summary>
    public async Task JoinMeetingByIdAsync(
        string meetingIdNumber,
        string passcode,
        string meetingId,
        Func<byte[], Task> audioDataCallback,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (_graphClient == null)
        {
            throw new InvalidOperationException("Graph client not initialized. Check credentials.");
        }

        try
        {
            _logger.LogInformation(
                "Joining meeting by ID {MeetingNumber} (internal: {MeetingId})",
                meetingIdNumber, meetingId);

            // Store the callback for when audio arrives
            _audioCallbacks[meetingId] = audioDataCallback;

            // Get notification URL from configuration
            var callbackUrl = _configuration["MediaPlatform:CallNotificationUrl"]
                ?? $"https://{_configuration["MediaPlatform:ServiceFqdn"]}/api/calling";

            // Create the call to join using meeting coordinates
            // Use the new method that returns both config and socket
            var (mediaConfig, audioSocket) = CreateMediaConfigWithSocket();

            var call = new Call
            {
                Direction = CallDirection.Outgoing,
                CallbackUri = callbackUrl,
                TenantId = _tenantId,
                MediaConfig = mediaConfig,
                RequestedModalities = new List<Modality?>
                {
                    Modality.Audio
                },
                MeetingInfo = new JoinMeetingIdMeetingInfo
                {
                    OdataType = "#microsoft.graph.joinMeetingIdMeetingInfo",
                    JoinMeetingId = meetingIdNumber.Replace(" ", ""),
                    Passcode = passcode
                }
            };

            _logger.LogInformation("Creating call to join meeting by ID...");

            var createdCall = await _graphClient.Communications.Calls
                .PostAsync(call, cancellationToken: cancellationToken);

            if (createdCall == null || string.IsNullOrEmpty(createdCall.Id))
            {
                // Clean up socket on failure
                audioSocket?.Dispose();
                throw new InvalidOperationException("Failed to create call - no call ID returned");
            }

            var callId = createdCall.Id;
            _activeCalls[meetingId] = callId;
            _callIdToMeetingId[callId] = meetingId;

            // Store the AudioSocket for this call - it must remain alive!
            if (audioSocket != null)
            {
                _audioSockets[callId] = audioSocket;
                _logger.LogInformation("Stored AudioSocket for call {CallId} - socket will remain alive", callId);
            }

            _logger.LogInformation(
                "Successfully joined meeting by ID {MeetingNumber} with call ID {CallId}. State: {State}",
                meetingIdNumber, callId, createdCall.State);
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError odataEx)
        {
            _logger.LogError(odataEx,
                "Graph API error joining meeting by ID: {Code} - {Message}",
                odataEx.Error?.Code, odataEx.Error?.Message);
            throw new InvalidOperationException(
                $"Failed to join meeting: {odataEx.Error?.Code} - {odataEx.Error?.Message}", odataEx);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join meeting by ID {MeetingNumber}", meetingIdNumber);
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

                // Hang up the call via Graph API
                if (_graphClient != null)
                {
                    try
                    {
                        await _graphClient.Communications.Calls[callId]
                            .DeleteAsync();
                        _logger.LogInformation("Successfully hung up call {CallId}", callId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error hanging up call {CallId}", callId);
                    }
                }

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

        return "Established";
    }

    /// <summary>
    /// Process incoming call state notification from Graph.
    /// </summary>
    public async Task ProcessNotificationAsync(string notificationBody)
    {
        try
        {
            _logger.LogInformation("Processing call notification, length={Length}", notificationBody.Length);
            _logger.LogDebug("Notification body: {Body}", notificationBody);

            using var doc = JsonDocument.Parse(notificationBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("value", out var notifications))
            {
                foreach (var notification in notifications.EnumerateArray())
                {
                    await ProcessSingleNotificationAsync(notification);
                }
            }
            else
            {
                // Single notification format
                await ProcessSingleNotificationAsync(root);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing notification");
            throw;
        }
    }

    private async Task ProcessSingleNotificationAsync(JsonElement notification)
    {
        try
        {
            var changeType = notification.TryGetProperty("changeType", out var ct) ? ct.GetString() : "unknown";
            var resourceUrl = notification.TryGetProperty("resourceUrl", out var ru) ? ru.GetString() : "";

            _logger.LogInformation("Processing notification: ChangeType={ChangeType}, Resource={ResourceUrl}",
                changeType, resourceUrl);

            // Extract call ID from resource URL (/communications/calls/{callId})
            if (!string.IsNullOrEmpty(resourceUrl) && resourceUrl.Contains("/calls/"))
            {
                var parts = resourceUrl.Split("/calls/");
                if (parts.Length > 1)
                {
                    var callId = parts[1].Split('/')[0].Split('?')[0];

                    if (_callIdToMeetingId.TryGetValue(callId, out var meetingId))
                    {
                        _logger.LogInformation(
                            "Notification for meeting {MeetingId}, call {CallId}",
                            meetingId, callId);

                        // Check for state changes
                        if (notification.TryGetProperty("resourceData", out var resourceData))
                        {
                            if (resourceData.TryGetProperty("state", out var state))
                            {
                                var callState = state.GetString();
                                _logger.LogInformation("Call state changed to: {State}", callState);

                                // Log termination reason if available (critical for diagnostics)
                                if (resourceData.TryGetProperty("resultInfo", out var resultInfo))
                                {
                                    var code = resultInfo.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                                    var subCode = resultInfo.TryGetProperty("subcode", out var sc) ? sc.GetInt32() : 0;
                                    var message = resultInfo.TryGetProperty("message", out var m) ? m.GetString() : "unknown";
                                    _logger.LogWarning("Call result info - Code: {Code}, SubCode: {SubCode}, Message: {Message}",
                                        code, subCode, message);
                                }

                                // Handle terminated state
                                if (callState == "terminated")
                                {
                                    _logger.LogInformation("Call {CallId} terminated, cleaning up", callId);
                                    _activeCalls.TryRemove(meetingId, out _);
                                    _callIdToMeetingId.TryRemove(callId, out _);
                                    _audioCallbacks.TryRemove(meetingId, out _);
                                }
                            }
                        }
                    }
                }
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing single notification");
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

            // Media notifications in service-hosted mode contain transcription data
            // or other media-related events from Graph

            using var doc = JsonDocument.Parse(notificationBody);
            var root = doc.RootElement;

            // Check for transcription content
            if (root.TryGetProperty("transcript", out var transcript))
            {
                var text = transcript.TryGetProperty("content", out var content) ? content.GetString() : "";
                var speaker = transcript.TryGetProperty("speakerId", out var spk) ? spk.GetString() : "Unknown";

                _logger.LogInformation("Transcription received: {Speaker}: {Text}", speaker, text);
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing media notification");
            throw;
        }
    }

    /// <summary>
    /// Parse Teams meeting join URL to extract meeting info.
    /// </summary>
    private MeetingJoinInfo ParseMeetingJoinUrl(string joinUrl)
    {
        try
        {
            var uri = new Uri(joinUrl);

            // Validate domain
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
                TenantId = tenantId ?? _tenantId ?? "",
                OrganizerId = organizerId ?? "",
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse context JSON from meeting URL: {Url}", joinUrl);
            return new MeetingJoinInfo
            {
                ThreadId = "",
                MessageId = "0",
                TenantId = _tenantId ?? "",
                OrganizerId = "",
            };
        }
        catch (UriFormatException ex)
        {
            _logger.LogError(ex, "Invalid URI format for meeting URL: {Url}", joinUrl);
            throw new ArgumentException($"Invalid meeting join URL format: {joinUrl}", nameof(joinUrl), ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse meeting URL: {Url}", joinUrl);
            throw new ArgumentException($"Invalid meeting join URL: {joinUrl}", nameof(joinUrl), ex);
        }
    }

    /// <summary>
    /// Create the appropriate media configuration based on settings.
    /// For ApplicationHostedMedia, the SDK generates the proper blob format.
    /// Returns both the MediaConfig and the AudioSocket (which must be kept alive until call ends).
    /// </summary>
    private (MediaConfig config, AudioSocket? socket) CreateMediaConfigWithSocket()
    {
        // Check if MediaPlatformService is enabled and initialized
        if (_useApplicationHostedMedia && _mediaPlatformService.IsEnabled && _mediaPlatformService.IsInitialized)
        {
            _logger.LogInformation("Creating AppHostedMediaConfig using MediaPlatformService SDK");

            try
            {
                // Create audio socket settings for receiving meeting audio
                var audioSettings = _mediaPlatformService.CreateAudioSocketSettings();

                // Create an AudioSocket (takes only AudioSocketSettings parameter)
                // IMPORTANT: This socket MUST remain alive for the duration of the call!
                var audioSocket = new AudioSocket(audioSettings);

                // Use the service to create the blob via static MediaPlatform.CreateMediaConfiguration()
                var blob = _mediaPlatformService.CreateMediaConfigurationBlob(audioSocket);

                if (string.IsNullOrEmpty(blob))
                {
                    _logger.LogWarning("CreateMediaConfigurationBlob returned null, falling back to ServiceHostedMedia");
                    audioSocket.Dispose();
                    return (CreateServiceHostedMediaConfig(), null);
                }

                _logger.LogInformation(
                    "Created AppHostedMediaConfig with SDK-generated blob (length={Length}). AudioSocket kept alive.",
                    blob.Length);

                // Return the socket so it can be stored - DO NOT DISPOSE HERE!
                // The socket must remain alive until the call ends.
                return (new AppHostedMediaConfig
                {
                    OdataType = "#microsoft.graph.appHostedMediaConfig",
                    Blob = blob
                }, audioSocket);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create AppHostedMediaConfig, falling back to ServiceHostedMedia");
                return (CreateServiceHostedMediaConfig(), null);
            }
        }

        return (CreateServiceHostedMediaConfig(), null);
    }

    /// <summary>
    /// Create the appropriate media configuration based on settings (legacy wrapper).
    /// </summary>
    private MediaConfig CreateMediaConfig()
    {
        var (config, socket) = CreateMediaConfigWithSocket();
        // Note: This will dispose the socket immediately - callers should use CreateMediaConfigWithSocket
        socket?.Dispose();
        return config;
    }

    /// <summary>
    /// Create ServiceHostedMediaConfig (default, no audio capture).
    /// </summary>
    private ServiceHostedMediaConfig CreateServiceHostedMediaConfig()
    {
        _logger.LogInformation("Creating ServiceHostedMediaConfig (no audio capture)");
        return new ServiceHostedMediaConfig
        {
            OdataType = "#microsoft.graph.serviceHostedMediaConfig",
            PreFetchMedia = new List<MediaInfo>()
        };
    }

    /// <summary>
    /// Create fallback media configuration blob.
    /// Used when SDK initialization fails but ApplicationHostedMedia is requested.
    /// NOTE: This may result in error 9999 from Graph API.
    /// </summary>
    private string CreateFallbackMediaConfigBlob()
    {
        _logger.LogWarning("Using fallback media config blob - this may fail with Graph API error 9999");

        var mediaConfig = new
        {
            audioSocket = new
            {
                receiveUnmixedMeetingAudio = true,
                supportedFormats = new[]
                {
                    new
                    {
                        format = "Pcm16K",
                        samplingRate = 16000,
                        channelCount = 1
                    }
                }
            },
            mediaDnsName = _serviceFqdn,
            mediaInstanceExternalPort = _mediaInstanceExternalPort,
            certificateThumbprint = _certificateThumbprint
        };

        var blob = JsonSerializer.Serialize(mediaConfig);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(blob));
    }

    /// <summary>
    /// Handle incoming audio frames from the media socket.
    /// Called at ~50 frames/second when audio is received.
    /// </summary>
    public async Task HandleAudioFrameAsync(string callId, byte[] audioData)
    {
        try
        {
            if (!_callIdToMeetingId.TryGetValue(callId, out var meetingId))
            {
                _logger.LogWarning("Received audio for unknown call {CallId}", callId);
                return;
            }

            if (!_audioCallbacks.TryGetValue(meetingId, out var callback))
            {
                _logger.LogDebug("No audio callback for meeting {MeetingId}", meetingId);
                return;
            }

            // Forward audio data to transcription callback
            await callback(audioData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling audio frame for call {CallId}", callId);
        }
    }

    /// <summary>
    /// Register an audio socket for a call to receive RTP frames.
    /// Called when call is established and ready for media.
    /// </summary>
    public void RegisterAudioSocket(string callId, AudioSocket socket)
    {
        _audioSockets[callId] = socket;
        _logger.LogInformation("Registered audio socket for call {CallId}", callId);

        // Subscribe to audio events
        socket.AudioMediaReceived += async (sender, args) =>
        {
            try
            {
                // Extract audio data from the buffer
                var buffer = args.Buffer;
                if (buffer?.Data != null && buffer.Length > 0)
                {
                    var audioData = new byte[buffer.Length];
                    Marshal.Copy(buffer.Data, audioData, 0, (int)buffer.Length);
                    await HandleAudioFrameAsync(callId, audioData);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing audio media for call {CallId}", callId);
            }
        };
    }

    /// <summary>
    /// Unregister and dispose of an audio socket when call ends.
    /// </summary>
    public void UnregisterAudioSocket(string callId)
    {
        if (_audioSockets.TryRemove(callId, out var socket))
        {
            socket.Dispose();
            _logger.LogInformation("Unregistered audio socket for call {CallId}", callId);
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
            // Clean up audio sockets
            foreach (var callId in _audioSockets.Keys.ToList())
            {
                UnregisterAudioSocket(callId);
            }

            // Leave active meetings
            foreach (var meetingId in _activeCalls.Keys.ToList())
            {
                var id = meetingId;
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
