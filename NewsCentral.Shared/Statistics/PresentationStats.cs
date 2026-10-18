namespace NewsCentral.Statistics;

/// <summary>
/// Aggregate poster statistics for one (team, presentation), stored at
/// {DataPath}\statistics\{teamId}\{presentationId}.json. <c>TeamId</c> is the SOURCE team recorded by
/// NewsViewer (the team that authored the presentation), not the team whose index was consumed.
/// Not signed and not part of the signing contract.
/// </summary>
public sealed class PresentationStats
{
    public int SchemaVersion { get; set; } = 1;

    public string TeamId { get; set; } = string.Empty;

    public string PresentationId { get; set; } = string.Empty;

    /// <summary>Key = yyyy-MM-dd (UTC) of the record's SessionStartTime.</summary>
    public SortedDictionary<string, DayCounts> Days { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// File names already folded into <see cref="Days"/> whose move out of <c>new\</c> is not yet
    /// confirmed. Crash-recovery guard: these are moved on the next run and never counted again.
    /// </summary>
    public List<string> PendingMove { get; set; } = new();

    public DateTime UpdatedUtc { get; set; }
}

public sealed class DayCounts
{
    /// <summary>Poster display sessions (notification records excluded).</summary>
    public int Views { get; set; }

    /// <summary>Poster sessions closed with CloseReason == "UrlLaunch" (the Intranet button).</summary>
    public int Clicks { get; set; }
}
