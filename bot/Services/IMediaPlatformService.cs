using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Skype.Bots.Media;

namespace PennieBot.Services;

/// <summary>
/// Interface for Real-time Media Platform initialization and management.
/// Handles the static SDK initialization required for ApplicationHostedMedia.
/// Note: MediaPlatform is a static class - this service manages initialization state.
/// </summary>
public interface IMediaPlatformService : IDisposable
{
    /// <summary>
    /// Whether ApplicationHostedMedia is enabled and initialized.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Whether the media platform has been successfully initialized.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Initialize the Real-time Media Platform.
    /// Must be called before any call operations when ApplicationHostedMedia is enabled.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the media platform instance. Since MediaPlatform is static, this returns null.
    /// Use CreateMediaConfigurationBlob() instead for media configuration.
    /// </summary>
    IMediaPlatform? GetMediaPlatform();

    /// <summary>
    /// Create the media configuration blob using the static MediaPlatform SDK.
    /// This generates the properly encoded blob that Graph API expects.
    /// </summary>
    /// <param name="audioSocket">The audio socket to include in the configuration</param>
    /// <returns>JSON string blob, or null if platform not initialized</returns>
    string? CreateMediaConfigurationBlob(AudioSocket audioSocket);

    /// <summary>
    /// Create audio socket settings for a new call.
    /// </summary>
    /// <param name="callId">Optional call ID. If not provided, a new GUID will be generated.</param>
    AudioSocketSettings CreateAudioSocketSettings(string? callId = null);

    /// <summary>
    /// Register a callback to receive audio data for a call.
    /// </summary>
    void RegisterAudioCallback(string callId, Func<byte[], Task> callback);

    /// <summary>
    /// Unregister the audio callback when a call ends.
    /// </summary>
    void UnregisterAudioCallback(string callId);

    /// <summary>
    /// Handle incoming audio media received event.
    /// </summary>
    Task HandleAudioReceivedAsync(string callId, AudioMediaBuffer buffer);
}
