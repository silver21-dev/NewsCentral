using System.Diagnostics;
using NewsCentral.Models;
using NewsCentral.Models.IndexFile;
using NewsViewer.Resources;
using NewsViewer.Services;

namespace NewsViewer.Forms;

public sealed class ViewerForm : Form
{
    // Image-area bounds: the area takes the content's exact aspect ratio fitted within
    // MaxImgW×MaxImgH (16:9 content reproduces the classic 1600×900). The Min clamps only
    // bite for extreme aspect ratios; the PictureBox stays SizeMode.Zoom so those rare
    // cases letterbox gracefully instead of producing a degenerate window.
    private const int MaxImgW = 1600, MaxImgH = 900;
    private const int MinImgW = 960, MinImgH = 540;
    private const int SidePanelWidth = 220;
    private const int PosterStripHeight = 44;
    private const int PanelMargin = 16;
    private const int ContentWidth = SidePanelWidth - (PanelMargin * 2);   // 168
    private const int ProgressWidth = 144;
    private const int FrameThickness = 5;

    // Per-instance geometry, computed in the constructor from the loaded bitmap.
    private readonly int _imageW;
    private readonly int _imageH;
    private readonly int _formW;
    private readonly int _formH;

    private readonly PublishedAssignmentIndex _assignment;
    private readonly string _imagePath;
    private readonly TelemetryWriter _telemetry;
    private readonly ViewerStateService _viewerState;
    private readonly bool _isOnline;

    private readonly PictureBox _pictureBox;
    private readonly Label _lblPosterText;
    private readonly Panel _pnlSide;
    private readonly RoundedPanel _pnlAutoClose;
    private readonly CheckBox _chkAutoClose;
    private readonly Label _lblCountdown;
    private readonly Label _lblCountdownUnit;
    private readonly Panel _progressFill;
    private readonly ToolTip _toolTip = new();

    private readonly System.Windows.Forms.Timer? _timer;
    private int _secondsRemaining;
    private int _totalSeconds;
    private readonly int _effectiveDurationSeconds;
    private readonly DateTime _sessionStart = DateTime.UtcNow;
    private string _closeReason = "UserClose";
    // False for a --manual launch: the daily-gate marker is stamped only by scheduled launches.
    private readonly bool _recordShown;

