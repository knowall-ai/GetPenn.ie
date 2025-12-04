using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace PennieBot.Services;

/// <summary>
/// Azure Speech Services implementation using SpeechRecognizer for continuous recognition.
/// Since we receive unmixed audio per participant from Teams, we don't need speaker diarization.
/// </summary>
public class SpeechTranscriptionService : ISpeechTranscriptionService, IDisposable
{
    private readonly ILogger<SpeechTranscriptionService> _logger;
    private readonly IConfiguration _configuration;
    private readonly Dictionary<string, SpeechRecognizer> _recognizers = new();
    private readonly Dictionary<string, PushAudioInputStream> _audioStreams = new();
    private readonly Dictionary<string, List<TranscriptionResult>> _transcripts = new();
    private readonly Dictionary<string, string> _currentSpeakers = new(); // meetingId -> speaker name
    private readonly object _lock = new();
    private bool _disposed;

    public SpeechTranscriptionService(
        ILogger<SpeechTranscriptionService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;

        // Log Speech configuration at startup to verify loading
        var speechKey = configuration["AZURE_SPEECH_KEY"];
        var speechRegion = configuration["AZURE_LOCATION"] ?? "uksouth";

        if (string.IsNullOrEmpty(speechKey))
        {
            _logger.LogWarning("STARTUP: AZURE_SPEECH_KEY is NOT configured - transcription will be disabled");
        }
        else
        {
            _logger.LogInformation(
                "STARTUP: Speech config loaded - Region={Region}, KeyConfigured=true",
                speechRegion);
        }
    }

