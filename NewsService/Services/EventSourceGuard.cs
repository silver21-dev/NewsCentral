using System.Diagnostics;

namespace NewsService.Services;

/// <summary>
/// Honours <c>ViewerTasks:RegisterEventSource = false</c>. Writing to a missing source through
/// <see cref="EventLog"/> creates it implicitly, so "never create it" means not writing to it:
/// the ContentChanged notifier skips the write, and the host's EventLog logger provider is not added.
/// </summary>
internal static class EventSourceGuard
{
    /// <summary>True when the setting is absent or not a recognisable "false".</summary>
    public static bool RegistrationAllowed(IConfiguration configuration) =>
        !bool.TryParse(configuration["ViewerTasks:RegisterEventSource"], out var value) || value;

    public static string SourceName(IConfiguration configuration)
    {
        var source = configuration["Logging:EventLog:SourceName"];
        return string.IsNullOrWhiteSpace(source) ? "NewsService" : source;
    }

    /// <summary>Null when it cannot be determined (e.g. access denied) - callers treat that as "do not interfere".</summary>
    public static bool? SourceExists(string source)
    {
        try { return EventLog.SourceExists(source); }
        catch { return null; }
    }

    /// <summary>The Windows operations behind <see cref="EnsureSource"/>, replaceable in tests.</summary>
    internal interface IEventSourceApi
    {
        bool? Exists(string source);
        string? LogOf(string source);
        void Create(string source, string log);
    }

    private sealed class WindowsEventSourceApi : IEventSourceApi
    {
        public bool? Exists(string source) => SourceExists(source);
        public string? LogOf(string source) { try { return EventLog.LogNameFromSourceName(source, "."); } catch { return null; } }
        public void Create(string source, string log) => EventLog.CreateEventSource(new EventSourceCreationData(source, log));
    }

    /// <summary>
    /// Creates the source explicitly at startup, before any write, when registration is allowed and the source
    /// is missing. Without this the source exists only as a side effect of the first write through it - which, with
    /// Logging:EventLog:LogLevel:Default at Error or None, can be the ContentChanged event (2001) itself.
    /// Never throws; a failure (e.g. a non-elevated dev console) is a Warning.
    /// </summary>
    public static void EnsureSource(IConfiguration configuration, ILogger logger, IEventSourceApi? api = null)
    {
        if (!RegistrationAllowed(configuration)) return;
        api ??= new WindowsEventSourceApi();
        var source = SourceName(configuration);
        var log = configuration["Logging:EventLog:LogName"];
        if (string.IsNullOrWhiteSpace(log)) log = "Application";
        try
        {
            if (api.Exists(source) == true)
            {
                var existing = api.LogOf(source);
                if (existing is not null && !string.Equals(existing, log, StringComparison.OrdinalIgnoreCase))
                    logger.LogWarning(
                        "Event source '{Source}' exists under log '{Existing}', not '{Log}' (Logging:EventLog:LogName); " +
                        "the Content Arrived task subscribes to that log name", source, existing, log);
                return;
            }
            api.Create(source, log);
            logger.LogInformation("Created event source '{Source}' in log '{Log}'", source, log);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create the event source '{Source}' in log '{Log}'; register it externally " +
                                  "or the Content Arrived task will not fire", source, log);
        }
    }

    public static string MissingSourceMessage(string source) =>
        $"ViewerTasks:RegisterEventSource is false and the event source '{source}' does not exist: the ContentChanged event " +
        "(2001) will not be attributed to a registered provider, and the NewsViewer Content Arrived task will not fire until " +
        "the source is registered externally (the generated registration script does this).";
}
