using System.Text;
using System.Text.Json;

namespace PennieBot.Services;

/// <summary>
/// Client for communicating with Pennie AI Foundry Agent.
/// </summary>
public class PennieAgentClient : IPennieAgentClient
{
    private readonly ILogger<PennieAgentClient> _logger;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;

    public PennieAgentClient(
        ILogger<PennieAgentClient> logger,
        IConfiguration configuration,
        HttpClient httpClient)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public async Task SendTranscriptAsync(
        TranscriptionResult result,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation(
                "Sending transcript to Pennie: {Speaker} - {Text}",
                result.Speaker, result.Text);

            // TODO: Implement Azure AI Foundry Agent API call
            // This would send the transcript to Pennie's endpoint
            // Pennie would:
            // 1. Analyze the transcript segment
            // 2. Determine if it contains a requirement
            // 3. Call MCP server to create/update work items
            // 4. Return any clarifying questions

            var agentEndpoint = _configuration["PENNIE_AGENT_ENDPOINT"];
            if (string.IsNullOrEmpty(agentEndpoint))
            {
                _logger.LogWarning("PENNIE_AGENT_ENDPOINT not configured, logging transcript only");
                return;
            }

            var payload = new
            {
                meetingId = result.MeetingId,
                speaker = result.Speaker,
                timestamp = result.Timestamp,
                text = result.Text,
                confidence = result.Confidence
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(
                $"{agentEndpoint}/transcript",
                content,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            _logger.LogInformation("Transcript sent successfully to Pennie");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending transcript to Pennie");
            // Don't throw - we don't want transcription failures to break the bot
        }
    }

    /// <inheritdoc/>
    public async Task<string> GetMeetingSummaryAsync(
        string meetingId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Requesting meeting summary for {MeetingId}", meetingId);

            // TODO: Implement Azure AI Foundry Agent API call
            // This would request Pennie to generate a summary of:
            // - All work items created
            // - Key decisions made
            // - Outstanding questions

            var agentEndpoint = _configuration["PENNIE_AGENT_ENDPOINT"];
            if (string.IsNullOrEmpty(agentEndpoint))
            {
                return "Meeting summary generation not configured.";
            }

            var response = await _httpClient.GetAsync(
                $"{agentEndpoint}/summary/{meetingId}",
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var summary = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogInformation("Received meeting summary from Pennie");

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting meeting summary from Pennie");
            return "Error generating meeting summary.";
        }
    }

    /// <inheritdoc/>
    public Task NotifyWorkItemCreatedAsync(int workItemId, string workItemType, string title)
    {
        try
        {
            _logger.LogInformation(
                "Work item created notification: {Type} #{Id} - {Title}",
                workItemType, workItemId, title);

            // TODO: This would typically be handled by Pennie posting to Teams chat
            // via the Bot Framework messaging API
            // For now, just log

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error notifying work item creation");
            return Task.CompletedTask;
        }
    }
}
