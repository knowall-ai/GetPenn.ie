using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Transcription;
using ConversationTranscriptionResult = Microsoft.CognitiveServices.Speech.Transcription.ConversationTranscriptionResult;

namespace PennieBot.Services;

/// <summary>
/// Azure Speech Services implementation with MeetingTranscriber for speaker diarization.
/// </summary>
public class SpeechTranscriptionService : ISpeechTranscriptionService, IDisposable
{
    private const string UnknownSpeakerConstant = "UNKNOWN_SPEAKER";
    private readonly ILogger<SpeechTranscriptionService> _logger;
    private readonly IConfiguration _configuration;
    private readonly Dictionary<string, ConversationTranscriber> _transcribers = new();
    private readonly Dictionary<string, PushAudioInputStream> _audioStreams = new();
    private readonly Dictionary<string, string> _speakerIdToNameMap = new();
    private readonly object _lock = new();
    private bool _disposed;

    public SpeechTranscriptionService(
        ILogger<SpeechTranscriptionService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Map speaker ID to friendly name.
    /// </summary>
    /// <param name="speakerId">Speaker ID from Azure Speech Services</param>
    /// <returns>Friendly speaker name</returns>
    private string MapSpeakerIdToName(string speakerId)
    {
        // Handle unknown speaker specially to avoid creating multiple "Unknown Speaker N" entries
        if (speakerId == UnknownSpeakerConstant)
        {
            if (!_speakerIdToNameMap.ContainsKey(UnknownSpeakerConstant))
            {
                _speakerIdToNameMap[UnknownSpeakerConstant] = "Unknown Speaker";
                _logger.LogWarning("Speaker diarization returned unknown speaker ID");
            }
            return _speakerIdToNameMap[UnknownSpeakerConstant];
        }

        // Check if we have a mapping for this speaker ID
        if (_speakerIdToNameMap.TryGetValue(speakerId, out var name))
        {
            return name;
        }

        // If no mapping exists, create a friendly speaker label
        // In production, this would:
        // 1. Query Teams Graph API to get participant names
        // 2. Match speaker ID to participant based on join time
        // 3. Store mapping for reuse during the meeting

        var speakerNumber = _speakerIdToNameMap.Count + 1;
        var friendlyName = $"Speaker {speakerNumber}";

        _speakerIdToNameMap[speakerId] = friendlyName;

        _logger.LogInformation("Mapped speaker ID {SpeakerId} to {FriendlyName}", speakerId, friendlyName);

        return friendlyName;
    }

    /// <inheritdoc/>
    public async Task StartTranscriptionAsync(
        string meetingId,
        Func<TranscriptionResult, Task> transcriptionCallback,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting transcription for meeting {MeetingId}", meetingId);

            // Create Speech configuration
            var speechKey = _configuration["AZURE_SPEECH_KEY"]
                ?? throw new InvalidOperationException("AZURE_SPEECH_KEY not configured");
            var speechRegion = _configuration["AZURE_LOCATION"] ?? "uksouth";

            var config = SpeechConfig.FromSubscription(speechKey, speechRegion);
            config.SpeechRecognitionLanguage = "en-US";

            // Enable detailed results
            config.SetProperty(PropertyId.SpeechServiceResponse_RequestWordLevelTimestamps, "true");

            // Create push audio stream (we'll push RTP audio data to this)
            var audioFormat = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1); // 16kHz, 16-bit, mono
            var audioStream = AudioInputStream.CreatePushStream(audioFormat);
            _audioStreams[meetingId] = (PushAudioInputStream)audioStream;

            var audioConfig = AudioConfig.FromStreamInput(audioStream);

            // Create Meeting Transcriber with speaker diarization
            // Note: MeetingTranscriber API requires Meeting object in newer versions
            // For now, use ConversationTranscriber as fallback for basic testing
            var transcriber = new ConversationTranscriber(config, audioConfig);

            // Subscribe to transcription events
            transcriber.Transcribing += (s, e) =>
            {
                // Interim results
                _logger.LogDebug("Transcribing (interim): {Text}", e.Result.Text);
            };

            transcriber.Transcribed += async (s, e) =>
            {
                if (e.Result.Reason == ResultReason.RecognizedSpeech)
                {
                    // Extract speaker information from conversation transcription result
                    var conversationResult = e.Result as ConversationTranscriptionResult;
                    var speakerId = conversationResult?.SpeakerId ?? UnknownSpeakerConstant;

                    // Map speaker ID to friendly name if available
                    // In production, maintain a mapping of speaker IDs to participant names
                    var speakerName = MapSpeakerIdToName(speakerId);

                    // Extract confidence score from detailed results
                    var confidence = 1.0;
                    try
                    {
                        var detailedResult = e.Result.Properties.GetProperty(PropertyId.SpeechServiceResponse_JsonResult);
                        if (!string.IsNullOrEmpty(detailedResult))
                        {
                            // Parse JSON to extract confidence score
                            var json = System.Text.Json.JsonDocument.Parse(detailedResult);
                            if (json.RootElement.TryGetProperty("NBest", out var nBest) &&
                                nBest.GetArrayLength() > 0 &&
                                nBest[0].TryGetProperty("Confidence", out var conf))
                            {
                                confidence = conf.GetDouble();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not extract confidence score, using default value");
                    }

                    var result = new TranscriptionResult
                    {
                        Speaker = speakerName,
                        Timestamp = DateTime.UtcNow,
                        Text = e.Result.Text,
                        Confidence = confidence,
                        IsFinal = true,
                        MeetingId = meetingId
                    };

                    _logger.LogInformation(
                        "Transcribed: {Speaker} ({SpeakerId}) @ {Timestamp}: {Text} (confidence: {Confidence:F2})",
                        result.Speaker, speakerId, result.Timestamp, result.Text, result.Confidence);

                    // Callback to send to Pennie AI agent
                    await transcriptionCallback(result);
                }
            };

            transcriber.Canceled += (s, e) =>
            {
                _logger.LogError(
                    "Transcription canceled: {Reason} - {ErrorDetails}",
                    e.Reason, e.ErrorDetails);
            };

            transcriber.SessionStarted += (s, e) =>
            {
                _logger.LogInformation("Transcription session started for meeting {MeetingId}", meetingId);
            };

            transcriber.SessionStopped += (s, e) =>
            {
                _logger.LogInformation("Transcription session stopped for meeting {MeetingId}", meetingId);
            };

            // Store transcriber for later stop
            _transcribers[meetingId] = transcriber;

            // Start transcription
            await transcriber.StartTranscribingAsync();

            _logger.LogInformation("Transcription started successfully for meeting {MeetingId}", meetingId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting transcription for meeting {MeetingId}", meetingId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task StopTranscriptionAsync(string meetingId)
    {
        try
        {
            _logger.LogInformation("Stopping transcription for meeting {MeetingId}", meetingId);

            if (_transcribers.TryGetValue(meetingId, out var transcriber))
            {
                await transcriber.StopTranscribingAsync();
                transcriber.Dispose();
                _transcribers.Remove(meetingId);
            }

            if (_audioStreams.TryGetValue(meetingId, out var audioStream))
            {
                audioStream.Close();
                _audioStreams.Remove(meetingId);
            }

            _logger.LogInformation("Transcription stopped for meeting {MeetingId}", meetingId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping transcription for meeting {MeetingId}", meetingId);
            throw;
        }
    }

    /// <inheritdoc/>
    public Task ProcessAudioAsync(string meetingId, byte[] audioData)
    {
        try
        {
            if (!_audioStreams.TryGetValue(meetingId, out var audioStream))
            {
                _logger.LogWarning("No audio stream found for meeting {MeetingId}", meetingId);
                return Task.CompletedTask;
            }

            // Push audio data to Speech Services
            audioStream.Write(audioData);

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing audio for meeting {MeetingId}", meetingId);
            throw;
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
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            lock (_lock)
            {
                // Dispose all active transcribers
                foreach (var kvp in _transcribers)
                {
                    try
                    {
                        kvp.Value.Dispose();
                        _logger.LogDebug("Disposed transcriber for meeting {MeetingId}", kvp.Key);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error disposing transcriber for meeting {MeetingId}", kvp.Key);
                    }
                }
                _transcribers.Clear();

                // Close all active audio streams
                foreach (var kvp in _audioStreams)
                {
                    try
                    {
                        kvp.Value.Close();
                        _logger.LogDebug("Closed audio stream for meeting {MeetingId}", kvp.Key);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error closing audio stream for meeting {MeetingId}", kvp.Key);
                    }
                }
                _audioStreams.Clear();

                _logger.LogInformation("SpeechTranscriptionService disposed");
            }
        }

        _disposed = true;
    }
}