    /// <inheritdoc/>
    public async Task StartTranscriptionAsync(
        string meetingId,
        Func<TranscriptionResult, Task> transcriptionCallback,
        CancellationToken cancellationToken = default)
    {
        // Input validation
        if (string.IsNullOrWhiteSpace(meetingId))
        {
            throw new ArgumentException("Meeting ID cannot be null or empty", nameof(meetingId));
        }
        if (transcriptionCallback == null)
        {
            throw new ArgumentNullException(nameof(transcriptionCallback));
        }

        try
        {
            _logger.LogInformation("Starting transcription for meeting {MeetingId}", meetingId);

            // Create Speech configuration
            var speechKey = _configuration["AZURE_SPEECH_KEY"]
                ?? throw new InvalidOperationException("AZURE_SPEECH_KEY not configured");
            var speechRegion = _configuration["AZURE_LOCATION"] ?? "uksouth";

            // Get speech language from configuration, default to en-GB for UK users
            var speechLanguage = _configuration["SpeechRecognitionLanguage"] ?? "en-GB";

            _logger.LogDebug("Creating SpeechConfig for region {Region}, language {Language}", speechRegion, speechLanguage);
            var config = SpeechConfig.FromSubscription(speechKey, speechRegion);
            config.SpeechRecognitionLanguage = speechLanguage;

            // Enable detailed results for confidence scores
            config.SetProperty(PropertyId.SpeechServiceResponse_RequestWordLevelTimestamps, "true");
            config.OutputFormat = OutputFormat.Detailed;

            // Create push audio stream for Teams audio (16kHz, 16-bit, mono PCM)
            var audioFormat = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
            var audioStream = AudioInputStream.CreatePushStream(audioFormat);
            _audioStreams[meetingId] = (PushAudioInputStream)audioStream;

            var audioConfig = AudioConfig.FromStreamInput(audioStream);

            // Use SpeechRecognizer for continuous recognition
            // This is simpler than ConversationTranscriber since we get unmixed audio per participant
            var recognizer = new SpeechRecognizer(config, audioConfig);

            // Subscribe to recognition events
            recognizer.Recognizing += (s, e) =>
            {
                _logger.LogDebug("Recognizing: {Text}", e.Result.Text ?? "(empty)");
            };

            recognizer.Recognized += async (s, e) =>
            {
                if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrEmpty(e.Result.Text))
                {
                    // Extract confidence score from detailed results
                    var confidence = 0.9; // Default confidence
                    try
                    {
                        var detailedResult = e.Result.Properties.GetProperty(PropertyId.SpeechServiceResponse_JsonResult);
                        if (!string.IsNullOrEmpty(detailedResult))
                        {
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
                        _logger.LogDebug(ex, "Could not extract confidence score");
                    }

                    // Get the current speaker from tracked audio
                    string speaker;
                    lock (_lock)
                    {
                        speaker = _currentSpeakers.TryGetValue(meetingId, out var spkName) ? spkName : "Unknown Speaker";
                    }

                    var result = new TranscriptionResult
                    {
                        Speaker = speaker,
                        Timestamp = DateTime.UtcNow,
                        Text = e.Result.Text,
                        Confidence = confidence,
                        IsFinal = true,
                        MeetingId = meetingId
                    };

                    _logger.LogInformation(
                        "Transcribed [{MeetingId}] @ {Timestamp}: {Text} (confidence: {Confidence:F2})",
                        meetingId, result.Timestamp.ToString("HH:mm:ss"), result.Text, result.Confidence);

                    // Store transcript for API access
                    lock (_lock)
                    {
                        if (_transcripts.TryGetValue(meetingId, out var transcripts))
                        {
                            transcripts.Add(result);
                        }
                    }

                    // Callback to send to Pennie AI agent
                    await transcriptionCallback(result);
                }
                else if (e.Result.Reason == ResultReason.NoMatch)
                {
                    _logger.LogDebug("No speech recognized in audio segment");
                }
            };

            recognizer.Canceled += (s, e) =>
            {
                if (e.Reason == CancellationReason.Error)
                {
                    _logger.LogError(
                        "Speech recognition canceled with error: Code={ErrorCode}, Details={ErrorDetails}",
                        e.ErrorCode, e.ErrorDetails ?? "(none)");
                }
                else
                {
                    _logger.LogDebug("Speech recognition canceled: Reason={Reason}", e.Reason);
                }
            };

            recognizer.SessionStarted += (s, e) =>
            {
                _logger.LogInformation("Speech session started for meeting {MeetingId}, SessionId={SessionId}",
                    meetingId, e.SessionId);
            };

            recognizer.SessionStopped += (s, e) =>
            {
                _logger.LogInformation("Speech session stopped for meeting {MeetingId}, SessionId={SessionId}",
                    meetingId, e.SessionId);
            };

            recognizer.SpeechStartDetected += (s, e) =>
            {
                _logger.LogDebug("Speech start detected at offset {Offset}", e.Offset);
            };

            recognizer.SpeechEndDetected += (s, e) =>
            {
                _logger.LogDebug("Speech end detected at offset {Offset}", e.Offset);
            };

            // Initialize transcript list for this meeting
            lock (_lock)
            {
                _transcripts[meetingId] = new List<TranscriptionResult>();
            }

            // Store recognizer for later stop
            _recognizers[meetingId] = recognizer;

            // Start continuous recognition
            await recognizer.StartContinuousRecognitionAsync();
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
        // Input validation
        if (string.IsNullOrWhiteSpace(meetingId))
        {
            throw new ArgumentException("Meeting ID cannot be null or empty", nameof(meetingId));
        }

        try
        {
            _logger.LogInformation("Stopping transcription for meeting {MeetingId}", meetingId);

            // Stop and dispose recognizer
            if (_recognizers.TryGetValue(meetingId, out var recognizer))
            {
                try
                {
                    await recognizer.StopContinuousRecognitionAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error stopping continuous recognition for meeting {MeetingId}", meetingId);
                }
                finally
                {
                    recognizer.Dispose();
                    _recognizers.Remove(meetingId);
                }
            }

            // Close and dispose audio stream
            if (_audioStreams.TryGetValue(meetingId, out var audioStream))
            {
                try
                {
                    audioStream.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error closing audio stream for meeting {MeetingId}", meetingId);
                }
                finally
                {
                    _audioStreams.Remove(meetingId);
                }
            }

            // Clean up tracking dictionaries
            lock (_lock)
            {
                _transcripts.Remove(meetingId);
                _currentSpeakers.Remove(meetingId);
                _audioBytesWritten.Remove(meetingId);
                _lastAudioLogTime.Remove(meetingId);
            }

            _logger.LogInformation("Transcription stopped for meeting {MeetingId}", meetingId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping transcription for meeting {MeetingId}", meetingId);
            throw;
        }
    }

    // Track audio bytes pushed per meeting for diagnostics
    private readonly Dictionary<string, long> _audioBytesWritten = new();
    private readonly Dictionary<string, DateTime> _lastAudioLogTime = new();

    /// <inheritdoc/>
    public Task ProcessAudioAsync(string meetingId, byte[] audioData, uint speakerId = 0, string? speakerName = null)
    {
        try
        {
            if (!_audioStreams.TryGetValue(meetingId, out var audioStream))
            {
                _logger.LogWarning("No audio stream found for meeting {MeetingId}. Recognizer exists: {HasRecognizer}",
                    meetingId, _recognizers.ContainsKey(meetingId));
                return Task.CompletedTask;
            }

            // Push audio data to Speech Services
            // The audio should be 16kHz, 16-bit, mono PCM
            audioStream.Write(audioData);

            // Calculate audio energy (RMS) to diagnose silence vs speech
            double rms = 0;
            int maxSample = 0;
            int nonZeroSamples = 0;
            for (int i = 0; i < audioData.Length - 1; i += 2)
            {
                // 16-bit little-endian PCM
                short sample = (short)(audioData[i] | (audioData[i + 1] << 8));
                rms += sample * sample;
                var absSample = Math.Abs(sample);
                if (absSample > maxSample) maxSample = absSample;
                if (sample != 0) nonZeroSamples++;
            }
            rms = Math.Sqrt(rms / (audioData.Length / 2));

            // Track bytes written for diagnostics
            lock (_lock)
            {
                if (!_audioBytesWritten.ContainsKey(meetingId))
                {
                    _audioBytesWritten[meetingId] = 0;
                    _lastAudioLogTime[meetingId] = DateTime.UtcNow;
                }
                _audioBytesWritten[meetingId] += audioData.Length;

                // Track current speaker - update when audio has significant energy (someone is speaking)
                // RMS > 100 indicates actual speech vs silence
                if (rms > 100 && speakerId > 0)
                {
                    var newSpeaker = speakerName ?? $"Speaker {speakerId}";
                    if (!_currentSpeakers.TryGetValue(meetingId, out var currentSpeaker) || currentSpeaker != newSpeaker)
                    {
                        _currentSpeakers[meetingId] = newSpeaker;
                        _logger.LogInformation("SPEAKER-CHANGE: Meeting {MeetingId} now hearing from {Speaker} (ID: {SpeakerId})",
                            meetingId, newSpeaker, speakerId);
                    }
                }

                // Log every 5 seconds with audio energy stats
                var now = DateTime.UtcNow;
                if ((now - _lastAudioLogTime[meetingId]).TotalSeconds >= 5)
                {
                    var bytesWritten = _audioBytesWritten[meetingId];
                    var kbps = (bytesWritten * 8.0 / 1000.0) / 5.0; // kilobits per second
                    var currentSpeakerName = _currentSpeakers.TryGetValue(meetingId, out var s) ? s : "Unknown";
                    _logger.LogWarning(
                        "AUDIO-ANALYSIS: {TotalKB:F1}KB ({Kbps:F1}kbps), LastRMS={RMS:F0}, MaxSample={Max}, NonZero={NonZero}/{Total}, Speaker={Speaker} for {MeetingId}",
                        bytesWritten / 1024.0, kbps, rms, maxSample, nonZeroSamples, audioData.Length / 2, currentSpeakerName, meetingId);
                    _audioBytesWritten[meetingId] = 0;
                    _lastAudioLogTime[meetingId] = now;
                }
            }

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing audio for meeting {MeetingId}", meetingId);
            throw;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<IndexedTranscriptionResult> GetTranscripts(string meetingId, int sinceIndex = 0)
    {
        lock (_lock)
        {
            if (!_transcripts.TryGetValue(meetingId, out var transcripts))
            {
                return Array.Empty<IndexedTranscriptionResult>();
            }

            var result = new List<IndexedTranscriptionResult>();
            for (var i = sinceIndex; i < transcripts.Count; i++)
            {
                result.Add(new IndexedTranscriptionResult
                {
                    Index = i + 1,
                    Result = transcripts[i]
                });
            }
            return result;
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
            lock (_lock)
            {
                foreach (var kvp in _recognizers)
                {
                    try
                    {
                        kvp.Value.Dispose();
                        _logger.LogDebug("Disposed recognizer for meeting {MeetingId}", kvp.Key);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error disposing recognizer for meeting {MeetingId}", kvp.Key);
                    }
                }
                _recognizers.Clear();

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
