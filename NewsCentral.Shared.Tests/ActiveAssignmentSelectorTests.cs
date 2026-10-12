using NewsCentral.Models.IndexFile;
using Xunit;

namespace NewsCentral.Shared.Tests;

/// <summary>
/// Covers ActiveAssignmentSelector — the pure active-window/day-of-week filter plus
/// "newest by PresentationLastModified" pick used by NewsViewer (poster + wallpaper) and mirrored
/// from NewsService's lock-screen selection. Exercised here with the IsWallpaper predicate.
/// </summary>
public sealed class ActiveAssignmentSelectorTests
{
    // Fixed instant so day-of-week mapping is deterministic.
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0);

    private static PublishedAssignmentIndex Make(
        string id, DateTime lastModified, bool isWallpaper, bool isNewsOfWeek = false,
        DateTime? start = null, DateTime? end = null, string days = "1,2,3,4,5,6,7") => new()
    {
        PresentationId           = id,
        PresentationLastModified = lastModified,
        ScheduleStart            = start ?? Now.AddDays(-1),
        ScheduleEnd              = end   ?? Now.AddDays(1),
        DaysOfWeek               = days,
        DisplayTypes             = new DisplayTypeInfo { IsWallpaper = isWallpaper, IsNewsOfWeek = isNewsOfWeek },
    };

    [Fact]
    public void PickNewestActive_ReturnsNewestMatching()
    {
        var older = Make("old", Now.AddDays(-3), isWallpaper: true);
        var newer = Make("new", Now.AddHours(-1), isWallpaper: true);

        var result = ActiveAssignmentSelector.PickNewestActive(
            new[] { older, newer }, Now, a => a.DisplayTypes.IsWallpaper);

        Assert.Equal("new", result?.PresentationId);
    }

    [Fact]
    public void PickNewestActive_ExcludesNonMatchingPredicate()
    {
        var wp  = Make("wp",  Now.AddDays(-2), isWallpaper: true);
        var not = Make("not", Now,             isWallpaper: false);   // newer, but not a wallpaper

        var result = ActiveAssignmentSelector.PickNewestActive(
            new[] { wp, not }, Now, a => a.DisplayTypes.IsWallpaper);

        Assert.Equal("wp", result?.PresentationId);
    }

    [Fact]
    public void PickNewestActive_ExcludesOutOfWindow()
    {
        var future = Make("future", Now, true, start: Now.AddDays(1),  end: Now.AddDays(2));
        var past   = Make("past",   Now, true, start: Now.AddDays(-2), end: Now.AddDays(-1));

        var result = ActiveAssignmentSelector.PickNewestActive(
            new[] { future, past }, Now, _ => true);

        Assert.Null(result);
    }

    [Fact]
    public void PickNewestActive_ExcludesWrongDayOfWeek()
    {
        var todayNum = Now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)Now.DayOfWeek;
        var otherDay = (todayNum == 1 ? 2 : 1).ToString();

        var a = Make("a", Now, true, days: otherDay);

        Assert.Null(ActiveAssignmentSelector.PickNewestActive(new[] { a }, Now, _ => true));
    }

    [Fact]
    public void PickNewestActive_Empty_ReturnsNull()
    {
        Assert.Null(ActiveAssignmentSelector.PickNewestActive(
            Array.Empty<PublishedAssignmentIndex>(), Now, _ => true));
    }

    [Fact]
    public void IsActive_TrueWithinWindowAndDay()
    {
        Assert.True(ActiveAssignmentSelector.IsActive(Make("a", Now, true), Now));
    }

    [Fact]
    public void IsActive_FalseAfterScheduleEnd()
    {
        var a = Make("a", Now, true, start: Now.AddDays(-2), end: Now.AddSeconds(-1));
        Assert.False(ActiveAssignmentSelector.IsActive(a, Now));
    }

    // ── Display (IsNewsOfWeek) vs wallpaper (IsWallpaper) predicate independence ──

    [Fact]
    public void NewsOfWeekPredicate_SkipsNonNewsOfWeek()
    {
        var a = Make("k03", Now, isWallpaper: true, isNewsOfWeek: false);   // wallpaper-only

        Assert.Null(ActiveAssignmentSelector.PickNewestActive(
            new[] { a }, Now, x => x.DisplayTypes.IsNewsOfWeek));
    }

    [Fact]
    public void NewsOfWeekPredicate_SelectsNewsOfWeek()
    {
        var a = Make("now", Now, isWallpaper: false, isNewsOfWeek: true);

        var result = ActiveAssignmentSelector.PickNewestActive(
            new[] { a }, Now, x => x.DisplayTypes.IsNewsOfWeek);

        Assert.Equal("now", result?.PresentationId);
    }

    [Fact]
    public void WallpaperPredicate_SelectsK03_WhileNewsOfWeekSkipsIt()
    {
        // K03: IsWallpaper + IsLogonScreen, IsNewsOfWeek = false. The wallpaper selection picks it;
        // the display (News-of-the-Week) selection must not — the two predicates are independent.
        var k03 = Make("k03", Now, isWallpaper: true, isNewsOfWeek: false);

        Assert.Equal("k03", ActiveAssignmentSelector.PickNewestActive(
            new[] { k03 }, Now, x => x.DisplayTypes.IsWallpaper)?.PresentationId);
        Assert.Null(ActiveAssignmentSelector.PickNewestActive(
            new[] { k03 }, Now, x => x.DisplayTypes.IsNewsOfWeek));
    }

    // ── PickNewestToTeam: poster ranking by newest-to-team ──

    private static PublishedAssignmentIndex Pub(
        string aid, DateTime modifiedUtc, DateTime? publishedUtc, bool active = true) => new()
    {
        AssignmentId             = aid,
        PresentationId           = "p-" + aid,
        PresentationLastModified = modifiedUtc,
        PublishedDate            = publishedUtc ?? default,
        ScheduleStart            = active ? Now.AddDays(-1) : Now.AddDays(5),
        ScheduleEnd              = active ? Now.AddDays(1)  : Now.AddDays(6),
        DaysOfWeek               = "1,2,3,4,5,6,7",
        DisplayTypes             = new DisplayTypeInfo { IsNewsOfWeek = true },
    };

    private static string? Pick(params PublishedAssignmentIndex[] items) =>
        ActiveAssignmentSelector.PickNewestToTeam(items, Now, a => a.DisplayTypes.IsNewsOfWeek)?.AssignmentId;

    private static DateTime U(int day, int hour = 0) => new(2026, 6, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ToTeam_OlderPresentationWithNewerPublishDate_BeatsNewerModified()
    {
        var older = Pub("older", U(1), U(14));
        var newer = Pub("newer", U(10), U(10));
        Assert.Equal("older", Pick(newer, older));
    }

    [Fact]
    public void ToTeam_NewerModified_BeatsOlderPublishDate()
    {
        var edited = Pub("edited", U(14), U(2));   // published before it was edited
        var other  = Pub("other",  U(1),  U(10));
        Assert.Equal("edited", Pick(other, edited));
    }

    [Fact]
    public void ToTeam_MissingPublishDate_FallsBackToLastModified()
    {
        var legacy = Pub("legacy", U(12), null);
        var other  = Pub("other",  U(1),  U(10));
        Assert.Equal("legacy", Pick(other, legacy));
    }

    [Fact]
    public void ToTeam_EqualRank_TieBreaksOnModifiedThenAssignmentId_IndependentOfOrder()
    {
        var a = Pub("b", U(5), U(10));
        var b = Pub("a", U(5), U(10));
        Assert.Equal("a", Pick(a, b));
        Assert.Equal("a", Pick(b, a));

        var older = Pub("a", U(1), U(10));   // same rank (10th), older modification
        var newer = Pub("z", U(10), U(10));  // same rank, newer modification wins first
        Assert.Equal("z", Pick(older, newer));
        Assert.Equal("z", Pick(newer, older));
    }

    [Fact]
    public void ToTeam_MixedUtcOffsets_CompareAsInstants()
    {
        // 2026-06-10 23:00 at +05:00 == 18:00Z, which is earlier than 2026-06-10 20:00Z.
        var plus5 = new DateTimeOffset(2026, 6, 10, 23, 0, 0, TimeSpan.FromHours(5)).LocalDateTime;
        var a = Pub("a", U(1), plus5);
        var b = Pub("b", U(1), U(10, 20));
        Assert.Equal("b", Pick(a, b));
    }

    [Fact]
    public void ToTeam_InactiveItems_NeverWin()
    {
        var inactive = Pub("inactive", U(14), U(14), active: false);
        var active   = Pub("active", U(1), U(2));
        Assert.Equal("active", Pick(inactive, active));
        Assert.Null(Pick(inactive));
    }
}
