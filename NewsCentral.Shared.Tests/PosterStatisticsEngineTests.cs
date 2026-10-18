using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsCentral.Statistics;
using Xunit.Abstractions;

namespace NewsCentral.Shared.Tests;

public sealed class PosterStatisticsEngineTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private const string Team = "cz-its";
    private static readonly string Pres = "4dd97030-61df-42d2-a89d-f57a7e171b2d";
    private static readonly DateTime Day1 = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly ITestOutputHelper _output;
    private readonly string _sandbox;
    private readonly string _dist;
    private readonly string _data;
    private readonly PosterStatisticsEngine _engine = new();

    public PosterStatisticsEngineTests(ITestOutputHelper output)
    {
        _output = output;
        _sandbox = Path.Combine(Path.GetTempPath(), "nc-stats-" + Guid.NewGuid().ToString("N"));
        _dist = Path.Combine(_sandbox, "dist");
        _data = Path.Combine(_sandbox, "data");
        Directory.CreateDirectory(Path.Combine(_dist, "uploads"));
        Directory.CreateDirectory(_data);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    // ── fixtures ──

    private string Uploads => Path.Combine(_dist, "uploads");
    private string Telemetry => Path.Combine(_dist, "telemetry");
    private string PresDir(string team = Team, string pres = "") => Path.Combine(Telemetry, team, pres.Length == 0 ? Pres : pres);

    private string Write(string dir, SessionTelemetry r, string? name = null)
    {
        Directory.CreateDirectory(dir);
        r.SessionId = string.IsNullOrEmpty(r.SessionId) ? Guid.NewGuid().ToString() : r.SessionId;
        var path = Path.Combine(dir, name ?? $"session-{r.SessionId}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(r, Json));
        return path;
    }

    private static SessionTelemetry Poster(DateTime start, string reason = "UserClose", string team = Team, string pres = "") => new()
    {
        TeamId = team,
        PresentationId = pres.Length == 0 ? Pres : pres,
        SessionStartTime = start,
        SessionEndTime = start.AddSeconds(30),
        CloseReason = reason
    };

    private static SessionTelemetry Notification(DateTime start) => new()
    {
        RecordType = SessionTelemetry.NotificationRecordType,
        TeamId = Team,
        PresentationId = Pres,
        ContentKey = "k",
        SessionStartTime = start,
        SessionEndTime = start.AddSeconds(5),
        CloseReason = "Expanded"
    };

    private Task<StatsRunSummary> Run(IProgress<StatsRunProgress>? p = null, CancellationToken ct = default) =>
        _engine.RunAsync(_dist, _data, p, ct);

    private PresentationStats Stats() => _engine.Load(_data, Team, Pres)!;

    private int Files(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0;

    private sealed class SyncProgress(Action<StatsRunProgress> a) : IProgress<StatsRunProgress>
    {
        public void Report(StatsRunProgress v) => a(v);
    }

    // ── 1 ──

    [Fact]
    public async Task ViewsAndClicks_ExcludeNotifications_ButNotificationFileIsMoved()
    {
        Write(Uploads, Poster(Day1));
        Write(Uploads, Poster(Day1.AddMinutes(1)));
        Write(Uploads, Poster(Day1.AddMinutes(2), "UrlLaunch"));
        Write(Uploads, Notification(Day1));

        var s = await Run();

        Assert.Equal(StatsRunOutcome.Completed, s.Outcome);
        Assert.Equal(4, s.Sorted);
        Assert.Equal(3, s.Counted);
        var day = Assert.Single(Stats().Days);
        Assert.Equal(3, day.Value.Views);
        Assert.Equal(1, day.Value.Clicks);
        Assert.Equal(4, Files(PresDir()));                       // notification moved with the rest
        Assert.Equal(0, Files(Uploads));
        Assert.Equal(0, Files(Path.Combine(PresDir(), "new")));
        Assert.NotNull(_engine.LoadLastRun(_data));
    }

    // ── 2 ──

    [Fact]
    public async Task RecordsOnTwoUtcDays_LandInTwoBuckets()
    {
        Write(Uploads, Poster(new DateTime(2026, 10, 10, 23, 59, 0, DateTimeKind.Utc)));
        Write(Uploads, Poster(new DateTime(2026, 10, 11, 0, 1, 0, DateTimeKind.Utc)));

        await Run();

        var days = Stats().Days;
        Assert.Equal(new[] { "2026-10-10", "2026-10-11" }, days.Keys);
        Assert.All(days.Values, d => Assert.Equal(1, d.Views));
    }

    // ── 3 ──

    [Fact]
    public async Task SecondRunWithEmptyInbox_DoesNotRewriteStats()
    {
        Write(Uploads, Poster(Day1));
        await Run();
        var path = Path.Combine(_data, "statistics", Team, Pres + ".json");
        var bytes = File.ReadAllBytes(path);
        var stamp = File.GetLastWriteTimeUtc(path);

        var s = await Run();

        Assert.Equal(StatsRunOutcome.Completed, s.Outcome);
        Assert.Equal(0, s.Sorted + s.Counted);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    // ── 4 ──

    [Fact]
    public async Task BadTeamBadPresentationAndMalformedJson_GoToInvalid_AndNothingEscapesTelemetry()
    {
        Write(Uploads, Poster(Day1, team: @"..\x"));
        Write(Uploads, Poster(Day1, pres: "not-a-guid"));
        File.WriteAllText(Path.Combine(Uploads, "session-broken.json"), "{ not json");

        var s = await Run();

        Assert.Equal(3, s.Invalid);
        Assert.Equal(3, Files(Path.Combine(Telemetry, "_invalid")));
        Assert.Equal(0, Files(Uploads));
        Assert.Equal(new[] { "data", "dist" }, Directory.GetDirectories(_sandbox).Select(Path.GetFileName).OrderBy(x => x));
        Assert.Empty(Directory.GetFiles(_sandbox));
        Assert.Equal(new[] { "telemetry", "uploads" }, Directory.GetDirectories(_dist).Select(Path.GetFileName).OrderBy(x => x));
        Assert.Equal(new[] { "_invalid" }, Directory.GetDirectories(Telemetry).Select(Path.GetFileName));
    }

    // ── 5 ──

    [Fact]
    public async Task ReUploadOfCountedFile_IsDeleted_AndNotRecounted()
    {
        var rec = Poster(Day1);
        Write(Uploads, rec);
        await Run();

        Write(Uploads, rec);                                      // same session id => same file name
        var s = await Run();

        Assert.Equal(1, s.DuplicatesDropped);
        Assert.Equal(0, Files(Uploads));
        Assert.Equal(1, Stats().Days.Values.Sum(d => d.Views));
    }

    // ── 6 ──

    [Fact]
    public async Task PendingMove_IsMovedNotRecounted()
    {
        var newDir = Path.Combine(PresDir(), "new");
        var f1 = Path.GetFileName(Write(newDir, Poster(Day1)));
        var f2 = Path.GetFileName(Write(newDir, Poster(Day1)));
        Write(newDir, Poster(Day1));                               // genuinely new, not pending

        var seeded = new PresentationStats { TeamId = Team, PresentationId = Pres, PendingMove = [f1, f2] };
        seeded.Days["2026-10-10"] = new DayCounts { Views = 2 };
        var statsPath = Path.Combine(_data, "statistics", Team, Pres + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(statsPath)!);
        File.WriteAllText(statsPath, JsonSerializer.Serialize(seeded, Json));

        var s = await Run();

        Assert.Equal(StatsRunOutcome.Completed, s.Outcome);
        Assert.Equal(1, s.Counted);
        Assert.Equal(3, Stats().Days["2026-10-10"].Views);         // 2 seeded + 1 new, pending pair not doubled
        Assert.Empty(Stats().PendingMove);
        Assert.Equal(3, Files(PresDir()));
        Assert.Equal(0, Files(newDir));
    }

    // ── 7 ──

    [Fact]
    public async Task CancelInSort_IsLossless_AndResumeReachesExactTotals()
    {
        SeedInbox(2000);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.Phase == StatsPhase.Sort && p.Done >= 300) cts.Cancel(); });

        var sw = Stopwatch.StartNew();
        var first = await Run(progress, cts.Token);
        Assert.Equal(StatsRunOutcome.Stopped, first.Outcome);
        Assert.Equal(2000, Files(Uploads) + Files(Path.Combine(PresDir(), "new")) + Files(PresDir()));
        Assert.True(Files(Uploads) > 0);

        var second = await Run();
        sw.Stop();

        Assert.Equal(StatsRunOutcome.Completed, second.Outcome);
        Assert.Equal(2000, Stats().Days.Values.Sum(d => d.Views));
        Assert.Equal(2000, Files(PresDir()));
        _output.WriteLine($"2000 files sorted+counted across a cancel/resume in {sw.ElapsedMilliseconds} ms " +
                          $"(~{2000 * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):F0} files/s)");
    }

    // ── 8 ──

    [Fact]
    public async Task CancelInCount_ResumesToExactTotals()
    {
        SeedInbox(2000);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.Phase == StatsPhase.Count && p.Done >= 300) cts.Cancel(); });

        var first = await Run(progress, cts.Token);

        Assert.Equal(StatsRunOutcome.Stopped, first.Outcome);
        var partial = Stats();
        Assert.Empty(partial.PendingMove);
        var counted = partial.Days.Values.Sum(d => d.Views);
        Assert.InRange(counted, 1, 1999);
        Assert.Equal(counted, Files(PresDir()));                   // every counted file was moved
        Assert.Equal(2000 - counted, Files(Path.Combine(PresDir(), "new")));

        var second = await Run();

        Assert.Equal(StatsRunOutcome.Completed, second.Outcome);
        Assert.Equal(2000, Stats().Days.Values.Sum(d => d.Views));
        Assert.Equal(2000, Files(PresDir()));
    }

    private void SeedInbox(int n)
    {
        for (var i = 0; i < n; i++)
            Write(Uploads, Poster(Day1.AddSeconds(i)));
    }

    // ── 9 ──

    [Fact]
    public async Task OwnerMismatch_Fails_AndMovesNothing()
    {
        Directory.CreateDirectory(Telemetry);
        File.WriteAllText(Path.Combine(Telemetry, "owner.json"),
            JsonSerializer.Serialize(new { dataPath = Path.Combine(_sandbox, "other-env"), claimedUtc = DateTime.UtcNow }, Json));
        Write(Uploads, Poster(Day1));

        var s = await Run();

        Assert.Equal(StatsRunOutcome.Failed, s.Outcome);
        Assert.Contains("belong to environment", s.Message);
        Assert.Equal(1, Files(Uploads));
        Assert.Empty(Directory.GetDirectories(Telemetry));
        Assert.False(File.Exists(Path.Combine(Telemetry, ".lock")));
    }

    [Fact]
    public async Task OwnerMissing_IsClaimed_AndEquivalentPathSpellingsMatch()
    {
        Write(Uploads, Poster(Day1));
        await Run();

        var owner = File.ReadAllText(Path.Combine(Telemetry, "owner.json"));
        Assert.Contains("dataPath", owner);

        Write(Uploads, Poster(Day1));
        var s = await _engine.RunAsync(_dist, _data.ToUpperInvariant() + Path.DirectorySeparatorChar, null, default);
        Assert.Equal(StatsRunOutcome.Completed, s.Outcome);
    }

    // ── 10 ──

    [Fact]
    public async Task FreshLock_Fails_StaleLock_IsTakenOver()
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var engine = new PosterStatisticsEngine(() => now);
        Directory.CreateDirectory(Telemetry);
        var lockPath = Path.Combine(Telemetry, ".lock");
        Write(Uploads, Poster(Day1));

        File.WriteAllText(lockPath, JsonSerializer.Serialize(new { startedUtc = now.AddMinutes(-10) }, Json));
        var fresh = await engine.RunAsync(_dist, _data, null, default);
        Assert.Equal(StatsRunOutcome.Failed, fresh.Outcome);
        Assert.Contains("already running", fresh.Message);
        Assert.Equal(1, Files(Uploads));
        Assert.True(File.Exists(lockPath));                         // not ours to release

        File.WriteAllText(lockPath, JsonSerializer.Serialize(new { startedUtc = now.AddHours(-3) }, Json));
        var stale = await engine.RunAsync(_dist, _data, null, default);
        Assert.Equal(StatsRunOutcome.Completed, stale.Outcome);
        Assert.Equal(0, Files(Uploads));
        Assert.False(File.Exists(lockPath));
    }

    // ── 11 ──

    [Fact]
    public async Task Prune_DeletesOldCountedAndInvalidFiles_KeepsFresh_StatsUnchanged()
    {
        Write(Uploads, Poster(Day1));
        await Run();
        var old = Path.Combine(PresDir(), "session-old.json");
        var fresh = Path.Combine(PresDir(), "session-fresh.json");
        File.WriteAllText(old, "{}");
        File.WriteAllText(fresh, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMonths(-14));
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddDays(-5));
        var oldInvalid = Path.Combine(Telemetry, "_invalid", "session-bad.json");
        Directory.CreateDirectory(Path.GetDirectoryName(oldInvalid)!);
        File.WriteAllText(oldInvalid, "x");
        File.SetLastWriteTimeUtc(oldInvalid, DateTime.UtcNow.AddMonths(-14));
        var before = JsonSerializer.Serialize(Stats(), Json);

        var s = await Run();

        Assert.Equal(2, s.Pruned);
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(oldInvalid));
        Assert.True(File.Exists(fresh));
        Assert.Equal(before, JsonSerializer.Serialize(Stats(), Json));
    }

    // ── 12 (service-level decision; the app project has no test project) ──

    [Fact]
    public void RootResolver_AzureBlobDisabledAndInvalid_AreNotAvailable_LocalResolves()
    {
        EffectiveEnvironmentSettings Eff(Action<DistributionSettings> edit)
        {
            var s = new EnvironmentSettings();
            edit(s.Distribution);
            return new(EnvironmentSettingsSource.EnvironmentFile, s, null, null);
        }

        Assert.Null(StatisticsRootResolver.Resolve(Eff(d => { d.Enabled = true; d.Mode = "AzureBlob"; }), _data).DistRoot);
        Assert.Null(StatisticsRootResolver.Resolve(Eff(d => d.Enabled = false), _data).DistRoot);
        Assert.Null(StatisticsRootResolver.Resolve(
            new EffectiveEnvironmentSettings(EnvironmentSettingsSource.Invalid, null, "bad", null), _data).DistRoot);

        Assert.Equal(@"X:\share", StatisticsRootResolver.Resolve(
            Eff(d => { d.Enabled = true; d.LocalPath = @"X:\share"; }), _data).DistRoot);
        Assert.Equal(Path.Combine(_data, "_distribution"), StatisticsRootResolver.Resolve(
            Eff(d => d.Enabled = true), _data).DistRoot);
    }
}
