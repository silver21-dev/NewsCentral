using System.Globalization;
using System.Runtime.InteropServices;
using NewsCentral.Launch;
using NewsViewer.Resources;

namespace NewsViewer.Forms;

/// <summary>
/// The "News arrived!" notification: a small, borderless, topmost, taskbar-less form in the bottom-right
/// corner of the primary work area (bottom-left for right-to-left UI cultures) that NEVER takes focus
/// (WS_EX_NOACTIVATE + ShowWithoutActivation) so it cannot steal keystrokes. Painted by hand, with no
/// child controls, so a click anywhere lands on the form itself. The timing and outcome rules live in
/// <see cref="NotificationCountdown"/>; this class only wires them to WinForms and Win32.
/// </summary>
internal sealed partial class NotificationForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopMost = 0x00000008;
    private const int WmWtsSessionChange = 0x02B1;
    private const int WtsSessionLock = 0x7;
    private const int WtsSessionUnlock = 0x8;
    private const uint NotifyForThisSession = 0;

    private readonly NotificationCountdown _countdown = new();
    private readonly string _title;
    private readonly Action? _onShown;
    private readonly bool _rtl;
    private readonly int _dpi;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly ToolTip _toolTip = new();
    private readonly Rectangle _closeRect;
    private bool _closeHover;
    private bool _registered;

    /// <summary>When the form reached the screen (UTC); null if it never did.</summary>
    public DateTime? ShownUtc { get; private set; }

    /// <summary>When the outcome was decided (UTC); null while undecided.</summary>
    public DateTime? OutcomeUtc { get; private set; }

    public NotificationOutcome? Outcome => _countdown.Outcome;

    /// <summary>Creates, shows and activates the poster on Expanded (null = none / opened later by the caller).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<Form?>? ExpandedFormFactory { get; init; }

    /// <summary>The poster the factory produced; the caller runs the message loop on it after this form closes.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Form? ExpandedForm { get; private set; }

    /// <param name="title">Poster title; empty falls back to the localized "Today's news".</param>
    /// <param name="onShown">Called once, from the Shown handler (the form is actually on screen).</param>
    public NotificationForm(string? title, Action? onShown)
    {
        _title = string.IsNullOrWhiteSpace(title) ? UiStrings.NotifyTitleFallback : title.Trim();
        _onShown = onShown;
        _rtl = CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;
        _dpi = DeviceDpi;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Theme.Current.PanelBg;
        Cursor = Cursors.Hand;
        Text = "NewsViewer";

        int w = Px(NotificationPlacement.WidthDips), h = Px(NotificationPlacement.HeightDips);
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        var (x, y) = NotificationPlacement.Compute(
            new ScreenRect(wa.Left, wa.Top, wa.Right, wa.Bottom), w, h, Px(NotificationPlacement.MarginDips), _rtl);
        Bounds = new Rectangle(x, y, w, h);

        // Close button: top-right (top-left for RTL), 4 DIPs inset.
        int c = Px(28), inset = Px(4);
        _closeRect = new Rectangle(_rtl ? inset : w - c - inset, inset, c, c);

        _toolTip.SetToolTip(this, UiStrings.NotifyOpenTooltip);

        // Screen readers announce the notification: "<heading>: <title>".
        AccessibleName = $"{UiStrings.NotifyHeading}: {_title}";
        AccessibleRole = AccessibleRole.Alert;

        _timer.Tick += (_, _) =>
        {
            if (_countdown.Tick(Bounds.Contains(Cursor.Position))) Finish();
        };
        Shown += OnFormShown;
    }

    private int Px(int dips) => NotificationPlacement.Dip(dips, _dpi);

    // Never activated, never in the taskbar / Alt-Tab, always topmost.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTopMost;
            return cp;
        }
    }

    private void OnFormShown(object? sender, EventArgs e)
    {
        if (_countdown.Shown())
        {
            ShownUtc = DateTime.UtcNow;
            _onShown?.Invoke();
        }
        _timer.Start();
    }

    private void Finish()
    {
        if (OutcomeUtc is not null) return;
        OutcomeUtc = DateTime.UtcNow;
        _timer.Stop();
        Close();
    }

    // ── Lock pause ────────────────────────────────────────────────────────────────

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { _registered = WTSRegisterSessionNotification(Handle, NotifyForThisSession); }
        catch { _registered = false; }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_registered)
        {
            try { WTSUnRegisterSessionNotification(Handle); } catch { }
            _registered = false;
        }
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmWtsSessionChange)
        {
            int code = m.WParam.ToInt32();
            if (code == WtsSessionLock) _countdown.SetLocked(true);
            else if (code == WtsSessionUnlock) _countdown.SetLocked(false);
        }
        base.WndProc(ref m);
    }

    // ── Input ─────────────────────────────────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool over = _closeRect.Contains(e.Location);
        if (over == _closeHover) return;
        _closeHover = over;
        _toolTip.SetToolTip(this, over ? UiStrings.NotifyDismissTooltip : UiStrings.NotifyOpenTooltip);
        Invalidate(_closeRect);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_closeHover) return;
        _closeHover = false;
        Invalidate(_closeRect);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        bool close = _closeRect.Contains(e.Location);
        if (!(close ? _countdown.Dismiss() : _countdown.Expand())) return;

        // Expanded: build, show and activate the poster BEFORE this form closes (see Program.cs).
        if (!close)
        {
            try { ExpandedForm = ExpandedFormFactory?.Invoke(); }
            catch { ExpandedForm = null; }
        }
        Finish();
    }

    // ── Painting ──────────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var theme = Theme.Current;
        int w = ClientSize.Width, h = ClientSize.Height;

        using (var border = new Pen(theme.Border))
            g.DrawRectangle(border, 0, 0, w - 1, h - 1);

        // Accent stripe on the leading edge.
        int stripe = Px(4);
        using (var accent = new SolidBrush(theme.Accent))
            g.FillRectangle(accent, _rtl ? w - stripe : 0, 0, stripe, h);

        // Text column: between the stripe and the close button.
        int lead = Px(16), trail = Px(36);
        int colW = w - stripe - lead - trail;
        var column = new Rectangle(_rtl ? trail : stripe + lead, 0, colW, h);

        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                    TextFormatFlags.VerticalCenter | (_rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft
                                                           : TextFormatFlags.Left);

        using (var headingFont = Ui.Font(11f, FontStyle.Bold))
        using (var titleFont = Ui.Font(9.5f))
        {
            TextRenderer.DrawText(g, UiStrings.NotifyHeading, headingFont,
                new Rectangle(column.X, Px(10), column.Width, Px(24)), theme.TextPrimary, flags);
            // The title is authored content: drawn with the same alignment but never forced right-to-left.
            var titleFlags = _rtl ? flags & ~TextFormatFlags.RightToLeft : flags;
            TextRenderer.DrawText(g, _title, titleFont,
                new Rectangle(column.X, Px(38), column.Width, Px(24)), theme.TextSecondary, titleFlags);
        }

        // Close ×
        if (_closeHover)
            using (var hover = new SolidBrush(theme.SurfaceHover))
                g.FillRectangle(hover, _closeRect);
        int pad = Px(9);
        using (var pen = new Pen(_closeHover ? theme.TextPrimary : theme.TextSecondary, Math.Max(1, Px(1))))
        {
            var r = Rectangle.Inflate(_closeRect, -pad, -pad);
            g.DrawLine(pen, r.Left, r.Top, r.Right - 1, r.Bottom - 1);
            g.DrawLine(pen, r.Right - 1, r.Top, r.Left, r.Bottom - 1);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSUnRegisterSessionNotification(IntPtr hWnd);
}
