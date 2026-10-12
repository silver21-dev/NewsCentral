namespace NewsCentral.Ui;

/// <summary>An opaque sRGB colour. Plain bytes so the palette logic stays pure (no System.Drawing).</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>
    /// Parses exactly <c>#RRGGBB</c> (hex digits in either case; surrounding whitespace ignored).
    /// Anything else — <c>#12345</c>, <c>123456</c>, <c>#GGGGGG</c>, <c>#RGB</c>, empty — is rejected.
    /// </summary>
    public static bool TryParse(string? text, out Rgb color)
    {
        color = default;
        var s = text?.Trim();
        if (s is not { Length: 7 } || s[0] != '#') return false;
        for (int i = 1; i < 7; i++)
            if (!Uri.IsHexDigit(s[i])) return false;
        color = new Rgb(
            Convert.ToByte(s.Substring(1, 2), 16),
            Convert.ToByte(s.Substring(3, 2), 16),
            Convert.ToByte(s.Substring(5, 2), 16));
        return true;
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);

    /// <summary>WCAG 2.x relative luminance (0 = black, 1 = white).</summary>
    public double RelativeLuminance => 0.2126 * Linear(R) + 0.7152 * Linear(G) + 0.0722 * Linear(B);

    private static double Linear(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>WCAG contrast ratio, 1 (identical) to 21 (black on white).</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        double la = a.RelativeLuminance, lb = b.RelativeLuminance;
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary><paramref name="amount"/> (0..1) of the way from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static Rgb Blend(Rgb from, Rgb to, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        return new Rgb(Mix(from.R, to.R, amount), Mix(from.G, to.G, amount), Mix(from.B, to.B, amount));

        static byte Mix(byte a, byte b, double t) => (byte)Math.Clamp((int)Math.Round(a + (b - a) * t), 0, 255);
    }
}

/// <summary>
/// The colours of the poster window's chrome that the frame settings can change, plus the ones that must
/// stay put (accent, hover/press borders). A theme maps to one of these and back (NewsViewer's Theme), so
/// the palette maths is testable without WinForms.
/// </summary>
public sealed record ChromePalette(
    Rgb PanelBg,
    Rgb Surface,
    Rgb SurfaceHover,
    Rgb SurfacePress,
    Rgb Border,
    Rgb BorderHover,
    Rgb BorderPress,
    Rgb Accent,
    Rgb AccentPaused,
    Rgb TextPrimary,
    Rgb TextSecondary,
    Rgb WindowFrame,
    Rgb FrameEdge,
    Rgb ProgressTrack,
    Rgb ProgressFill);

/// <summary>Which configured frame colours were unusable (for the launch-log notes).</summary>
public readonly record struct FrameColorIssues(bool FrameColorInvalid, bool FrameTextColorInvalid)
{
    public IEnumerable<string> Notes()
    {
        if (FrameColorInvalid) yield return "FrameColorInvalid";
        if (FrameTextColorInvalid) yield return "FrameTextColorInvalid";
    }
}

/// <summary>
/// Derives the poster window's palette from the theme and the fleet-wide <c>Ui:FrameColor</c> /
/// <c>Ui:FrameTextColor</c> settings. Pure and total: it never throws, and with no (or an unusable)
/// frame colour the result is the theme palette itself.
/// </summary>
public static class FramePaletteBuilder
{
    /// <summary>Minimum contrast (WCAG AA, normal text) the automatic text colour keeps on cards.</summary>
    public const double MinCardContrast = 4.5;

    // Cards / secondary buttons are the frame shifted toward the text extreme by a small fixed amount
    // (lighter on dark frames, darker on light ones); hover shifts a little further, press a little less.
    private const double CardShift = 0.10;
    private const double HoverShift = 0.18;
    private const double PressShift = 0.05;
    private const double BorderShift = 0.22;
    private const double EdgeShift = 0.30;

    public static (ChromePalette Palette, FrameColorIssues Issues) Build(
        ChromePalette theme, string? frameColor, string? frameTextColor)
    {
        bool frameGiven = !string.IsNullOrWhiteSpace(frameColor);
        bool textGiven = !string.IsNullOrWhiteSpace(frameTextColor);
        bool frameValid = Rgb.TryParse(frameColor, out var frame);
        bool textValid = Rgb.TryParse(frameTextColor, out var explicitText);

        var issues = new FrameColorIssues(
            FrameColorInvalid: frameGiven && !frameValid,
            FrameTextColorInvalid: textGiven && !textValid);

        // No usable frame colour: the whole frame is the theme. (A text colour alone changes nothing.)
        if (!frameGiven || !frameValid) return (theme, issues);

        // The extreme (white or black) that reads best on the frame: it is both the automatic text colour
        // and the direction the cards are shifted in.
        var extreme = Rgb.Contrast(Rgb.White, frame) >= Rgb.Contrast(Rgb.Black, frame) ? Rgb.White : Rgb.Black;
        bool autoText = !(textGiven && textValid);
        var text = autoText ? extreme : explicitText;

        // For the automatic text colour the shifts are trimmed so text on a card stays >= 4.5:1 (the frame
        // itself is always >= 4.58:1 against its best extreme). An explicit colour is the admin's choice.
        Rgb Shifted(double amount) =>
            Rgb.Blend(frame, extreme, autoText ? FitShift(frame, extreme, text, amount) : amount);

        var card = Shifted(CardShift);
        var hover = Shifted(HoverShift);
        var press = Shifted(PressShift);

        var palette = theme with
        {
            PanelBg = frame,
            WindowFrame = frame,
            FrameEdge = Rgb.Blend(frame, extreme, EdgeShift),
            Border = Rgb.Blend(frame, extreme, BorderShift),
            Surface = card,
            SurfaceHover = hover,
            SurfacePress = press,
            TextPrimary = text,
            TextSecondary = Rgb.Blend(text, card, 0.35),
            ProgressTrack = Rgb.Blend(card, text, 0.30),
            ProgressFill = text,
            AccentPaused = Rgb.Blend(text, card, 0.60)
            // Accent, BorderHover and BorderPress stay the theme's: the Close button keeps its accent.
        };
        return (palette, issues);
    }

    // Largest amount <= desired (in 1% steps) at which `text` still has MinCardContrast on the shifted frame.
    private static double FitShift(Rgb frame, Rgb extreme, Rgb text, double desired)
    {
        for (double amount = desired; amount > 0.0; amount -= 0.01)
            if (Rgb.Contrast(text, Rgb.Blend(frame, extreme, amount)) >= MinCardContrast)
                return Math.Max(amount, 0.0);
        return 0.0;
    }
}
