using System.Text;
using System.Text.Json;
using System.Web;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace PennieBot.Services;

/// <summary>
/// Service that looks up online meeting details from Teams SDK context via Graph API.
/// The Teams SDK provides a base64-encoded meeting ID that contains the thread ID,
/// which can be used to construct the join URL or look up meeting details.
/// </summary>
public class OnlineMeetingService : IOnlineMeetingService
{
    private readonly ILogger<OnlineMeetingService> _logger;
    private readonly IConfiguration _configuration;
    private GraphServiceClient? _graphClient;

    public OnlineMeetingService(
        ILogger<OnlineMeetingService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    private async Task<GraphServiceClient> GetGraphClientAsync()
    {
        if (_graphClient != null)
            return _graphClient;

        var appId = _configuration["MicrosoftAppId"];
        var appSecret = _configuration["MicrosoftAppPassword"];
        var tenantId = _configuration["MicrosoftAppTenantId"];

        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(appSecret) || string.IsNullOrEmpty(tenantId))
        {
            throw new InvalidOperationException(
                "Bot credentials not configured. MicrosoftAppId, MicrosoftAppPassword, and MicrosoftAppTenantId required.");
        }

        var credential = new ClientSecretCredential(tenantId, appId, appSecret);
        _graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });

        return _graphClient;
    }

    /// <summary>
    /// Decodes the Teams SDK meeting ID to extract the thread ID.
    /// The format is: base64(sequence#threadId#tenantSequence)
    /// Example decoded: 0#19:meeting_OTQ0YmY3NDYtYjE4MS00YzQ1LThmMzQtNzYyMmZmODZkMWYw@thread.v2#0
    /// </summary>
    private (string? threadId, string? error) DecodeMeetingId(string encodedMeetingId)
    {
        try
        {
            // The Teams SDK meeting ID is base64 encoded
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encodedMeetingId));
            _logger.LogDebug("Decoded meeting ID: {Decoded}", decoded);

            // Format: sequence#threadId#tenantSequence
            // Example: 0#19:meeting_xxx@thread.v2#0
            var parts = decoded.Split('#');
            if (parts.Length >= 2)
            {
                var threadId = parts[1];
                _logger.LogInformation("Extracted thread ID: {ThreadId}", threadId);
                return (threadId, null);
            }

            return (null, $"Unexpected meeting ID format: {decoded}");
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Failed to decode meeting ID as base64: {MeetingId}", encodedMeetingId);
            return (null, "Invalid base64 encoding");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error decoding meeting ID: {MeetingId}", encodedMeetingId);
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Constructs a Teams join URL from the thread ID and tenant ID.
    /// This URL format is used to join meetings via Graph Communications API.
    /// </summary>
    private string ConstructJoinUrl(string threadId, string? tenantId)
    {
        // URL encode the thread ID (@ becomes %40, : becomes %3a)
        var encodedThreadId = HttpUtility.UrlEncode(threadId);

        // Base join URL format
        var joinUrl = $"https://teams.microsoft.com/l/meetup-join/{encodedThreadId}/0";

        // Add tenant context if available
        if (!string.IsNullOrEmpty(tenantId))
        {
            var context = JsonSerializer.Serialize(new { Tid = tenantId });
            var encodedContext = HttpUtility.UrlEncode(context);
            joinUrl += $"?context={encodedContext}";
        }

        _logger.LogInformation("Constructed join URL: {JoinUrl}", joinUrl);
        return joinUrl;
    }

    public async Task<string?> GetMeetingJoinUrlAsync(
        string meetingId,
        string? chatId = null,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Looking up meeting join URL for meetingId: {MeetingId}, chatId: {ChatId}, tenantId: {TenantId}",
            meetingId, chatId, tenantId);

        // First, try to decode the meeting ID and construct the URL directly
        var (threadId, error) = DecodeMeetingId(meetingId);

        if (threadId != null)
        {
            // We have the thread ID - construct the join URL
            var joinUrl = ConstructJoinUrl(threadId, tenantId);
            return joinUrl;
        }

        _logger.LogWarning("Could not decode meeting ID directly: {Error}. Attempting Graph API lookup...", error);

        // Fall back to Graph API lookup via chat
        if (!string.IsNullOrEmpty(chatId))
        {
            try
            {
                var meetingInfo = await LookupMeetingViaChatAsync(chatId, cancellationToken);
                if (meetingInfo?.JoinUrl != null)
                {
                    _logger.LogInformation("Found join URL via chat lookup: {JoinUrl}", meetingInfo.JoinUrl);
                    return meetingInfo.JoinUrl;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to lookup meeting via chat: {ChatId}", chatId);
            }
        }

        _logger.LogWarning("Could not find meeting join URL for meetingId: {MeetingId}", meetingId);
        return null;
    }

    public async Task<OnlineMeetingInfo?> GetMeetingInfoAsync(
        string meetingId,
        CancellationToken cancellationToken = default)
    {
        var (threadId, _) = DecodeMeetingId(meetingId);

        if (threadId == null)
        {
            return null;
        }

        return new OnlineMeetingInfo
        {
            MeetingId = meetingId,
            ThreadId = threadId,
            JoinUrl = ConstructJoinUrl(threadId, null)
        };
    }

    /// <summary>
    /// Attempts to find the online meeting by looking up the chat's associated meeting.
    /// Uses Graph API: GET /chats/{chatId}?$expand=tabs
    /// </summary>
    private async Task<OnlineMeetingInfo?> LookupMeetingViaChatAsync(
        string chatId,
        CancellationToken cancellationToken)
    {
        try
        {
            var graphClient = await GetGraphClientAsync();

            // Get the chat which should include online meeting info
            var chat = await graphClient.Chats[chatId]
                .GetAsync(config =>
                {
                    config.QueryParameters.Select = new[]
                    {
                        "id", "chatType", "webUrl", "onlineMeetingInfo"
                    };
                }, cancellationToken);

            if (chat?.OnlineMeetingInfo?.JoinWebUrl != null)
            {
                return new OnlineMeetingInfo
                {
                    JoinUrl = chat.OnlineMeetingInfo.JoinWebUrl,
                    MeetingId = chat.OnlineMeetingInfo.CalendarEventId
                };
            }

            _logger.LogDebug("Chat {ChatId} does not have online meeting info", chatId);
            return null;
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError odataEx)
        {
            _logger.LogWarning(
                "Graph API error looking up chat {ChatId}: {Code} - {Message}",
                chatId, odataEx.Error?.Code, odataEx.Error?.Message);
            return null;
        }
    }

    public async Task<MeetingCoordinates?> GetMeetingCoordinatesAsync(
        string meetingId,
        string? chatId = null,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Looking up meeting coordinates for meetingId: {MeetingId}, chatId: {ChatId}",
            meetingId, chatId);

        try
        {
            var graphClient = await GetGraphClientAsync();

            // First, try to get meeting info via chat if provided
            if (!string.IsNullOrEmpty(chatId))
            {
                var chatMeetingInfo = await LookupMeetingViaChatWithCoordinatesAsync(
                    graphClient, chatId, cancellationToken);
                if (chatMeetingInfo != null)
                {
                    return chatMeetingInfo;
                }
            }

            // Decode meeting ID to get thread ID and construct join URL
            var (threadId, error) = DecodeMeetingId(meetingId);
            if (threadId == null)
            {
                _logger.LogWarning("Could not decode meeting ID: {Error}", error);
                return null;
            }

            // Construct the join URL
            var joinUrl = ConstructJoinUrl(threadId, tenantId);

            // Try to look up meeting by join URL using filter
            var coordinates = await LookupMeetingByJoinUrlAsync(
                graphClient, joinUrl, cancellationToken);

            return coordinates;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting meeting coordinates for meetingId: {MeetingId}", meetingId);
            return null;
        }
    }

    /// <summary>
    /// Lookup meeting via chat and extract coordinates from the online meeting.
    /// </summary>
    private async Task<MeetingCoordinates?> LookupMeetingViaChatWithCoordinatesAsync(
        GraphServiceClient graphClient,
        string chatId,
        CancellationToken cancellationToken)
    {
        try
        {
            // Get the chat which should include online meeting info
            var chat = await graphClient.Chats[chatId]
                .GetAsync(config =>
                {
                    config.QueryParameters.Select = new[]
                    {
                        "id", "chatType", "webUrl", "onlineMeetingInfo"
                    };
                }, cancellationToken);

            if (chat?.OnlineMeetingInfo?.JoinWebUrl != null)
            {
                _logger.LogInformation("Found meeting join URL via chat: {JoinUrl}",
                    chat.OnlineMeetingInfo.JoinWebUrl);

                // Now look up the full meeting details to get coordinates
                return await LookupMeetingByJoinUrlAsync(
                    graphClient, chat.OnlineMeetingInfo.JoinWebUrl, cancellationToken);
            }

            _logger.LogDebug("Chat {ChatId} does not have online meeting info", chatId);
            return null;
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError odataEx)
        {
            _logger.LogWarning(
                "Graph API error looking up chat {ChatId}: {Code} - {Message}",
                chatId, odataEx.Error?.Code, odataEx.Error?.Message);
            return null;
        }
    }

    /// <summary>
    /// Look up online meeting by join URL and extract coordinates.
    /// Uses the Graph API filter: communications/onlineMeetings?$filter=JoinWebUrl eq 'url'
    /// </summary>
    private async Task<MeetingCoordinates?> LookupMeetingByJoinUrlAsync(
        GraphServiceClient graphClient,
        string joinUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Looking up meeting by join URL: {JoinUrl}", joinUrl);

            // Use the filter endpoint to find the meeting by join URL
            var meetings = await graphClient.Communications.OnlineMeetings
                .GetAsync(config =>
                {
                    config.QueryParameters.Filter = $"JoinWebUrl eq '{joinUrl}'";
                }, cancellationToken);

            var meeting = meetings?.Value?.FirstOrDefault();
            if (meeting?.JoinMeetingIdSettings != null)
            {
                var joinMeetingId = meeting.JoinMeetingIdSettings.JoinMeetingId;
                var passcode = meeting.JoinMeetingIdSettings.Passcode;

                _logger.LogInformation(
                    "Found meeting coordinates - JoinMeetingId: {JoinMeetingId}, HasPasscode: {HasPasscode}",
                    joinMeetingId, !string.IsNullOrEmpty(passcode));

                return new MeetingCoordinates
                {
                    JoinMeetingId = joinMeetingId,
                    Passcode = passcode
                };
            }

            _logger.LogWarning("No meeting found with join URL: {JoinUrl}", joinUrl);
            return null;
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError odataEx)
        {
            _logger.LogWarning(
                "Graph API error looking up meeting by URL: {Code} - {Message}",
                odataEx.Error?.Code, odataEx.Error?.Message);
            return null;
        }
    }
}
