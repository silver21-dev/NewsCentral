using System.Globalization;

namespace NewsCentral.Statistics;

/// <param name="HasData">False only when there is no statistics file at all for the presentation.</param>
public readonly record struct StatsTotals(int Views, int Clicks, bool HasData);

/// <summary>
/// Pure presentation of <see cref="PresentationStats"/> for the Assignments panel. Statistics exist per
/// (source team, presentation), not per target team, so a Published batch shows the presentation's figures
/// since the earliest publish date among its assignments, at UTC day granularity: views earlier on that
/// day are included.
/// </summary>
public static class PosterStatsQuery
{
    /// <summary>
    /// Sums the day buckets dated on or after <paramref name="sinceUtc"/>'s UTC date; null sums every day.
    /// A Local value is converted to UTC; an Unspecified one is taken as UTC (the convention of every
    /// stored timestamp, see DisplayTime). Dates are compared as yyyy-MM-dd strings, the bucket key format.
    /// </summary>
    public static StatsTotals Since(PresentationStats? stats, DateTime? sinceUtc)
    {
        if (stats is null) return new StatsTotals(0, 0, false);

        string? cutoff = null;
        if (sinceUtc is { } since)
        {
            var utc = since.Kind == DateTimeKind.Unspecified ? since : since.ToUniversalTime();
            cutoff = utc.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        int views = 0, clicks = 0;
        foreach (var (day, counts) in stats.Days)
        {
            if (cutoff is not null && string.CompareOrdinal(day, cutoff) < 0) continue;
            views += counts.Views;
            clicks += counts.Clicks;
        }
        return new StatsTotals(views, clicks, true);
    }

    // TODO(i18n)
    public static string Format(StatsTotals t)
    {
        if (!t.HasData) return "No data yet";
        var inv = CultureInfo.InvariantCulture;
        if (t.Views == 0) return "0 views";

        var rate = (t.Clicks * 100.0 / t.Views).ToString("F1", inv);
        return $"{t.Views.ToString("N0", inv)} views · {t.Clicks.ToString("N0", inv)} clicks ({rate}%)";
    }
}
