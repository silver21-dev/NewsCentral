using NewsCentral.Launch;
using NewsCentral.Models.IndexFile;
using Xunit;

namespace NewsCentral.Shared.Tests;

/// <summary>
/// The ordered NewsViewer launch decision (docs/newsviewer-spec.md "Launch decision"): every rule in
/// order, with fake probes whose calls are recorded to prove laziness where it matters.
/// </summary>
public sealed class LaunchDecisionTests
{
    private sealed class FakeProbes : ILaunchProbes
    {
        public bool Visible { get; set; } = true;
        public TimeSpan Idle { get; set; } = TimeSpan.Zero;
        public bool Busy { get; set; }
        public List<string> Calls { get; } = new();
        public bool IsSessionVisible() { Calls.Add("visible"); return Visible; }
        public TimeSpan IdleTime() { Calls.Add("idle"); return Idle; }
        public bool IsBusy() { Calls.Add("busy"); return Busy; }
    }

    // Defaults describe "today's poster already shown, new content, user present": the MiniForm case.
    private static LaunchInputs Inputs(Func<LaunchInputs, LaunchInputs>? tweak = null)
    {
        var i = new LaunchInputs
        {
            ContentAvailable = true, ContentKey = "k2", Title = "Title", GateHit = true, Marker = "k1"
        };
        return tweak is null ? i : tweak(i);
    }

    private static LaunchDecisionResult Decide(Func<LaunchInputs, LaunchInputs>? tweak = null, FakeProbes? p = null) =>
        LaunchDecider.Decide(Inputs(tweak), p ?? new FakeProbes());

    [Fact]
    public void Active_False_WinsOverEverything()
    {
        var r = Decide(i => i with { Active = false, MutexAcquired = false, Manual = true });
        Assert.Equal(LaunchAction.Exit, r.Action);
        Assert.Equal("Inactive", r.Reason);
    }

    [Fact]
    public void MutexNotAcquired_ExitsAlreadyRunning_EvenWithManual()
    {
        var r = Decide(i => i with { MutexAcquired = false, Manual = true });
        Assert.Equal("AlreadyRunning", r.Reason);
    }

    [Fact]
    public void Manual_BypassesVisibilityIdleAndBusy_AndWritesMarker()
    {
        var p = new FakeProbes { Visible = false, Idle = TimeSpan.FromHours(1), Busy = true };
        var r = Decide(i => i with { Manual = true, ContentTrigger = true, GateHit = true }, p);

        Assert.Equal(LaunchAction.FullPoster, r.Action);
        Assert.True(r.WriteMarker);
        Assert.Equal("k2", r.ContentKey);
        Assert.Empty(p.Calls);
    }

    [Fact]
    public void Manual_WithNoUsableContent_ExitsNoContent()
    {
        var r = Decide(i => i with { Manual = true, ContentAvailable = false });
        Assert.Equal(LaunchAction.Exit, r.Action);
        Assert.Equal("NoContent", r.Reason);
    }

    [Fact]
    public void NoContent_Exits_BeforeProbesAreConsulted()
    {
        var p = new FakeProbes();
        var r = Decide(i => i with { ContentAvailable = false }, p);
        Assert.Equal("NoContent", r.Reason);
        Assert.Empty(p.Calls);
    }

    [Fact]
    public void SessionNotVisible_Exits_AndGuardsTheFullPosterToo()
    {
        var p = new FakeProbes { Visible = false };
        var r = Decide(i => i with { GateHit = false }, p);   // poster not yet shown today
        Assert.Equal(LaunchAction.Exit, r.Action);
        Assert.Equal("NotVisible", r.Reason);
        Assert.False(r.WriteMarker);
    }

    [Fact]
    public void Idle_AppliesOnlyWithContentTrigger()
    {
        var idle = new FakeProbes { Idle = TimeSpan.FromMinutes(10) };

        var withTrigger = Decide(i => i with { ContentTrigger = true }, idle);
        Assert.Equal("Idle", withTrigger.Reason);

        var p2 = new FakeProbes { Idle = TimeSpan.FromMinutes(10) };
        var noTrigger = Decide(null, p2);
        Assert.Equal(LaunchAction.MiniForm, noTrigger.Action);
        Assert.DoesNotContain("idle", p2.Calls);
    }

    [Fact]
    public void Idle_ExactlyAtThreshold_IsNotIdle()
    {
        var p = new FakeProbes { Idle = LaunchDecider.IdleThreshold };
        var r = Decide(i => i with { ContentTrigger = true }, p);
        Assert.Equal(LaunchAction.MiniForm, r.Action);
    }

    [Fact]
    public void Busy_Exits()
    {
        var r = Decide(null, new FakeProbes { Busy = true });
        Assert.Equal("Busy", r.Reason);
    }