    public ViewerForm(
        PublishedAssignmentIndex assignment,
        string imagePath,
        TelemetryWriter telemetry,
        ViewerStateService viewerState,
        bool isOnline,
        int effectiveDurationSeconds,
        bool recordShown = true)
    {
        _recordShown = recordShown;
        _assignment = assignment;
        _imagePath = imagePath;
        _telemetry = telemetry;
        _viewerState = viewerState;
        _isOnline = isOnline;
        _effectiveDurationSeconds = effectiveDurationSeconds;

        // ── Image — loaded FIRST so the window can adapt to the content's aspect ratio ──
        // Missing file / decode failure → null bitmap → 1600×900 fallback area and continue
        // (the pre-adaptive silent-tolerance behavior, preserved).
        Bitmap? bmp = null;
        if (File.Exists(_imagePath))
        {
            try
            {
                var bytes = File.ReadAllBytes(_imagePath);
                using var ms = new MemoryStream(bytes);
                bmp = new Bitmap(ms);
            }
            catch { }
        }

        // Image area = the bitmap's aspect ratio fitted within MaxImgW×MaxImgH (upscaling
        // small images to fit the box is intended), clamped to the Min bounds for extreme
        // ratios — where SizeMode.Zoom letterboxes gracefully.
        if (bmp is not null)
        {
            double scale = Math.Min((double)MaxImgW / bmp.Width, (double)MaxImgH / bmp.Height);
            _imageW = Math.Clamp((int)Math.Round(bmp.Width * scale), MinImgW, MaxImgW);
            _imageH = Math.Clamp((int)Math.Round(bmp.Height * scale), MinImgH, MaxImgH);
        }
        else
        {
            _imageW = MaxImgW;
            _imageH = MaxImgH;
        }
        _formW = _imageW + SidePanelWidth;
        _formH = _imageH + PosterStripHeight;

        // ── Form — solid 5px frame around the whole window ───────────────────
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(
            _imageW + SidePanelWidth + 2 * FrameThickness,
            _imageH + PosterStripHeight + 2 * FrameThickness);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Current.WindowFrame;
        TopMost = true;
        Text = "NewsViewer";
        Paint += OnFramePaint;

        // ── Root content host (inset by the frame thickness) ─────────────────
        var root = new Panel
        {
            Location = new Point(FrameThickness, FrameThickness),
            Size = new Size(_formW, _formH),
            BackColor = Theme.Current.PanelBg
        };
        Controls.Add(root);

        // ── PictureBox — sized to the content's aspect ratio, gray stage backing ──
        _pictureBox = new PictureBox
        {
            Location = new Point(0, 0),
            Size = new Size(_imageW, _imageH),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Theme.Current.Stage,
            Image = bmp
        };
        root.Controls.Add(_pictureBox);

        // ── Side panel — fixed column, gray surface, left separator ─────────
        _pnlSide = new Panel
        {
            Location = new Point(_imageW, 0),
            Size = new Size(SidePanelWidth, _formH),
            BackColor = Theme.Current.PanelBg
        };
        _pnlSide.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Current.Border);
            e.Graphics.DrawLine(pen, 0, 0, 0, _pnlSide.Height);
        };
        root.Controls.Add(_pnlSide);

        // ── Branding block (~64px): wordmark + status pill ───────────────────
        // Rhythm: 16px outer margins, 12px between items.
        var lblWordmark = new Label
        {
            Text = UiStrings.AppName,
            Font = Ui.Font(13f, FontStyle.Bold),   // GDI+ has no SemiBold FontStyle; Bold is the nearest weight
            ForeColor = Theme.Current.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(PanelMargin, 16)
        };
        _pnlSide.Controls.Add(lblWordmark);

        // Status pill — rounded surface chip; the text is drawn in its Paint handler so no
        // opaque child rectangle can cover the rounded corners.
        var statusText = _isOnline ? UiStrings.Online : UiStrings.Offline;
        var statusColor = _isOnline ? Theme.Current.OnlineFg : Theme.Current.OfflineFg;
        var pillFont = Ui.Font(8.5f);
        var statusPill = new RoundedPanel
        {
            Radius = 10,
            FillColor = Theme.Current.Surface,
            BorderColor = Theme.Current.Border,
            Location = new Point(PanelMargin, 48),
            Size = new Size(TextRenderer.MeasureText(statusText, pillFont).Width + 20, 22)
        };
        statusPill.Paint += (_, e) => TextRenderer.DrawText(
            e.Graphics, statusText, pillFont, statusPill.ClientRectangle, statusColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        _pnlSide.Controls.Add(statusPill);

        // Primary action — accent-filled Close.
        var btnClose = new RoundedButton
        {
            IsPrimary = true,
            Text = UiStrings.Close,
            Font = Ui.Font(11f),
            Size = new Size(ContentWidth, 40),
            Location = new Point(PanelMargin, 84)
        };
        btnClose.Click += (_, _) => { _closeReason = "UserClose"; Close(); };
        _pnlSide.Controls.Add(btnClose);

        // Secondary action — ghost/surface button.
        var btnMoreInfo = new RoundedButton
        {
            Text = UiStrings.MoreInformation,
            Font = Ui.Font(8.5f),
            Size = new Size(ContentWidth, 48),
            Location = new Point(PanelMargin, 136)
        };
        btnMoreInfo.Click += OnMoreInfoClicked;
        _pnlSide.Controls.Add(btnMoreInfo);

        // ── Auto-close card — TableLayoutPanel guarantees no row overlap ─────
        _pnlAutoClose = new RoundedPanel
        {
            Location = new Point(PanelMargin, 196),
            Size = new Size(ContentWidth, 126),
            FillColor = Theme.Current.Surface,
            BorderColor = Theme.Current.Border,
            Padding = new Padding(1)
        };
        _pnlSide.Controls.Add(_pnlAutoClose);

        var cardLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.Current.Surface,
            Padding = new Padding(11, 9, 11, 9)
        };
        cardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _pnlAutoClose.Controls.Add(cardLayout);

        var header = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Current.Surface,
            Margin = new Padding(0, 0, 0, 6)
        };

        _chkAutoClose = new CheckBox
        {
            Text = string.Empty,
            Checked = true,
            AutoSize = false,
            Size = new Size(16, 16),
            Margin = new Padding(0, 1, 7, 0),
            BackColor = Theme.Current.Surface,
            FlatStyle = FlatStyle.Standard,
            Cursor = Cursors.Hand
        };
        _chkAutoClose.CheckedChanged += OnAutoCloseChanged;

        var lblAutoCloseCaption = new Label
        {
            Text = UiStrings.FormClosesIn,
            AutoSize = true,
            Font = Ui.Font(9.5f),
            ForeColor = Theme.Current.TextPrimary,
            BackColor = Theme.Current.Surface,
            Margin = new Padding(0, 2, 0, 0),
            Cursor = Cursors.Hand
        };
        lblAutoCloseCaption.Click += (_, _) => _chkAutoClose.Checked = !_chkAutoClose.Checked;

        header.Controls.Add(_chkAutoClose);
        header.Controls.Add(lblAutoCloseCaption);
        cardLayout.Controls.Add(header, 0, 0);

        _lblCountdown = new Label
        {
            Text = string.Empty,
            AutoSize = true,
            Font = Ui.Font(11f),
            ForeColor = Theme.Current.TextPrimary,
            BackColor = Theme.Current.Surface,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0)
        };
        cardLayout.Controls.Add(_lblCountdown, 0, 1);

        _lblCountdownUnit = new Label
        {
            Text = UiStrings.Seconds,
            AutoSize = true,
            Font = Ui.Font(8.5f),
            ForeColor = Theme.Current.TextSecondary,
            BackColor = Theme.Current.Surface,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 2, 0, 6)
        };
        cardLayout.Controls.Add(_lblCountdownUnit, 0, 2);

        var progressTrack = new Panel
        {
            Size = new Size(ProgressWidth, 5),
            BackColor = Theme.Current.Border,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0)
        };
        _progressFill = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(ProgressWidth, 5),
            BackColor = Theme.Current.Accent
        };
        progressTrack.Controls.Add(_progressFill);
        cardLayout.Controls.Add(progressTrack, 0, 3);

        _toolTip.SetToolTip(_chkAutoClose, UiStrings.AutoCloseTooltip);
        _toolTip.SetToolTip(lblAutoCloseCaption, UiStrings.AutoCloseTooltip);

        // ── Caption bar — hairline divider ties it to the image above ────────
        var pnlPoster = new Panel
        {
            Location = new Point(0, _imageH),
            Size = new Size(_imageW, PosterStripHeight),
            BackColor = Theme.Current.PanelBg
        };
        pnlPoster.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Current.Border);
            e.Graphics.DrawLine(pen, 0, 0, pnlPoster.Width, 0);
        };
        root.Controls.Add(pnlPoster);

        var posterText = !string.IsNullOrWhiteSpace(_assignment.PosterText)
            ? _assignment.PosterText
            : _assignment.PresentationName;

        _lblPosterText = new Label
        {
            Text = posterText,
            Font = Ui.Font(12.5f),
            ForeColor = Theme.Current.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = false,
            Location = new Point(0, 1),
            Size = new Size(_imageW, PosterStripHeight - 1),
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(0)
        };
        pnlPoster.Controls.Add(_lblPosterText);

        // ── Countdown timer ──────────────────────────────────────────────────
        // Duration is already resolved by the caller (PresentationDefaults.ResolveDuration): the view
        // performs NO resolution of its own. -1 means never auto-close — no timer, and the countdown
        // card is hidden so there is no frozen "0" or stale count; the poster stays until the user
        // closes it (Close button, which leaves _closeReason at its "UserClose" default).
        if (_effectiveDurationSeconds == PresentationDefaults.NeverAutoClose)
        {
            _pnlAutoClose.Visible = false;
        }
        else
        {
            _secondsRemaining = _effectiveDurationSeconds;
            _totalSeconds = _secondsRemaining;

            UpdateCountdownUi();

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }

        FormClosed += OnFormClosed;
    }

    // ── Frame ────────────────────────────────────────────────────────────────

    private void OnFramePaint(object? sender, PaintEventArgs e)
    {
        int w = ClientSize.Width;
        int h = ClientSize.Height;
        using var edge = new Pen(Theme.Current.FrameEdge);
        e.Graphics.DrawRectangle(edge, 0, 0, w - 1, h - 1);
        e.Graphics.DrawRectangle(edge, FrameThickness - 1, FrameThickness - 1,
            w - 2 * (FrameThickness - 1) - 1, h - 2 * (FrameThickness - 1) - 1);
    }

    // ── Countdown ──────────────────────────────────────────────────────────────

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _secondsRemaining--;
        UpdateCountdownUi();
        if (_secondsRemaining <= 0)
        {
            _closeReason = "Timeout";
            Close();
        }
    }

    private void OnAutoCloseChanged(object? sender, EventArgs e)
    {
        if (_chkAutoClose.Checked)
        {
            _lblCountdown.ForeColor = Theme.Current.TextPrimary;
            _lblCountdownUnit.ForeColor = Theme.Current.TextSecondary;
            _progressFill.BackColor = Theme.Current.Accent;
            _timer?.Start();
        }
        else
        {
            _timer?.Stop();
            _lblCountdown.ForeColor = Theme.Current.AccentPaused;
            _lblCountdownUnit.ForeColor = Theme.Current.AccentPaused;
            _progressFill.BackColor = Theme.Current.AccentPaused;
        }
    }

    private void UpdateCountdownUi()
    {
        _lblCountdown.Text = _secondsRemaining.ToString();
        int w = _totalSeconds > 0
            ? (int)Math.Round(ProgressWidth * (double)_secondsRemaining / _totalSeconds)
            : 0;
        _progressFill.Width = Math.Clamp(w, 0, ProgressWidth);
    }

    // ── More info ────────────────────────────────────────────────────────────

    private void OnMoreInfoClicked(object? sender, EventArgs e)
    {
        var url = _assignment.Content.MoreInfoUrl;
        if (!string.IsNullOrWhiteSpace(url))
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }
        _closeReason = "UrlLaunch";
        Close();
    }

    // ── Cleanup ────────────────────────────────────────────────────────────────

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        _timer?.Stop();
        _timer?.Dispose();
        _toolTip.Dispose();
        _pictureBox.Image?.Dispose();

        if (_recordShown)
            _viewerState.RecordShown(_assignment.PresentationId);

        _telemetry.WriteSession(
            _assignment.PresentationId,
            _assignment.SourceTeamFolderName,
            _sessionStart,
            DateTime.UtcNow,
            _closeReason);
    }

    // ── Fonts — the single source for every control font in this form ─────────
    // Segoe UI Variable with Segoe UI fallback when not installed: the Display
    // optical variant at wordmark sizes (≥ 13pt), the Text variant for body sizes.
    // No other `new Font(...)` may appear in this form.
    private static class Ui
    {
        private static readonly string TextFamily = Resolve("Segoe UI Variable Text");
        private static readonly string DisplayFamily = Resolve("Segoe UI Variable Display");

        private static string Resolve(string family)
        {
            using var installed = new System.Drawing.Text.InstalledFontCollection();
            return installed.Families.Any(f => string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase))
                ? family
                : "Segoe UI";
        }

        internal static Font Font(float size, FontStyle style = FontStyle.Regular)
            => new(size >= 13f ? DisplayFamily : TextFamily, size, style);
    }
}