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
    IContentChangedNotifier? contentChanged = null)
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Options;

    // Per-team verification result from the previous cycle. SyncService is a singleton and Worker
    // runs cycles sequentially, so a plain Dictionary is safe and survives across cycles. Lets
    // acceptance follow the cycle-wide "log on change" rule: loud only when the index or the
    // verification result changed, so a misconfigured team (Unsigned/Disabled) is reported at least
    // once per service start instead of flooding every cycle — or being silent forever.
    private readonly Dictionary<string, VerifyResult> _lastVerifyResult =
        new(StringComparer.OrdinalIgnoreCase);

    // Change-gated across cycles — same rationale as _lastVerifyResult above and _lastSummary in
    // EntraTeamResolutionService: a machine legitimately without any teams (static or dynamic)
    // must not warn every poll interval forever, but the transition into and back out of that
    // state should always be visible at least once.
    private bool _lastEffectiveTeamsEmpty;

    public async Task RunCycleAsync(string[] teams, CancellationToken ct)
    {
        bool online = false;
        string syncSource = "None";

        // Entra device team resolution — time-boxed and failure-isolated so a Graph problem
        // never stalls or fails blob sync. Runs FIRST so resolved-teams.json reflects this cycle
        // before the effective team set is computed below.
        await RefreshEntraTeamsAsync(ct);

        // Effective teams = static (registry/appsettings) ∪ dynamic (Entra-resolved). Per-team
        // verification routes through SignatureGate.VerifyWithPrecedence (registry key wins →
        // delivered key for dynamic teams → unsigned), so dynamic-only branching never leaks here.
        var (effectiveTeams, dynamicTeams) = ResolveEffectiveTeams(teams, cache.Root);

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
            }
            else
            {
                online = await SyncAllTeamsAsync(effectiveTeams, dynamicTeams, ct);
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

    // ── Step 1 — team sync ───────────────────────────────────────────────────

    private async Task<bool> SyncAllTeamsAsync(
        IReadOnlyList<string> teams, HashSet<string> dynamicTeams, CancellationToken ct)
    {
        // An empty list is vacuously "no failures" but is not "synced successfully" — without this
        // check, RunCycleAsync would report isOnline = true on a cycle that synced nothing at all.
        if (teams.Count == 0) return false;

        bool allOk = true;
        foreach (var teamFolder in teams)
        {
            ct.ThrowIfCancellationRequested();
            try { await SyncTeamAsync(teamFolder, dynamicTeams.Contains(teamFolder), ct); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to sync team {Team}", teamFolder);
                allOk = false;
            }
        }
        return allOk;
    }

    internal async Task SyncTeamAsync(string teamFolder, bool isDynamic, CancellationToken ct)
    {
        var remoteJson = await repository.ReadTextAsync($"{teamFolder}/index.json", ct);
        if (remoteJson is null)
        {
            logger.LogWarning("No index.json in repository for team {Team}", teamFolder);
            return;
        }

        var remoteIndex = JsonSerializer.Deserialize<TeamIndexFile>(remoteJson, Json);
        if (remoteIndex is null) return;

        var keys          = SigningKeyConfigurationReader.GetPublicKeys(configuration, teamFolder);
        var result        = SignatureGate.VerifyWithPrecedence(remoteIndex, keys, isDynamic);
        var requireSigned = configuration.GetValue<bool>("Signing:RequireSignedIndex");

        // Tracked for rejected results too, so a later recovery (e.g. Invalid → Valid after a key
        // is provisioned) counts as a change and logs at its natural level.
        var resultChanged = !_lastVerifyResult.TryGetValue(teamFolder, out var previous)
                            || previous != result;
        _lastVerifyResult[teamFolder] = result;

        if (SignatureGate.ShouldReject(result, requireSigned, out var reason))
        {
            // Security outcome — always Error, never demoted or gated on change.
            logger.LogError("Team {Team}: index rejected — {Reason}", teamFolder, reason);
            return;
        }

        var cachedIndex  = await cache.ReadJsonAsync<TeamIndexFile>($"{teamFolder}/index.json");
        var indexChanged = cachedIndex?.IndexHash != remoteIndex.IndexHash;

        // Acceptance logs at its natural level only when something changed — the index bytes or the
        // verification result — and at Debug on the steady state, matching every other cycle path.
        if (indexChanged || resultChanged)
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
            return;
        }

        logger.LogInformation("Team {Team}: index changed — syncing {Count} assignment(s)",
            teamFolder, remoteIndex.PublishedAssignments.Count);

        var allImagesCached = true;
        foreach (var assignment in remoteIndex.PublishedAssignments)
        {
            ct.ThrowIfCancellationRequested();
            if (!await SyncImageAsync(teamFolder, assignment, ct)) allImagesCached = false;
        }

        // Write the new index only after all images are safely cached.
        await cache.WriteTextAsync($"{teamFolder}/index.json", remoteJson);
        logger.LogInformation("Team {Team}: sync complete", teamFolder);

        // Exactly once per accepted change, only after the index and every image are in the cache so an
        // event-triggered NewsViewer finds the new files. A partial sync (an image missing from the
        // repository) is not announced. Thrown download/write errors never reach here.
        if (allImagesCached) NotifyContentChanged();
    }

    /// <summary>True when the image is cached and current (or the assignment has none); false when it was not found in the repository.</summary>
    private async Task<bool> SyncImageAsync(
        string teamFolder, PublishedAssignmentIndex assignment, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(assignment.Content.ImagePath)) return true;

        var relativePath = assignment.Content.ImagePath;
        var storedHash   = cache.ReadStoredHash(relativePath);

        if (storedHash == assignment.Content.ImageHash && cache.FileExists(relativePath))
        {
            logger.LogDebug("Image up-to-date: {Path}", relativePath);
            return true;
        }

        var bytes = await repository.ReadBytesAsync(relativePath, ct);
        if (bytes is null)
        {
            logger.LogWarning("Image not found in repository: {Path}", relativePath);
            return false;
        }

        await cache.WriteBytesAsync(relativePath, bytes);
        logger.LogDebug("Image synced: {Path} ({Bytes} bytes)", relativePath, bytes.Length);
        return true;
    }

    private void NotifyContentChanged()
    {
        try { contentChanged?.NotifyContentChanged(); }
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

        try { await ApplyLockScreenAsync(teams, assignments); }
        catch (Exception ex) { logger.LogError(ex, "Lock screen apply failed for this cycle"); }

        try { await ApplyWallpaperAsync(teams, assignments); }
        catch (Exception ex) { logger.LogError(ex, "Wallpaper apply failed for this cycle"); }
    }

    /// <summary>
    /// Reads every team's cached index once and flattens all published assignments into one list
    /// — the single enumeration pass both display-surface winners are picked from.
    /// </summary>
    private async Task<List<PublishedAssignmentIndex>> CollectAssignmentsAsync(IReadOnlyList<string> teams)
    {
        var all = new List<PublishedAssignmentIndex>();
        foreach (var teamFolder in teams)
        {
            var index = await cache.ReadJsonAsync<TeamIndexFile>($"{teamFolder}/index.json");
            if (index?.PublishedAssignments is null) continue;
            all.AddRange(index.PublishedAssignments);
        }
        return all;
    }

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

        // Stale-file cleanup from the PREVIOUS cycle's publish — never the cycle that just
        // published a file, since Windows may still hold it open from the apply that just ran.
        var livePath = lockScreen.GetCurrentLockScreenPath();
        imagePublisher.SweepExcept(livePath is null ? null : Path.GetFileName(livePath), "lockscreen");

        var winner = ActiveAssignmentSelector.PickNewestActive(
            assignments ?? await CollectAssignmentsAsync(teams), DateTime.Now,
            a => a.DisplayTypes.IsLogonScreen);

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

        var livePath = lockScreen.GetCurrentWallpaperPath();
        imagePublisher.SweepExcept(livePath is null ? null : Path.GetFileName(livePath), "wallpaper");

        var winner = ActiveAssignmentSelector.PickNewestActive(
            assignments ?? await CollectAssignmentsAsync(teams), DateTime.Now,
            a => a.DisplayTypes.IsWallpaper);

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
