using NewsCentral.Launch;
using Xunit;

namespace NewsCentral.Shared.Tests;

/// <summary>Pure geometry/flag rule behind the full-screen-foreground busy check.</summary>
public sealed class FullScreenHeuristicTests
{
    private static readonly ScreenRect Monitor = new(0, 0, 1920, 1080);
    private static readonly ScreenRect WorkArea = new(0, 0, 1920, 1040);

    private static bool Busy(ScreenRect? window, bool zoomed = false, bool has = true, bool own = false,
        string? cls = "Chrome_WidgetWin_1", ScreenRect? monitor = null, bool useDefaultMonitor = true) =>
        FullScreenHeuristic.IsBusy(has, own, cls, window, useDefaultMonitor ? monitor ?? Monitor : monitor, zoomed);

    [Fact] public void FullMonitor_NotZoomed_IsBusy() => Assert.True(Busy(Monitor));

    [Fact] public void BiggerThanMonitor_NotZoomed_IsBusy() =>
        Assert.True(Busy(new ScreenRect(-8, -8, 1928, 1088)));

    [Fact] public void Maximized_CoveringMonitor_IsNotBusy() => Assert.False(Busy(Monitor, zoomed: true));

    [Fact] public void WorkAreaSized_IsNotBusy() => Assert.False(Busy(WorkArea));

    [Fact] public void OneEdgeShort_IsNotBusy() => Assert.False(Busy(new ScreenRect(0, 0, 1919, 1080)));

    [Fact] public void NoForegroundWindow_IsNotBusy() => Assert.False(Busy(Monitor, has: false));

    [Fact] public void OwnProcess_IsNotBusy() => Assert.False(Busy(Monitor, own: true));

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    public void DesktopAndShell_AreNotBusy(string cls) => Assert.False(Busy(Monitor, cls: cls));

    [Fact] public void ClassNameMatchIsExact() => Assert.True(Busy(Monitor, cls: "progman"));

    [Fact] public void UnknownClassName_StillEvaluatesGeometry() => Assert.True(Busy(Monitor, cls: null));

    [Fact] public void WindowRectFailure_IsNotBusy() => Assert.False(Busy(null));

    [Fact] public void MonitorFailure_IsNotBusy() =>
        Assert.False(Busy(Monitor, monitor: null, useDefaultMonitor: false));

    [Fact] public void SecondMonitor_OffsetCoordinates_Work()
    {
        var m2 = new ScreenRect(1920, 0, 3840, 1080);
        Assert.True(Busy(m2, monitor: m2));
        Assert.False(Busy(Monitor, monitor: m2));   // window on the other monitor
    }
}
