using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NewsCentral.Configuration;
using NewsCentral.Models;

namespace NewsCentral.Statistics;

/// <summary>
/// Authoring-app aggregator for poster statistics. Filesystem only — no MAUI, no Blazor.
/// <para>
/// Sorts the flat inbox <c>{dist}\uploads\session-*.json</c> (written by NewsViewer, forwarded unchanged by
/// NewsService) into <c>{dist}\telemetry\{teamId}\{presentationId}\new\</c>, then folds those files into
/// per-presentation day buckets in <c>{DataPath}\statistics\</c>, then prunes old raw files. Both stages are
/// cancellable and resumable without double counting: a sort move is the commit for that file, and a count
/// batch is guarded by <see cref="PresentationStats.PendingMove"/>. The inbox contract is untouched — the
/// engine only reads and moves files out of <c>uploads\</c>, never writes into it.
/// </para>
/// <para>
/// The HMAC is NOT re-verified here: NewsService's TelemetryUploader already discarded records with an
/// invalid signature before forwarding them to the share.
/// </para>
/// <para>
/// Note on <c>teamId</c>: it is the SOURCE (authoring) team of the presentation, because NewsViewer writes
/// <c>assignment.SourceTeamFolderName</c> — not the team whose index.json the device consumed.
/// </para>
/// </summary>
public sealed class PosterStatisticsEngine
{
    private const int BatchSize = 500;
    private const int ProgressEvery = 50;
    private const int MaxConsecutiveIoFailures = 20;
    private static readonly TimeSpan LockStaleAfter = TimeSpan.FromHours(2);

    private static readonly JsonSerializerOptions SessionOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions StatsOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Func<DateTime> _utcNow;
    private readonly Action<string>? _log;

    public PosterStatisticsEngine(Func<DateTime>? utcNow = null, Action<string>? log = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _log = log;
    }

    // ── Public surface ───────────────────────────────────────────────────────

    /// <summary>
    /// Runs one sort + count + prune pass. Never throws for cancellation (returns Stopped) or for share /
    /// I/O failures (returns Failed with the counts reached so far). Safe to re-run after either.
    /// </summary>
    public Task<StatsRunSummary> RunAsync(
        string distRoot, string dataPath, IProgress<StatsRunProgress>? progress, CancellationToken ct) =>
        Task.Run(() => Run(distRoot, dataPath, progress, ct), CancellationToken.None);

    public PresentationStats? Load(string dataPath, string teamId, string presentationId)
    {
        if (!IsValidTeamId(teamId) || !TryNormalizePresentationId(presentationId, out var pres))
            return null;
        try { return ReadStats(StatsPath(dataPath, teamId, pres)); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log($"Could not read statistics for {teamId}/{pres}: {ex.Message}");
            return null;
        }
    }

    public StatsRunSummary? LoadLastRun(string dataPath)
    {
        try
        {
            var path = RunPath(dataPath);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<StatsRunSummary>(File.ReadAllText(path), StatsOptions)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log($"Could not read last run summary: {ex.Message}");
            return null;
        }
    }

    // ── Run ──────────────────────────────────────────────────────────────────

