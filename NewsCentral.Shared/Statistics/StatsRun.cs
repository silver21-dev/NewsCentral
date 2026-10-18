namespace NewsCentral.Statistics;

public enum StatsRunOutcome { Completed, Stopped, Failed, NotAvailable }

public enum StatsPhase { Sort, Count, Prune }

/// <summary>Contents of {DataPath}\statistics\_run.json — the last statistics run.</summary>
public sealed class StatsRunSummary
{
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public StatsRunOutcome Outcome { get; set; }

    /// <summary>Inbox files moved into a presentation's <c>new\</c> folder.</summary>
    public int Sorted { get; set; }

    /// <summary>Poster sessions folded into the statistics (notification records are moved but not counted).</summary>
    public int Counted { get; set; }

    /// <summary>Files moved to <c>_invalid\</c>.</summary>
    public int Invalid { get; set; }

    /// <summary>Re-uploads of already-counted files, deleted from the inbox.</summary>
    public int DuplicatesDropped { get; set; }

    /// <summary>Raw files (counted or invalid) deleted for being older than the retention window.</summary>
    public int Pruned { get; set; }

    public string? Message { get; set; }
}

/// <param name="Total">Null when unknown (the sort phase streams the inbox without counting it first).</param>
public sealed record StatsRunProgress(StatsPhase Phase, int Done, int? Total, string? CurrentItem);

public static class StatisticsDefaults
{
    /// <summary>
    /// How long counted raw session files (and <c>_invalid\</c>) are kept, by LastWriteTime. A constant,
    /// not a setting, for the same reason as NewsService's TelemetryDefaults.RetentionDays: it is data
    /// hygiene, and a configurable 0 would be ambiguous between delete-everything and keep-forever.
    /// </summary>
    public const int RawRetentionMonths = 13;
}
