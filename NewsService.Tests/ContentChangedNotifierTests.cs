using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// The ContentChanged notifier fires exactly once per accepted index change, after the cache is
/// fully materialized, and never for an unchanged, rejected or partially synced index; a throwing
/// notifier never breaks the cycle.
/// </summary>
public sealed class ContentChangedNotifierTests : IDisposable
{
    private const string Team = "cz-its";
    private const string ImagePath = "cz-its/images/generated/img.png";
    private static readonly byte[] ImageBytes = [1, 2, 3, 4];
    private static readonly string ImageHash =
        "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ImageBytes)).ToLowerInvariant();

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "nsvc-notify-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private (SyncService Sut, FakeNotifier Notifier, FakeRepository Repo, CacheManager Cache) NewSut(
        bool requireSigned = false, string? publicKey = null, bool throwing = false)
    {
        var settings = new Dictionary<string, string?> { ["Signing:RequireSignedIndex"] = requireSigned.ToString() };
        if (publicKey is not null) settings[$"Signing:{Team}:PublicKey"] = publicKey;
        var config   = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var repo     = new FakeRepository();
        var cache    = new CacheManager(_root, JsonDefaults.Options);
        var notifier = new FakeNotifier { Throw = throwing };
        var sut = new SyncService(repo, cache, lockScreen: null!, imagePublisher: null!, telemetry: null!,
                                  entra: null!, configuration: config, logger: new NullLogger<SyncService>(),
                                  contentChanged: notifier);
        return (sut, notifier, repo, cache);
    }

    private static string IndexJson(string hash, bool withImage = true)
    {
        var index = new TeamIndexFile
        {
            TeamFolderName = Team,
            TeamName       = "CZ ITS",
            GeneratedAt    = new DateTime(2026, 7, 19, 8, 0, 0, DateTimeKind.Utc),
            IndexHash      = hash
        };
        if (withImage)
            index.PublishedAssignments.Add(new PublishedAssignmentIndex
            {
                Content = new ContentInfo { ImagePath = ImagePath, ImageHash = ImageHash }
            });
        return JsonSerializer.Serialize(index, JsonDefaults.Options);
    }

    private Task Sync(SyncService sut) =>
        sut.SyncAllTeamsAsync([Team], new HashSet<string>(), CancellationToken.None);

    [Fact]
    public async Task AcceptedChange_WithImage_NotifiesOnce_AfterCacheIsPopulated()
    {
        var (sut, notifier, repo, cache) = NewSut();
        repo.Text = IndexJson("h1");
        repo.Image = ImageBytes;
        notifier.OnNotify = () =>
        {
            Assert.True(cache.FileExists(ImagePath));
            Assert.Equal("h1", cache.ReadJsonAsync<TeamIndexFile>($"{Team}/index.json").Result?.IndexHash);
        };

        await Sync(sut);

        Assert.Equal(1, notifier.Calls);
    }

    [Fact]
    public async Task UnchangedHash_NoNotification()
    {
        var (sut, notifier, repo, _) = NewSut();
        repo.Text = IndexJson("h1");
        repo.Image = ImageBytes;
        await Sync(sut);
        Assert.Equal(1, notifier.Calls);

        await Sync(sut);
        await Sync(sut);

        Assert.Equal(1, notifier.Calls);
    }

    [Fact]
    public async Task RejectedIndex_BadSignature_RequireSigned_NoNotification()
    {
        var (_, pub) = SigningKeyTool.GenerateKeyPair();
        var (sut, notifier, repo, _) = NewSut(requireSigned: true, publicKey: pub);
        repo.Text = IndexJson("h1");   // unsigned while a key is configured and signing is required
        repo.Image = ImageBytes;

        await Sync(sut);

        Assert.Equal(0, notifier.Calls);
    }

    [Fact]
    public async Task MaterializationFailure_ImageMissing_NoNotification()
    {
        var (sut, notifier, repo, _) = NewSut();
        repo.Text = IndexJson("h1");
        repo.Image = null;   // repository has the index but not the image

        await Sync(sut);

        Assert.Equal(0, notifier.Calls);
    }

    [Fact]
    public async Task MaterializationFailure_DownloadThrows_NoNotification()
    {
        var (sut, notifier, repo, _) = NewSut();
        repo.Text = IndexJson("h1");
        repo.ThrowOnBytes = true;

        await Sync(sut);

        Assert.Equal(0, notifier.Calls);
    }

    [Fact]
    public async Task ThrowingNotifier_DoesNotBreakTheSync()
    {
        var (sut, notifier, repo, cache) = NewSut(throwing: true);
        repo.Text = IndexJson("h1");
        repo.Image = ImageBytes;

        await Sync(sut);   // must not throw

        Assert.Equal(1, notifier.Calls);
        Assert.Equal("h1", (await cache.ReadJsonAsync<TeamIndexFile>($"{Team}/index.json"))?.IndexHash);
    }

    private sealed class FakeNotifier : IContentChangedNotifier
    {
        public int Calls { get; private set; }
        public bool Throw { get; set; }
        public Action? OnNotify { get; set; }

        public void NotifyContentChanged()
        {
            Calls++;
            OnNotify?.Invoke();
            if (Throw) throw new InvalidOperationException("event log unavailable");
        }
    }

    private sealed class FakeRepository : IRepositoryReader
    {
        public string? Text { get; set; }
        public byte[]? Image { get; set; }
        public bool ThrowOnBytes { get; set; }
        public bool IsAvailable => true;
        public string SyncSource => "Share";

        public Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default) =>
            Task.FromResult(Text);

        public Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken ct = default) =>
            ThrowOnBytes ? throw new IOException("download failed") : Task.FromResult(Image);
    }

    private sealed class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) { }
    }
}
