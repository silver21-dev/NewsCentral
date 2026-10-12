using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Models.IndexFile;
using NewsService.Configuration;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Cross-surface behaviour that only shows up when both the lock screen and the wallpaper are
/// driven through SyncService.ApplyDisplaySurfacesAsync together: independent publishing and
/// sweeping (one enumeration pass, two predicates — never two walks of the index files, and
/// never one surface's sweep touching the other's files), independent toggles, and try/catch
/// isolation so a failure in one surface can never block the other. Uses a real CacheManager and
/// a real ImagePublisher against temp directories, plus a fake IPersonalizationService recording
/// both surfaces' CSP calls.
/// </summary>
public sealed class DisplaySurfacesTests : IDisposable
{
    private const string Team = "cz-its";

    private readonly string _cacheRoot =
        Path.Combine(Path.GetTempPath(), "nsvc-display-cache-" + Guid.NewGuid().ToString("N"));
    private readonly string _publishRoot =
        Path.Combine(Path.GetTempPath(), "nsvc-display-publish-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_cacheRoot, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_publishRoot, recursive: true); } catch { /* best effort */ }
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private (SyncService Sut, FakePersonalization Fake, CapturingLogger<SyncService> Log)
        NewSut(IConfiguration? configuration = null)
    {
        var cache = new CacheManager(_cacheRoot, JsonDefaults.Options);
        var imgConfig = new ServiceConfiguration { Delivery = new DeliverySection { PublishedImagePath = _publishRoot } };
        var publisher = new ImagePublisher(imgConfig, new CapturingLogger<ImagePublisher>());
        var fake = new FakePersonalization();
        var log  = new CapturingLogger<SyncService>();

        var config = configuration ?? new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:PublishedImagePath"] = _publishRoot
            })
            .Build();

        var sut = new SyncService(
            repository: null!, cache: cache, lockScreen: fake, imagePublisher: publisher,
            telemetry: null!, entra: null!, configuration: config, logger: log, contentChanged: new NullContentChangedNotifier());

