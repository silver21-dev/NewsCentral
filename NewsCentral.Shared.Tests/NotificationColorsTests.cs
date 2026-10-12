using NewsCentral.Ui;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class NotificationColorsTests
{
    // The Dark theme's chrome colours (NewsViewer/Forms/FluentControls.cs).
    private static readonly ChromePalette Theme = new(
        PanelBg: new Rgb(26, 26, 28), Surface: new Rgb(38, 38, 42), SurfaceHover: new Rgb(50, 50, 56),
        SurfacePress: new Rgb(30, 30, 34), Border: new Rgb(62, 62, 66), BorderHover: new Rgb(76, 140, 220),
        BorderPress: new Rgb(56, 110, 180), Accent: new Rgb(76, 140, 220), AccentPaused: new Rgb(90, 90, 96),
        TextPrimary: new Rgb(240, 240, 240), TextSecondary: new Rgb(150, 150, 155),
        WindowFrame: new Rgb(48, 48, 52), FrameEdge: new Rgb(90, 90, 96),
        ProgressTrack: new Rgb(62, 62, 66), ProgressFill: new Rgb(76, 140, 220));

    private static readonly Rgb OnlineGreen = new(92, 200, 110);
    private static readonly Rgb OnlineGreenLightTheme = new(0, 130, 0);

    private static ChromePalette Poster(string? frame, string? text = null) =>
        FramePaletteBuilder.Build(Theme, frame, text).Palette;

    private static NotificationColors Colors(string? frame, string? text = null) =>
        NotificationColors.From(Poster(frame, text), Theme);

    // ── Notification form ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("#1E3A5F", null)]
    [InlineData("#F0E6D2", null)]
    [InlineData("#1E3A5F", "#FFD700")]
    public void ValidFrame_NotificationFollowsThePosterPalette(string frame, string? text)
    {
        var poster = Poster(frame, text);
        var c = NotificationColors.From(poster, Theme);

        Assert.Equal(poster.PanelBg, c.Background);          // frame colour
        Assert.Equal(poster.Border, c.Border);               // palette hairline
        Assert.Equal(poster.TextPrimary, c.Heading);
        Assert.Equal(poster.TextPrimary, c.Title);           // heading and title = primary text
        Assert.Equal(poster.TextPrimary, c.CloseIconHover);
        Assert.Equal(poster.SurfaceHover, c.CloseHoverFill); // derived hover shade
        Assert.Equal(Theme.Accent, c.Accent);                // accent colours unchanged
        Assert.NotEqual(Theme.PanelBg, c.Background);
    }

    [Fact]
    public void NavyFrame_GivesWhiteText_BeigeFrame_GivesBlackText()
    {
        Assert.Equal(Rgb.White, Colors("#1E3A5F").Heading);
        Assert.Equal(Rgb.Black, Colors("#F0E6D2").Heading);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("#12345")]
    [InlineData("123456")]
    public void EmptyOrInvalidFrame_NotificationIsExactlyTheTheme(string? frame)
    {
        var c = Colors(frame, "#FFD700");
        Assert.Equal(Theme.PanelBg, c.Background);
        Assert.Equal(Theme.Border, c.Border);
        Assert.Equal(Theme.Accent, c.Accent);
        Assert.Equal(Theme.TextPrimary, c.Heading);
        Assert.Equal(Theme.TextSecondary, c.Title);           // today's dimmer title
        Assert.Equal(Theme.TextSecondary, c.CloseIcon);
        Assert.Equal(Theme.TextPrimary, c.CloseIconHover);
        Assert.Equal(Theme.SurfaceHover, c.CloseHoverFill);
    }

    // ── Online pill ─────────────────────────────────────────────────────────────

    [Fact]
    public void Pill_WithoutFrame_IsUnchanged_EvenWhenContrastIsLow()
    {
        // Light theme: green on its card is only about 3.6:1 — the existing look is left exactly as it is.
        var light = Theme with { Surface = new Rgb(225, 225, 225), Border = new Rgb(173, 173, 173) };
        var pill = PillColors.Resolve(light, light, OnlineGreenLightTheme);
        Assert.Equal(light.Surface, pill.Fill);
        Assert.Equal(light.Border, pill.Border);
        Assert.Equal(OnlineGreenLightTheme, pill.Text);
        Assert.Equal(OnlineGreenLightTheme, pill.Dot);
    }

    [Fact]
    public void Pill_WithFrame_BackgroundFollowsTheCardShade()
    {
        var poster = Poster("#1E3A5F");
        var pill = PillColors.Resolve(poster, Theme, OnlineGreen);
        Assert.Equal(poster.Surface, pill.Fill);
        Assert.Equal(poster.Border, pill.Border);
    }

    [Fact]
    public void Pill_GreenOnDarkCard_KeepsItsColour_BothDotAndText()
    {
        var poster = Poster("#101820");
        Assert.True(Rgb.Contrast(OnlineGreen, poster.Surface) >= FramePaletteBuilder.MinCardContrast);
        var pill = PillColors.Resolve(poster, Theme, OnlineGreen);
        Assert.Equal(OnlineGreen, pill.Text);
        Assert.Equal(OnlineGreen, pill.Dot);
    }

    [Fact]
    public void Pill_LowContrastGreen_TextSwitchesToPrimary_DotKeepsGreen()
    {
        var poster = Poster("#F0E6D2");   // beige card: bright green is far below 4.5:1
        Assert.True(Rgb.Contrast(OnlineGreen, poster.Surface) < FramePaletteBuilder.MinCardContrast);
        var pill = PillColors.Resolve(poster, Theme, OnlineGreen);
        Assert.Equal(poster.TextPrimary, pill.Text);
        Assert.Equal(Rgb.Black, pill.Text);
        Assert.Equal(OnlineGreen, pill.Dot);
    }

    [Fact]
    public void Pill_RuleIsExactlyTheFourPointFiveThreshold()
    {
        var poster = Poster("#1E3A5F");
        foreach (var status in new[] { OnlineGreen, new Rgb(235, 110, 90), new Rgb(190, 0, 0), new Rgb(0, 130, 0) })
        {
            var pill = PillColors.Resolve(poster, Theme, status);
            bool low = Rgb.Contrast(status, poster.Surface) < FramePaletteBuilder.MinCardContrast;
            Assert.Equal(low ? poster.TextPrimary : status, pill.Text);
            Assert.Equal(status, pill.Dot);
        }
    }
}
