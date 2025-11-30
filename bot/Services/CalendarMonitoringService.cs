using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System.Collections.Concurrent;

namespace PennieBot.Services;

/// <summary>
/// Background service that monitors a resource account's calendar and auto-joins meetings.
/// Polls the calendar at configurable intervals and joins meetings when they start.
/// </summary>
public class CalendarMonitoringService : BackgroundService, ICalendarMonitoringService
{
    private readonly ILogger<CalendarMonitoringService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IGraphCallService _graphCallService;
    private readonly ConcurrentDictionary<string, DateTime> _joinedMeetings = new();
    private GraphServiceClient? _graphClient;
    private string? _resourceAccountEmail;
    private string? _resourceAccountUserId;
    private int _pollingIntervalSeconds;
    private int _joinBeforeMinutes;

    public bool IsEnabled { get; private set; }
    public bool IsMonitoring { get; private set; }
    public string? ResourceAccountEmail => _resourceAccountEmail;

    public CalendarMonitoringService(
        ILogger<CalendarMonitoringService> logger,
        IConfiguration configuration,
        IGraphCallService graphCallService)
    {
        _logger = logger;
        _configuration = configuration;
        _graphCallService = graphCallService;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("CalendarMonitoringService starting...");

        // Load configuration
        var resourceAccountSection = _configuration.GetSection("ResourceAccount");
        _resourceAccountEmail = resourceAccountSection["Email"];
        _resourceAccountUserId = resourceAccountSection["UserId"];
        _pollingIntervalSeconds = int.TryParse(resourceAccountSection["CalendarPollingIntervalSeconds"], out var interval) ? interval : 60;
        _joinBeforeMinutes = int.TryParse(resourceAccountSection["JoinBeforeMinutes"], out var joinBefore) ? joinBefore : 1;

        // Validate configuration
        if (string.IsNullOrEmpty(_resourceAccountEmail) || string.IsNullOrEmpty(_resourceAccountUserId))
        {
            _logger.LogWarning(
                "CalendarMonitoringService disabled: ResourceAccount.Email or ResourceAccount.UserId not configured. " +
                "Configure these in appsettings.json to enable auto-joining meetings.");
            IsEnabled = false;
            return;
        }

        // Get bot credentials for Graph API authentication
        var appId = _configuration["MicrosoftAppId"];
        var appSecret = _configuration["MicrosoftAppPassword"];
        var tenantId = _configuration["MicrosoftAppTenantId"];

        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(appSecret) || string.IsNullOrEmpty(tenantId))
        {
            _logger.LogWarning(
                "CalendarMonitoringService disabled: Bot credentials not configured. " +
                "MicrosoftAppId, MicrosoftAppPassword, and MicrosoftAppTenantId required.");
            IsEnabled = false;
            return;
        }

