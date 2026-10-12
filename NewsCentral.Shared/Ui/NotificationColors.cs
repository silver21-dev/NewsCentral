namespace NewsCentral.Ui;

/// <summary>
/// Colours of the "News arrived!" notification form. It follows the POSTER palette (the theme with the
/// frame colours applied): with no usable frame colour the poster palette is the theme, so the form looks
/// exactly as it always did.
/// </summary>
public sealed record NotificationColors(
    Rgb Background, Rgb Border, Rgb Accent, Rgb Heading, Rgb Title,
    Rgb CloseIcon, Rgb CloseIconHover, Rgb CloseHoverFill)
{
    /// <param name="poster">The poster palette (<see cref="FramePaletteBuilder.Build"/> result).</param>
    /// <param name="theme">The plain theme palette, to tell whether a frame colour is in effect.</param>
    public static NotificationColors From(ChromePalette poster, ChromePalette theme)
    {
        bool frameActive = poster != theme;
        return new NotificationColors(
            Background: poster.PanelBg,
            Border: poster.Border,
            Accent: poster.Accent,
            Heading: poster.TextPrimary,
            // The theme look keeps its dimmer secondary title colour; a branded frame uses the primary text colour.
            Title: frameActive ? poster.TextPrimary : poster.TextSecondary,
            CloseIcon: poster.TextSecondary,
            CloseIconHover: poster.TextPrimary,
            CloseHoverFill: poster.SurfaceHover);
    }
}

/// <summary>Colours of the Online/Offline status pill on the poster's side panel.</summary>
public readonly record struct PillColors(Rgb Fill, Rgb Border, Rgb Text, Rgb Dot)
{
    /// <summary>
    /// The pill follows the palette's card shade. The status colour (green/red) is kept for the dot and the
    /// text — except that, with a frame colour in effect, the text uses the primary text colour when the
    /// status colour on the card would be below <see cref="FramePaletteBuilder.MinCardContrast"/>. Without a
    /// frame colour nothing changes (the theme's own pill is left exactly as it is).
    /// </summary>
    public static PillColors Resolve(ChromePalette poster, ChromePalette theme, Rgb statusColor)
    {
        bool frameActive = poster != theme;
        var text = frameActive && Rgb.Contrast(statusColor, poster.Surface) < FramePaletteBuilder.MinCardContrast
            ? poster.TextPrimary
            : statusColor;
        return new PillColors(poster.Surface, poster.Border, text, statusColor);
    }
}
