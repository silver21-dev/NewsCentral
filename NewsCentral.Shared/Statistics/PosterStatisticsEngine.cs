using System.Diagnostics;
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
/// Reads each telemetry file once and moves it once: from the flat inbox <c>{dist}\uploads\session-*.json</c>
/// (written by NewsViewer, forwarded unchanged by NewsService) straight to
/// <c>{dist}\telemetry\{teamId}\{presentationId}\</c>, folding poster sessions into per-presentation day
/// buckets in <c>{DataPath}\statistics\</c>. The inbox contract is untouched: the engine only reads files
/// and moves them out of <c>uploads\</c>, never writes into it.
/// </para>
/// <para>
/// Run order: <b>guards</b> (lock, owner) → <b>recover</b> (finish moves a crashed run had already counted)
/// → <b>process</b> (batches of <see cref="StatisticsDefaults.BatchSize"/>: bounded-parallel read and
/// classify, serial stats save with <see cref="PresentationStats.PendingMove"/>, bounded-parallel move,
/// serial PendingMove clear) → <b>prune</b>. The stats save is the commit point of a batch; recovery runs
/// to completion before any new input is read, so a file whose stats were saved but whose move did not
/// happen is moved on the next run and never counted twice. Stats files are only ever written serially.
/// </para>
/// <para>
/// Legacy M1 runs staged files in <c>{team}\{pres}\new\</c>; those are drained as uncounted input and the
/// emptied <c>new\</c> folders removed.
/// </para>
/// <para>
/// The HMAC is NOT re-verified here: NewsService's TelemetryUploader already discarded records with an
/// invalid signature before forwarding them to the share. <c>teamId</c> is the SOURCE (authoring) team,
/// because NewsViewer writes <c>assignment.SourceTeamFolderName</c>.
/// </para>
/// </summary>
public sealed class PosterStatisticsEngine
{
    private const int ProgressEvery = 50;
    private const int MaxConsecutiveIoFailures = 20;
    private static readonly TimeSpan LockStaleAfter = TimeSpan.FromHours(2);

