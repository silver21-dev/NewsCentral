using NewsCentral.Launch;
using NewsCentral.Models.IndexFile;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class NotificationCountdownTests
{
    private static NotificationCountdown Shown(int seconds = 3)
    {
        var c = new NotificationCountdown(seconds);
        Assert.True(c.Shown());
        return c;
    }

    [Fact]
    public void DefaultDuration_Is60Seconds() => Assert.Equal(60, new NotificationCountdown().Remaining);

    [Fact]
    public void Shown_ReturnsTrueOnlyOnce_SoTheMarkerIsWrittenOnce()
    {
        var c = new NotificationCountdown();
        Assert.False(c.IsShown);
        Assert.True(c.Shown());
        Assert.False(c.Shown());
    }

    [Fact]
    public void BeforeShown_TicksDoNothing()
    {
        var c = new NotificationCountdown(2);
        for (int i = 0; i < 5; i++) Assert.False(c.Tick(false));
        Assert.Equal(2, c.Remaining);
        Assert.Null(c.Outcome);
    }

    [Fact]
    public void RunsToTimeout()
    {
        var c = Shown(3);
        Assert.False(c.Tick(false));
        Assert.False(c.Tick(false));
        Assert.True(c.Tick(false));
        Assert.Equal(NotificationOutcome.TimedOut, c.Outcome);
        Assert.Equal(0, c.Remaining);
    }

    [Fact]
    public void Hover_Pauses_ThenResumesWithTheRemainingTime()
    {
        var c = Shown(3);
        c.Tick(false);                                   // 2 left
        for (int i = 0; i < 100; i++) c.Tick(true);      // hovering: frozen
        Assert.Equal(2, c.Remaining);
        Assert.Null(c.Outcome);

        Assert.False(c.Tick(false));                     // resumed: 1 left
        Assert.True(c.Tick(false));                      // timed out
    }

    [Fact]
    public void Lock_Pauses_AndUnlockResumes()
    {
        var c = Shown(2);
        c.SetLocked(true);
        for (int i = 0; i < 100; i++) c.Tick(false);
        Assert.Equal(2, c.Remaining);

        c.SetLocked(false);
        c.Tick(false);
        Assert.True(c.Tick(false));
        Assert.Equal(NotificationOutcome.TimedOut, c.Outcome);
    }

    [Fact]
    public void Dismiss_And_Expand_SetTheOutcome()
    {
        var d = Shown();
        Assert.True(d.Dismiss());
        Assert.Equal(NotificationOutcome.Dismissed, d.Outcome);

        var e = Shown();
        Assert.True(e.Expand());
        Assert.Equal(NotificationOutcome.Expanded, e.Outcome);
    }

    [Fact]
    public void FirstOutcomeWins_AndTicksAfterwardsAreInert()
    {
        var c = Shown(1);
        Assert.True(c.Expand());
        Assert.False(c.Dismiss());
        Assert.False(c.Tick(false));
        Assert.Equal(NotificationOutcome.Expanded, c.Outcome);

        var t = Shown(1);
        Assert.True(t.Tick(false));
        Assert.False(t.Expand());
        Assert.Equal(NotificationOutcome.TimedOut, t.Outcome);
    }
}

public sealed class NotificationPlacementTests
{
    [Theory]
    // 1920x1080 with a 40px taskbar, 100%: 320x72, 5px margin
    [InlineData(0, 0, 1920, 1040, 96, 1595, 963)]
    // 1366x768 work area ends at 728
    [InlineData(0, 0, 1366, 728, 96, 1041, 651)]
    // 150%: 480x108, margin 8 (7.5 rounds away from zero)
    [InlineData(0, 0, 2560, 1360, 144, 2072, 1244)]
    // work area not anchored at the origin (taskbar on the left/top)
    [InlineData(48, 30, 1920, 1080, 96, 1595, 1003)]
    public void BottomRight_LeavesTheMarginFromBothEdges(int l, int t, int r, int b, int dpi, int x, int y)
    {
        int w = NotificationPlacement.Dip(NotificationPlacement.WidthDips, dpi);
        int h = NotificationPlacement.Dip(NotificationPlacement.HeightDips, dpi);
        int m = NotificationPlacement.Dip(NotificationPlacement.MarginDips, dpi);

        var p = NotificationPlacement.Compute(new ScreenRect(l, t, r, b), w, h, m, rtl: false);

        Assert.Equal((x, y), p);
        Assert.Equal(m, r - (p.X + w));
        Assert.Equal(m, b - (p.Y + h));
    }

    [Theory]
    [InlineData(0, 0, 1920, 1040, 96, 5, 963)]
    [InlineData(48, 30, 1920, 1080, 96, 53, 1003)]
    [InlineData(0, 0, 2560, 1360, 144, 8, 1244)]
    public void Rtl_IsBottomLeft(int l, int t, int r, int b, int dpi, int x, int y)
    {
        int w = NotificationPlacement.Dip(NotificationPlacement.WidthDips, dpi);
        int h = NotificationPlacement.Dip(NotificationPlacement.HeightDips, dpi);
        int m = NotificationPlacement.Dip(NotificationPlacement.MarginDips, dpi);

        Assert.Equal((x, y), NotificationPlacement.Compute(new ScreenRect(l, t, r, b), w, h, m, rtl: true));
    }

    [Fact]
    public void TinyWorkArea_NeverPlacedBeforeItsTopLeft() =>
        Assert.Equal((0, 0), NotificationPlacement.Compute(new ScreenRect(0, 0, 200, 50), 320, 72, 5, false));

    [Theory]
    [InlineData(320, 96, 320)]
    [InlineData(320, 120, 400)]
    [InlineData(320, 144, 480)]
    [InlineData(320, 192, 640)]
    [InlineData(5, 144, 8)]
    public void Dip_ScalesByDpi(int dips, int dpi, int px) => Assert.Equal(px, NotificationPlacement.Dip(dips, dpi));
}

public sealed class LaunchLogLineTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 12, 9, 30, 0, TimeSpan.FromHours(2));

    [Fact]
    public void Key_IsWrittenExactlyAsStoredInTheMarker()
    {
        var item = new PublishedAssignmentIndex
        {
            PresentationId = "4dd97030", PresentationVersion = 3, Content = new ContentInfo { ImageHash = "sha256:ab" }
        };
        var key = ContentKeys.For(item);

        var line = LaunchLogLine.Format(T, "content", "MiniForm", "NewContent", key, "Title");

        Assert.Contains($" | key={key} | ", line);
        Assert.Contains("key=4dd97030|v3|sha256:ab", line);
    }

    [Fact]
    public void Title_CannotForgeAColumn() =>
        Assert.Contains("title=a/b c", LaunchLogLine.Format(T, "none", "MiniForm", "NewContent", "k", "a|b\nc"));

    [Fact]
    public void Outcome_IsAppendedLast() =>
        Assert.EndsWith(" | key=k | outcome=Dismissed",
            LaunchLogLine.Format(T, "none", "MiniForm", "Outcome", "k", outcome: "Dismissed"));

    [Fact]
    public void NoKey_IsADash() =>
        Assert.EndsWith("reason=NoContent | key=-", LaunchLogLine.Format(T, "none", "Exit", "NoContent", null));
}
