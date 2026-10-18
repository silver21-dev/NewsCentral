using NewsCentral.Statistics;

namespace NewsCentral.Shared.Tests;

public sealed class PosterStatsQueryTests
{
    private static PresentationStats Stats(params (string Day, int V, int C)[] days)
    {
        var s = new PresentationStats();
        foreach (var (d, v, c) in days) s.Days[d] = new DayCounts { Views = v, Clicks = c };
        return s;
    }

    [Fact]
    public void NullStats_HasNoData()
    {
        var t = PosterStatsQuery.Since(null, DateTime.UtcNow);

        Assert.False(t.HasData);
        Assert.Equal("No data yet", PosterStatsQuery.Format(t));
    }

    [Fact]
    public void OnlyDaysOnOrAfterTheUtcDate_AreSummed()
    {
        var s = Stats(("2026-10-14", 100, 10), ("2026-10-15", 5, 1), ("2026-10-16", 7, 2));

        var t = PosterStatsQuery.Since(s, new DateTime(2026, 10, 15, 23, 59, 0, DateTimeKind.Utc));

        Assert.True(t.HasData);
        Assert.Equal(12, t.Views);                                  // the publish day itself is included
        Assert.Equal(3, t.Clicks);
    }

    [Fact]
    public void LocalSinceWithPlusTwoOffset_IsConvertedToUtcBeforeComparing()
    {
        var s = Stats(("2026-10-14", 4, 1), ("2026-10-15", 6, 2));
        // 2026-10-15 01:00 at +02:00 is 2026-10-14 23:00 UTC, so the 14th counts.
        var local = new DateTimeOffset(2026, 10, 15, 1, 0, 0, TimeSpan.FromHours(2)).LocalDateTime;

        var t = PosterStatsQuery.Since(s, local);

        Assert.Equal(10, t.Views);
        Assert.Equal(3, t.Clicks);
    }

    [Fact]
    public void NullSince_SumsEverything()
    {
        var s = Stats(("2026-01-01", 1, 0), ("2026-10-15", 2, 1));

        var t = PosterStatsQuery.Since(s, null);

        Assert.Equal(3, t.Views);
        Assert.Equal(1, t.Clicks);
    }

    [Fact]
    public void Format_GroupsThousands_AndShowsRateToOneDecimal()
    {
        Assert.Equal("1,234 views · 56 clicks (4.5%)", PosterStatsQuery.Format(new StatsTotals(1234, 56, true)));
    }

    [Fact]
    public void Format_DataButNothingSince_IsZeroViews_WithoutRate()
    {
        Assert.Equal("0 views", PosterStatsQuery.Format(new StatsTotals(0, 0, true)));
    }
}
