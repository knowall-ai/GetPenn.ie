using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Skype.Bots.Media;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;

namespace PennieBot.Services;

/// <summary>
/// Service for initializing and managing the Real-time Media Platform SDK.
/// Required for ApplicationHostedMedia mode to receive RTP audio streams.
/// MediaPlatform is a static class - this service manages initialization state.
/// </summary>
public class MediaPlatformService : IMediaPlatformService
{
    private readonly ILogger<MediaPlatformService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, Func<byte[], Task>> _audioCallbacks = new();

    private bool _platformInitialized;
    private bool _initialized;
    private bool _disposed;

    // Configuration values
    private string? _serviceFqdn;
    private string? _certificateThumbprint;
    private int _mediaInstanceExternalPort;
    private int _instancePublicPort;
    private int _instanceInternalPort;
    private string? _appId;
    private string? _tenantId;

    public bool IsEnabled { get; private set; }
    public bool IsInitialized => _initialized;

    public MediaPlatformService(
        ILogger<MediaPlatformService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;

        // Read configuration
        var mediaPlatformConfig = configuration.GetSection("MediaPlatform");
        IsEnabled = bool.TryParse(mediaPlatformConfig["UseApplicationHostedMedia"], out var enabled) && enabled;

        if (IsEnabled)
        {
            _logger.LogInformation("MediaPlatformService: ApplicationHostedMedia mode ENABLED");
        }
        else
        {
            _logger.LogInformation("MediaPlatformService: ServiceHostedMedia mode (no audio capture)");
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            _logger.LogWarning("MediaPlatformService already initialized");
            return;
        }

        if (!IsEnabled)
        {
            _logger.LogInformation("ApplicationHostedMedia disabled, skipping media platform initialization");
            _initialized = true;
            return;
        }

        try
        {
            _logger.LogInformation("Initializing Real-time Media Platform SDK...");

            // Load configuration
            _appId = _configuration["MicrosoftAppId"];
            _tenantId = _configuration["MicrosoftAppTenantId"];

            var mediaPlatformConfig = _configuration.GetSection("MediaPlatform");
            _serviceFqdn = mediaPlatformConfig["ServiceFqdn"];
            _certificateThumbprint = mediaPlatformConfig["CertificateThumbprint"];
            _mediaInstanceExternalPort = int.TryParse(mediaPlatformConfig["MediaInstanceExternalPort"], out var port) ? port : 20000;
            _instancePublicPort = int.TryParse(mediaPlatformConfig["InstancePublicPort"], out var pubPort) ? pubPort : 8445;
            _instanceInternalPort = int.TryParse(mediaPlatformConfig["InstanceInternalPort"], out var intPort) ? intPort : 8445;

            // Validate required configuration
            if (string.IsNullOrEmpty(_serviceFqdn))
            {
                throw new InvalidOperationException("MediaPlatform:ServiceFqdn is required for ApplicationHostedMedia");
            }

            if (string.IsNullOrEmpty(_certificateThumbprint))
            {
                throw new InvalidOperationException("MediaPlatform:CertificateThumbprint is required for ApplicationHostedMedia");
            }

            if (string.IsNullOrEmpty(_appId))
            {
                throw new InvalidOperationException("MicrosoftAppId is required for ApplicationHostedMedia");
            }

            _logger.LogInformation(
                "Configuration loaded: FQDN={Fqdn}, CertThumbprint={Thumbprint}, Port={Port}",
                _serviceFqdn, _certificateThumbprint, _mediaInstanceExternalPort);

            // Create media platform settings (uses static MediaPlatform class)
            var mediaSettings = CreateMediaPlatformSettings();

            // Initialize the static media platform
            // This starts TCP/UDP listeners for media streams
            _logger.LogInformation("Initializing static MediaPlatform with settings: FQDN={Fqdn}, Port={Port}",
                _serviceFqdn, _mediaInstanceExternalPort);

            MediaPlatform.Initialize(mediaSettings);
            _platformInitialized = true;

            _logger.LogInformation("Real-time Media Platform SDK initialized successfully");
            _initialized = true;

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Real-time Media Platform SDK");
            throw;
        }
    }

    public IMediaPlatform? GetMediaPlatform()
    {
        // MediaPlatform is a static class, we don't return an instance
        // This method exists for interface compatibility, but media config
        // is generated via static MediaPlatform.CreateMediaConfiguration()
        if (!IsEnabled || !_initialized)
        {
            return null;
        }

        // Return null - callers should use CreateMediaConfigurationBlob() instead
        return null;
    }

