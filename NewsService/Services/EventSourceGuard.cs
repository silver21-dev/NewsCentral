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

    public static string MissingSourceMessage(string source) =>
        $"ViewerTasks:RegisterEventSource is false and the event source '{source}' does not exist: the ContentChanged event " +
        "(2001) will not be attributed to a registered provider, and the NewsViewer Content Arrived task will not fire until " +
        "the source is registered externally (the generated registration script does this).";
}