        return (sut, fake, log);
    }

    private async Task WriteTeamIndexAsync(CacheManager cache, TeamIndexFile index) =>
        await cache.WriteJsonAsync($"{Team}/index.json", index);

    private static async Task<(string RelativePath, string Hash)> WriteImageAsync(
        CacheManager cache, string fileName, byte[] bytes)
    {
        var relativePath = $"{Team}/images/generated/{fileName}";
        await cache.WriteBytesAsync(relativePath, bytes);
        var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (relativePath, hash);
    }

    private static PublishedAssignmentIndex NewAssignment(
        string presentationId, string imagePath, string imageHash, bool isLogonScreen, bool isWallpaper) => new()
    {
        AssignmentId              = Guid.NewGuid().ToString(),
        PresentationId            = presentationId,
        PresentationLastModified  = DateTime.Now,
        ScheduleStart             = DateTime.Now.AddDays(-1),
        ScheduleEnd               = DateTime.Now.AddDays(1),
        DaysOfWeek                = "1,2,3,4,5,6,7",
        SourceTeamFolderName      = Team,
        DisplayTypes              = new DisplayTypeInfo { IsLogonScreen = isLogonScreen, IsWallpaper = isWallpaper },
        Content                   = new ContentInfo { ImagePath = imagePath, ImageHash = imageHash }
    };

    // ── Facts ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BothSurfacesActive_PublishesTwoFiles_WritesBothTrios_NeitherSweepDeletesTheOthers()
    {
        var (sut, fake, _) = NewSut();
        var cache = new CacheManager(_cacheRoot, JsonDefaults.Options);

        var (lockPath, lockHash) = await WriteImageAsync(cache, "lock.jpg", "lock-image-bytes"u8.ToArray());
        var (wallPath, wallHash) = await WriteImageAsync(cache, "wall.jpg", "wall-image-bytes"u8.ToArray());

        await WriteTeamIndexAsync(cache, new TeamIndexFile
        {
            TeamFolderName = Team,
            PublishedAssignments =
            [
                NewAssignment("pres-lock", lockPath, lockHash, isLogonScreen: true,  isWallpaper: false),
                NewAssignment("pres-wall", wallPath, wallHash, isLogonScreen: false, isWallpaper: true),
            ]
        });

        await sut.ApplyDisplaySurfacesAsync([Team]);

        var published = Directory.Exists(_publishRoot) ? Directory.GetFiles(_publishRoot) : [];
        Assert.Contains(published, p => Path.GetFileName(p).StartsWith("lockscreen-"));
        Assert.Contains(published, p => Path.GetFileName(p).StartsWith("wallpaper-"));
        Assert.Single(fake.SetCalls);
        Assert.Single(fake.WallpaperSetCalls);
    }

    [Fact]
    public async Task LockScreenDisabled_OnlyWallpaperTouched_LockScreenValuesUntouched()
    {
        const string preexistingLock = @"C:\Windows\Web\NewsCentral\lockscreen-preexisting.jpg";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:PublishedImagePath"]   = _publishRoot,
                ["Delivery:LockScreenEnabled"]    = "false"
            })
            .Build();
        var (sut, fake, _) = NewSut(config);
        fake.Current = preexistingLock;   // simulates a value applied by an earlier cycle
        var cache = new CacheManager(_cacheRoot, JsonDefaults.Options);

        var (wallPath, wallHash) = await WriteImageAsync(cache, "wall.jpg", "wall-bytes"u8.ToArray());
        await WriteTeamIndexAsync(cache, new TeamIndexFile
        {
            TeamFolderName = Team,
            PublishedAssignments = [NewAssignment("pres-wall", wallPath, wallHash, false, true)]
        });

        await sut.ApplyDisplaySurfacesAsync([Team]);

        Assert.Empty(fake.SetCalls);
        Assert.Empty(fake.ClearCalls);
        Assert.Equal(preexistingLock, fake.Current);   // left exactly as it was
        Assert.Single(fake.WallpaperSetCalls);          // wallpaper still applied
    }

    [Fact]
    public async Task WallpaperDisabled_OnlyLockScreenTouched_WallpaperValuesUntouched()
    {
        const string preexistingWallpaper = @"C:\Windows\Web\NewsCentral\wallpaper-preexisting.jpg";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:PublishedImagePath"] = _publishRoot,
                ["Delivery:WallpaperEnabled"]   = "false"
            })
            .Build();
        var (sut, fake, _) = NewSut(config);
        fake.WallpaperCurrent = preexistingWallpaper;
        var cache = new CacheManager(_cacheRoot, JsonDefaults.Options);

        var (lockPath, lockHash) = await WriteImageAsync(cache, "lock.jpg", "lock-bytes"u8.ToArray());
        await WriteTeamIndexAsync(cache, new TeamIndexFile
        {
            TeamFolderName = Team,
            PublishedAssignments = [NewAssignment("pres-lock", lockPath, lockHash, true, false)]
        });

        await sut.ApplyDisplaySurfacesAsync([Team]);

        Assert.Empty(fake.WallpaperSetCalls);
        Assert.Empty(fake.WallpaperClearCalls);
        Assert.Equal(preexistingWallpaper, fake.WallpaperCurrent);   // left exactly as it was
        Assert.Single(fake.SetCalls);                                 // lock screen still applied
    }

    [Fact]
    public async Task WallpaperHashMismatch_NotPublished_CspNotWritten()
    {
        var (sut, fake, log) = NewSut();
        var cache = new CacheManager(_cacheRoot, JsonDefaults.Options);

        var (wallPath, _) = await WriteImageAsync(cache, "wall.jpg", "wall-bytes"u8.ToArray());
        await WriteTeamIndexAsync(cache, new TeamIndexFile
        {
            TeamFolderName = Team,
            PublishedAssignments =
            [
                NewAssignment("pres-wall", wallPath, "sha256:" + new string('0', 64), false, true)
            ]
        });

        await sut.ApplyDisplaySurfacesAsync([Team]);

        Assert.Empty(fake.WallpaperSetCalls);
        var published = Directory.Exists(_publishRoot) ? Directory.GetFiles(_publishRoot) : [];
        Assert.DoesNotContain(published, p => Path.GetFileName(p).StartsWith("wallpaper-"));
        Assert.Contains(log.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("publish failed"));
    }

    [Fact]
    public async Task ExceptionInLockScreenSurface_StillLetsWallpaperApply()
    {
        var (sut, fake, log) = NewSut();
        fake.ThrowOnSetLockScreen = true;
        var cache = new CacheManager(_cacheRoot, JsonDefaults.Options);

        var (lockPath, lockHash) = await WriteImageAsync(cache, "lock.jpg", "lock-bytes"u8.ToArray());
        var (wallPath, wallHash) = await WriteImageAsync(cache, "wall.jpg", "wall-bytes"u8.ToArray());
        await WriteTeamIndexAsync(cache, new TeamIndexFile
        {
            TeamFolderName = Team,
            PublishedAssignments =
            [
                NewAssignment("pres-lock", lockPath, lockHash, true, false),
                NewAssignment("pres-wall", wallPath, wallHash, false, true),
            ]
        });

        var ex = await Record.ExceptionAsync(() => sut.ApplyDisplaySurfacesAsync([Team]));

        Assert.Null(ex);   // the cycle itself never throws
        Assert.Single(fake.WallpaperSetCalls);   // wallpaper still applied despite the lock-screen throw
        Assert.Contains(log.Entries,
            e => e.Level == LogLevel.Error && e.Message.Contains("Lock screen apply failed"));
    }

    // ── Test double ───────────────────────────────────────────────────────────

    private sealed class FakePersonalization : IPersonalizationService
    {
        public string? Current { get; set; }
        public bool SetResult { get; set; } = true;
        public bool ThrowOnSetLockScreen { get; set; }
        public List<string> SetCalls { get; } = new();
        public List<string?> ClearCalls { get; } = new();

        public string? WallpaperCurrent { get; set; }
        public bool WallpaperSetResult { get; set; } = true;
        public List<string> WallpaperSetCalls { get; } = new();
        public List<string?> WallpaperClearCalls { get; } = new();

        public bool SetLockScreen(string imagePath)
        {
            if (ThrowOnSetLockScreen) throw new InvalidOperationException("simulated lock-screen failure");
            SetCalls.Add(imagePath);
            if (SetResult) Current = imagePath;
            return SetResult;
        }

        public string? GetCurrentLockScreenPath() => Current;

        public void ClearLockScreen()
        {
            ClearCalls.Add(Current);
            Current = null;
        }

        public bool SetWallpaper(string imagePath)
        {
            WallpaperSetCalls.Add(imagePath);
            if (WallpaperSetResult) WallpaperCurrent = imagePath;
            return WallpaperSetResult;
        }

        public string? GetCurrentWallpaperPath() => WallpaperCurrent;

        public void ClearWallpaper()
        {
            WallpaperClearCalls.Add(WallpaperCurrent);
            WallpaperCurrent = null;
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
