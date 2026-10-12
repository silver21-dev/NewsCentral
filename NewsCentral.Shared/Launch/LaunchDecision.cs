using NewsCentral.Models.IndexFile;

namespace NewsCentral.Launch;

public enum LaunchAction
{
    /// <summary>Nothing is shown; the process exits.</summary>
    Exit,
    /// <summary>The full poster form is shown (gate consumed as before).</summary>
    FullPoster,
    /// <summary>The small "News arrived!" notification form is shown.</summary>
    MiniForm
}

/// <summary>Outcome of <see cref="LaunchDecider.Decide"/>.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Reason">Stable reason token, written to the launch log.</param>
/// <param name="ContentKey">Current content key (null when there is no selected item).</param>
/// <param name="Title">Title of the selected item; only set for <see cref="LaunchAction.MiniForm"/>.</param>
/// <param name="WriteMarker">The caller must store <paramref name="ContentKey"/> as the user's marker
/// (FullPoster on display; MarkerInitialized silently).</param>
public readonly record struct LaunchDecisionResult(
    LaunchAction Action, string Reason, string? ContentKey = null, string? Title = null, bool WriteMarker = false);

/// <summary>Everything the decision depends on except the (lazily evaluated) environment probes.</summary>
public sealed record LaunchInputs
{
    public bool Active { get; init; } = true;
    public bool MutexAcquired { get; init; } = true;
    public bool Manual { get; init; }
    /// <summary>Launched with <c>--trigger content</c> (the ContentChanged event task).</summary>
    public bool ContentTrigger { get; init; }
    /// <summary>A poster item was selected and its image is usable.</summary>
    public bool ContentAvailable { get; init; }
    /// <summary>At least one monitor is Full HD or better (a poster needs one). Checked right after
    /// <see cref="ContentAvailable"/>, where it used to be folded into it.</summary>
    public bool HasQualifyingMonitor { get; init; } = true;
    /// <summary><c>Display:ShowInRemoteSessions</c>; false = exit in remote sessions (previous behavior).</summary>
    public bool ShowInRemoteSessions { get; init; } = true;
    public string? ContentKey { get; init; }
    public string? Title { get; init; }
    /// <summary>Today's poster was already shown (daily gate hit).</summary>
    public bool GateHit { get; init; }
    public bool BypassDailyGate { get; init; }
    public bool NotifyNewContent { get; init; } = true;
    /// <summary>Per-user "last content seen or notified" marker; null/empty on first run.</summary>
    public string? Marker { get; init; }
}

/// <summary>Environment checks, evaluated lazily and only when the decision reaches them.</summary>
public interface ILaunchProbes
{
    /// <summary>This is a remote session: RDP, Citrix ICA or VMware Horizon (see <see cref="RemoteDetection"/>).</summary>
    bool IsRemoteSession();
    /// <summary>Session is active, unlocked, and the input desktop is "Default". Any failure = false.</summary>
    bool IsSessionVisible();
    /// <summary>The session has a shell (taskbar window). A published-app / seamless session does not, and
    /// has no desktop to show a poster on. A failed probe counts as "has a shell".</summary>
    bool HasShell();
    /// <summary>Time since the user's last input.</summary>
    TimeSpan IdleTime();
    /// <summary>A full-screen app, presentation, or do-not-disturb state is active.</summary>
    bool IsBusy();
}

/// <summary>
/// The ordered launch decision. Pure: no I/O, probes are injected. Rule order matters and is
/// documented in docs/newsviewer-spec.md ("Launch decision").
/// </summary>
public static class LaunchDecider
{
    /// <summary>Idle limit for content-triggered launches only (Windows Hello unlock may not register as input).</summary>
    public static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(2);

