using NewsCentral.Ui;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class FramePaletteTests
{
    // The Dark theme's chrome colours (NewsViewer/Forms/FluentControls.cs).
    private static readonly ChromePalette Theme = new(
        PanelBg: new Rgb(26, 26, 28), Surface: new Rgb(38, 38, 42), SurfaceHover: new Rgb(50, 50, 56),
        SurfacePress: new Rgb(30, 30, 34), Border: new Rgb(62, 62, 66), BorderHover: new Rgb(76, 140, 220),
        BorderPress: new Rgb(56, 110, 180), Accent: new Rgb(76, 140, 220), AccentPaused: new Rgb(90, 90, 96),
        TextPrimary: new Rgb(240, 240, 240), TextSecondary: new Rgb(150, 150, 155),
        WindowFrame: new Rgb(48, 48, 52), FrameEdge: new Rgb(90, 90, 96),
        ProgressTrack: new Rgb(62, 62, 66), ProgressFill: new Rgb(76, 140, 220));

    private static ChromePalette Build(string? frame, string? text = null) =>
        FramePaletteBuilder.Build(Theme, frame, text).Palette;

    private static Rgb Hex(string s) { Assert.True(Rgb.TryParse(s, out var c)); return c; }

    // ── Parsing ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("#1E3A5F", 0x1E, 0x3A, 0x5F)]
    [InlineData("#1e3a5f", 0x1E, 0x3A, 0x5F)]
    [InlineData("#aBcDeF", 0xAB, 0xCD, 0xEF)]
    [InlineData("  #FFFFFF ", 255, 255, 255)]
    [InlineData("#000000", 0, 0, 0)]
    public void Parse_AcceptsRRGGBB_AnyCase(string text, int r, int g, int b)
    {
        Assert.True(Rgb.TryParse(text, out var c));
        Assert.Equal(new Rgb((byte)r, (byte)g, (byte)b), c);
    }

    [Theory]
    [InlineData("#12345")]
    [InlineData("123456")]
    [InlineData("#GGGGGG")]
    [InlineData("#1234567")]
    [InlineData("#FFF")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData(null)]
    public void Parse_RejectsEverythingElse(string? text) => Assert.False(Rgb.TryParse(text, out _));

    [Fact]
    public void ToHex_RoundTrips() => Assert.Equal("#1E3A5F", Hex("#1e3a5f").ToHex());

    // ── Fallbacks ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyFrame_IsExactlyTheTheme_WithNoIssues(string? frame)
    {
        var (palette, issues) = FramePaletteBuilder.Build(Theme, frame, "#FFD700");   // a text colour alone changes nothing
        Assert.Equal(Theme, palette);
        Assert.Empty(issues.Notes());
    }

    [Theory]
    [InlineData("#12345")]
    [InlineData("123456")]
    [InlineData("#GGGGGG")]
    [InlineData("nonsense")]
    public void InvalidFrame_FallsBackToTheWholeTheme_AndIsFlagged(string frame)
    {
        var (palette, issues) = FramePaletteBuilder.Build(Theme, frame, "#FFD700");
        Assert.Equal(Theme, palette);
        Assert.True(issues.FrameColorInvalid);
        Assert.Equal(new[] { "FrameColorInvalid" }, issues.Notes());
    }

    [Fact]
    public void InvalidTextAlone_FallsBackToAutomaticContrast_AndIsFlagged()
    {
        var (palette, issues) = FramePaletteBuilder.Build(Theme, "#1E3A5F", "oops");
        Assert.Equal(Hex("#FFFFFF"), palette.TextPrimary);
        Assert.False(issues.FrameColorInvalid);
        Assert.True(issues.FrameTextColorInvalid);
        Assert.Equal(new[] { "FrameTextColorInvalid" }, issues.Notes());
        Assert.Equal(Build("#1E3A5F"), palette);   // identical to no text colour at all
    }

    [Fact]
    public void BothInvalid_FlagsBoth_AndDoesNotThrow()
    {
        var (palette, issues) = FramePaletteBuilder.Build(Theme, "#zz", "#yy");
        Assert.Equal(Theme, palette);
        Assert.Equal(new[] { "FrameColorInvalid", "FrameTextColorInvalid" }, issues.Notes());
    }

    // ── Automatic text ──────────────────────────────────────────────────────────

    [Fact]
    public void AutoText_IsBlackOnLightFrames() => Assert.Equal(Rgb.Black, Build("#F0E6D2").TextPrimary);

    [Fact]
    public void AutoText_IsWhiteOnDarkFrames() => Assert.Equal(Rgb.White, Build("#1E3A5F").TextPrimary);

    [Fact]
    public void ExplicitText_IsUsed()
    {
        var p = Build("#1E3A5F", "#FFD700");
        Assert.Equal(Hex("#FFD700"), p.TextPrimary);
    }

    // ── Surfaces and shades ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("#1E3A5F")]
    [InlineData("#F0E6D2")]
    public void Panel_TitleBar_AndBorder_AreTheFrameColour(string frame)
    {
        var p = Build(frame);
        Assert.Equal(Hex(frame), p.PanelBg);       // side panel, title bar
        Assert.Equal(Hex(frame), p.WindowFrame);   // thick outer border
    }

    [Fact]
    public void DarkFrame_CardsAreLighter_HoverMoreSo()
    {
        var frame = Hex("#1E3A5F");
        var p = Build("#1E3A5F");
        Assert.True(p.Surface.RelativeLuminance > frame.RelativeLuminance);
        Assert.True(p.SurfaceHover.RelativeLuminance > p.Surface.RelativeLuminance);
        Assert.True(p.SurfacePress.RelativeLuminance > frame.RelativeLuminance);
        Assert.True(p.SurfacePress.RelativeLuminance < p.Surface.RelativeLuminance);
    }

    [Fact]
    public void LightFrame_CardsAreDarker_HoverMoreSo()
    {
        var frame = Hex("#F0E6D2");
        var p = Build("#F0E6D2");
        Assert.True(p.Surface.RelativeLuminance < frame.RelativeLuminance);
        Assert.True(p.SurfaceHover.RelativeLuminance < p.Surface.RelativeLuminance);
    }

    [Fact]
    public void ShadeDirection_FollowsTheFrame_NotAnExplicitTextColour()
    {
        var dark = Hex("#1E3A5F");
        var p = Build("#1E3A5F", "#000000");   // black text on a dark frame: the cards still go lighter
        Assert.True(p.Surface.RelativeLuminance > dark.RelativeLuminance);
    }

    [Fact]
    public void AutoText_KeepsAtLeast4_5Contrast_OnEveryCardState_ForAnyFrame()
    {
        // 16 steps per channel = 4096 frames, including every awkward mid-tone.
        for (int r = 0; r < 256; r += 17)
        for (int g = 0; g < 256; g += 17)
        for (int b = 0; b < 256; b += 17)
        {
            var frame = new Rgb((byte)r, (byte)g, (byte)b);
            var p = Build(frame.ToHex());
            Assert.True(Rgb.Contrast(p.TextPrimary, p.PanelBg) >= FramePaletteBuilder.MinCardContrast, $"frame {frame.ToHex()}");
            Assert.True(Rgb.Contrast(p.TextPrimary, p.Surface) >= FramePaletteBuilder.MinCardContrast, $"card {frame.ToHex()}");
            Assert.True(Rgb.Contrast(p.TextPrimary, p.SurfaceHover) >= FramePaletteBuilder.MinCardContrast, $"hover {frame.ToHex()}");
            Assert.True(Rgb.Contrast(p.TextPrimary, p.SurfacePress) >= FramePaletteBuilder.MinCardContrast, $"press {frame.ToHex()}");
        }
    }

    [Fact]
    public void Cards_AreVisiblyDifferentFromTheFrame_ForTypicalColours()
    {
        foreach (var frame in new[] { "#1E3A5F", "#F0E6D2", "#2B2B2B", "#C0392B", "#FFFFFF" })
        {
            var p = Build(frame);
            Assert.NotEqual(p.PanelBg, p.Surface);
        }
    }

    // ── Derived text and progress colours ───────────────────────────────────────

    [Fact]
    public void SecondaryText_IsPrimaryBlended35PercentTowardTheCard()
    {
        var p = Build("#1E3A5F", "#FFD700");
        Assert.Equal(Rgb.Blend(p.TextPrimary, p.Surface, 0.35), p.TextSecondary);
    }

    [Fact]
    public void Progress_IsDerivedFromTextAndCard()
    {
        var p = Build("#1E3A5F", "#FFD700");
        Assert.Equal(p.TextPrimary, p.ProgressFill);
        Assert.Equal(Rgb.Blend(p.Surface, p.TextPrimary, 0.30), p.ProgressTrack);
        Assert.Equal(Rgb.Blend(p.TextPrimary, p.Surface, 0.60), p.AccentPaused);
    }

    [Fact]
    public void AccentAndHoverBorders_AreUnchanged()
    {
        var p = Build("#1E3A5F");
        Assert.Equal(Theme.Accent, p.Accent);
        Assert.Equal(Theme.BorderHover, p.BorderHover);
        Assert.Equal(Theme.BorderPress, p.BorderPress);
    }

    // ── Robustness ──────────────────────────────────────────────────────────────

    [Fact]
    public void NeverThrows_OnGarbage()
    {
        foreach (var junk in new[] { null, "", " ", "#", "##", "#######", new string('x', 10_000), "\0\0", "#12\n34" })
        {
            var ex = Record.Exception(() => FramePaletteBuilder.Build(Theme, junk, junk));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void BlackAndWhiteFrames_Work()
    {
        Assert.Equal(Rgb.White, Build("#000000").TextPrimary);
        Assert.Equal(Rgb.Black, Build("#FFFFFF").TextPrimary);
    }
}
