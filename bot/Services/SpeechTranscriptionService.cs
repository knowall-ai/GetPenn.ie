using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Transcription;

namespace PennieBot.Services;

/// <summary>
/// Azure Speech Services implementation with MeetingTranscriber for speaker diarization.
/// </summary>
public class SpeechTranscriptionService : ISpeechTranscriptionService
{
    private readonly ILogger<SpeechTranscriptionService> _logger;
    private readonly IConfiguration _configuration;
    private readonly Dictionary<string, MeetingTranscriber> _transcribers = new();
    private readonly Dictionary<string, PushAudioInputStream> _audioStreams = new();

    public SpeechTranscriptionService(
        ILogger<SpeechTranscriptionService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
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
            var transcriber = new MeetingTranscriber(config, audioConfig);

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
                    var result = new TranscriptionResult
                    {
                        Speaker = e.Result.SpeakerId ?? "Unknown",
                        Timestamp = DateTime.UtcNow,
                        Text = e.Result.Text,
                        Confidence = 1.0, // TODO: Extract from detailed results
                        IsFinal = true,
                        MeetingId = meetingId
                    };

                    _logger.LogInformation(
                        "Transcribed: {Speaker} @ {Timestamp}: {Text}",
                        result.Speaker, result.Timestamp, result.Text);

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
}
