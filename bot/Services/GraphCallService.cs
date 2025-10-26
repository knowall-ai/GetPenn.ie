using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Client;
using Microsoft.Graph.Communications.Common;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Identity.Client;
using System.Collections.Concurrent;

namespace PennieBot.Services;

/// <summary>
/// Service for managing Teams meeting audio via Microsoft Graph Communications SDK.
/// </summary>
public class GraphCallService : IGraphCallService
{
    private readonly ILogger<GraphCallService> _logger;
    private readonly IConfiguration _configuration;
    private ICommunicationsClient? _communicationsClient;
    private readonly ConcurrentDictionary<string, ICall> _activeCalls = new();
    private readonly ConcurrentDictionary<string, Func<byte[], Task>> _audioCallbacks = new();

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
        try
        {
            _logger.LogInformation("Initializing Graph Communications Client...");

            var appId = _configuration["TeamsAppId"]
                ?? throw new InvalidOperationException("TeamsAppId not configured");
            var appPassword = _configuration["TeamsAppPassword"]
                ?? throw new InvalidOperationException("TeamsAppPassword not configured");
            var tenantId = _configuration["AzureTenantId"]
                ?? throw new InvalidOperationException("AzureTenantId not configured");

            // Get bot base URL for callback endpoints
            var botBaseUrl = _configuration["BotBaseUrl"]
                ?? throw new InvalidOperationException("BotBaseUrl not configured");

            // Create authentication provider using MSAL
            var confidentialClientApp = ConfidentialClientApplicationBuilder
                .Create(appId)
                .WithClientSecret(appPassword)
                .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
                .Build();

            // Get access token for Graph Communications
            var authResult = await confidentialClientApp
                .AcquireTokenForClient(new[] { "https://graph.microsoft.com/.default" })
                .ExecuteAsync(cancellationToken);

            // Configure media platform for audio capture
            var mediaConfiguration = MediaPlatformSettings
                .CreateFromSettings(new MediaPlatformSettingsConfiguration
                {
                    ApplicationId = appId,
                    MediaPlatformInstanceSettings = new MediaPlatformInstanceSettings
                    {
                        ServiceFqdn = new Uri(botBaseUrl).Host,
                        CertificateThumbprint = null, // Not required for application-hosted media
                        InstanceInternalPort = 8445,
                        InstancePublicPort = 8445
                    }
                });

            // Create Graph Communications client
            _communicationsClient = new CommunicationsClientBuilder(
                "PennieBot",
                appId,
                _logger)
                .SetAuthenticationProvider(new DelegateAuthenticationProvider(
                    (requestMessage) =>
                    {
                        requestMessage.Headers.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authResult.AccessToken);
                        return Task.CompletedTask;
                    }))
                .SetNotificationUrl(new Uri($"{botBaseUrl}/api/calling"))
                .SetMediaPlatformSettings(mediaConfiguration)
                .SetServiceBaseUrl(new Uri("https://graph.microsoft.com/v1.0"))
                .Build();

            _logger.LogInformation("Graph Communications Client initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Graph Communications Client");
            throw;
        }
    }

    /// <summary>
    /// Join a Teams meeting and start audio capture.
    /// </summary>
    public async Task JoinMeetingAsync(
        string meetingJoinUrl,
        string meetingId,
        Func<byte[], Task> audioDataCallback,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (_communicationsClient == null)
            {
                throw new InvalidOperationException("Graph Communications Client not initialized. Call InitializeAsync first.");
            }

            _logger.LogInformation("Joining meeting: {MeetingId} via {JoinUrl}", meetingId, meetingJoinUrl);

            // Store audio callback for this meeting
            _audioCallbacks[meetingId] = audioDataCallback;

            // Create join meeting parameters
            var joinMeetingParameters = new JoinMeetingParameters
            {
                ChatInfo = new ChatInfo
                {
                    ThreadId = meetingId // Use meeting ID as thread ID
                },
                MeetingInfo = new OrganizerMeetingInfo
                {
                    Organizer = new IdentitySet
                    {
                        User = new Identity
                        {
                            Id = _configuration["TeamsAppId"]
                        }
                    }
                },
                MediaConfig = new ServiceHostedMediaConfig
                {
                    PreFetchMedia = new List<MediaInfo>
                    {
                        new MediaInfo
                        {
                            Uri = "https://example.com/hold-music.wav",
                            ResourceId = Guid.NewGuid().ToString()
                        }
                    }
                },
                TenantId = _configuration["AzureTenantId"]
            };

            // Create the call
            var call = await _communicationsClient.Calls()
                .AddAsync(joinMeetingParameters, null)
                .ConfigureAwait(false);

            if (call == null)
            {
                throw new InvalidOperationException("Failed to create call");
            }

            // Store active call
            _activeCalls[meetingId] = call;

            // Subscribe to call state changes
            call.OnUpdated += (sender, args) =>
            {
                _logger.LogInformation("Call state changed: {State} for meeting {MeetingId}",
                    call.Resource.State, meetingId);
            };

            // Subscribe to audio streams
            if (call.Resource.MediaState?.Audio == MediaState.Active)
            {
                SubscribeToAudioStreams(call, meetingId);
            }
            else
            {
                // Wait for audio to become active
                call.OnUpdated += (sender, args) =>
                {
                    if (call.Resource.MediaState?.Audio == MediaState.Active)
                    {
                        SubscribeToAudioStreams(call, meetingId);
                    }
                };
            }

            _logger.LogInformation("Successfully joined meeting: {MeetingId}", meetingId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join meeting: {MeetingId}", meetingId);
            _audioCallbacks.TryRemove(meetingId, out _);
            throw;
        }
    }

    /// <summary>
    /// Subscribe to audio streams from the meeting.
    /// </summary>
    private void SubscribeToAudioStreams(ICall call, string meetingId)
    {
        try
        {
            _logger.LogInformation("Subscribing to audio streams for meeting: {MeetingId}", meetingId);

            // TODO: Implement audio stream subscription
            // The Graph Communications SDK provides audio frames at 50 frames/sec (20ms each)
            // We need to:
            // 1. Subscribe to incoming audio media
            // 2. Receive RTP audio frames
            // 3. Convert to 16kHz, mono, 16-bit PCM format
            // 4. Call the audio callback to send to Speech Services

            // This requires MediaPlatform configuration and IMediaSocket implementation
            // Reference: https://docs.microsoft.com/en-us/graph/api/resources/communications-api-overview

            _logger.LogWarning("Audio stream subscription not yet fully implemented - placeholder");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to subscribe to audio streams for meeting: {MeetingId}", meetingId);
        }
    }

    /// <summary>
    /// Leave the current meeting and stop audio capture.
    /// </summary>
    public async Task LeaveMeetingAsync(string meetingId)
    {
        try
        {
            if (!_activeCalls.TryRemove(meetingId, out var call))
            {
                _logger.LogWarning("No active call found for meeting: {MeetingId}", meetingId);
                return;
            }

            _logger.LogInformation("Leaving meeting: {MeetingId}", meetingId);

            // Delete (hang up) the call
            await call.DeleteAsync().ConfigureAwait(false);

            // Remove audio callback
            _audioCallbacks.TryRemove(meetingId, out _);

            _logger.LogInformation("Successfully left meeting: {MeetingId}", meetingId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to leave meeting: {MeetingId}", meetingId);
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
        if (!_activeCalls.TryGetValue(meetingId, out var call))
        {
            return "NotInMeeting";
        }

        return call.Resource.State?.ToString() ?? "Unknown";
    }
}
