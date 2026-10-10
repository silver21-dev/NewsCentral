using NewsCentral.Configuration;

namespace NewsCentral.Shared.Tests;

public sealed class DeliveredImageResolverTests
{
    private const string Company = "TestCo";
    private const string Svc = @"Software\TestCo\NewsCentral\NewsService\Delivery";
    private const string Csp = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP";
    private const string WpPath = @"C:\Windows\Web\NewsCentral\wp.jpg";
    private const string LsPath = @"C:\Windows\Web\NewsCentral\ls.jpg";

    private static DeliveredImageResolver Make(
        Dictionary<(string, string), object?> reg, Func<string, bool>? exists = null, List<string>? warnings = null) =>
        new((k, v) => reg.TryGetValue((k, v), out var o) ? o : null,
            exists ?? (_ => true),
            warnings is null ? null : warnings.Add,
            Company);

    private static Dictionary<(string, string), object?> AllOn() => new()
    {
        [(Svc, "WallpaperEnabled")] = 1,
        [(Svc, "LockScreenEnabled")] = 1,
        [(Csp, "DesktopImagePath")] = WpPath,
        [(Csp, "LockScreenImagePath")] = LsPath
    };

    [Fact]
    public void AllConditionsMet_ReturnsPaths()
    {
        var r = Make(AllOn());
        Assert.Equal(WpPath, r.Resolve(DeliveredSurface.Wallpaper));
        Assert.Equal(LsPath, r.Resolve(DeliveredSurface.LockScreen));
    }

    [Fact]
    public void FlagOff_OnlyThatSurfaceIsUnavailable()
    {
        var reg = AllOn();
        reg[(Svc, "LockScreenEnabled")] = 0;
        var r = Make(reg);
        Assert.Equal(WpPath, r.Resolve(DeliveredSurface.Wallpaper));
        Assert.Null(r.Resolve(DeliveredSurface.LockScreen));
    }

    [Fact]
    public void FlagAbsent_UsesNewsServiceDefault()
    {
        var reg = AllOn();
        reg.Remove((Svc, "WallpaperEnabled"));
        reg.Remove((Svc, "LockScreenEnabled"));
        bool Default(string key) =>
            bool.Parse(ConfigManifests.NewsService.Keys.First(k => k.CanonicalKey == key).Default);
        var r = Make(reg);
        Assert.Equal(Default("Delivery:WallpaperEnabled"), r.Resolve(DeliveredSurface.Wallpaper) is not null);
        Assert.Equal(Default("Delivery:LockScreenEnabled"), r.Resolve(DeliveredSurface.LockScreen) is not null);
    }

    [Fact]
    public void FlagAsRegSz_IsHonoured()
    {
        var reg = AllOn();
        reg[(Svc, "WallpaperEnabled")] = "false";
        Assert.Null(Make(reg).Resolve(DeliveredSurface.Wallpaper));
    }

    [Fact]
    public void CspValueMissing_Unavailable()
    {
        var reg = AllOn();
        reg.Remove((Csp, "DesktopImagePath"));
        Assert.Null(Make(reg).Resolve(DeliveredSurface.Wallpaper));
    }

    [Fact]
    public void CspValueEmpty_Unavailable()
    {
        var reg = AllOn();
        reg[(Csp, "LockScreenImagePath")] = "";
        Assert.Null(Make(reg).Resolve(DeliveredSurface.LockScreen));
    }

    [Fact]
    public void FileMissing_Unavailable()
    {
        var r = Make(AllOn(), exists: p => p != WpPath);
        Assert.Null(r.Resolve(DeliveredSurface.Wallpaper));
        Assert.Equal(LsPath, r.Resolve(DeliveredSurface.LockScreen));
    }

    [Fact]
    public void RegistryThrows_WarnsAndIsUnavailable()
    {
        var warnings = new List<string>();
        var r = new DeliveredImageResolver(
            (_, _) => throw new UnauthorizedAccessException("denied"), _ => true, warnings.Add, Company);
        Assert.Null(r.Resolve(DeliveredSurface.Wallpaper));
        Assert.Single(warnings);
    }

    [Fact]
    public void FileExistsThrows_WarnsAndIsUnavailable()
    {
        var reg = AllOn();
        var warnings = new List<string>();
        var r = new DeliveredImageResolver(
            (k, v) => reg.GetValueOrDefault((k, v)), _ => throw new IOException("io"), warnings.Add, Company);
        Assert.Null(r.Resolve(DeliveredSurface.LockScreen));
        Assert.Single(warnings);
    }
}