    public static LaunchDecisionResult Decide(LaunchInputs i, ILaunchProbes probes)
    {
        // 1. Master switch.
        if (!i.Active) return new(LaunchAction.Exit, "Inactive");

        // 2. Another instance already runs in this session.
        if (!i.MutexAcquired) return new(LaunchAction.Exit, "AlreadyRunning");

        // 3. --manual: an on-demand launch bypasses the gate and every check below, the remote-session
        //    switch included. (It still needs something to show, and a monitor to show it on.)
        if (i.Manual)
        {
            if (!i.ContentAvailable) return new(LaunchAction.Exit, "NoContent");
            if (!i.HasQualifyingMonitor) return new(LaunchAction.Exit, "NoQualifyingMonitor", i.ContentKey);
            return new(LaunchAction.FullPoster, "Manual", i.ContentKey, WriteMarker: true);
        }

        // 4. Remote sessions are supported by default; the switch restores the old exit (now logged).
        //    The probe is only consulted when the switch is off.
        if (!i.ShowInRemoteSessions && probes.IsRemoteSession()) return new(LaunchAction.Exit, "RemoteSession");

        // 5. Nothing to show.
        if (!i.ContentAvailable) return new(LaunchAction.Exit, "NoContent");

        // 5b. No Full-HD-or-better monitor (a typical small RDP client window lands here).
        if (!i.HasQualifyingMonitor) return new(LaunchAction.Exit, "NoQualifyingMonitor", i.ContentKey);

        // 6-9. Never show behind a locked screen / on the secure desktop, in a session without a shell,
        //      to an away user on content arrival, or over a presentation. The gate is not consumed, so
        //      a later trigger retries.
        if (!probes.IsSessionVisible()) return new(LaunchAction.Exit, "NotVisible", i.ContentKey);
        if (!probes.HasShell()) return new(LaunchAction.Exit, "NoShell", i.ContentKey);
        if (i.ContentTrigger && probes.IdleTime() > IdleThreshold) return new(LaunchAction.Exit, "Idle", i.ContentKey);
        if (probes.IsBusy()) return new(LaunchAction.Exit, "Busy", i.ContentKey);

        // 10. Today's poster not shown yet (or the gate is bypassed): the normal full poster.
        if (!i.GateHit || i.BypassDailyGate)
            return new(LaunchAction.FullPoster, "Gate", i.ContentKey, WriteMarker: true);

        // 11. Notification disabled: previous behavior (silent exit).
        if (!i.NotifyNewContent) return new(LaunchAction.Exit, "NotifyDisabled", i.ContentKey);

        // 12. First run after upgrade: adopt the current content silently so a poster the user already
        //     saw does not raise a notification.
        if (string.IsNullOrEmpty(i.Marker))
            return new(LaunchAction.Exit, "MarkerInitialized", i.ContentKey, WriteMarker: true);

        // 13. Nothing new since the user last saw or was notified.
        if (string.Equals(i.Marker, i.ContentKey, StringComparison.Ordinal))
            return new(LaunchAction.Exit, "AlreadySeen", i.ContentKey);

        // 14. New content since the marker.
        return new(LaunchAction.MiniForm, "NewContent", i.ContentKey, i.Title);
    }

    /// <summary>
    /// Whether a poster that asks for a Virtual Desktop actually gets one. Desktop switching is not reliable
    /// in a remote session, so there it falls back to the standard desktop (normal path, gate, --manual and
    /// Expanded alike). Console sessions are unchanged.
    /// </summary>
    public static bool UseVirtualDesktop(bool presentationWantsVirtualDesktop, bool remoteSession) =>
        presentationWantsVirtualDesktop && !remoteSession;

    /// <summary>
    /// The HKCU wallpaper style / background colour re-assert never runs in a remote session, regardless of
    /// <c>Display:ShowInRemoteSessions</c>: on a session host NewsService's wallpaper surface may be off, and
    /// each user's own style and colour must not be rewritten.
    /// </summary>
    public static bool ShouldAssertWallpaperStyle(bool remoteSession) => !remoteSession;
}

/// <summary>Remote-session detection rule (the Win32 gathering lives in NewsViewer's LaunchProbes).</summary>
public static class RemoteDetection
{
    /// <summary>
    /// Remote when ANY signal says so: <c>GetSystemMetrics(SM_REMOTESESSION)</c> (RDP and Citrix ICA),
    /// the <c>ViewClient_Machine_Name</c> environment variable (VMware Horizon), or a WTS client protocol
    /// type other than console (0). An unreadable protocol type (null) is ignored.
    /// </summary>
    public static bool IsRemote(bool smRemoteSession, bool horizonEnvironment, int? wtsClientProtocolType) =>
        smRemoteSession || horizonEnvironment || (wtsClientProtocolType is int p && p != 0);
}

