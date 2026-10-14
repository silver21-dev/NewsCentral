using System.Diagnostics;
using NewsCentral.Configuration;

namespace NewsService.Services;

/// <summary>
/// Signals that a new index was accepted and its content is fully available in the local cache.
/// Implementations must never throw into the sync loop.
/// </summary>
public interface IContentChangedNotifier
{
    void NotifyContentChanged();
}

/// <summary>
/// Writes the dedicated <see cref="ServiceEventIds.ContentChanged"/> Information event straight to
/// the Windows EventLog — deliberately NOT through ILogger, so it is emitted regardless of any
/// configured log level. Source and log come from the same <c>Logging:EventLog</c> settings the
/// installer registers the source with. The message is fixed and carries no user data, team names
/// or paths.
/// </summary>
public sealed class EventLogContentChangedNotifier(
    IConfiguration configuration,
    ILogger<EventLogContentChangedNotifier> logger) : IContentChangedNotifier
{
    internal const string Message = "New content accepted and available in local cache.";

    private bool _warnedMissingSource;

    public void NotifyContentChanged()
    {
        try
        {
            var source = EventSourceGuard.SourceName(configuration);
            // WriteEntry would create a missing source; with registration disabled, that is not ours to do.
            if (!EventSourceGuard.RegistrationAllowed(configuration) && EventSourceGuard.SourceExists(source) == false)
            {
                if (!_warnedMissingSource)
                {
                    _warnedMissingSource = true;
                    logger.LogWarning("{Message}", EventSourceGuard.MissingSourceMessage(source));
                }
                return;
            }
            EventLog.WriteEntry(source, Message, EventLogEntryType.Information, ServiceEventIds.ContentChanged);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write the ContentChanged EventLog event");
        }
    }
}

/// <summary>No-op notifier for callers (and tests) that do not care about the event.</summary>
internal sealed class NullContentChangedNotifier : IContentChangedNotifier
{
    public void NotifyContentChanged() { }
}
