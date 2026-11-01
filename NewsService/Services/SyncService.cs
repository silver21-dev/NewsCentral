using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NewsCentral.Configuration;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;
using NewsService.Models;

namespace NewsService.Services;

/// <summary>
/// Orchestrates one complete poll cycle:
///   1. For each team: compare remote index hash vs cached hash; sync changed images.
///   2. Apply both display surfaces — the lock screen and the desktop wallpaper — each from the
///      most-recently-modified active assignment matching its display type, or a configurable
///      default image when no content is active. Both surfaces are picked in a single
///      enumeration pass over the cached team indexes (ActiveAssignmentSelector.PickNewestActive
///      with two predicates), never two separate walks of the index files. The winning image for
///      each surface is re-verified against its signed hash and published to a protected,
///      non-user-writable folder (IImagePublisher, one content-derived namespace per surface)
///      before PersonalizationCSP is pointed at it — not at the ProgramData cache directly.
///      Three-state, registry-driven, and stateless per surface: the live PersonalizationCSP
///      value is the single source of truth. When there is no active content and no usable
///      default, a value NewsService previously published is CLEARED — returning the machine to
///      Windows' own default at the next lock/logon — but a value it did not publish (GPO,
///      Intune, or a manual admin change) is always left alone. Either surface can be disabled
///      independently via Delivery:LockScreenEnabled / Delivery:WallpaperEnabled (both default
///      true) for RDS/VDI/RemoteApp hosts, where one machine-wide value cannot correctly serve
///      many sessions; disabling one is NOT a revert — whatever is currently applied for that
///      surface stays frozen in place. Lock screen applies before wallpaper; each surface is
///      isolated in its own try/catch so a failure in one can never prevent the other from
///      applying. A failed write or clear is never recorded as applied — the next cycle
///      re-evaluates and retries naturally.
///   3. Write status.json.
///   4. Upload session telemetry from the uploads folder.
///
/// NewsViewer no longer applies any wallpaper image — it retains only a small per-user HKCU
/// wallpaper STYLE assertion (style is not covered by PersonalizationCSP and is unreachable from
/// NewsService's session-0 context). See docs/newsviewer-spec.md.
/// </summary>
public sealed class SyncService(
    IRepositoryReader repository,
    CacheManager cache,
    IPersonalizationService lockScreen,
    IImagePublisher imagePublisher,
    TelemetryUploader telemetry,
    EntraTeamResolutionService entra,
    IConfiguration configuration,
    ILogger<SyncService> logger,
    IContentChangedNotifier contentChanged,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private static readonly JsonSerializerOptions Json = JsonDefaults.Options;

    // Per-team verification result from the previous cycle. SyncService is a singleton and Worker
    // runs cycles sequentially, so a plain Dictionary is safe and survives across cycles. Lets
    // acceptance follow the cycle-wide "log on change" rule: loud only when the index or the
    // verification result changed, so a misconfigured team (Unsigned/Disabled) is reported at least
    // once per service start instead of flooding every cycle — or being silent forever.
    private readonly Dictionary<string, VerifyResult> _lastVerifyResult =
        new(StringComparer.OrdinalIgnoreCase);

    // Per team: the last index-rejection message that was logged at Error and when. A persistent
    // misconfiguration (wrong or missing key) would otherwise log an Error every poll; instead the
    // Error is repeated only when the reason changes or RejectionRepeatInterval has passed, and the
    // message drops to Debug in between. Cleared when the team's index is accepted. Singleton +
    // sequential cycles, so a plain Dictionary is safe.
    private static readonly TimeSpan RejectionRepeatInterval = TimeSpan.FromMinutes(60);
    private readonly Dictionary<string, (string Message, DateTimeOffset LoggedAt)> _lastRejection =
        new(StringComparer.OrdinalIgnoreCase);

    // Change-gated across cycles — same rationale as _lastVerifyResult above and _lastSummary in
    // EntraTeamResolutionService: a machine legitimately without any teams (static or dynamic)
    // must not warn every poll interval forever, but the transition into and back out of that
    // state should always be visible at least once.
    private bool _lastEffectiveTeamsEmpty;

    // Per team: the IndexHash whose sync last failed to complete (an image could not be cached), so
    // the Error is logged once per failing hash and repeats of the same hash drop to Debug. Cleared
    // when that hash finally completes. Singleton + sequential cycles, so a plain Dictionary is safe.
    private readonly Dictionary<string, string> _incompleteHash = new(StringComparer.OrdinalIgnoreCase);

    // Per team: the IndexHash for which cache self-heal found images it could not restore, so the
    // Warning is raised once per team and hash and repeats drop to Debug. Cleared when a heal
    // succeeds or the hash changes.
    private readonly Dictionary<string, string> _unhealableHash = new(StringComparer.OrdinalIgnoreCase);

    public async Task RunCycleAsync(string[] teams, CancellationToken ct)
    {
        bool online = false;
        string syncSource = "None";

        // Entra device team resolution — time-boxed and failure-isolated so a Graph problem
        // never stalls or fails blob sync. Runs FIRST so resolved-teams.json reflects this cycle
        // before the effective team set is computed below.
        await RefreshEntraTeamsAsync(ct);

        // Effective teams = static (registry/appsettings) ∪ dynamic (Entra-resolved). Per-team
        // verification routes through SignatureGate.Verify (KeyId selects the configured key, team binding),
        // so dynamic and static teams verify identically.
        var (effectiveTeams, _) = ResolveEffectiveTeams(teams, cache.Root);

        // An empty effective set is a legitimate, ongoing state — never a reason to skip the rest
        // of the cycle. Display-surface teardown, status.json, and telemetry retention all still
        // need to run below regardless of team count; a machine whose last team just disappeared
        // needs its stale CSP values cleared precisely BECAUSE there is nothing left to sync.
        LogEmptyEffectiveTeamsIfNeeded(effectiveTeams.Count == 0);

        try
        {
            if (!repository.IsAvailable)
            {
                logger.LogWarning("Repository not reachable — content served from cache only");
                await HealCachedTeamsAsync(effectiveTeams, ct);
            }
            else
            {
                online = await SyncAllTeamsAsync(effectiveTeams, ct);
                syncSource = online ? repository.SyncSource : "None";
            }

            await ApplyDisplaySurfacesAsync(effectiveTeams);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during sync cycle");
        }
        finally
        {
            await WriteStatusAsync(online, syncSource);
        }

        await telemetry.ProcessAsync(ct);
    }

    /// <summary>
    /// Warns once when the effective team set (static union dynamic) newly becomes empty, then
    /// demotes to Debug while that state persists so a machine legitimately without any teams —
    /// static or Entra-resolved — does not warn every poll interval forever. Warns again if teams
    /// later appear and subsequently disappear. Names both sources and reports whether Entra is
    /// enabled, since the fix differs depending on which one is expected to be supplying teams.
    /// </summary>
    private void LogEmptyEffectiveTeamsIfNeeded(bool isEmpty)
    {
        if (!isEmpty)
        {
            _lastEffectiveTeamsEmpty = false;
            return;
        }

        var entraState = configuration.GetValue<bool>("Entra:Enabled") ? "enabled" : "disabled";

        if (!_lastEffectiveTeamsEmpty)
            logger.LogWarning(
                "No teams to sync this cycle — static team list is empty " +
                "(HKLM\\Software\\{Company}\\{Solution}\\NewsService\\teams\\) and Entra " +
                "dynamic-team resolution is {EntraState} (Entra:Enabled, resolved-teams.json).",
                SolutionConstants.Company, SolutionConstants.SolutionName, entraState);
        else
            logger.LogDebug(
                "No teams to sync (unchanged) — static team list still empty, Entra still {EntraState}.",
                entraState);

        _lastEffectiveTeamsEmpty = true;
    }

    // ── Step 0 — Entra dynamic-team resolution (time-boxed, isolated) ─────────

    private async Task RefreshEntraTeamsAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await entra.RefreshAsync(cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // service shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Entra team resolution failed — continuing with static teams.");
        }
    }

    /// <summary>
    /// Combines the static team list with the dynamic (Entra-resolved) team set read from
    /// resolved-teams.json. Returned as the de-duplicated effective list plus the dynamic set
    /// (so callers can ask isDynamic per team). Pure aside from the resolved-teams.json read.
    /// </summary>
    internal static (List<string> Effective, HashSet<string> Dynamic) ResolveEffectiveTeams(
        string[] staticTeams, string cacheRootPath)
    {
        var dynamicTeams = ResolvedTeamsReader.ReadDynamicTeamFolders(cacheRootPath);
        var effective    = EffectiveTeams.Union(staticTeams, dynamicTeams);
        return (effective, dynamicTeams);
    }

    // Rejections are never silenced: Error on the first occurrence, on a changed reason, and again once
    // per RejectionRepeatInterval while unchanged; Debug in between.
    private void LogIndexRejected(string team, string reason, string suffix = "")
    {
        var message = reason + suffix;
        var now     = _clock.GetUtcNow();

        if (!_lastRejection.TryGetValue(team, out var last)
            || last.Message != message
            || now - last.LoggedAt >= RejectionRepeatInterval)
        {
            _lastRejection[team] = (message, now);
            logger.LogError("Team {Team}: index rejected — {Reason}", team, message);
        }
        else
        {
            logger.LogDebug("Team {Team}: index rejected — {Reason}", team, message);
        }
    }

    // ── Step 1 — team sync ───────────────────────────────────────────────────

    internal async Task<bool> SyncAllTeamsAsync(
        IReadOnlyList<string> teams, CancellationToken ct)
    {
        // An empty list is vacuously "no failures" but is not "synced successfully" — without this
        // check, RunCycleAsync would report isOnline = true on a cycle that synced nothing at all.
        if (teams.Count == 0) return false;

        bool allOk = true;
        bool contentChangedThisCycle = false;
        try
        {
            foreach (var teamFolder in teams)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (await SyncTeamAsync(teamFolder, ct))
                        contentChangedThisCycle = true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to sync team {Team}", teamFolder);
                    allOk = false;
                }
            }
        }
        finally
        {
            // One ContentChanged event per cycle, after ALL teams have finished — a task launched by it
            // never races a team still downloading. In a finally so teams already completed are still
            // announced if the service is stopping mid-cycle (their index is cached and would never
            // look "changed" again).
            if (contentChangedThisCycle) NotifyContentChanged();
        }
        return allOk;
    }

    /// <summary>
    /// Syncs one team. Returns true only when an accepted index change was fully materialized in the
    /// cache this cycle — every referenced image cached, then the index written last. False for an
    /// unchanged, rejected or incomplete sync (an incomplete sync leaves the previously cached index in
    /// effect and is retried next cycle, downloading only what is still missing).
    /// </summary>
    internal async Task<bool> SyncTeamAsync(string teamFolder, CancellationToken ct)
    {
        var remoteJson = await repository.ReadTextAsync($"{teamFolder}/index.json", ct);
        if (remoteJson is null)
        {
            logger.LogWarning("No index.json in repository for team {Team}", teamFolder);
            return false;
        }

        var remoteIndex = JsonSerializer.Deserialize<TeamIndexFile>(remoteJson, Json);
        if (remoteIndex is null) return false;

        var keys          = SigningKeyConfigurationReader.GetPublicKeys(configuration);
        var result        = SignatureGate.Verify(remoteIndex, teamFolder, keys);
        var requireSigned = configuration.GetValue("Signing:RequireSignedIndex", true);

        // Tracked for rejected results too, so a later recovery (e.g. Invalid → Valid after a key
        // is provisioned) counts as a change and logs at its natural level.
        var resultChanged = !_lastVerifyResult.TryGetValue(teamFolder, out var previous)
                            || previous != result;
        _lastVerifyResult[teamFolder] = result;

        if (SignatureGate.ShouldReject(result, requireSigned, out var reason, remoteIndex))
        {
            LogIndexRejected(teamFolder, reason);
            return false;
        }

        // Path hardening: one bad ImagePath rejects the WHOLE index (before anything is read, written
        // or published), even when IndexHash is unchanged — a tampered path must not ride on a stale hash.
        if (!TryValidateImagePaths(teamFolder, remoteIndex, out var pathFailure))
        {
            LogIndexRejected(teamFolder, pathFailure, "; previous cached state kept");
            return false;
        }

        _lastRejection.Remove(teamFolder);   // accepted: a later rejection must log immediately

        var cachedIndex  = await cache.ReadJsonAsync<TeamIndexFile>($"{teamFolder}/index.json");
        var indexChanged = cachedIndex?.IndexHash != remoteIndex.IndexHash;

        // Acceptance logs at its natural level only when something changed — the index bytes or the
        // verification result — and at Debug on the steady state, matching every other cycle path.
        // A retry of a hash that already failed to complete is not news: it stays at Debug.
        bool retryOfIncomplete = _incompleteHash.TryGetValue(teamFolder, out var failedHash)
                                 && failedHash == remoteIndex.IndexHash;
        if ((indexChanged && !retryOfIncomplete) || resultChanged)
        {
            if (result == VerifyResult.Unsigned)
                logger.LogWarning("Team {Team}: index accepted — {Reason}", teamFolder, reason);
            else
                logger.LogInformation("Team {Team}: index accepted — {Reason}", teamFolder, reason);
        }
        else
        {
            logger.LogDebug("Team {Team}: index accepted — {Reason}", teamFolder, reason);
        }

        if (!indexChanged)
        {
            logger.LogDebug("Team {Team}: index unchanged", teamFolder);
            await SelfHealCacheAsync(teamFolder, cachedIndex!, ct);
            return false;   // a heal is not a content change: no index rewrite, no notification
        }

        _unhealableHash.Remove(teamFolder);

        logger.Log(retryOfIncomplete ? LogLevel.Debug : LogLevel.Information,
            "Team {Team}: index changed — syncing {Count} assignment(s)",
            teamFolder, remoteIndex.PublishedAssignments.Count);

        var missing = new List<string>();
        foreach (var assignment in remoteIndex.PublishedAssignments)
        {
            ct.ThrowIfCancellationRequested();
            if (!await SyncImageAsync(teamFolder, assignment, retryOfIncomplete ? LogLevel.Debug : LogLevel.Error, ct))
                missing.Add(assignment.Content.ImagePath);
        }

        // The index is written LAST and only when every referenced image is cached. On an incomplete
        // sync the previously cached index stays in effect (NewsViewer keeps showing the previous
        // content), the hash still differs next cycle so the sync is retried, and images already
        // downloaded are kept — only the missing ones are fetched again.
        if (missing.Count > 0)
        {
            if (!retryOfIncomplete)
                logger.LogError("Team {Team}: sync incomplete — index not updated, will retry; missing image(s): {Images}",
                    teamFolder, string.Join(", ", missing));
            else
                logger.LogDebug("Team {Team}: sync still incomplete — missing image(s): {Images}",
                    teamFolder, string.Join(", ", missing));
            _incompleteHash[teamFolder] = remoteIndex.IndexHash;
            return false;
        }

        await cache.WriteTextAtomicAsync($"{teamFolder}/index.json", remoteJson);
        _incompleteHash.Remove(teamFolder);
        logger.LogInformation("Team {Team}: sync complete", teamFolder);
        return true;
    }

    /// <summary>
    /// Runs cache self-heal for every team's cached index without contacting the repository for the
    /// index — the path taken when the source is unreachable, so a tampered file is still deleted
    /// (the re-download then fails and the slot stays empty until the source returns).
    /// </summary>
    internal async Task HealCachedTeamsAsync(IReadOnlyList<string> teams, CancellationToken ct)
    {
        foreach (var teamFolder in teams)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var cachedIndex = await cache.ReadJsonAsync<TeamIndexFile>($"{teamFolder}/index.json");
                if (cachedIndex is not null) await SelfHealCacheAsync(teamFolder, cachedIndex, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Team {Team}: offline cache self-heal skipped", teamFolder);
            }
        }
    }

    /// <summary>
    /// Cache self-heal (unchanged index, or source unreachable): every image the cached index references
    /// is checked locally. A file whose SHA-256 differs from <c>ImageHash</c> is logged, deleted and
    /// re-fetched through the verified per-image sync; an absent file is re-fetched the same way. A
    /// re-fetch that fails verification or cannot reach the source leaves the slot empty (retried next
    /// cycle). Covers files removed (antivirus, cleanup) or replaced in place. Never throws except for
    /// cancellation.
    /// </summary>
    private async Task SelfHealCacheAsync(string teamFolder, TeamIndexFile cachedIndex, CancellationToken ct)
    {
        try
        {
            // The cached index may predate path validation: never build a path from an unvalidated entry.
            if (!TryValidateImagePaths(teamFolder, cachedIndex, out var pathFailure))
            {
                logger.LogError("Team {Team}: cached index skipped by self-heal — {Reason}", teamFolder, pathFailure);
                return;
            }

            var missing = new List<PublishedAssignmentIndex>();
            foreach (var a in cachedIndex.PublishedAssignments)
            {
                ct.ThrowIfCancellationRequested();
                if (!cache.FileExists(a.Content.ImagePath)) { missing.Add(a); continue; }
                if (await DeleteIfTamperedAsync(teamFolder, a)) missing.Add(a);
            }
            if (missing.Count == 0) return;

            var repeat = _unhealableHash.TryGetValue(teamFolder, out var known) && known == cachedIndex.IndexHash;
            var restored = new List<string>();
            var failed   = new List<string>();
            foreach (var assignment in missing)
            {
                ct.ThrowIfCancellationRequested();
                (await SyncImageAsync(teamFolder, assignment, repeat ? LogLevel.Debug : LogLevel.Error, ct)
                    ? restored : failed).Add(assignment.Content.ImagePath);
            }

            if (restored.Count > 0)
                logger.LogWarning("Team {Team}: cache self-heal restored {Count} image(s): {Images}",
                    teamFolder, restored.Count, string.Join(", ", restored));

            if (failed.Count == 0)
            {
                _unhealableHash.Remove(teamFolder);
                return;
            }
            ReportUnhealable(teamFolder, cachedIndex.IndexHash, string.Join(", ", failed));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ReportUnhealable(teamFolder, cachedIndex.IndexHash, ex.Message);
        }
    }

    // Cached-file mismatches already reported, keyed "path|actual hash": the Error is logged once per
    // file and tampered content, repeats of the same content drop to Debug.
    private readonly HashSet<string> _reportedMismatch = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the cached file did not match its <c>ImageHash</c> and was deleted (so the caller
    /// re-fetches it). A file with a missing or malformed hash is left alone — the verified sync path
    /// reports that.
    /// </summary>
    private async Task<bool> DeleteIfTamperedAsync(string teamFolder, PublishedAssignmentIndex assignment)
    {
        var path = assignment.Content.ImagePath;
        if (!ImageHashVerifier.TryParse(assignment.Content.ImageHash, out var expected, out _)) return false;

        var actual = await cache.ComputeHashAsync(path);
        if (actual is null || ImageHashVerifier.DigestMatches(actual, expected)) return false;

        var actualHex = Convert.ToHexString(actual).ToLowerInvariant();
        var repeat = !_reportedMismatch.Add($"{path}|{actualHex}");
        logger.Log(repeat ? LogLevel.Debug : LogLevel.Error,
            "Team {Team}: cached image {Path} (assignment {AssignmentId}) does not match ImageHash — " +
            "expected sha256:{Expected}, actual sha256:{Actual}; deleting and re-downloading",
            teamFolder, path, assignment.AssignmentId, Convert.ToHexString(expected).ToLowerInvariant(), actualHex);

        try
        {
            cache.DeleteImage(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Log(repeat ? LogLevel.Debug : LogLevel.Error, ex,
                "Team {Team}: could not delete tampered cached image {Path}", teamFolder, path);
        }
        return true;
    }

    private void ReportUnhealable(string teamFolder, string indexHash, string detail)
    {
        bool repeat = _unhealableHash.TryGetValue(teamFolder, out var known) && known == indexHash;
        _unhealableHash[teamFolder] = indexHash;
        logger.Log(repeat ? LogLevel.Debug : LogLevel.Warning,
            "Team {Team}: cache self-heal could not restore: {Detail}", teamFolder, detail);
    }

    /// <summary>
    /// Validates every entry's <c>ImagePath</c> (shape, then containment for the cache path and the
    /// repository source — share path or blob name). Names the first offending entry in
    /// <paramref name="failure"/>; any failure means the whole index must be rejected.
    /// </summary>
    private bool TryValidateImagePaths(string teamFolder, TeamIndexFile index, out string failure)
    {
        foreach (var a in index.PublishedAssignments)
        {
            var path = a.Content.ImagePath;
            var entry = $"assignment {a.AssignmentId} (presentation {a.PresentationId}) ImagePath '{path}'";

            var shape = ImagePathValidator.Validate(path, teamFolder);
            if (!shape.IsValid) { failure = $"{entry}: {shape.Reason}"; return false; }

            if (!cache.TryValidateImagePath(teamFolder, path, out var cacheReason))
            { failure = $"{entry}: cache path {cacheReason}"; return false; }

            if (!repository.TryValidateImagePath(teamFolder, path, out var sourceReason))
            { failure = $"{entry}: source path {sourceReason}"; return false; }
        }

        failure = string.Empty;
        return true;
    }

    /// <summary>
    /// True when the image is cached and verified; false when it could not be verified, downloaded or
    /// cached. A cached file is a hit only if its SHA-256 equals <c>ImageHash</c>; a download is written
    /// only after its SHA-256 matches, so a missing hash or a mismatch leaves the previous file untouched.
    /// <paramref name="rejectLevel"/> is Error on first sight and Debug for repeats of the same IndexHash.
    /// </summary>
    private async Task<bool> SyncImageAsync(
        string teamFolder, PublishedAssignmentIndex assignment, LogLevel rejectLevel, CancellationToken ct)
    {
        var relativePath = assignment.Content.ImagePath;

        if (!ImageHashVerifier.TryParse(assignment.Content.ImageHash, out var expected, out var hashProblem))
        {
            logger.Log(rejectLevel, "Team {Team}: image {Path} rejected — {Reason}; previous cached file kept",
                teamFolder, relativePath, hashProblem);
            return false;
        }

        var cachedHash = cache.FileExists(relativePath) ? await cache.ComputeHashAsync(relativePath) : null;
        if (cachedHash is not null && ImageHashVerifier.DigestMatches(cachedHash, expected))
        {
            logger.LogDebug("Image up-to-date: {Path}", relativePath);
            return true;
        }

        try
        {
            var bytes = await repository.ReadBytesAsync(relativePath, ct);
            if (bytes is null)
            {
                // Reported once per failing IndexHash by SyncTeamAsync, not per cycle.
                logger.LogDebug("Image not found in repository: {Path}", relativePath);
                return false;
            }

            if (!ImageHashVerifier.Matches(bytes, expected))
            {
                logger.Log(rejectLevel,
                    "Team {Team}: image {Path} rejected — downloaded bytes do not match ImageHash; previous cached file kept",
                    teamFolder, relativePath);
                return false;
            }

            await cache.WriteBytesAsync(relativePath, bytes);
            logger.LogDebug("Image synced: {Path} ({Bytes} bytes)", relativePath, bytes.Length);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Image download/cache failed: {Path}", relativePath);
            return false;
        }
    }

    private void NotifyContentChanged()
    {
        try { contentChanged.NotifyContentChanged(); }
        catch (Exception ex) { logger.LogWarning(ex, "Content-changed notification failed"); }
    }

    // ── Step 2 — display surfaces (lock screen, then wallpaper) ────────────────
    //
    // Three-state, registry-driven, and stateless per surface: the live PersonalizationCSP value
    // for each surface is the single source of truth. Delivery:{LockScreen,Wallpaper}Enabled
    // (both default true) gate their surface ENTIRELY — when false, nothing for that surface is
    // read, published, swept, written, or cleared. Otherwise each cycle computes the intended
    // source image for the surface, publishes it (re-verified, protected folder — see
    // IImagePublisher), and dispatches on (intended published path, live CSP value): write when
    // they differ, clear when intended is null and the live value is one NewsService itself
    // published, leave alone when intended is null and the live value is foreign (or absent).
    // There is no servicestate.json — a failed write or clear simply fails to match next cycle
    // and retries naturally.

    /// <summary>
    /// Applies both display surfaces for one cycle: one enumeration pass over every team's cached
    /// index feeds both winners (ActiveAssignmentSelector.PickNewestActive with two predicates —
    /// never two separate walks of the index files), then lock screen applies before wallpaper,
    /// each isolated in its own try/catch so a failure in one can never prevent the other.
    /// </summary>
    internal async Task ApplyDisplaySurfacesAsync(IReadOnlyList<string> teams)
    {
        var assignments = await CollectAssignmentsAsync(teams);
        if (assignments is null) return;   // a cached index failed validation: nothing published, swept or cleared

        try { await ApplyLockScreenAsync(teams, assignments); }
        catch (Exception ex) { logger.LogError(ex, "Lock screen apply failed for this cycle"); }

        try { await ApplyWallpaperAsync(teams, assignments); }
        catch (Exception ex) { logger.LogError(ex, "Wallpaper apply failed for this cycle"); }
    }

    /// <summary>
    /// Reads every team's cached index once and flattens all published assignments into one list
    /// — the single enumeration pass both display-surface winners are picked from. The cached index is
    /// used here without a fresh download (source unreachable, or a downloaded index rejected), so it is
    /// re-validated like a downloaded one (it may predate path validation). Returns null when any team's
    /// cached index fails: the caller then leaves the display surfaces entirely alone (no publish, sweep,
    /// write or clear) so the previously published state stays in place.
    /// </summary>
    private async Task<List<PublishedAssignmentIndex>?> CollectAssignmentsAsync(IReadOnlyList<string> teams)
    {
        var all = new List<PublishedAssignmentIndex>();
        var valid = true;
        foreach (var teamFolder in teams)
        {
            var index = await cache.ReadJsonAsync<TeamIndexFile>($"{teamFolder}/index.json");
            if (index?.PublishedAssignments is null) continue;

            if (!TryValidateImagePaths(teamFolder, index, out var failure))
            {
                // Security outcome: Error the first time this index/reason is seen, Debug on repeats.
                var key = $"{index.IndexHash}|{failure}";
                var repeat = _invalidCachedIndex.TryGetValue(teamFolder, out var known) && known == key;
                _invalidCachedIndex[teamFolder] = key;
                logger.Log(repeat ? LogLevel.Debug : LogLevel.Error,
                    "Team {Team}: cached index rejected for display — {Reason}; display surfaces left untouched",
                    teamFolder, failure);
                valid = false;
                continue;
            }

            _invalidCachedIndex.Remove(teamFolder);
            all.AddRange(index.PublishedAssignments);
        }
        return valid ? all : null;
    }

    // Per team: "{IndexHash}|{reason}" of the cached index last rejected for display, so the Error is
    // logged once per distinct failure and repeats drop to Debug. Cleared when the cached index is valid.
    private readonly Dictionary<string, string> _invalidCachedIndex = new(StringComparer.OrdinalIgnoreCase);

    // internal (not private) so tests can exercise the LockScreenEnabled=false early-out, and
    // the surface standalone, without standing up the full RunCycleAsync dependency graph.
    // assignments, when supplied, is the ApplyDisplaySurfacesAsync one-pass collection; when
    // omitted (standalone call) this surface collects its own.
    internal async Task ApplyLockScreenAsync(
        IReadOnlyList<string> teams, IReadOnlyList<PublishedAssignmentIndex>? assignments = null)
    {
        if (!configuration.GetValue("Delivery:LockScreenEnabled", true))
        {
            logger.LogDebug(
                "Lock screen disabled (Delivery:LockScreenEnabled = false) — surface left untouched");
            return;
        }

        // Standalone calls collect (and validate) here, before anything is swept.
        var all = assignments ?? await CollectAssignmentsAsync(teams);
        if (all is null) return;

        // Stale-file cleanup from the PREVIOUS cycle's publish — never the cycle that just
        // published a file, since Windows may still hold it open from the apply that just ran.
        var livePath = lockScreen.GetCurrentLockScreenPath();
        imagePublisher.SweepExcept(livePath is null ? null : Path.GetFileName(livePath), "lockscreen");

        var winner = ActiveAssignmentSelector.PickNewestActive(all, DateTime.Now, a => a.DisplayTypes.IsLogonScreen);

        ApplySurface(winner, "Delivery:DefaultLockScreenPath", "lockscreen", "Lock screen",
            lockScreen.GetCurrentLockScreenPath, lockScreen.SetLockScreen, lockScreen.ClearLockScreen);
    }

    // internal (not private) — same rationale as ApplyLockScreenAsync above.
    internal async Task ApplyWallpaperAsync(
        IReadOnlyList<string> teams, IReadOnlyList<PublishedAssignmentIndex>? assignments = null)
    {
        if (!configuration.GetValue("Delivery:WallpaperEnabled", true))
        {
            logger.LogDebug(
                "Wallpaper disabled (Delivery:WallpaperEnabled = false) — surface left untouched");
            return;
        }

        var all = assignments ?? await CollectAssignmentsAsync(teams);
        if (all is null) return;

        var livePath = lockScreen.GetCurrentWallpaperPath();
        imagePublisher.SweepExcept(livePath is null ? null : Path.GetFileName(livePath), "wallpaper");

        var winner = ActiveAssignmentSelector.PickNewestActive(all, DateTime.Now, a => a.DisplayTypes.IsWallpaper);

        ApplySurface(winner, "Delivery:DefaultWallpaperPath", "wallpaper", "Wallpaper",
            lockScreen.GetCurrentWallpaperPath, lockScreen.SetWallpaper, lockScreen.ClearWallpaper);
    }

    /// <summary>
    /// Surface-agnostic: computes the intended source image (winner, else configured default,
    /// else none) and its expected hash, publishes it, then dispatches the three-state apply.
    /// Shared by <see cref="ApplyLockScreenAsync"/> and <see cref="ApplyWallpaperAsync"/>,
    /// parameterised by the default-path config key, the publish filename prefix, a label for
    /// logging, and the surface's get/set/clear operations.
    /// </summary>
    private void ApplySurface(
        PublishedAssignmentIndex? winner,
        string defaultPathConfigKey,
        string publishPrefix,
        string surfaceName,
        Func<string?> getCurrent,
        Func<string, bool> setCurrent,
        Action clearCurrent)
    {
        // Compute the intended SOURCE path, its expected hash, and a human-readable source label.
        string? sourcePath;
        string? expectedHash;
        string source;
        if (winner is not null)
        {
            sourcePath   = cache.Resolve(winner.Content.ImagePath);
            expectedHash = winner.Content.ImageHash;
            source       = $"presentation {winner.PresentationId}, team {winner.SourceTeamFolderName}";
        }
        else
        {
            var defaultPath = configuration.GetValue<string>(defaultPathConfigKey);
            if (!string.IsNullOrEmpty(defaultPath) && File.Exists(defaultPath))
            {
                sourcePath = defaultPath;
                source     = "default";
                // The default image is admin-supplied and carries no index hash to check against.
                // Hashing the file itself makes Publish's verification a self-consistent no-op —
                // NOT a skipped security check — so Publish always verifies what it copies.
                expectedHash = "sha256:" +
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(defaultPath))).ToLowerInvariant();
            }
            else
            {
                if (!string.IsNullOrEmpty(defaultPath))
                    logger.LogWarning(
                        "{Surface}: default path configured but file not found: {Path}",
                        surfaceName, defaultPath);
                sourcePath   = null;
                expectedHash = null;
                source       = string.Empty;
            }
        }

        string? intended = null;
        if (sourcePath is not null)
        {
            intended = imagePublisher.Publish(sourcePath, expectedHash!, publishPrefix);
            if (intended is null)
                logger.LogWarning(
                    "{Surface}: image publish failed for {Source} — no usable image this cycle",
                    surfaceName, source);
        }

        ApplyIntendedSurface(intended, source, surfaceName, getCurrent, setCurrent, clearCurrent);
    }

    /// <summary>
    /// Registry-gated apply — the three-state model, surface-agnostic. Compares
    /// <paramref name="intended"/> against the live PersonalizationCSP value for this surface:
    ///   • intended non-null, live already matches → no-op (steady state).
    ///   • intended non-null, live differs or is absent → write the trio; log Information.
    ///   • intended null, live absent → no-op, silent.
    ///   • intended null, live present and inside Delivery:PublishedImagePath (i.e. one WE
    ///     published) → <b>clear</b> the trio; log Information. This is the teardown this
    ///     surface's fail-open predecessor lacked — no active content and no usable default
    ///     returns to Windows' own default at the next lock/logon, instead of staying frozen on
    ///     stale content forever.
    ///   • intended null, live present but NOT inside Delivery:PublishedImagePath → left alone,
    ///     logged at Debug. NewsCentral never clears a CSP value it did not write — a value set by
    ///     GPO, Intune, or a manual admin change is someone else's to manage.
    /// A failed write or clear is logged and is <b>never</b> recorded as applied/cleared — the next
    /// cycle re-evaluates against the unchanged live value and retries.
    /// </summary>
    private void ApplyIntendedSurface(
        string? intended, string source, string surfaceName,
        Func<string?> getCurrent, Func<string, bool> setCurrent, Action clearCurrent)
    {
        var current = getCurrent();

        if (intended is not null)
        {
            if (current is not null && PathsEqual(current, intended))
            {
                logger.LogDebug("{Surface} already current: {Path}", surfaceName, intended);
                return;
            }

            if (setCurrent(intended))
                logger.LogInformation("{Surface} applied: {Source} -> {Path}", surfaceName, source, intended);
            else
                logger.LogError("{Surface} apply failed: {Source} -> {Path}", surfaceName, source, intended);
            return;
        }

        // intended == null — no active content and no usable default (or publishing it failed).
        if (current is null)
        {
            logger.LogDebug("{Surface} already unset — nothing to clear", surfaceName);
            return;
        }

        if (!IsUnderPublishRoot(current))
        {
            logger.LogDebug(
                "{Surface} left unchanged — current value is not one NewsService published: {Path}",
                surfaceName, current);
            return;
        }

        clearCurrent();
        logger.LogInformation(
            "{Surface} cleared — no active content and no usable default (was: {Path})",
            surfaceName, current);
    }

    /// <summary>
    /// Registry-gated apply for the lock screen alone — the three-state model. Thin wrapper over
    /// <see cref="ApplyIntendedSurface"/> kept as its own entry point for direct testability.
    /// </summary>
    internal void ApplyIntendedLockScreen(string? intended, string source) =>
        ApplyIntendedSurface(intended, source, "Lock screen",
            lockScreen.GetCurrentLockScreenPath, lockScreen.SetLockScreen, lockScreen.ClearLockScreen);

    /// <summary>
    /// Registry-gated apply for the wallpaper alone — mirrors
    /// <see cref="ApplyIntendedLockScreen"/> exactly. Thin wrapper over
    /// <see cref="ApplyIntendedSurface"/> kept as its own entry point for direct testability.
    /// </summary>
    internal void ApplyIntendedWallpaper(string? intended, string source) =>
        ApplyIntendedSurface(intended, source, "Wallpaper",
            lockScreen.GetCurrentWallpaperPath, lockScreen.SetWallpaper, lockScreen.ClearWallpaper);

    /// <summary>
    /// True when <paramref name="path"/> resolves to a location inside
    /// <c>Delivery:PublishedImagePath</c> — the ownership test that gates clearing, shared by
    /// both surfaces since they publish into the same protected folder. Compares
    /// fully-normalized absolute paths (<see cref="Path.GetFullPath(string)"/>,
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>) with a directory-prefix check, never a raw
    /// string <c>StartsWith</c> on the configured value, so a trailing separator, a relative
    /// <c>..</c> segment, or different casing on either side cannot defeat it. A misconfigured or
    /// unresolvable <c>PublishedImagePath</c> fails <b>closed</b>: every live value is then treated
    /// as foreign and is never cleared.
    /// </summary>
    private bool IsUnderPublishRoot(string path)
    {
        var publishRoot = configuration.GetValue<string>("Delivery:PublishedImagePath");
        if (string.IsNullOrWhiteSpace(publishRoot)) return false;

        try
        {
            var normalizedRoot = Path.GetFullPath(publishRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedPath = Path.GetFullPath(path);

            return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(
                       normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;   // unresolvable path → fail closed, treat as foreign, never clear
        }
    }

    /// <summary>Full-path, case-insensitive comparison of two file paths.</summary>
    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Step 3 — status.json ─────────────────────────────────────────────────

    private async Task WriteStatusAsync(bool isOnline, string syncSource)
    {
        await cache.WriteJsonAsync("status.json", new StatusFile
        {
            LastSyncTime = DateTime.UtcNow,
            IsOnline     = isOnline,
            SyncSource   = syncSource
        });
    }
}
