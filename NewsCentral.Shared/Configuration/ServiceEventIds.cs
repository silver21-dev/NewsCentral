namespace NewsCentral.Configuration;

/// <summary>
/// Windows EventLog event IDs written by NewsService directly (not through ILogger) because other
/// components key off them — single source of truth for the writer and for any event-triggered
/// task or tooling. Log = Application, Source = NewsService (see docs/packaging.md).
/// ILogger-routed entries carry event ID 0, so these never collide with them.
/// </summary>
public static class ServiceEventIds
{
    /// <summary>
    /// New content was accepted and is fully materialized in the local cache. Information level.
    /// Trigger for the NewsViewer event-triggered launch task.
    /// </summary>
    public const int ContentChanged = 2001;
}
