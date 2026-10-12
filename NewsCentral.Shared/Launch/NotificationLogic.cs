namespace NewsCentral.Launch;

/// <summary>How a "News arrived!" notification ended.</summary>
public enum NotificationOutcome
{
    /// <summary>The user clicked the body: the poster form was opened.</summary>
    Expanded,
    /// <summary>The user clicked the close button.</summary>
    Dismissed,
    /// <summary>The countdown reached zero.</summary>
    TimedOut
}

/// <summary>
/// Countdown and outcome state machine of the notification form, separated from WinForms so it can be
/// tested. The countdown only runs once the form is actually on screen (<see cref="Shown"/>), and is
/// frozen while the mouse hovers over the form or the session is locked. The first outcome wins.
/// </summary>
public sealed class NotificationCountdown
{
    /// <summary>How long the notification stays up (excluding hover and lock pauses).</summary>
    public const int NotifyDurationSeconds = 60;

    public NotificationCountdown(int durationSeconds = NotifyDurationSeconds) => Remaining = durationSeconds;

    public int Remaining { get; private set; }
    public bool IsShown { get; private set; }
    public bool IsLocked { get; private set; }
    public NotificationOutcome? Outcome { get; private set; }

    /// <summary>
    /// The form is on screen. Returns true exactly once - the caller then writes the content marker, so
    /// a form that never reached the screen never marks its item as notified.
    /// </summary>
    public bool Shown()
    {
        if (IsShown) return false;
        IsShown = true;
        return true;
    }

    public void SetLocked(bool locked) => IsLocked = locked;

    /// <summary>One-second timer tick. Returns true when this tick ended the notification (timeout).</summary>
    public bool Tick(bool mouseOver)
    {
        if (!IsShown || Outcome is not null || IsLocked || mouseOver) return false;
        if (--Remaining > 0) return false;
        Remaining = 0;
        Outcome = NotificationOutcome.TimedOut;
        return true;
    }

    /// <summary>Returns true when this call decided the outcome (false if one was already decided).</summary>
    public bool Expand() => Decide(NotificationOutcome.Expanded);

    /// <inheritdoc cref="Expand"/>
    public bool Dismiss() => Decide(NotificationOutcome.Dismissed);

    private bool Decide(NotificationOutcome outcome)
    {
        if (Outcome is not null) return false;
        Outcome = outcome;
        return true;
    }
}

/// <summary>Placement and scaling math for the notification form.</summary>
public static class NotificationPlacement
{
    public const int WidthDips = 320;
    public const int HeightDips = 72;
    public const int MarginDips = 5;

    /// <summary>Scales device-independent pixels (96 dpi) to device pixels.</summary>
    public static int Dip(int dips, int dpi) => (int)Math.Round(dips * dpi / 96.0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Top-left of a <paramref name="width"/> x <paramref name="height"/> form in the bottom-right corner of
    /// <paramref name="workArea"/> (bottom-left when <paramref name="rtl"/>), <paramref name="margin"/> pixels
    /// from both edges. Never placed before the work area's top-left.
    /// </summary>
    public static (int X, int Y) Compute(ScreenRect workArea, int width, int height, int margin, bool rtl)
    {
        int x = rtl ? workArea.Left + margin : workArea.Right - width - margin;
        int y = workArea.Bottom - height - margin;
        return (Math.Max(x, workArea.Left), Math.Max(y, workArea.Top));
    }
}

/// <summary>Formats one launch-log line (kept pure so the key format is testable).</summary>
public static class LaunchLogLine
{
    public static string Format(DateTimeOffset timestamp, string trigger, string decision, string reason,
        string? contentKey, string? title = null, string? outcome = null, string? note = null)
    {
        var line = string.Join(" | ",
            timestamp.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            $"trigger={trigger}",
            $"decision={decision}",
            $"reason={reason}",
            $"key={Key(contentKey)}");
        if (title is not null) line += $" | title={Field(title)}";
        if (outcome is not null) line += $" | outcome={outcome}";
        if (note is not null) line += $" | note={Field(note)}";
        return line;
    }

    // The content key is written exactly as stored in viewerstate.json (its own '|' separators intact).
    private static string Key(string? key) =>
        string.IsNullOrEmpty(key) ? "-" : key.Replace('\r', ' ').Replace('\n', ' ');

    // Free text (title) must not be able to forge a column.
    private static string Field(string? s) =>
        string.IsNullOrEmpty(s) ? "-" : s.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
}