    [Theory]
    [InlineData(1, true)]   // NOT_PRESENT
    [InlineData(2, true)]   // BUSY
    [InlineData(3, true)]   // RUNNING_D3D_FULL_SCREEN
    [InlineData(4, true)]   // PRESENTATION_MODE
    [InlineData(5, false)]  // ACCEPTS_NOTIFICATIONS
    [InlineData(6, false)]  // QUIET_TIME — first-hour period must not suppress the poster
    [InlineData(7, true)]   // APP
    [InlineData(0, false)]
    public void QueryUserNotificationState_BusyClassification(int state, bool busy) =>
        Assert.Equal(busy, ProbeMath.IsBusyState(state));

    [Fact]
    public void GateNotHit_FullPoster_EvenWhenMarkerDiffers()
    {
        var r = Decide(i => i with { GateHit = false });
        Assert.Equal(LaunchAction.FullPoster, r.Action);
        Assert.True(r.WriteMarker);
    }

    [Fact]
    public void BypassDailyGate_FullPoster()
    {
        var r = Decide(i => i with { BypassDailyGate = true });
        Assert.Equal(LaunchAction.FullPoster, r.Action);
    }

    [Fact]
    public void NotifyDisabled_IsEvaluatedAfterTheGate()
    {
        // Poster not shown yet: still shows with the switch off.
        var shows = Decide(i => i with { GateHit = false, NotifyNewContent = false });
        Assert.Equal(LaunchAction.FullPoster, shows.Action);

        // Gate hit: silent exit.
        var silent = Decide(i => i with { NotifyNewContent = false });
        Assert.Equal("NotifyDisabled", silent.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyMarker_Initializes_Silently(string? marker)
    {
        var r = Decide(i => i with { Marker = marker });
        Assert.Equal(LaunchAction.Exit, r.Action);
        Assert.Equal("MarkerInitialized", r.Reason);
        Assert.True(r.WriteMarker);
        Assert.Equal("k2", r.ContentKey);
    }

    [Fact]
    public void MarkerEqualsKey_AlreadySeen()
    {
        var r = Decide(i => i with { Marker = "k2" });
        Assert.Equal("AlreadySeen", r.Reason);
        Assert.False(r.WriteMarker);
    }

    [Fact]
    public void MarkerDiffers_MiniForm_WithKeyAndTitle_NoMarkerWrite()
    {
        var r = Decide();
        Assert.Equal(LaunchAction.MiniForm, r.Action);
        Assert.Equal("k2", r.ContentKey);
        Assert.Equal("Title", r.Title);
        Assert.False(r.WriteMarker);
    }

    // ── ContentKey, idle wrap-around, launch-log trimming ───────────────────────

    private static PublishedAssignmentIndex Item(int version, string hash) => new()
    {
        PresentationId = "p1", PresentationVersion = version, Content = new ContentInfo { ImageHash = hash }
    };

    [Fact]
    public void ContentKey_ChangesWithVersion_AndWithHash_ButNotOtherwise()
    {
        var baseKey = ContentKeys.For(Item(1, "sha256:a"));
        Assert.Equal(baseKey, ContentKeys.For(Item(1, "sha256:a")));
        Assert.NotEqual(baseKey, ContentKeys.For(Item(2, "sha256:a")));
        Assert.NotEqual(baseKey, ContentKeys.For(Item(1, "sha256:b")));
    }

    [Fact]
    public void Idle_NormalDifference() =>
        Assert.Equal(TimeSpan.FromMilliseconds(5000), ProbeMath.Idle(10_000, 5_000));

    [Fact]
    public void Idle_SurvivesTickCounterWrapAround()
    {
        // Last input just before the 32-bit counter wrapped; "now" just after.
        var idle = ProbeMath.Idle(nowTick: 1_000, lastInputTick: uint.MaxValue - 999);
        Assert.Equal(TimeSpan.FromMilliseconds(2_000), idle);
    }

    [Fact]
    public void LaunchLog_UnderLimit_Unchanged()
    {
        var text = "a\nb\nc\n";
        Assert.Equal(text, LaunchLogTrimmer.Trim(text, 1000));
    }

    [Fact]
    public void LaunchLog_OverLimit_KeepsNewestHalf_OnLineBoundaries()
    {
        var lines = Enumerable.Range(0, 2000).Select(n => $"line-{n:0000}-" + new string('x', 40)).ToArray();
        var text = string.Join('\n', lines) + "\n";
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) > LaunchLogTrimmer.MaxBytes);

        var trimmed = LaunchLogTrimmer.Trim(text);

        var size = System.Text.Encoding.UTF8.GetByteCount(trimmed);
        Assert.InRange(size, LaunchLogTrimmer.MaxBytes / 2 - 100, LaunchLogTrimmer.MaxBytes / 2);
        Assert.EndsWith(lines[^1] + "\n", trimmed);            // newest kept
        Assert.DoesNotContain(lines[0], trimmed);               // oldest dropped
        Assert.All(trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            l => Assert.StartsWith("line-", l));                // no partial lines
    }
}