    // Must agree with NewsViewer's and NewsService's JsonDefaults (both identical); guarded by a parity test.
    private static readonly JsonSerializerOptions SessionOptions = new()
    {
        WriteIndented = true,
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

    /// <summary>Test seam: called with the source path before each counted-file move; false simulates a crash.</summary>
    internal Func<string, bool>? BeforeMove { get; set; }

    /// <summary>Test seam: the option set the engine reads session files with.</summary>
    internal static JsonSerializerOptions SessionJsonOptions => SessionOptions;

    public PosterStatisticsEngine(Func<DateTime>? utcNow = null, Action<string>? log = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _log = log;
    }

    // ── Public surface ───────────────────────────────────────────────────────

    /// <summary>
    /// Runs one recover + process + prune pass. Never throws for cancellation (returns Stopped) or for
    /// share / I/O failures (returns Failed with the counts reached so far). Safe to re-run after either.
    /// </summary>
    public Task<StatsRunSummary> RunAsync(
        string distRoot, string dataPath, IProgress<StatsRunProgress>? progress, CancellationToken ct) =>
        Task.Run(() => Run(distRoot, dataPath, progress, ct), CancellationToken.None);

    public PresentationStats? Load(string dataPath, string teamId, string presentationId)
    {
        if (!IsValidTeamId(teamId) || !TryNormalizePresentationId(presentationId, out var pres))
            return null;
        try { return ReadStats(StatsPath(dataPath, teamId, pres)); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
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
                summary.Outcome = pass.Stopped ? StatsRunOutcome.Stopped
                    : pass.Problems.Count > 0 ? StatsRunOutcome.Failed
                    : StatsRunOutcome.Completed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The share (or a folder on it) went away mid-run. Counts so far are kept; a re-run resumes.
                summary.Outcome = StatsRunOutcome.Failed;
                pass.Problems.Add(ex.Message);
            }
            catch (SimulatedCrashException)
            {
                summary.Outcome = StatsRunOutcome.Failed;
                pass.Problems.Add("simulated crash");
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

    // ── The phases ───────────────────────────────────────────────────────────

    private enum Kind { Valid, Invalid, Duplicate, Skipped, Gone }

    private sealed class Item
    {
        public string Path = "";
        public string Name = "";
        public Kind Kind;
        public string Team = "", Pres = "", Day = "", Dest = "";
        public bool IsView, IsClick;
        public Exception? Error;
        public bool MoveOk;
    }

    private sealed class SimulatedCrashException : Exception;

    private sealed class Pass(
        PosterStatisticsEngine e, string distRoot, string telemetry, string dataPath,
        StatsRunSummary summary, IProgress<StatsRunProgress>? progress, CancellationToken ct)
    {
        private readonly string _invalidDir = Path.Combine(telemetry, "_invalid");
        private readonly Dictionary<string, PresentationStats> _stats = new(StringComparer.Ordinal);
        private readonly HashSet<string> _corrupt = new(StringComparer.Ordinal);
        private readonly HashSet<string> _failedDest = new(StringComparer.OrdinalIgnoreCase);
        private int _consecutiveIoFailures;
        private int _skipped;
        private int _done;

        public bool Stopped { get; private set; }
        public List<string> Problems { get; } = new();

        public void Execute()
        {
            var sw = Stopwatch.StartNew();
            try { Recover(); }
            finally { summary.RecoverMs = sw.ElapsedMilliseconds; }

            if (!Stopped)
            {
                sw.Restart();
                try { Process(); }
                finally { summary.ProcessMs = sw.ElapsedMilliseconds; }
            }

            if (!Stopped)
            {
                sw.Restart();
                try { Prune(); }
                finally { summary.PruneMs = sw.ElapsedMilliseconds; }
            }
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

        private void Report(StatsPhase phase, int done, string? item) =>
            progress?.Report(new StatsRunProgress(phase, done, null, item));

        // A single locked/vanished file is skipped; a run of failures, or a share that is gone, aborts.
        private void NoteSkip(string item, Exception ex)
        {
            _skipped++;
            e.Log($"Skipped {item}: {ex.Message}");
            if (++_consecutiveIoFailures >= MaxConsecutiveIoFailures || !Directory.Exists(distRoot))
                throw new IOException($"lost access to the distribution share ({ex.Message})", ex);
        }

        private void NoteOk() => _consecutiveIoFailures = 0;

        private string CountedDir(string team, string pres) => Path.Combine(telemetry, team, pres);

        private bool TryGetStats(string team, string pres, out PresentationStats stats)
        {
            var key = $"{team}/{pres}";
            if (_stats.TryGetValue(key, out stats!)) return true;
            if (_corrupt.Contains(key)) return false;
            try
            {
                stats = ReadStats(e.StatsPath(dataPath, team, pres))
                        ?? new PresentationStats { TeamId = team, PresentationId = pres };
                _stats[key] = stats;
                return true;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                _corrupt.Add(key);
                e.Log($"Statistics for {key} are unreadable: {ex.Message}");
                Problems.Add($"statistics file for {key} is unreadable; its raw files were left in place");
                stats = null!;
                return false;
            }
        }

        // ── 1. Recover ──

        // Finishes the moves of files a crashed run had already counted. Must complete before Process reads
        // anything: such a file may still sit in uploads\ and would otherwise be counted a second time.
        private void Recover()
        {
            var statsRoot = Path.Combine(dataPath, "statistics");
            if (!Directory.Exists(statsRoot)) return;

            var unfinished = 0;
            foreach (var teamDir in Directory.EnumerateDirectories(statsRoot))
            {
                var team = Path.GetFileName(teamDir);
                if (!IsValidTeamId(team)) continue;

                foreach (var file in Directory.EnumerateFiles(teamDir, "*.json"))
                {
                    if (CancelRequested()) return;
                    var pres = Path.GetFileNameWithoutExtension(file);
                    if (!TryNormalizePresentationId(pres, out var norm) || norm != pres) continue;
                    if (!TryGetStats(team, pres, out var stats) || stats.PendingMove.Count == 0) continue;

                    var counted = CountedDir(team, pres);
                    var remaining = new List<string>();
                    foreach (var name in stats.PendingMove)
                    {
                        if (Path.GetFileName(name) != name) continue;                     // never follow a tampered path
                        var dest = Path.Combine(counted, name);
                        if (File.Exists(dest)) { _done++; continue; }                      // already counted + moved

                        var src = new[] { Path.Combine(distRoot, "uploads", name), Path.Combine(counted, "new", name) }
                            .FirstOrDefault(File.Exists);
                        if (src is null) continue;                                         // gone

                        try
                        {
                            Directory.CreateDirectory(counted);
                            File.Move(src, dest, overwrite: true);
                            NoteOk();
                            Report(StatsPhase.Recover, ++_done, name);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            remaining.Add(name);
                            e.Log($"Recovery could not move {name}: {ex.Message}");
                            _consecutiveIoFailures++;
                        }
                    }

                    stats.PendingMove = remaining;
                    e.SaveStats(file, stats);
                    unfinished += remaining.Count;
                }
            }

            // Reading new input now could count those files again.
            if (unfinished > 0)
                throw new IOException($"recovery could not move {unfinished} already-counted file(s); nothing new was read");
        }

        // ── 2. Process ──

        private IEnumerable<string> LegacyFiles(HashSet<string> legacyDirs)
        {
            foreach (var teamDir in Directory.EnumerateDirectories(telemetry))
            {
                if (!IsValidTeamId(Path.GetFileName(teamDir))) continue;                  // also skips _invalid
                foreach (var presDir in Directory.EnumerateDirectories(teamDir))
                {
                    var name = Path.GetFileName(presDir);
                    if (!TryNormalizePresentationId(name, out var norm) || norm != name) continue;
                    var newDir = Path.Combine(presDir, "new");
                    if (!Directory.Exists(newDir)) continue;
                    legacyDirs.Add(newDir);
                    foreach (var f in Directory.EnumerateFiles(newDir, "session-*.json")) yield return f;
                }
            }
        }

        private void Process()
        {
            var uploads = Path.Combine(distRoot, "uploads");
            var legacyDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IEnumerable<string> Inputs()
            {
                if (Directory.Exists(uploads))
                    foreach (var f in Directory.EnumerateFiles(uploads, "session-*.json")) yield return f;
                foreach (var f in LegacyFiles(legacyDirs)) yield return f;
            }

            try
            {
                foreach (var chunk in Inputs().Chunk(StatisticsDefaults.BatchSize))
                {
                    if (CancelRequested()) break;
                    ProcessBatch(chunk);
                    if (Stopped) break;
                }
            }
            finally
            {
                foreach (var dir in legacyDirs)
                {
                    try { Directory.Delete(dir); }                                         // non-recursive: only if empty
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }

        private void ProcessBatch(string[] chunk)
        {
            // a. Read + classify, bounded-parallel. A cancel keeps only the files already read.
            var items = new Item?[chunk.Length];
            var reads = 0;
            try
            {
                Parallel.ForEachAsync(
                    Enumerable.Range(0, chunk.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = StatisticsDefaults.ReadParallelism, CancellationToken = ct },
                    (i, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        items[i] = Classify(chunk[i]);
                        var n = Interlocked.Increment(ref reads);
                        if (progress is not null && n % ProgressEvery == 0)
                            Report(StatsPhase.Process, _done + n, items[i]!.Name);
                        return ValueTask.CompletedTask;
                    }).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                Stopped = true;
            }

            var read = items.Where(x => x is not null).Select(x => x!).ToList();

            // A name seen twice (e.g. an inbox file and a legacy new\ file) or one whose earlier move failed
            // is a duplicate: it is counted at most once.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in read.Where(x => x.Kind == Kind.Valid))
                if (!seen.Add(it.Dest) || _failedDest.Contains(it.Dest))
                    it.Kind = Kind.Duplicate;

            // b. Save stats (serial, one write per stats file), PendingMove first.
            var groups = new List<(string Key, PresentationStats Stats, List<string> Baseline, List<Item> Items)>();
            foreach (var g in read.Where(x => x.Kind == Kind.Valid).GroupBy(x => $"{x.Team}/{x.Pres}"))
            {
                var first = g.First();
                if (!TryGetStats(first.Team, first.Pres, out var stats))
                {
                    foreach (var it in g) it.Kind = Kind.Gone;                             // left in place, run ends Failed
                    continue;
                }

                var views = 0;
                foreach (var it in g.Where(x => x.IsView))
                {
                    if (!stats.Days.TryGetValue(it.Day, out var counts))
                        stats.Days[it.Day] = counts = new DayCounts();
                    counts.Views++;
                    if (it.IsClick) counts.Clicks++;
                    views++;
                }

                // Notification-only groups change no counts, so need no guard: re-processing them is harmless.
                if (views == 0) continue;

                var baseline = stats.PendingMove.ToList();
                stats.PendingMove = baseline.Concat(g.Select(x => x.Name)).ToList();
                e.SaveStats(e.StatsPath(dataPath, first.Team, first.Pres), stats);
                summary.Counted += views;
                groups.Add((g.Key, stats, baseline, g.ToList()));
            }

            // c. Move (MoveParallelism). From here on the batch always completes.
            var moves = read.Where(x => x.Kind is Kind.Valid or Kind.Invalid or Kind.Duplicate).ToList();
            Parallel.ForEachAsync(
                moves,
                new ParallelOptions { MaxDegreeOfParallelism = StatisticsDefaults.MoveParallelism },
                (it, _) =>
                {
                    try
                    {
                        switch (it.Kind)
                        {
                            case Kind.Valid:
                                if (e.BeforeMove is { } hook && !hook(it.Path)) throw new SimulatedCrashException();
                                Directory.CreateDirectory(Path.GetDirectoryName(it.Dest)!);
                                File.Move(it.Path, it.Dest, overwrite: true);
                                break;
                            case Kind.Invalid:
                                Directory.CreateDirectory(_invalidDir);
                                File.Move(it.Path, Path.Combine(_invalidDir, it.Name), overwrite: true);
                                break;
                            default:
                                File.Delete(it.Path);
                                break;
                        }
                        it.MoveOk = true;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        it.Error = ex;                                                     // left in place
                    }
                    return ValueTask.CompletedTask;
                }).GetAwaiter().GetResult();

            // d. Clear PendingMove for what moved; a failed move stays listed for the next run's recovery.
            foreach (var (_, stats, baseline, groupItems) in groups)
            {
                stats.PendingMove = baseline.Concat(groupItems.Where(x => !x.MoveOk).Select(x => x.Name)).ToList();
                var first = groupItems[0];
                e.SaveStats(e.StatsPath(dataPath, first.Team, first.Pres), stats);
            }

            foreach (var it in moves)
            {
                if (it.MoveOk)
                {
                    _done++;
                    summary.Processed++;
                    if (it.Kind == Kind.Invalid) summary.Invalid++;
                    else if (it.Kind == Kind.Duplicate) summary.DuplicatesDropped++;
                }
                else if (it.Kind == Kind.Valid)
                {
                    _failedDest.Add(it.Dest);
                }
            }

            // Failure accounting last, so a share-loss abort never precedes the saves above.
            foreach (var it in read)
            {
                if (it.Kind == Kind.Skipped || (it.Kind != Kind.Gone && !it.MoveOk))
                    NoteSkip(it.Name, it.Error ?? new IOException("not moved"));
                else if (it.Kind != Kind.Gone)
                    NoteOk();
            }

            Report(StatsPhase.Process, _done, null);
        }

        // Synchronous reads on the bounded set of pool threads: async file I/O costs more than it saves for
        // many tiny files, and over SMB the 8-way concurrency is what hides the latency.
        private Item Classify(string path)
        {
            var item = new Item { Path = path, Name = System.IO.Path.GetFileName(path) };

            SessionTelemetry? record;
            try
            {
                var json = File.ReadAllText(path);
                record = JsonSerializer.Deserialize<SessionTelemetry>(json, SessionOptions);
            }
            catch (JsonException)
            {
                item.Kind = Kind.Invalid;
                return item;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                item.Kind = Kind.Gone;
                return item;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                item.Kind = Kind.Skipped;
                item.Error = ex;
                return item;
            }

            if (record is null
                || !IsValidTeamId(record.TeamId)
                || !TryNormalizePresentationId(record.PresentationId, out var pres)
                || record.SessionStartTime == default)
            {
                item.Kind = Kind.Invalid;
                return item;
            }

            item.Team = record.TeamId;
            item.Pres = pres;
            item.Dest = System.IO.Path.Combine(CountedDir(item.Team, pres), item.Name);

            if (File.Exists(item.Dest))
            {
                item.Kind = Kind.Duplicate;                                                // re-upload of a counted file
                return item;
            }

            item.Kind = Kind.Valid;
            item.Day = DayKey(record.SessionStartTime);
            item.IsView = !string.Equals(record.RecordType, SessionTelemetry.NotificationRecordType, StringComparison.Ordinal);
            item.IsClick = item.IsView && string.Equals(record.CloseReason, "UrlLaunch", StringComparison.Ordinal);
            return item;
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
                        foreach (var f in Directory.EnumerateFiles(presDir)) yield return f;   // parent level only
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
                if (progress is not null && ++done % ProgressEvery == 0)
                    Report(StatsPhase.Prune, done, null);
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
