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
    /// <summary>A poster item was selected and its image is usable (and a qualifying monitor exists).</summary>
    public bool ContentAvailable { get; init; }
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
    /// <summary>Session is active, unlocked, and the input desktop is "Default". Any failure = false.</summary>
    bool IsSessionVisible();
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

        // 3. --manual: an on-demand launch bypasses the gate and every check below. (It still needs
        //    something to show; with no usable item there is nothing to display.)
        if (i.Manual)
            return i.ContentAvailable
                ? new(LaunchAction.FullPoster, "Manual", i.ContentKey, WriteMarker: true)
                : new(LaunchAction.Exit, "NoContent");

        // 4. Nothing to show.
        if (!i.ContentAvailable) return new(LaunchAction.Exit, "NoContent");

        // 5-7. Never show behind a locked screen / on the secure desktop, to an away user on content
        //      arrival, or over a presentation. The gate is not consumed, so a later trigger retries.
        if (!probes.IsSessionVisible()) return new(LaunchAction.Exit, "NotVisible", i.ContentKey);
        if (i.ContentTrigger && probes.IdleTime() > IdleThreshold) return new(LaunchAction.Exit, "Idle", i.ContentKey);
        if (probes.IsBusy()) return new(LaunchAction.Exit, "Busy", i.ContentKey);

        // 8. Today's poster not shown yet (or the gate is bypassed): the normal full poster.
        if (!i.GateHit || i.BypassDailyGate)
            return new(LaunchAction.FullPoster, "Gate", i.ContentKey, WriteMarker: true);

        // 9. Notification disabled: previous behavior (silent exit).
        if (!i.NotifyNewContent) return new(LaunchAction.Exit, "NotifyDisabled", i.ContentKey);

        // 10. First run after upgrade: adopt the current content silently so a poster the user already
        //     saw does not raise a notification.
        if (string.IsNullOrEmpty(i.Marker))
            return new(LaunchAction.Exit, "MarkerInitialized", i.ContentKey, WriteMarker: true);

        // 11. Nothing new since the user last saw or was notified.
        if (string.Equals(i.Marker, i.ContentKey, StringComparison.Ordinal))
            return new(LaunchAction.Exit, "AlreadySeen", i.ContentKey);

        // 12. New content since the marker.
        return new(LaunchAction.MiniForm, "NewContent", i.ContentKey, i.Title);
    }
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