    /// <summary>
    /// Create the media configuration blob using the static MediaPlatform SDK.
    /// This generates the properly encoded blob that Graph API expects.
    /// </summary>
    public string? CreateMediaConfigurationBlob(AudioSocket audioSocket)
    {
        if (!_platformInitialized)
        {
            _logger.LogWarning("MediaPlatform not initialized, cannot create media config blob");
            return null;
        }

        try
        {
            // Use the static MediaPlatform to create the configuration
            JObject mediaConfig = MediaPlatform.CreateMediaConfiguration(audioSocket);

            // Return as JSON string (this is what Graph API expects)
            var blob = mediaConfig.ToString(Newtonsoft.Json.Formatting.None);
            _logger.LogInformation("Created media configuration blob (length={Length})", blob.Length);

            return blob;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create media configuration blob");
            return null;
        }
    }

    public AudioSocketSettings CreateAudioSocketSettings(string? callId = null)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("ApplicationHostedMedia is not enabled");
        }

        // CallId is required by AudioSocket constructor - generate one if not provided
        var audioCallId = callId ?? Guid.NewGuid().ToString();

        // Create audio socket settings for receiving unmixed meeting audio
        // 16kHz, mono, 16-bit PCM - suitable for Azure Speech SDK
        return new AudioSocketSettings
        {
            CallId = audioCallId,
            StreamDirections = StreamDirection.Recvonly,
            SupportedAudioFormat = AudioFormat.Pcm16K,
            ReceiveUnmixedMeetingAudio = true
        };
    }

    public void RegisterAudioCallback(string callId, Func<byte[], Task> callback)
    {
        _audioCallbacks[callId] = callback;
        _logger.LogInformation("Registered audio callback for call {CallId}", callId);
    }

    public void UnregisterAudioCallback(string callId)
    {
        if (_audioCallbacks.TryRemove(callId, out _))
        {
            _logger.LogInformation("Unregistered audio callback for call {CallId}", callId);
        }
    }

    public async Task HandleAudioReceivedAsync(string callId, AudioMediaBuffer buffer)
    {
        try
        {
            if (!_audioCallbacks.TryGetValue(callId, out var callback))
            {
                return;
            }

            // With ReceiveUnmixedMeetingAudio=true, audio is in UnmixedAudioBuffers (per-speaker)
            // buffer.Data is empty/zeros in unmixed mode
            var unmixedBuffers = buffer.UnmixedAudioBuffers;

            if (unmixedBuffers != null && unmixedBuffers.Length > 0)
            {
                // Process each speaker's audio separately
                foreach (var unmixedBuffer in unmixedBuffers)
                {
                    if (unmixedBuffer.Length > 0)
                    {
                        var audioData = new byte[unmixedBuffer.Length];
                        Marshal.Copy(unmixedBuffer.Data, audioData, 0, (int)unmixedBuffer.Length);
                        await callback(audioData);
                    }
                }
            }
            // Fallback: try mixed buffer if no unmixed buffers
            else if (buffer.Data != IntPtr.Zero && buffer.Length > 0)
            {
                var audioData = new byte[buffer.Length];
                Marshal.Copy(buffer.Data, audioData, 0, (int)buffer.Length);
                await callback(audioData);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling audio for call {CallId}", callId);
        }
    }

    private MediaPlatformSettings CreateMediaPlatformSettings()
    {
        // Resolve the public IP address from DNS or configuration
        var publicIp = ResolvePublicIpAddress(_serviceFqdn!);

        _logger.LogInformation("Resolved public IP: {IP} for FQDN: {Fqdn}", publicIp, _serviceFqdn);

        // MediaPlatformSettings uses nested MediaPlatformInstanceSettings
        // for instance-specific configuration (ports, certificate, IP)
        var instanceSettings = new MediaPlatformInstanceSettings
        {
            // Certificate thumbprint for MTLS authentication (looked up from Windows store)
            CertificateThumbprint = _certificateThumbprint!,

            // Service FQDN for media streams
            ServiceFqdn = _serviceFqdn!,

            // Port configuration
            InstancePublicPort = _instancePublicPort,
            InstanceInternalPort = _instanceInternalPort,

            // Public IP address where media streams will be received
            InstancePublicIPAddress = publicIp
        };

        return new MediaPlatformSettings
        {
            // Application identity
            ApplicationId = _appId!,

            // Instance-specific settings (nested object)
            MediaPlatformInstanceSettings = instanceSettings
        };
    }

    private IPAddress ResolvePublicIpAddress(string fqdn)
    {
        try
        {
            // Try to resolve from DNS
            var addresses = Dns.GetHostAddresses(fqdn);
            var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

            if (ipv4 != null)
            {
                return ipv4;
            }

            _logger.LogWarning("Could not resolve IPv4 address for {Fqdn}, using IPAddress.Any", fqdn);
            return IPAddress.Any;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve IP for {Fqdn}, using IPAddress.Any", fqdn);
            return IPAddress.Any;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _audioCallbacks.Clear();

            // MediaPlatform is static - use static Shutdown method
            if (_platformInitialized)
            {
                try
                {
                    MediaPlatform.Shutdown();
                    _logger.LogInformation("MediaPlatform shutdown completed");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error shutting down media platform");
                }
            }

            _logger.LogInformation("MediaPlatformService disposed");
        }

        _disposed = true;
    }
}