    private StatsRunSummary Run(string distRoot, string dataPath, IProgress<StatsRunProgress>? progress, CancellationToken ct)
    {
        var summary = new StatsRunSummary { StartedUtc = _utcNow() };

        if (!Directory.Exists(distRoot))
            return GuardFailure(summary, $"distribution folder not found: {distRoot}");

        var telemetry = Path.Combine(distRoot, "telemetry");
        var lockPath = Path.Combine(telemetry, ".lock");

        try
        {
            Directory.CreateDirectory(telemetry);
            var held = TryAcquireLock(lockPath);
            if (held is not null)
                return GuardFailure(summary, $"already running (since {held.Value:u})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GuardFailure(summary, $"cannot lock the telemetry folder: {ex.Message}");
        }

        try
        {
            var ownerMessage = CheckOwner(Path.Combine(telemetry, "owner.json"), dataPath);
            if (ownerMessage is not null)
                return GuardFailure(summary, ownerMessage);

            var pass = new Pass(this, distRoot, telemetry, dataPath, summary, progress, ct);
            try
            {
                pass.Execute();
                summary.Outcome = ct.IsCancellationRequested && pass.Stopped ? StatsRunOutcome.Stopped
                    : pass.Problems.Count > 0 ? StatsRunOutcome.Failed
                    : StatsRunOutcome.Completed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The share (or a folder on it) went away mid-run. Counts so far are kept; a re-run resumes.
                summary.Outcome = StatsRunOutcome.Failed;
                pass.Problems.Add(ex.Message);
            }

            summary.Message = pass.BuildMessage();
            summary.FinishedUtc = _utcNow();
            WriteRunSummary(dataPath, summary);
            return summary;
        }
        finally
        {
            try { File.Delete(lockPath); } catch (Exception ex) { Log($"Could not release lock: {ex.Message}"); }
        }
    }

    // A guard failure means "this call did not run", so it does not overwrite the last real run summary.
    private StatsRunSummary GuardFailure(StatsRunSummary summary, string message)
    {
        summary.Outcome = StatsRunOutcome.Failed;
        summary.Message = message;
        summary.FinishedUtc = _utcNow();
        return summary;
    }

    /// <returns>null when the lock is now held; otherwise the time the live holder started.</returns>
    private DateTime? TryAcquireLock(string lockPath)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var fs = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var w = new StreamWriter(fs, new UTF8Encoding(false));
                w.Write(JsonSerializer.Serialize(new LockFile { StartedUtc = _utcNow() }, StatsOptions));
                return null;
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                var started = ReadLockStart(lockPath);
                if (_utcNow() - started < LockStaleAfter)
                    return started;

                Log($"Replacing stale statistics lock (since {started:u})");
                File.Delete(lockPath);
            }
        }
        return _utcNow();
    }

    private DateTime ReadLockStart(string lockPath)
    {
        try
        {
            var lf = JsonSerializer.Deserialize<LockFile>(File.ReadAllText(lockPath), StatsOptions);
            if (lf is not null && lf.StartedUtc != default) return lf.StartedUtc;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return File.GetLastWriteTimeUtc(lockPath);
    }

    private string? CheckOwner(string ownerPath, string dataPath)
    {
        if (!File.Exists(ownerPath))
        {
            WriteAtomic(ownerPath, JsonSerializer.Serialize(
                new OwnerFile { DataPath = dataPath, ClaimedUtc = _utcNow() }, StatsOptions));
            return null;
        }

        OwnerFile? owner;
        try { owner = JsonSerializer.Deserialize<OwnerFile>(File.ReadAllText(ownerPath), StatsOptions); }
        catch (JsonException) { owner = null; }

        if (owner is null || string.IsNullOrWhiteSpace(owner.DataPath))
            return "owner.json is unreadable; refusing to touch this distribution's telemetry";

        return NormalizePath(owner.DataPath) == NormalizePath(dataPath)
            ? null
            : $"this distribution's statistics belong to environment '{owner.DataPath}'";
    }

    private static string NormalizePath(string p) =>
        Path.GetFullPath(p).TrimEnd('\\', '/').ToUpperInvariant();

    private void WriteRunSummary(string dataPath, StatsRunSummary summary)
    {
        try { WriteAtomic(RunPath(dataPath), JsonSerializer.Serialize(summary, StatsOptions)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Could not write _run.json: {ex.Message}");
        }
    }

    // ── The three phases ─────────────────────────────────────────────────────

    private sealed class Pass(
        PosterStatisticsEngine e, string distRoot, string telemetry, string dataPath,
        StatsRunSummary summary, IProgress<StatsRunProgress>? progress, CancellationToken ct)
    {
        private readonly string _invalidDir = Path.Combine(telemetry, "_invalid");
        private int _consecutiveIoFailures;
        private int _skipped;

        public bool Stopped { get; private set; }
        public List<string> Problems { get; } = new();

        public void Execute()
        {
            Sort();
            if (!Stopped) Count();
            if (!Stopped) Prune();
        }

        public string? BuildMessage()
        {
            var parts = new List<string>();
            if (Stopped) parts.Add("stopped; counts so far are kept and a re-run resumes");
            if (_skipped > 0) parts.Add($"{_skipped} file(s) skipped (locked or unreadable); retried next run");
            parts.AddRange(Problems);
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }

        private bool CancelRequested()
        {
            if (!ct.IsCancellationRequested) return false;
            Stopped = true;
            return true;
        }

        private void Report(StatsPhase phase, int done, int? total, string? item, bool force = false)
        {
            if (progress is not null && (force || done % ProgressEvery == 0))
                progress.Report(new StatsRunProgress(phase, done, total, item));
        }

        // A single locked/vanished file is skipped; a run of failures, or a share that is gone, aborts.
        private void NoteSkip(string item, Exception ex)
        {
            _skipped++;
            e.Log($"Skipped {item}: {ex.Message}");
            if (++_consecutiveIoFailures >= MaxConsecutiveIoFailures || !Directory.Exists(distRoot))
                throw new IOException($"lost access to the distribution share ({ex.Message})", ex);
        }

        private void NoteOk() => _consecutiveIoFailures = 0;

        // ── 1. Sort ──

        private void Sort()
        {
            var uploads = Path.Combine(distRoot, "uploads");
            if (!Directory.Exists(uploads)) return;

            var done = 0;
            foreach (var path in Directory.EnumerateFiles(uploads, "session-*.json"))
            {
                if (CancelRequested()) return;
                var name = Path.GetFileName(path);
                SortOne(path, name);
                Report(StatsPhase.Sort, ++done, null, name);
            }
            Report(StatsPhase.Sort, done, null, null, force: true);
        }

        private void SortOne(string path, string name)
        {
            SessionTelemetry? record = null;
            var bad = false;
            try
            {
                record = JsonSerializer.Deserialize<SessionTelemetry>(File.ReadAllText(path), SessionOptions);
            }
            catch (JsonException) { bad = true; }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; } // vanished
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { NoteSkip(name, ex); return; }

            string team = string.Empty, pres = string.Empty;
            if (!bad)
            {
                bad = record is null
                      || !IsValidTeamId(record.TeamId)
                      || !TryNormalizePresentationId(record.PresentationId, out pres)
                      || record.SessionStartTime == default;
                team = record?.TeamId ?? string.Empty;
            }

            try
            {
                if (bad)
                {
                    Directory.CreateDirectory(_invalidDir);
                    File.Move(path, Path.Combine(_invalidDir, name), overwrite: true);
                    summary.Invalid++;
                }
                else
                {
                    var presDir = Path.Combine(telemetry, team, pres);
                    if (File.Exists(Path.Combine(presDir, name)))
                    {
                        File.Delete(path);                  // re-upload of a counted file
                        summary.DuplicatesDropped++;
                    }
                    else
                    {
                        var newDir = Path.Combine(presDir, "new");
                        Directory.CreateDirectory(newDir);
                        File.Move(path, Path.Combine(newDir, name), overwrite: true);
                        summary.Sorted++;
                    }
                }
                NoteOk();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                NoteSkip(name, ex);   // no copy+delete fallback: a failed move is skipped
            }
        }

        // ── 2. Count ──

        private void Count()
        {
            var targets = new List<(string Team, string Pres, string NewDir)>();
            var total = 0;
            foreach (var teamDir in Directory.EnumerateDirectories(telemetry))
            {
                var team = Path.GetFileName(teamDir);
                if (!IsValidTeamId(team)) continue;                       // also skips _invalid
                foreach (var presDir in Directory.EnumerateDirectories(teamDir))
                {
                    var pres = Path.GetFileName(presDir);
                    if (!TryNormalizePresentationId(pres, out var norm) || norm != pres) continue;
                    var newDir = Path.Combine(presDir, "new");
                    if (!Directory.Exists(newDir)) continue;
                    var n = Directory.EnumerateFiles(newDir).Count();
                    if (n == 0) continue;
                    total += n;
                    targets.Add((team, pres, newDir));
                }
            }

            var done = 0;
            foreach (var (team, pres, newDir) in targets)
            {
                if (CancelRequested()) return;
                try { CountFolder(team, pres, newDir, ref done, total); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    e.Log($"Statistics for {team}/{pres} are unreadable: {ex.Message}");
                    Problems.Add($"statistics file for {team}/{pres} is unreadable; its raw files were left in new\\");
                }
                if (Stopped) return;
            }
            Report(StatsPhase.Count, done, total, null, force: true);
        }

        private void CountFolder(string team, string pres, string newDir, ref int done, int total)
        {
            var presDir = Path.GetDirectoryName(newDir)!;
            var statsPath = e.StatsPath(dataPath, team, pres);
            var stats = ReadStats(statsPath) ?? new PresentationStats { TeamId = team, PresentationId = pres };

            // Crash recovery first: these are already counted — move them, never count them again.
            if (stats.PendingMove.Count > 0)
            {
                var remaining = new List<string>();
                foreach (var name in stats.PendingMove)
                {
                    var src = Path.Combine(newDir, name);
                    if (!File.Exists(src)) continue;                      // already moved
                    try { File.Move(src, Path.Combine(presDir, name), overwrite: true); NoteOk(); done++; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        NoteSkip(name, ex);
                        remaining.Add(name);
                    }
                }
                stats.PendingMove = remaining;
                e.SaveStats(statsPath, stats);
                if (remaining.Count > 0) return;                          // cannot safely continue this folder
            }

            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var names = Directory.EnumerateFiles(newDir)
                    .Select(p => Path.GetFileName(p))
                    .Where(n => !skipped.Contains(n))
                    .Take(BatchSize)
                    .ToList();
                if (names.Count == 0) return;

                var batch = new List<string>(names.Count);
                foreach (var name in names)
                {
                    if (CancelRequested()) break;                          // commit what was read so far
                    ReadForCount(newDir, name, stats, batch, skipped);
                    Report(StatsPhase.Count, ++done, total, name);
                }

                if (batch.Count > 0)
                {
                    // The save with PendingMove is the commit point; the moves below always complete.
                    stats.PendingMove = batch;
                    e.SaveStats(statsPath, stats);

                    var failed = new List<string>();
                    foreach (var name in batch)
                    {
                        try { File.Move(Path.Combine(newDir, name), Path.Combine(presDir, name), overwrite: true); NoteOk(); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            failed.Add(name);
                            _skipped++;
                            e.Log($"Could not move counted file {name}: {ex.Message}");
                            _consecutiveIoFailures++;
                        }
                    }
                    stats.PendingMove = failed;
                    e.SaveStats(statsPath, stats);

                    if (failed.Count > 0)
                    {
                        if (!Directory.Exists(distRoot) || _consecutiveIoFailures >= MaxConsecutiveIoFailures)
                            throw new IOException("lost access to the distribution share while moving counted files");
                        return;                                           // retried by crash recovery next run
                    }
                }

                if (Stopped) return;
            }
        }

        private void ReadForCount(string newDir, string name, PresentationStats stats, List<string> batch, HashSet<string> skipped)
        {
            var path = Path.Combine(newDir, name);
            SessionTelemetry? record;
            try
            {
                record = JsonSerializer.Deserialize<SessionTelemetry>(File.ReadAllText(path), SessionOptions);
            }
            catch (JsonException) { record = null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(name);
                NoteSkip(name, ex);
                return;
            }

            if (record is null || record.SessionStartTime == default)
            {
                try
                {
                    Directory.CreateDirectory(_invalidDir);
                    File.Move(path, Path.Combine(_invalidDir, name), overwrite: true);
                    summary.Invalid++;
                    NoteOk();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(name);
                    NoteSkip(name, ex);
                }
                return;
            }

            batch.Add(name);                                               // notifications are moved, just not counted
            if (string.Equals(record.RecordType, SessionTelemetry.NotificationRecordType, StringComparison.Ordinal))
                return;

            var day = DayKey(record.SessionStartTime);
            if (!stats.Days.TryGetValue(day, out var counts))
                stats.Days[day] = counts = new DayCounts();
            counts.Views++;
            if (string.Equals(record.CloseReason, "UrlLaunch", StringComparison.Ordinal))
                counts.Clicks++;
            summary.Counted++;
        }

        // ── 3. Prune ──

        private void Prune()
        {
            var cutoff = e._utcNow().AddMonths(-StatisticsDefaults.RawRetentionMonths);
            var done = 0;

            IEnumerable<string> Candidates()
            {
                if (Directory.Exists(_invalidDir))
                    foreach (var f in Directory.EnumerateFiles(_invalidDir)) yield return f;

                foreach (var teamDir in Directory.EnumerateDirectories(telemetry))
                {
                    if (!IsValidTeamId(Path.GetFileName(teamDir))) continue;
                    foreach (var presDir in Directory.EnumerateDirectories(teamDir))
                    {
                        if (!TryNormalizePresentationId(Path.GetFileName(presDir), out var norm)
                            || norm != Path.GetFileName(presDir)) continue;
                        foreach (var f in Directory.EnumerateFiles(presDir)) yield return f;   // parent level only, never new\
                    }
                }
            }

            foreach (var file in Candidates())
            {
                if (CancelRequested()) return;
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        summary.Pruned++;
                    }
                    NoteOk();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    NoteSkip(Path.GetFileName(file), ex);
                }
                Report(StatsPhase.Prune, ++done, null, null);
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>The existing team folder-name rule: non-empty and unchanged by TeamFolderNameCanonicalizer.</summary>
    private static bool IsValidTeamId(string? teamId) =>
        !string.IsNullOrEmpty(teamId) && TeamFolderNameCanonicalizer.Canonicalize(teamId) == teamId;

    private static bool TryNormalizePresentationId(string? raw, out string normalized)
    {
        if (Guid.TryParseExact(raw, "D", out var g))
        {
            normalized = g.ToString("D");
            return true;
        }
        normalized = string.Empty;
        return false;
    }

    private static string DayKey(DateTime start)
    {
        var utc = start.Kind == DateTimeKind.Local ? start.ToUniversalTime() : start;
        return utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private string StatsPath(string dataPath, string team, string pres) =>
        Path.Combine(dataPath, "statistics", team, pres + ".json");

    private static string RunPath(string dataPath) => Path.Combine(dataPath, "statistics", "_run.json");

    private static PresentationStats? ReadStats(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<PresentationStats>(File.ReadAllText(path), StatsOptions)
              ?? throw new InvalidDataException("empty statistics document")
            : null;

    private void SaveStats(string path, PresentationStats stats)
    {
        stats.UpdatedUtc = _utcNow();
        WriteAtomic(path, JsonSerializer.Serialize(stats, StatsOptions));
    }

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    private void Log(string message) => _log?.Invoke(message);

    private sealed class LockFile { public DateTime StartedUtc { get; set; } }

    private sealed class OwnerFile
    {
        public string DataPath { get; set; } = string.Empty;
        public DateTime ClaimedUtc { get; set; }
    }
}
