using System.Text.Json.Serialization;

namespace NewsCentral.Statistics;

public enum StatsRunOutcome { Completed, Stopped, Failed, NotAvailable }

public enum StatsPhase { Recover, Process, Prune }

/// <summary>Contents of {DataPath}\statistics\_run.json — the last statistics run.</summary>
public sealed class StatsRunSummary
{
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public StatsRunOutcome Outcome { get; set; }

    /// <summary>Input files handled: moved to a counted folder, moved to <c>_invalid\</c>, or dropped as duplicates.</summary>
    public int Processed { get; set; }

    /// <summary>Pre-M1.1 name of <see cref="Processed"/>; read-only compatibility so an old _run.json still loads. Never written.</summary>
    [JsonPropertyName("sorted")]
    public int Sorted { set => Processed = value; }

    /// <summary>Poster sessions that produced a view (notification records are moved but not counted).</summary>
    public int Counted { get; set; }

    /// <summary>Files moved to <c>_invalid\</c>.</summary>
    public int Invalid { get; set; }

    /// <summary>Re-uploads of already-counted files, deleted from the input location.</summary>
    public int DuplicatesDropped { get; set; }

    /// <summary>Raw files (counted or invalid) deleted for being older than the retention window.</summary>
    public int Pruned { get; set; }

    public long RecoverMs { get; set; }
    public long ProcessMs { get; set; }
    public long PruneMs { get; set; }

    public string? Message { get; set; }
}

/// <param name="Done">Input files handled so far; within a batch it advances as files are read.</param>
/// <param name="Total">Always null: the inbox is streamed, never counted up front.</param>
public sealed record StatsRunProgress(StatsPhase Phase, int Done, int? Total, string? CurrentItem);

public static class StatisticsDefaults
{
    /// <summary>
    /// How long counted raw session files (and <c>_invalid\</c>) are kept, by LastWriteTime. A constant,
    /// not a setting, for the same reason as NewsService's TelemetryDefaults.RetentionDays: it is data
    /// hygiene, and a configurable 0 would be ambiguous between delete-everything and keep-forever.
    /// </summary>
    public const int RawRetentionMonths = 13;

    /// <summary>Input files per batch: read, saved to the stats files, moved, then PendingMove cleared.</summary>
    public const int BatchSize = 500;

    /// <summary>Concurrent file reads (and moves) within a batch. A constant, not a setting.</summary>
    public const int ReadParallelism = 8;

    /// <summary>
    /// Concurrent moves within a batch. 1 because, measured on a local NTFS disk, parallel renames into one
    /// folder were ~7x SLOWER than sequential ones (directory-lock contention). Revisit only with a measurement
    /// on a real SMB share, where round-trip latency could make a small value worthwhile.
    /// </summary>
    public const int MoveParallelism = 1;
}
