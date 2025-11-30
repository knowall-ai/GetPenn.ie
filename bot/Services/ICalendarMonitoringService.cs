namespace PennieBot.Services;

/// <summary>
/// Service interface for monitoring a resource account's calendar and auto-joining meetings.
/// </summary>
public interface ICalendarMonitoringService
{
    /// <summary>
    /// Whether the service is enabled and configured.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Whether the service has started monitoring.
    /// </summary>
    bool IsMonitoring { get; }

    /// <summary>
    /// The resource account email being monitored.
    /// </summary>
    string? ResourceAccountEmail { get; }
}
