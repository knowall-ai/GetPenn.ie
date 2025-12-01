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
    ///
    /// IMPORTANT: Uses OrganizerMeetingInfo with ChatInfo, which is the correct approach
    /// for joining meetings via Graph API. TokenMeetingInfo is NOT designed to be manually
    /// constructed - it's only meant to be received from incoming call notifications.
    /// See: https://github.com/microsoftgraph/microsoft-graph-comms-samples
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
                "Parsed meeting info: ThreadId={ThreadId}, TenantId={TenantId}, OrganizerId={OrganizerId}",
                joinInfo.ThreadId, joinInfo.TenantId, joinInfo.OrganizerId);

            // Use OrganizerMeetingInfo with ChatInfo - this is the recommended approach
            // OrganizerMeetingInfo requires the actual organizer's Azure AD Object ID (not the bot's app ID)
            _logger.LogInformation(
                "Using OrganizerMeetingInfo approach. Meeting tenant: {MeetingTenant}, Bot tenant: {BotTenant}",
                joinInfo.TenantId ?? "(unknown)", _tenantId ?? "(unknown)");

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

            // Use OrganizerMeetingInfo with the organizer ID from the join URL context
            // The Oid in the context is the organizer's Azure AD Object ID
            var tenantId = joinInfo.TenantId ?? _tenantId ?? "";
            var organizerId = joinInfo.OrganizerId;

            // If we don't have an organizer ID from the URL, try to look it up
            if (string.IsNullOrEmpty(organizerId))
            {
                _logger.LogInformation("No organizer ID in URL context, attempting lookup...");
                organizerId = await LookupMeetingOrganizerAsync(meetingJoinUrl, joinInfo.ThreadId, cancellationToken);
            }

            if (string.IsNullOrEmpty(organizerId))
            {
                _logger.LogWarning("Could not determine organizer ID. Meeting join may fail.");
                throw new InvalidOperationException(
                    "Cannot join meeting: Unable to determine the meeting organizer. " +
                    "The meeting join URL must include the organizer ID (Oid) in the context parameter.");
            }

            _logger.LogInformation(
                "Creating call with OrganizerMeetingInfo, tenant={TenantId}, organizer={OrganizerId}",
                tenantId, organizerId);

            var call = new Call
            {
                Direction = CallDirection.Outgoing,
                CallbackUri = callbackUrl,
                TenantId = tenantId,
                MediaConfig = mediaConfig,
                RequestedModalities = new List<Modality?>
                {
                    Modality.Audio
                },
                // ChatInfo identifies the meeting thread
                ChatInfo = new ChatInfo
                {
                    OdataType = "#microsoft.graph.chatInfo",
                    ThreadId = joinInfo.ThreadId,
                    MessageId = joinInfo.MessageId
                },
                // OrganizerMeetingInfo with the actual organizer's AAD Object ID
                MeetingInfo = new OrganizerMeetingInfo
                {
                    OdataType = "#microsoft.graph.organizerMeetingInfo",
                    Organizer = new IdentitySet
                    {
                        User = new Identity
                        {
                            Id = organizerId,
                            AdditionalData = new Dictionary<string, object>
                            {
                                { "tenantId", tenantId }
                            }
                        }
                    },
                    AdditionalData = new Dictionary<string, object>
                    {
                        { "allowConversationWithoutHost", true }
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

            // Register AudioSocket with event handlers for audio reception
            if (audioSocket != null)
            {
                RegisterAudioSocket(callId, audioSocket);
            }

            _logger.LogInformation(
                "Successfully joined meeting {MeetingId} with call ID {CallId}. State: {State}",
                meetingId, callId, createdCall.State);

            // Send a chat message to announce Pennie has joined
            // Try threadId from URL first, then fall back to Graph response
            var threadId = !string.IsNullOrEmpty(joinInfo.ThreadId)
                ? joinInfo.ThreadId
                : createdCall.ChatInfo?.ThreadId;

            _logger.LogInformation(
                "Chat notification: URL ThreadId={UrlThreadId}, Graph ThreadId={GraphThreadId}, Using={FinalThreadId}",
                joinInfo.ThreadId ?? "(empty)", createdCall.ChatInfo?.ThreadId ?? "(null)", threadId ?? "(none)");

            if (!string.IsNullOrEmpty(threadId))
            {
                await SendChatMessageAsync(
                    threadId,
                    "Hi! I'm Pennie the Prepper. I'm now listening to this meeting and will help capture requirements for your Azure DevOps backlog.",
                    cancellationToken);
            }
            else
            {
                _logger.LogWarning("Cannot send chat notification: No thread ID available from URL or Graph response");
            }
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError odataEx)
        {
            // Extract detailed error information from ODataError
            var errorCode = odataEx.Error?.Code ?? "Unknown";
            var errorMessage = odataEx.Error?.Message ?? "No message";
            var additionalData = odataEx.Error?.InnerError?.AdditionalData;
            var innerCode = additionalData != null && additionalData.TryGetValue("code", out var codeVal) ? codeVal?.ToString() : null;
            var innerMessage = additionalData != null && additionalData.TryGetValue("message", out var msgVal) ? msgVal?.ToString() : null;
            var requestId = additionalData != null && additionalData.TryGetValue("request-id", out var reqIdVal) ? reqIdVal?.ToString() : null;
            var date = additionalData != null && additionalData.TryGetValue("date", out var dateVal) ? dateVal?.ToString() : null;

            _logger.LogError(odataEx,
                "Graph API error joining meeting {MeetingId}: Code={Code}, Message={Message}, " +
                "InnerCode={InnerCode}, InnerMessage={InnerMessage}, RequestId={RequestId}, Date={Date}",
                meetingId, errorCode, errorMessage, innerCode, innerMessage, requestId, date);

            // Log the full error details for debugging
            if (odataEx.Error?.InnerError?.AdditionalData != null)
            {
                foreach (var kvp in odataEx.Error.InnerError.AdditionalData)
                {
                    _logger.LogError("  Graph error detail: {Key} = {Value}", kvp.Key, kvp.Value);
                }
            }

            // Build a more descriptive error message
            var detailedMessage = $"Failed to join meeting: {errorCode} - {errorMessage}";
            if (!string.IsNullOrEmpty(innerCode) || !string.IsNullOrEmpty(innerMessage))
            {
                detailedMessage += $" (Inner: {innerCode} - {innerMessage})";
            }
            if (!string.IsNullOrEmpty(requestId))
            {
                detailedMessage += $" [RequestId: {requestId}]";
            }

            throw new InvalidOperationException(detailedMessage, odataEx);
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

            // Register AudioSocket with event handlers for audio reception
            if (audioSocket != null)
            {
                RegisterAudioSocket(callId, audioSocket);
            }

            _logger.LogInformation(
                "Successfully joined meeting by ID {MeetingNumber} with call ID {CallId}. State: {State}",
                meetingIdNumber, callId, createdCall.State);

            // Send a chat message to announce Pennie has joined (if we can get the thread ID from the response)
            var threadId = createdCall.ChatInfo?.ThreadId;
            if (!string.IsNullOrEmpty(threadId))
            {
                await SendChatMessageAsync(
                    threadId,
                    "Hi! I'm Pennie the Prepper. I'm now listening to this meeting and will help capture requirements for your Azure DevOps backlog.",
                    cancellationToken);
            }
            else
            {
                _logger.LogInformation("No thread ID in call response - skipping chat notification");
            }
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
            _logger.LogInformation("Leaving meeting {MeetingId}. Active calls: {ActiveCalls}",
                meetingId, string.Join(", ", _activeCalls.Keys));

            // Remove callbacks
            _audioCallbacks.TryRemove(meetingId, out _);

            if (_activeCalls.TryRemove(meetingId, out var callId))
            {
                _callIdToMeetingId.TryRemove(callId, out _);

                // Clean up audio socket
                UnregisterAudioSocket(callId);

                // Hang up the call via Graph API
                if (_graphClient != null)
                {
                    try
                    {
                        _logger.LogInformation("Hanging up call {CallId} via Graph API...", callId);
                        await _graphClient.Communications.Calls[callId]
                            .DeleteAsync();
                        _logger.LogInformation("Successfully hung up call {CallId}", callId);
                    }
                    catch (Microsoft.Graph.Models.ODataErrors.ODataError odataEx)
                    {
                        _logger.LogWarning(
                            "Graph API error hanging up call {CallId}: {Code} - {Message}",
                            callId, odataEx.Error?.Code, odataEx.Error?.Message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error hanging up call {CallId}", callId);
                    }
                }

                _logger.LogInformation("Cleaned up call {CallId} for meeting {MeetingId}", callId, meetingId);
            }
            else
            {
                _logger.LogWarning(
                    "Meeting {MeetingId} not found in active calls. Cannot leave. Active meetings: {ActiveMeetings}",
                    meetingId, string.Join(", ", _activeCalls.Keys));
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
    /// Send a message to the meeting chat.
    /// Uses the Microsoft Graph API POST /chats/{chatId}/messages endpoint.
    /// </summary>
    public async Task SendChatMessageAsync(string threadId, string message, CancellationToken cancellationToken = default)
    {
        if (_graphClient == null)
        {
            _logger.LogWarning("Cannot send chat message: Graph client not initialized");
            return;
        }

        if (string.IsNullOrEmpty(threadId))
        {
            _logger.LogWarning("Cannot send chat message: Thread ID is empty");
            return;
        }

        try
        {
            _logger.LogInformation("Sending chat message to thread {ThreadId}", threadId);

            var chatMessage = new ChatMessage
            {
                Body = new ItemBody
                {
                    ContentType = BodyType.Text,
                    Content = message
                }
            };

            await _graphClient.Chats[threadId].Messages
                .PostAsync(chatMessage, cancellationToken: cancellationToken);

            _logger.LogInformation("Successfully sent chat message to meeting");
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError odataEx)
        {
            // Log but don't throw - chat message is optional, don't fail the meeting join
            _logger.LogWarning(
                "Failed to send chat message (Graph error): {Code} - {Message}",
                odataEx.Error?.Code, odataEx.Error?.Message);
        }
        catch (Exception ex)
        {
            // Log but don't throw - chat message is optional
            _logger.LogWarning(ex, "Failed to send chat message to thread {ThreadId}", threadId);
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
    /// Look up the meeting organizer's AAD Object ID via Graph API.
    /// Uses the onlineMeetings endpoint with a filter on joinWebUrl.
    /// </summary>
    /// <param name="joinUrl">The Teams meeting join URL</param>
    /// <param name="threadId">The meeting thread ID (as fallback for chat lookup)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The organizer's AAD Object ID, or null if not found</returns>
    private async Task<string?> LookupMeetingOrganizerAsync(
        string joinUrl,
        string threadId,
        CancellationToken cancellationToken)
    {
        if (_graphClient == null)
        {
            _logger.LogWarning("Graph client not available for meeting organizer lookup");
            return null;
        }

        try
        {
            // Method 1: Try to look up via onlineMeetings endpoint with joinWebUrl filter
            // This requires OnlineMeetings.Read.All permission
            _logger.LogInformation("Looking up meeting organizer via onlineMeetings API...");

            // URL-encode the join URL for the filter query
            var encodedUrl = Uri.EscapeDataString(joinUrl);

            try
            {
                // Use the /communications/onlineMeetings endpoint with filter
                // Note: This may require different permissions or not work for all meeting types
                var meetings = await _graphClient.Communications.OnlineMeetings
                    .GetAsync(config =>
                    {
                        config.QueryParameters.Filter = $"joinWebUrl eq '{joinUrl}'";
                        config.QueryParameters.Select = new[] { "id", "subject", "participants" };
                    }, cancellationToken);

                if (meetings?.Value?.Count > 0)
                {
                    var meeting = meetings.Value[0];
                    var organizerId = meeting.Participants?.Organizer?.Identity?.User?.Id;

                    if (!string.IsNullOrEmpty(organizerId))
                    {
                        _logger.LogInformation(
                            "Found organizer via onlineMeetings API: {OrganizerId} (meeting: {Subject})",
                            organizerId, meeting.Subject);
                        return organizerId;
                    }
                }

                _logger.LogInformation("No meeting found via onlineMeetings API filter");
            }
            catch (Microsoft.Graph.Models.ODataErrors.ODataError odataEx)
            {
                _logger.LogWarning(
                    "OnlineMeetings API lookup failed (may lack permission): {Code} - {Message}",
                    odataEx.Error?.Code, odataEx.Error?.Message);
            }

            // Method 2: Try to look up via resource account calendar events
            // This requires Calendars.Read permission (which we have)
            var resourceAccountUserId = _configuration["ResourceAccount:UserId"];
            if (!string.IsNullOrEmpty(resourceAccountUserId) && !string.IsNullOrEmpty(threadId))
            {
                _logger.LogInformation("Attempting calendar lookup for thread: {ThreadId}", threadId);

                try
                {
                    var now = DateTime.UtcNow;
                    var windowStart = now.AddHours(-2);  // Look back 2 hours
                    var windowEnd = now.AddHours(24);    // Look ahead 24 hours

                    var calendarView = await _graphClient.Users[resourceAccountUserId].Calendar.CalendarView
                        .GetAsync(config =>
                        {
                            config.QueryParameters.StartDateTime = windowStart.ToString("o");
                            config.QueryParameters.EndDateTime = windowEnd.ToString("o");
                            config.QueryParameters.Select = new[]
                            {
                                "id", "subject", "start", "end", "isOnlineMeeting",
                                "onlineMeeting", "onlineMeetingUrl"
                            };
                        }, cancellationToken);

                    var events = calendarView?.Value ?? new List<Event>();
                    _logger.LogInformation("Found {Count} calendar events to search for matching meeting", events.Count);

                    // Find the event that matches our thread ID
                    foreach (var evt in events)
                    {
                        if (evt.IsOnlineMeeting != true) continue;

                        var calendarJoinUrl = evt.OnlineMeeting?.JoinUrl ?? evt.OnlineMeetingUrl;
                        if (string.IsNullOrEmpty(calendarJoinUrl)) continue;

                        // Check if this join URL contains our thread ID
                        // The thread ID is URL-encoded in the join URL
                        var urlEncodedThreadId = System.Web.HttpUtility.UrlEncode(threadId);
                        if (calendarJoinUrl.Contains(threadId) || calendarJoinUrl.Contains(urlEncodedThreadId))
                        {
                            _logger.LogInformation(
                                "Found matching calendar event: {Subject} with join URL: {Url}",
                                evt.Subject, calendarJoinUrl);

                            // Parse the calendar's join URL to get the organizer ID
                            var calendarJoinInfo = ParseMeetingJoinUrl(calendarJoinUrl);
                            if (!string.IsNullOrEmpty(calendarJoinInfo.OrganizerId))
                            {
                                _logger.LogInformation(
                                    "Found organizer ID from calendar event: {OrganizerId}",
                                    calendarJoinInfo.OrganizerId);
                                return calendarJoinInfo.OrganizerId;
                            }
                            else
                            {
                                _logger.LogWarning("Calendar join URL doesn't contain organizer ID (Oid)");
                            }
                        }
                    }

                    _logger.LogInformation("No matching calendar event found for thread: {ThreadId}", threadId);
                }
                catch (Microsoft.Graph.Models.ODataErrors.ODataError odataEx)
                {
                    _logger.LogWarning(
                        "Calendar lookup failed: {Code} - {Message}",
                        odataEx.Error?.Code, odataEx.Error?.Message);
                }
            }
            else
            {
                _logger.LogDebug("Calendar lookup skipped: ResourceAccount:UserId not configured or no thread ID");
            }

            // Method 3: Try to look up via chat if we have a thread ID
            // This requires Chat.Read permission
            if (!string.IsNullOrEmpty(threadId) && threadId.Contains("@thread"))
            {
                _logger.LogInformation("Attempting chat lookup for thread: {ThreadId}", threadId);

                try
                {
                    var chat = await _graphClient.Chats[threadId]
                        .GetAsync(config =>
                        {
                            config.QueryParameters.Select = new[] { "id", "onlineMeetingInfo" };
                        }, cancellationToken);

                    // The chat's OnlineMeetingInfo might contain the organizer
                    if (chat?.OnlineMeetingInfo != null)
                    {
                        _logger.LogInformation("Found chat with onlineMeetingInfo, but organizer ID extraction not available via this method");
                        // Note: OnlineMeetingInfo on Chat doesn't directly expose organizer ID
                        // But it confirms we have the right meeting
                    }
                }
                catch (Microsoft.Graph.Models.ODataErrors.ODataError odataEx)
                {
                    _logger.LogWarning(
                        "Chat lookup failed (may lack permission): {Code} - {Message}",
                        odataEx.Error?.Code, odataEx.Error?.Message);
                }
            }

            // Method 4: For ad-hoc calls not in any calendar, try using the bot's app ID as fallback
            // This is a last resort for group calls where someone added Pennie directly
            // The bot's service principal has Calls.JoinGroupCall.All permission
            if (!string.IsNullOrEmpty(_appId))
            {
                _logger.LogWarning(
                    "Could not find organizer via calendar or API lookups. " +
                    "Trying bot app ID as fallback organizer (for ad-hoc calls): {AppId}", _appId);
                return _appId;
            }

            _logger.LogWarning(
                "Could not determine organizer ID via Graph API and no fallback available. " +
                "Ensure ResourceAccount:UserId is configured or the meeting is accessible.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error looking up meeting organizer via Graph API");
            return null;
        }
    }

    /// <summary>
    /// Create a meeting token from the join URL for use with TokenMeetingInfo.
    /// According to Microsoft's graph-comms-samples, the token uses URL-safe Base64 encoding.
    /// HttpServerUtility.UrlTokenEncode is equivalent to Base64Url with a trailing digit for padding count.
    /// See: https://github.com/microsoftgraph/microsoft-graph-comms-samples
    /// NOTE: This method is kept for backwards compatibility but TokenMeetingInfo is not recommended.
    /// Use OrganizerMeetingInfo with ChatInfo instead.
    /// </summary>
    private string CreateMeetingToken(string joinUrl)
    {
        try
        {
            // URL-safe Base64 encoding as used in Microsoft's samples:
            // 1. Convert to bytes
            // 2. Base64 encode
            // 3. Replace + with -, / with _
            // 4. Remove trailing = padding and append a digit indicating count of removed padding
            var bytes = System.Text.Encoding.UTF8.GetBytes(joinUrl);
            var base64 = Convert.ToBase64String(bytes);

            // Count and remove padding
            var paddingCount = 0;
            while (base64.EndsWith("="))
            {
                base64 = base64.Substring(0, base64.Length - 1);
                paddingCount++;
            }

            // Replace URL-unsafe characters
            var urlSafeBase64 = base64.Replace('+', '-').Replace('/', '_');

            // Append padding count digit (as UrlTokenEncode does)
            var token = urlSafeBase64 + paddingCount.ToString();

            _logger.LogInformation("Created meeting token from URL: {TokenLength} chars (URL-safe Base64)", token.Length);
            _logger.LogDebug("Meeting token (URL-safe Base64): {Token}", token);

            return token;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create meeting token from URL: {Url}", joinUrl);
            throw;
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

        // Track audio frame count for logging
        var frameCount = 0;
        var lastLogTime = DateTime.UtcNow;

        // Subscribe to audio events
        socket.AudioMediaReceived += async (sender, args) =>
        {
            try
            {
                // Extract audio data from the buffer
                var buffer = args.Buffer;
                frameCount++;
                var now = DateTime.UtcNow;

                // With ReceiveUnmixedMeetingAudio=true, audio is in UnmixedAudioBuffers (per-speaker)
                // buffer.Data is empty/zeros in unmixed mode
                var unmixedBuffers = buffer.UnmixedAudioBuffers;

                // Log audio reception every 5 seconds (avoid flooding logs at 50 fps)
                if ((now - lastLogTime).TotalSeconds >= 5)
                {
                    _logger.LogInformation(
                        "AUDIO: Received {FrameCount} frames in last 5s for call {CallId} (unmixed buffers: {UnmixedCount})",
                        frameCount, callId, unmixedBuffers?.Length ?? 0);
                    frameCount = 0;
                    lastLogTime = now;
                }

                // Process unmixed audio buffers (one per active speaker)
                if (unmixedBuffers != null && unmixedBuffers.Length > 0)
                {
                    foreach (var unmixedBuffer in unmixedBuffers)
                    {
                        if (unmixedBuffer.Length > 0)
                        {
                            var audioData = new byte[unmixedBuffer.Length];
                            Marshal.Copy(unmixedBuffer.Data, audioData, 0, (int)unmixedBuffer.Length);

                            // Log speaker info occasionally
                            if ((now - lastLogTime).TotalSeconds < 0.1) // Only on first frame after log
                            {
                                _logger.LogDebug(
                                    "UNMIXED-AUDIO: Speaker={SpeakerId}, Length={Length} for call {CallId}",
                                    unmixedBuffer.ActiveSpeakerId, unmixedBuffer.Length, callId);
                            }

                            await HandleAudioFrameAsync(callId, audioData);
                        }
                    }
                }
                // Fallback: If no unmixed buffers, try the mixed buffer (shouldn't happen)
                else if (buffer?.Data != null && buffer.Data != IntPtr.Zero && buffer.Length > 0)
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
