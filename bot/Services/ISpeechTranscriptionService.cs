namespace PennieBot.Services;

/// <summary>
/// Interface for Azure Speech Services integration with speaker diarization.
/// </summary>
public interface ISpeechTranscriptionService
{
    /// <summary>
    /// Start real-time transcription with speaker diarization.
    /// </summary>
    /// <param name="meetingId">Unique meeting identifier</param>
    /// <param name="audioStreamCallback">Callback to receive audio data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task StartTranscriptionAsync(
        string meetingId,
        Func<TranscriptionResult, Task> transcriptionCallback,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop transcription for a meeting.
    /// </summary>
    /// <param name="meetingId">Meeting identifier</param>
    Task StopTranscriptionAsync(string meetingId);

    /// <summary>
    /// Send audio data to Speech Services for transcription.
    /// </summary>
    /// <param name="meetingId">Meeting identifier</param>
    /// <param name="audioData">Raw audio bytes (16kHz, mono, 16-bit PCM)</param>
    /// <param name="speakerId">Speaker ID (MSI) from Teams unmixed audio buffer</param>
    /// <param name="speakerName">Optional speaker name if known</param>
    Task ProcessAudioAsync(string meetingId, byte[] audioData, uint speakerId = 0, string? speakerName = null);

    /// <summary>
    /// Get transcripts for a meeting since a specific index.
    /// </summary>
    /// <param name="meetingId">Meeting identifier</param>
    /// <param name="sinceIndex">Return transcripts after this index (0 for all)</param>
    /// <returns>List of transcription results with their indices</returns>
    IReadOnlyList<IndexedTranscriptionResult> GetTranscripts(string meetingId, int sinceIndex = 0);
}

/// <summary>
/// Transcription result with index for polling.
/// </summary>
public class IndexedTranscriptionResult
{
    public int Index { get; set; }
    public TranscriptionResult Result { get; set; } = new();
}

/// <summary>
/// Transcription result with speaker attribution.
/// </summary>
public class TranscriptionResult
{
    /// <summary>
    /// Speaker name or identifier.
    /// </summary>
    public string Speaker { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the speech occurred.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Transcribed text.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Confidence score (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// Whether this is a final result (vs interim).
    /// </summary>
    public bool IsFinal { get; set; }

    /// <summary>
    /// Meeting identifier.
    /// </summary>
    public string MeetingId { get; set; } = string.Empty;
}
