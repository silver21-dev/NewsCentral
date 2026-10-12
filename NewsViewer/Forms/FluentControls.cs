using System.ComponentModel;
using System.Drawing.Drawing2D;
using NewsCentral.Ui;

namespace NewsViewer.Forms;

/// <summary>
/// Instance color palette for the viewer UI. Two built-in palettes exist — <see cref="Light"/>
/// (the classic Fluent gray look) and <see cref="Dark"/> (the default). The active palette is
/// <see cref="Current"/>, resolved ONCE in Program.cs from Ui:Theme (registry REG_SZ override
/// Ui\Theme = "Dark" | "Light"; code default Dark) before any Form is constructed — the same
/// resolve-once-then-pass pattern used for DisplayDurationSeconds.
/// </summary>
internal sealed class Theme
{
    internal Color PanelBg { get; init; }
    internal Color Surface { get; init; }
    internal Color SurfaceHover { get; init; }
    internal Color SurfacePress { get; init; }
    internal Color Border { get; init; }
    internal Color BorderHover { get; init; }
    internal Color BorderPress { get; init; }
    internal Color Accent { get; init; }
    internal Color AccentPaused { get; init; }
    internal Color TextPrimary { get; init; }
    internal Color TextSecondary { get; init; }
    internal Color Stage { get; init; }
    internal Color WindowFrame { get; init; }
    internal Color FrameEdge { get; init; }
    internal Color OnlineFg { get; init; }
    internal Color OfflineFg { get; init; }


    // Countdown progress bar. Unset (the built-in palettes) = the border colour for the track and the
    // accent for the fill, exactly as the poster always drew them; the frame palette sets both explicitly.
    private Color? _progressTrack;
    private Color? _progressFill;
    internal Color ProgressTrack { get => _progressTrack ?? Border; init => _progressTrack = value; }
    internal Color ProgressFill { get => _progressFill ?? Accent; init => _progressFill = value; }

    private static Rgb ToRgb(Color c) => new(c.R, c.G, c.B);
    private static Color ToColor(Rgb c) => Color.FromArgb(c.R, c.G, c.B);

    /// <summary>The chrome colours the frame settings can change (see <see cref="FramePaletteBuilder"/>).</summary>
    internal ChromePalette ToChrome() => new(
        ToRgb(PanelBg), ToRgb(Surface), ToRgb(SurfaceHover), ToRgb(SurfacePress), ToRgb(Border),
        ToRgb(BorderHover), ToRgb(BorderPress), ToRgb(Accent), ToRgb(AccentPaused), ToRgb(TextPrimary),
        ToRgb(TextSecondary), ToRgb(WindowFrame), ToRgb(FrameEdge), ToRgb(ProgressTrack), ToRgb(ProgressFill));

    /// <summary>A copy of this theme with the chrome colours of <paramref name="p"/>; Stage and the
    /// online/offline colours are kept.</summary>
    internal Theme WithChrome(ChromePalette p) => new()
    {
        PanelBg = ToColor(p.PanelBg), Surface = ToColor(p.Surface), SurfaceHover = ToColor(p.SurfaceHover),
        SurfacePress = ToColor(p.SurfacePress), Border = ToColor(p.Border), BorderHover = ToColor(p.BorderHover),
        BorderPress = ToColor(p.BorderPress), Accent = ToColor(p.Accent), AccentPaused = ToColor(p.AccentPaused),
        TextPrimary = ToColor(p.TextPrimary), TextSecondary = ToColor(p.TextSecondary),
        Stage = Stage, WindowFrame = ToColor(p.WindowFrame), FrameEdge = ToColor(p.FrameEdge),
        OnlineFg = OnlineFg, OfflineFg = OfflineFg,
        ProgressTrack = ToColor(p.ProgressTrack), ProgressFill = ToColor(p.ProgressFill)
    };
    internal const int Radius = 0;

    internal static readonly Theme Light = new()
    {
        PanelBg = Color.FromArgb(240, 240, 240),
        Surface = Color.FromArgb(225, 225, 225),
        SurfaceHover = Color.FromArgb(229, 241, 251),
        SurfacePress = Color.FromArgb(204, 228, 247),
        Border = Color.FromArgb(173, 173, 173),
        BorderHover = Color.FromArgb(0, 120, 215),
        BorderPress = Color.FromArgb(0, 84, 153),
        Accent = Color.FromArgb(0, 120, 215),
        AccentPaused = Color.FromArgb(160, 160, 160),
        TextPrimary = Color.FromArgb(0, 0, 0),
        TextSecondary = Color.FromArgb(96, 96, 96),
        Stage = Color.FromArgb(96, 96, 96),
        WindowFrame = Color.FromArgb(150, 150, 150),
        FrameEdge = Color.FromArgb(105, 105, 105),
        OnlineFg = Color.FromArgb(0, 130, 0),
        OfflineFg = Color.FromArgb(190, 0, 0)
    };

