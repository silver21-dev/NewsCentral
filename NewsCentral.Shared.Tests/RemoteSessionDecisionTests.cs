using NewsCentral.Configuration;
using NewsCentral.Launch;
using Xunit;

namespace NewsCentral.Shared.Tests;

/// <summary>Remote-session support: the RemoteSession / NoShell / NoQualifyingMonitor rules, remote detection,
/// the Virtual Desktop fallback and the wallpaper-style gate.</summary>
public sealed class RemoteSessionDecisionTests
{
    private sealed class FakeProbes : ILaunchProbes
    {
        public bool Remote { get; set; }
        public bool Visible { get; set; } = true;
        public bool Shell { get; set; } = true;
        public TimeSpan Idle { get; set; } = TimeSpan.Zero;
        public bool Busy { get; set; }
        public List<string> Calls { get; } = new();
        public bool IsRemoteSession() { Calls.Add("remote"); return Remote; }
        public bool IsSessionVisible() { Calls.Add("visible"); return Visible; }
        public bool HasShell() { Calls.Add("shell"); return Shell; }
        public TimeSpan IdleTime() { Calls.Add("idle"); return Idle; }
        public bool IsBusy() { Calls.Add("busy"); return Busy; }
    }

    // Defaults: today's poster already shown, new content, user present.
    private static LaunchDecisionResult Decide(Func<LaunchInputs, LaunchInputs>? tweak = null, FakeProbes? p = null)
    {
        var i = new LaunchInputs { ContentAvailable = true, ContentKey = "k2", Title = "T", GateHit = true, Marker = "k1" };
        return LaunchDecider.Decide(tweak is null ? i : tweak(i), p ?? new FakeProbes());
    }

    // ── RemoteSession ───────────────────────────────────────────────────────────

    [Fact]
    public void ShowInRemoteSessions_DefaultsToTrue() => Assert.True(new LaunchInputs().ShowInRemoteSessions);

    [Fact]
    public void RemoteSession_Exits_OnlyWhenRemoteAndSwitchOff()
    {
        var r = Decide(i => i with { ShowInRemoteSessions = false }, new FakeProbes { Remote = true });
        Assert.Equal(LaunchAction.Exit, r.Action);
        Assert.Equal("RemoteSession", r.Reason);
    }

    [Fact]
    public void SwitchOff_ButNotRemote_IsTheConsolePath()
    {
        var r = Decide(i => i with { ShowInRemoteSessions = false }, new FakeProbes { Remote = false });
        Assert.Equal(LaunchAction.MiniForm, r.Action);
    }

    [Fact]
    public void RemoteWithSwitchOn_IsNotExcluded()
    {
        var r = Decide(null, new FakeProbes { Remote = true });
        Assert.Equal(LaunchAction.MiniForm, r.Action);
    }

    [Fact]
    public void SwitchOn_DoesNotEvenConsultTheRemoteProbe()
    {
        var p = new FakeProbes { Remote = true };
        Decide(null, p);
        Assert.DoesNotContain("remote", p.Calls);
    }

    [Fact]
    public void Manual_BypassesTheRemoteSwitch_AndTheShellCheck()
    {
        var p = new FakeProbes { Remote = true, Shell = false };
        var r = Decide(i => i with { Manual = true, ShowInRemoteSessions = false }, p);
        Assert.Equal(LaunchAction.FullPoster, r.Action);
        Assert.Equal("Manual", r.Reason);
        Assert.Empty(p.Calls);
    }

    [Fact]
    public void RemoteSwitch_IsCheckedBeforeNoContent()
    {
        var r = Decide(i => i with { ShowInRemoteSessions = false, ContentAvailable = false }, new FakeProbes { Remote = true });
        Assert.Equal("RemoteSession", r.Reason);
    }

    [Fact]
    public void Inactive_AndAlreadyRunning_StillWinOverRemote()
    {
        var p = new FakeProbes { Remote = true };
        Assert.Equal("Inactive", Decide(i => i with { Active = false, ShowInRemoteSessions = false }, p).Reason);
        Assert.Equal("AlreadyRunning", Decide(i => i with { MutexAcquired = false, ShowInRemoteSessions = false }, p).Reason);
    }

    // ── NoShell ─────────────────────────────────────────────────────────────────

