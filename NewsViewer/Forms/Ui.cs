using System.Globalization;

namespace NewsViewer.Forms;

// ── Fonts — the single source for every control font in the viewer forms ─────────
// Segoe UI Variable with Segoe UI fallback when not installed: the Display
// optical variant at wordmark sizes (≥ 13pt), the Text variant for body sizes.
// No other `new Font(...)` may appear in a form.
internal static class Ui
{
    // CurrentUICulture is fixed for the life of this one-shot process (--culture is applied in
    // Main before any form), so the family is resolved once. CJK cultures use a CJK-capable UI
    // font for both variants; Resolve falls back to Segoe UI if it is missing.
    private static readonly string TextFamily = Resolve(CjkFamily() ?? "Segoe UI Variable Text");
    private static readonly string DisplayFamily = Resolve(CjkFamily() ?? "Segoe UI Variable Display");

    // zh-Hans (zh-CN, zh-SG, ...) and zh-Hant (zh-TW, zh-HK, zh-MO, ...) are found by walking the
    // culture's parent chain; null for every other culture.
    private static string? CjkFamily()
    {
        for (var c = CultureInfo.CurrentUICulture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            if (string.Equals(c.Name, "zh-Hans", StringComparison.OrdinalIgnoreCase)) return "Microsoft YaHei UI";
            if (string.Equals(c.Name, "zh-Hant", StringComparison.OrdinalIgnoreCase)) return "Microsoft JhengHei UI";
        }
        return null;
    }

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