        try
        {
            // Create Graph client with client credentials (application permission)
            var credential = new ClientSecretCredential(tenantId, appId, appSecret);
            _graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });

            IsEnabled = true;
            _logger.LogInformation(
                "CalendarMonitoringService enabled. Monitoring calendar for {Email} (User ID: {UserId}). " +
                "Polling every {Interval} seconds, joining {JoinBefore} minute(s) before start.",
                _resourceAccountEmail, _resourceAccountUserId, _pollingIntervalSeconds, _joinBeforeMinutes);

            await base.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize CalendarMonitoringService");
            IsEnabled = false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled || _graphClient == null)
        {
            _logger.LogInformation("CalendarMonitoringService not executing (disabled or not configured)");
            return;
        }

        IsMonitoring = true;
        _logger.LogInformation("CalendarMonitoringService executing - starting calendar polling loop");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckCalendarAndJoinMeetingsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in calendar monitoring loop");
            }

            // Wait for next poll
            await Task.Delay(TimeSpan.FromSeconds(_pollingIntervalSeconds), stoppingToken);
        }

        IsMonitoring = false;
        _logger.LogInformation("CalendarMonitoringService stopped");
    }

    private async Task CheckCalendarAndJoinMeetingsAsync(CancellationToken cancellationToken)
    {
        if (_graphClient == null || string.IsNullOrEmpty(_resourceAccountUserId))
            return;

        try
        {
            var now = DateTime.UtcNow;
            var windowStart = now.AddMinutes(-5); // Check meetings that may have just started
            var windowEnd = now.AddMinutes(15);   // Check meetings starting in next 15 minutes

            _logger.LogDebug(
                "Checking calendar for {Email} from {Start:HH:mm} to {End:HH:mm} UTC",
                _resourceAccountEmail, windowStart, windowEnd);

            // Query calendar events in the time window
            // Uses Calendars.Read application permission
            var calendarView = await _graphClient.Users[_resourceAccountUserId].Calendar.CalendarView
                .GetAsync(config =>
                {
                    config.QueryParameters.StartDateTime = windowStart.ToString("o");
                    config.QueryParameters.EndDateTime = windowEnd.ToString("o");
                    config.QueryParameters.Select = new[]
                    {
                        "id", "subject", "start", "end", "isOnlineMeeting",
                        "onlineMeeting", "onlineMeetingUrl", "location"
                    };
                    config.QueryParameters.Orderby = new[] { "start/dateTime" };
                }, cancellationToken);

            var events = calendarView?.Value ?? new List<Event>();

            if (events.Count == 0)
            {
                _logger.LogDebug("No upcoming meetings found in the next 15 minutes");
                return;
            }

            _logger.LogInformation("Found {Count} event(s) in calendar window", events.Count);

            foreach (var evt in events)
            {
                await ProcessCalendarEventAsync(evt, now, cancellationToken);
            }

            // Clean up old joined meetings (older than 2 hours)
            var cutoff = now.AddHours(-2);
            foreach (var meetingId in _joinedMeetings.Keys.ToList())
            {
                if (_joinedMeetings.TryGetValue(meetingId, out var joinedAt) && joinedAt < cutoff)
                {
                    _joinedMeetings.TryRemove(meetingId, out _);
                }
            }
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError odataEx)
        {
            _logger.LogError(
                "Graph API error checking calendar: {Code} - {Message}",
                odataEx.Error?.Code, odataEx.Error?.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking calendar");
        }
    }

    private async Task ProcessCalendarEventAsync(Event evt, DateTime now, CancellationToken cancellationToken)
    {
        var eventId = evt.Id ?? "";
        var subject = evt.Subject ?? "(No subject)";

        // Skip if not an online meeting
        if (evt.IsOnlineMeeting != true)
        {
            _logger.LogDebug("Skipping non-online event: {Subject}", subject);
            return;
        }

        // Get meeting join URL
        var joinUrl = evt.OnlineMeeting?.JoinUrl ?? evt.OnlineMeetingUrl;
        if (string.IsNullOrEmpty(joinUrl))
        {
            _logger.LogDebug("Skipping event without join URL: {Subject}", subject);
            return;
        }

        // Parse start time
        var startStr = evt.Start?.DateTime;
        if (string.IsNullOrEmpty(startStr) || !DateTime.TryParse(startStr, out var startTime))
        {
            _logger.LogWarning("Could not parse start time for event: {Subject}", subject);
            return;
        }

        // Convert to UTC if necessary (Graph returns in event's timezone)
        if (evt.Start?.TimeZone == "UTC" || evt.Start?.TimeZone == "Etc/UTC")
        {
            // Already UTC
        }
        else
        {
            // Assume UTC for simplicity (Graph often returns in UTC)
            // In production, convert from the specified timezone
        }

        // Calculate when to join (X minutes before start)
        var joinTime = startTime.AddMinutes(-_joinBeforeMinutes);
        var timeTillJoin = joinTime - now;
        var timeTillStart = startTime - now;

        _logger.LogDebug(
            "Event: {Subject} starts at {Start:HH:mm} UTC. Time till join: {TillJoin:mm\\:ss}, till start: {TillStart:mm\\:ss}",
            subject, startTime, timeTillJoin, timeTillStart);

        // Check if we should join now
        // Join if: join time has passed AND meeting hasn't ended AND we haven't already joined
        var endStr = evt.End?.DateTime;
        DateTime? endTime = null;
        if (!string.IsNullOrEmpty(endStr) && DateTime.TryParse(endStr, out var parsedEnd))
        {
            endTime = parsedEnd;
        }

        var shouldJoin = timeTillJoin <= TimeSpan.Zero &&
                         (endTime == null || now < endTime) &&
                         !_joinedMeetings.ContainsKey(eventId) &&
                         !_graphCallService.IsInMeeting(eventId);

        if (shouldJoin)
        {
            _logger.LogInformation(
                "JOINING meeting: {Subject} (starts at {Start:HH:mm} UTC, join URL: {Url})",
                subject, startTime, joinUrl);

            try
            {
                // Mark as joined before actually joining to prevent duplicate attempts
                _joinedMeetings[eventId] = now;

                // Join the meeting via GraphCallService
                await _graphCallService.JoinMeetingAsync(
                    joinUrl,
                    eventId,
                    async audioData =>
                    {
                        // Audio callback - this is where transcription would happen
                        _logger.LogDebug("Received {Bytes} bytes of audio from meeting {Id}", audioData.Length, eventId);
                    },
                    cancellationToken);

                _logger.LogInformation("Successfully joined meeting: {Subject}", subject);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to join meeting: {Subject}", subject);
                // Remove from joined list so we can retry
                _joinedMeetings.TryRemove(eventId, out _);
            }
        }
        else if (!_joinedMeetings.ContainsKey(eventId) && timeTillJoin > TimeSpan.Zero)
        {
            _logger.LogDebug(
                "Will join meeting '{Subject}' in {Minutes:F1} minutes",
                subject, timeTillJoin.TotalMinutes);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("CalendarMonitoringService stopping...");
        IsMonitoring = false;
        await base.StopAsync(cancellationToken);
    }
}
