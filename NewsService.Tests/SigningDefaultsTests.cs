using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;
using NewsService.Services;
using NewsViewer.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Signing:RequireSignedIndex is fail-closed when no value is configured, in both verifiers, and NewsService
/// says once at startup that per-team Signing subkeys from the old model are ignored.
/// </summary>
public sealed class SigningDefaultsTests : IDisposable
{
    private const string Team = "cz-its";
    private static readonly byte[] ImageBytes = [1, 2, 3];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nsvc-sigdefault-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    // ── Default: unsigned and Disabled are rejected, no RequireSignedIndex value present ──────────

    [Theory]
    [InlineData(true)]    // key configured, index unsigned
    [InlineData(false)]   // no key configured at all (Disabled)
    public async Task SyncService_NoRequireSignedIndexValue_RejectsUnsignedAndDisabled(bool keyConfigured)
    {
        var (_, pub) = SigningKeyTool.GenerateKeyPair();
        var config = keyConfigured ? Config(("Signing:PublicKey", pub)) : Config();
        var log = new CapturingLogger<SyncService>();
        var repo = new FakeRepository
        {
            Text = JsonSerializer.Serialize(
                new TeamIndexFile { TeamFolderName = Team, TeamName = Team, IndexHash = "h1" }, JsonDefaults.Options)
        };
        var cache = new CacheManager(_root, JsonDefaults.Options);
        var sut = new SyncService(repo, cache, lockScreen: null!, imagePublisher: null!, telemetry: null!, entra: null!,
                                  configuration: config, logger: log, contentChanged: new NullContentChangedNotifier());

        await sut.SyncTeamAsync(Team, CancellationToken.None);

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("index rejected")
                                          && e.Message.Contains(keyConfigured ? "unsigned, signing required" : "no public key, signing required"));
        Assert.False(File.Exists(Path.Combine(_root, Team, "index.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PresentationSelector_NoRequireSignedIndexValue_RejectsUnsignedAndDisabled(bool explicitFalse)
    {
        Directory.CreateDirectory(Path.Combine(_root, Team, "images", "generated"));
        File.WriteAllBytes(Path.Combine(_root, Team, "images", "generated", "a.png"), ImageBytes);
        var item = new PublishedAssignmentIndex
        {
            AssignmentId             = "asg-1",
            PresentationId           = "p1",
            PresentationLastModified = DateTime.Now.AddDays(-1),
            PublishedDate            = DateTime.Now.AddDays(-1),
            ScheduleStart            = DateTime.Now.AddDays(-30),
            ScheduleEnd              = DateTime.Now.AddDays(30),
            DaysOfWeek               = "1,2,3,4,5,6,7",
            SourceTeamFolderName     = Team,
            DisplayTypes             = new DisplayTypeInfo { IsNewsOfWeek = true },
            Content = new ContentInfo
            {
                ImagePath = $"{Team}/images/generated/a.png",
                ImageHash = "sha256:" + Convert.ToHexString(SHA256.HashData(ImageBytes)).ToLowerInvariant()
            }
        };
        var index = new TeamIndexFile { TeamFolderName = Team, TeamName = Team, IndexHash = "h1" };
        index.PublishedAssignments.Add(item);
        File.WriteAllText(Path.Combine(_root, Team, "index.json"), JsonSerializer.Serialize(index,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() }
            }));

        // Control: explicitly opting out of signing shows the content, so the rejection below is the default at work.
        var config = explicitFalse ? Config(("Signing:RequireSignedIndex", "false")) : Config();
        var (assignment, _) = new PresentationSelector(_root, config).SelectActive([Team]);

        if (explicitFalse) Assert.Equal("p1", assignment?.PresentationId);
        else Assert.Null(assignment);
    }

    // ── Leftover per-team Signing subkeys ─────────────────────────────────────

    [Fact]
    public void PerTeamSigningKey_LogsExactlyOneWarning_NamingTheTeam()
    {
        var log = new CapturingLogger<Worker>();
        var config = Config(
            ("Signing:PublicKey", "FLAT"),
            ("Signing:RequireSignedIndex", "true"),
            ("Signing:cz-its:PublicKey", "OLD"));

        Worker.LogIgnoredPerTeamSigningKeys(config, log);

        var warning = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("cz-its", warning.Message);
        Assert.Contains("Per-team signing keys are ignored", warning.Message);
    }

    [Fact]
    public void SeveralLeftovers_AreListedCommaSeparated()
    {
        var log = new CapturingLogger<Worker>();
        var config = Config(("Signing:b-team:PublicKeyPrevious", "OLD"), ("Signing:a-team:PublicKey", "OLD"));

        Worker.LogIgnoredPerTeamSigningKeys(config, log);

        var warning = Assert.Single(log.Entries);
        Assert.Contains("Signing\\a-team, Signing\\b-team", warning.Message);
    }

    [Fact]
    public void FlatKeysOnly_NoWarning()
    {
        var log = new CapturingLogger<Worker>();
        var config = Config(
            ("Signing:PublicKey", "FLAT"),
            ("Signing:PublicKeyPrevious", "PREV"),
            ("Signing:RequireSignedIndex", "true"));

        Worker.LogIgnoredPerTeamSigningKeys(config, log);

        Assert.Empty(log.Entries);
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class FakeRepository : IRepositoryReader
    {
        public string? Text { get; set; }
        public bool IsAvailable => true;
        public string SyncSource => "Share";

        public Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default) =>
            Task.FromResult(Text);

        public Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