    [Fact]
    public void NoShell_Exits_AfterNotVisible_AndBeforeIdle()
    {
        var p = new FakeProbes { Shell = false, Idle = TimeSpan.FromHours(1), Busy = true };
        var r = Decide(i => i with { ContentTrigger = true }, p);
        Assert.Equal("NoShell", r.Reason);
        Assert.DoesNotContain("idle", p.Calls);
        Assert.DoesNotContain("busy", p.Calls);
        Assert.Equal(new[] { "visible", "shell" }, p.Calls);
    }

    [Fact]
    public void NotVisible_WinsOverNoShell()
    {
        var p = new FakeProbes { Visible = false, Shell = false };
        Assert.Equal("NotVisible", Decide(null, p).Reason);
        Assert.DoesNotContain("shell", p.Calls);
    }

    [Fact]
    public void NoShell_AlsoGuardsTheFullPoster()
    {
        var r = Decide(i => i with { GateHit = false }, new FakeProbes { Shell = false });
        Assert.Equal("NoShell", r.Reason);
        Assert.False(r.WriteMarker);
    }

    // ── NoQualifyingMonitor ─────────────────────────────────────────────────────

    [Fact]
    public void NoQualifyingMonitor_IsItsOwnReason_AfterNoContent_BeforeProbes()
    {
        var p = new FakeProbes();
        var r = Decide(i => i with { HasQualifyingMonitor = false }, p);
        Assert.Equal(LaunchAction.Exit, r.Action);
        Assert.Equal("NoQualifyingMonitor", r.Reason);
        Assert.Empty(p.Calls);

        // No item at all is still NoContent, whatever the monitors.
        Assert.Equal("NoContent", Decide(i => i with { HasQualifyingMonitor = false, ContentAvailable = false }).Reason);
    }

    [Fact]
    public void NoQualifyingMonitor_AlsoStopsManual()
    {
        var r = Decide(i => i with { Manual = true, HasQualifyingMonitor = false });
        Assert.Equal("NoQualifyingMonitor", r.Reason);
        Assert.Equal("NoContent", Decide(i => i with { Manual = true, ContentAvailable = false }).Reason);
    }

    [Fact]
    public void HasQualifyingMonitor_DefaultsToTrue() => Assert.True(new LaunchInputs().HasQualifyingMonitor);

    // ── Detection ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false, null, false)]    // console, nothing readable
    [InlineData(false, false, 0, false)]       // console protocol
    [InlineData(true, false, 0, true)]         // SM_REMOTESESSION (RDP / ICA)
    [InlineData(false, true, 0, true)]         // Horizon environment variable
    [InlineData(false, false, 2, true)]        // WTS client protocol RDP
    [InlineData(false, false, 1, true)]        // WTS client protocol ICA
    [InlineData(true, true, 2, true)]
    [InlineData(true, false, null, true)]      // an unreadable protocol never cancels another signal
    public void RemoteDetection_AnySignalIsEnough(bool sm, bool horizon, int? protocol, bool expected) =>
        Assert.Equal(expected, RemoteDetection.IsRemote(sm, horizon, protocol));

    // ── Virtual Desktop fallback / wallpaper style ──────────────────────────────

    [Theory]
    [InlineData(true, false, true)]     // console + UseVirtualDesktop -> virtual desktop
    [InlineData(true, true, false)]     // remote + UseVirtualDesktop -> standard desktop
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void UseVirtualDesktop_FallsBackToStandardInRemoteSessions(bool wants, bool remote, bool expected) =>
        Assert.Equal(expected, LaunchDecider.UseVirtualDesktop(wants, remote));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WallpaperStyleReassert_NeverRunsInARemoteSession(bool remote, bool expected) =>
        Assert.Equal(expected, LaunchDecider.ShouldAssertWallpaperStyle(remote));

    // ── Log note, manifest ──────────────────────────────────────────────────────

    [Fact]
    public void LaunchLog_NoteIsAppendedLast() =>
        Assert.EndsWith(" | key=k | note=VirtualDesktopFallback",
            LaunchLogLine.Format(DateTimeOffset.Now, "none", "FullPoster", "Manual", "k", note: "VirtualDesktopFallback"));

    [Fact]
    public void Manifest_HasShowInRemoteSessions_DefaultTrue_Dword()
    {
        var key = Assert.Single(ConfigManifests.NewsViewer.Keys, k => k.CanonicalKey == "Display:ShowInRemoteSessions");
        Assert.Equal("true", key.Default);
        Assert.Equal(RegistryValueType.Dword, key.RegistryType);
        Assert.Equal(@"Display\ShowInRemoteSessions", key.RegistrySubkeyPath);
        Assert.Contains("RemoteSession", key.ValueHint);
    }
}