    internal static readonly Theme Dark = new()
    {
        PanelBg = Color.FromArgb(26, 26, 28),
        Surface = Color.FromArgb(38, 38, 42),
        SurfaceHover = Color.FromArgb(50, 50, 56),
        SurfacePress = Color.FromArgb(30, 30, 34),
        Border = Color.FromArgb(62, 62, 66),
        BorderHover = Color.FromArgb(76, 140, 220),
        BorderPress = Color.FromArgb(56, 110, 180),
        Accent = Color.FromArgb(76, 140, 220),
        AccentPaused = Color.FromArgb(90, 90, 96),
        TextPrimary = Color.FromArgb(240, 240, 240),
        TextSecondary = Color.FromArgb(150, 150, 155),
        Stage = Color.FromArgb(12, 12, 14),
        WindowFrame = Color.FromArgb(48, 48, 52),
        FrameEdge = Color.FromArgb(90, 90, 96),
        OnlineFg = Color.FromArgb(92, 200, 110),
        OfflineFg = Color.FromArgb(235, 110, 90)
    };

    // Declared AFTER Light/Dark: static initializers run in textual order, so Dark must
    // already exist when this default is evaluated. Program.cs overwrites it at startup.
    internal static Theme Current { get; set; } = Dark;

    /// <summary>
    /// The palette of the POSTER window only: <see cref="Current"/> with the fleet-wide frame colours
    /// (Ui:FrameColor / Ui:FrameTextColor) applied. The same object as <see cref="Current"/> when no frame
    /// colour is configured, so the default look is untouched. The notification form never uses it.
    /// </summary>
    internal static Theme Poster { get; set; } = Dark;

    internal static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        if (d <= 0) { path.AddRectangle(r); path.CloseFigure(); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class RoundedPanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Theme Theme { get; set; } = Theme.Current;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = Theme.Radius;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; } = Theme.Current.Surface;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Theme.Current.Border;

    public RoundedPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundedRect(rect, Radius);
        using (var b = new SolidBrush(FillColor)) e.Graphics.FillPath(b, path);
        using (var p = new Pen(BorderColor)) e.Graphics.DrawPath(p, path);
        base.OnPaint(e);
    }
}

internal sealed class RoundedButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Theme Theme { get; set; } = Theme.Current;

    /// <summary>
    /// Accent-filled primary action: fill = Theme.Accent, hover/press shaded ±12%, white
    /// text, no contrasting border (the outline is painted in the fill color). When false,
    /// the button renders as the standard ghost/surface style from the color properties.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsPrimary { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = Theme.Radius;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color NormalColor { get; set; } = Theme.Current.Surface;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HoverColor { get; set; } = Theme.Current.SurfaceHover;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color PressColor { get; set; } = Theme.Current.SurfacePress;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Theme.Current.Border;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HoverBorderColor { get; set; } = Theme.Current.BorderHover;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color PressBorderColor { get; set; } = Theme.Current.BorderPress;

    private bool _hover;
    private bool _press;

    public RoundedButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = Theme.Current.TextPrimary;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _press = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _press = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _press = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundedRect(rect, Radius);
        Color fill, bord;
        var text = Enabled ? ForeColor : Theme.TextSecondary;   // disabled buttons read as dimmed
        if (IsPrimary)
        {
            var accent = Theme.Accent;
            fill = _press ? Shade(accent, -0.12f) : _hover ? Shade(accent, 0.12f) : accent;
            bord = fill;
            text = Color.White;
        }
        else
        {
            fill = _press ? PressColor : _hover ? HoverColor : NormalColor;
            bord = _press ? PressBorderColor : _hover ? HoverBorderColor : BorderColor;
        }
        using (var b = new SolidBrush(fill)) e.Graphics.FillPath(b, path);
        using (var p = new Pen(bord)) e.Graphics.DrawPath(p, path);
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak;
        if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;   // keeps punctuation such as the status dot on the correct side
        TextRenderer.DrawText(e.Graphics, Text, Font, rect, text, flags);
    }

    // Positive amount blends toward white, negative toward black (fraction of the distance).
    private static Color Shade(Color c, float amount) => amount >= 0
        ? Color.FromArgb(c.R + (int)((255 - c.R) * amount),
                         c.G + (int)((255 - c.G) * amount),
                         c.B + (int)((255 - c.B) * amount))
        : Color.FromArgb((int)(c.R * (1 + amount)),
                         (int)(c.G * (1 + amount)),
                         (int)(c.B * (1 + amount)));
}