/// <summary>Identity of a poster item that changes when it is republished or updated.</summary>
public static class ContentKeys
{
    /// <summary>
    /// Presentation id + version + image hash. The version bumps on every republish/edit; the image
    /// hash additionally covers an image replaced without a version change.
    /// </summary>
    public static string For(PublishedAssignmentIndex a) =>
        $"{a.PresentationId}|v{a.PresentationVersion}|{a.Content.ImageHash}";
}

/// <summary>Pure helpers for the environment probes (testable without Win32).</summary>
public static class ProbeMath
{
    /// <summary>Wrap-safe idle time: both ticks are 32-bit millisecond counters that wrap every ~49.7 days.</summary>
    public static TimeSpan Idle(uint nowTick, uint lastInputTick) =>
        TimeSpan.FromMilliseconds(unchecked(nowTick - lastInputTick));

    /// <summary>
    /// QUERY_USER_NOTIFICATION_STATE → busy? Busy = NOT_PRESENT(1), BUSY(2), RUNNING_D3D_FULL_SCREEN(3),
    /// PRESENTATION_MODE(4), APP(7). ACCEPTS_NOTIFICATIONS(5) and QUIET_TIME(6) are not busy — quiet time
    /// is the first-hour period for new users and must not suppress the morning poster.
    /// </summary>
    public static bool IsBusyState(int quns) => quns is 1 or 2 or 3 or 4 or 7;
}

/// <summary>Size-capped launch-log maintenance.</summary>
public static class LaunchLogTrimmer
{
    public const int MaxBytes = 64 * 1024;

    /// <summary>
    /// When <paramref name="content"/> exceeds <paramref name="maxBytes"/> (UTF-8), keeps the newest
    /// half, starting on a line boundary. Otherwise returns it unchanged.
    /// </summary>
    public static string Trim(string content, int maxBytes = MaxBytes)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(content);
        if (bytes <= maxBytes) return content;

        var lines = content.Split('\n');
        var kept = new List<string>();
        long size = 0;
        for (int n = lines.Length - 1; n >= 0; n--)
        {
            size += System.Text.Encoding.UTF8.GetByteCount(lines[n]) + 1;
            if (size > maxBytes / 2) break;
            kept.Add(lines[n]);
        }
        kept.Reverse();
        return string.Join('\n', kept);
    }
}

/// <summary>Plain rectangle (screen coordinates, Win32 RECT semantics) for the full-screen heuristic.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom);

/// <summary>
/// Pure rule behind the full-screen-foreground busy check, which covers what
/// SHQueryUserNotificationState misses (browser F11 / full-screen video). Win32 gathering lives in
/// NewsViewer's LaunchProbes; every gathering failure is passed in as a null rectangle → not busy.
/// </summary>
public static class FullScreenHeuristic
{
    private static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd" };

    /// <param name="hasForegroundWindow">GetForegroundWindow returned a window.</param>
    /// <param name="isOwnProcess">The foreground window belongs to this process.</param>
    /// <param name="className">Window class name (null when unreadable).</param>
    /// <param name="window">GetWindowRect, null on failure.</param>
    /// <param name="monitor">The full monitor rectangle (rcMonitor, not the work area), null on failure.</param>
    /// <param name="isZoomed">The window is maximized (IsZoomed).</param>
    public static bool IsBusy(bool hasForegroundWindow, bool isOwnProcess, string? className,
        ScreenRect? window, ScreenRect? monitor, bool isZoomed)
    {
        if (!hasForegroundWindow || isOwnProcess) return false;
        if (className is not null && Array.Exists(ShellClasses, c => string.Equals(c, className, StringComparison.Ordinal)))
            return false;
        if (window is not { } w || monitor is not { } m) return false;

        // A maximized window can cover the whole monitor when the taskbar auto-hides; that is not full screen.
        if (isZoomed) return false;

        return w.Left <= m.Left && w.Top <= m.Top && w.Right >= m.Right && w.Bottom >= m.Bottom;
    }
}
