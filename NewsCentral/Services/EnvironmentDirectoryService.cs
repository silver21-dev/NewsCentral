using NewsCentral.Audit;
using System.Text.Json;
using NewsCentral.Configuration;

namespace NewsCentral.Services;

/// <summary>Why <see cref="EnvironmentDirectoryService.AddAsync"/> succeeded or failed.</summary>
public enum AddOutcome
{
    Added,
    AddedNotInitialized,
    RejectedNotAllowed,
    RejectedEmptyPath,
    RejectedRelativePath,
    RejectedAlreadyListed,
    RejectedNotFound,
    RejectedTimedOut,
    RejectedError
}

/// <summary>
/// <see cref="ErrorMessage"/> is set whenever <see cref="Outcome"/> is not <see cref="AddOutcome.Added"/>
/// or <see cref="AddOutcome.AddedNotInitialized"/> — <see cref="AddOutcome.AddedNotInitialized"/> is a
/// SUCCESS carrying an informational note, not a rejection.
/// </summary>
public sealed record AddResult(AddOutcome Outcome, string? ErrorMessage = null);

/// <summary>
/// Owns the per-user environment list (add/remove/hide), startup/current-environment lookup, and
/// environment switching. The single entry point the UI (EnvironmentPicker, Login) talks to — it
/// never reads <see cref="UserEnvironmentStore"/> or calls <see cref="EnvironmentListBuilder"/>
/// directly.
///
/// Never probes a listed environment (see <see cref="EnvironmentProbe"/>'s own remarks) — disk is
/// touched only by <see cref="AddAsync"/> (via the probe), by an actual switch (through the normal
/// storage/seeder path, outside this class), and by <see cref="UserEnvironmentStore"/> itself reading
/// the local state file.
/// </summary>
public sealed class EnvironmentDirectoryService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private const string SharedDirectoryRelativePath = "config/environments.json";
    private const string SharedDirectoryTempRelativePath = "config/environments.json.tmp";

    private readonly EnvironmentCatalog _catalog;
    private readonly AppConfiguration _config;
    private readonly UserEnvironmentStore _store;
    private readonly EnvironmentContext _environment;
    private readonly AuthenticationService _authService;
    private readonly TeamContextService _teamContext;
    private readonly IStorageService _storage;
    private readonly EnvironmentSettingsService _settingsService;
    private readonly IAuditLog _audit;

    private UserEnvironmentState? _cachedState;

    /// <summary>Raised after any add/remove/hide/unhide/switch/sync that changed the local cache —
    /// the picker's cue to re-render.</summary>
    public event Action? Changed;

    public EnvironmentDirectoryService(
        EnvironmentCatalog catalog,
        AppConfiguration config,
        UserEnvironmentStore store,
        EnvironmentContext environment,
        AuthenticationService authService,
        TeamContextService teamContext,
        IStorageService storage,
        EnvironmentSettingsService settingsService,
        IAuditLog? audit = null)
    {
        _catalog = catalog;
        _config = config;
        _store = store;
        _environment = environment;
        _authService = authService;
        _teamContext = teamContext;
        _storage = storage;
        _settingsService = settingsService;
        _audit = audit ?? NullAuditLog.Instance;

        // M5a.1: the fingerprint in the shared directory now follows EVERY settings save, not just
        // ones made through this page's own direct call (removed) — covers the MainLayout banner's
        // "Save to environment" and any other future caller of EnvironmentSettingsService.SaveAsync.
        // No circular dependency: EnvironmentSettingsService's own constructor does not take an
        // EnvironmentDirectoryService (verified by reading it — its dependencies are IStorageService,
        // EnvironmentContext, AppConfiguration, AuthenticationService, EnvironmentCatalog only).
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    private async void OnSettingsChanged()
    {
        try
        {
            var effective = await _settingsService.GetAsync();
            await UpdateCurrentFingerprintAsync(effective.DistributionFingerprint);
        }
        catch (Exception ex)
        {
            // Must never throw out of an event handler — logged only, exactly like
            // SyncSharedDirectoryAsync's own failure handling.
            System.Diagnostics.Debug.WriteLine(
                $"[EnvironmentDirectoryService] Failed to update the shared-directory fingerprint after a settings change: {ex.Message}");
        }
    }

    private UserEnvironmentState GetState() => _cachedState ??= _store.Load();

    private async Task SaveStateAsync(UserEnvironmentState state)
    {
        await _store.SaveAsync(state);
        _cachedState = state;
    }

    /// <summary>The environment list for the picker — built entirely from stored data, never probed.</summary>
    public IReadOnlyList<EnvironmentOption> GetOptions(bool includeHidden = false) =>
        EnvironmentListBuilder.Build(_catalog, _config.DataPath, GetState(), _environment.DataPath, includeHidden);

    /// <summary>
    /// The locally-cached Shared entries this machine has learned of — including tombstones — for
    /// the Environment Management page's collision check (M5a). Read-only; callers never mutate
    /// the returned list directly.
    /// </summary>
    public IReadOnlyList<SharedDirectoryEntry> SharedEntries => GetState().SharedEntries;

    /// <summary>
    /// The current environment's own (non-tombstoned) entry in the local Shared-entry cache, or
    /// null when the current environment is not in the shared directory at all — the Environment
    /// Management page uses this to decide whether to show the collision section or the
    /// "not in the shared directory" message.
    /// </summary>
    public SharedDirectoryEntry? FindCurrentSharedEntry()
    {
        var canonical = EnvironmentPaths.Canonicalize(_environment.DataPath);
        return GetState().SharedEntries.FirstOrDefault(
            e => e.DeletedUtc == null && EnvironmentPaths.Canonicalize(e.DataPath) == canonical);
    }

    /// <summary>
    /// After a successful Save on the Environment Management page: if the current environment is
    /// in the local Shared-entry cache, updates that entry's DistributionFingerprint (ModifiedUtc =
    /// now) and syncs. A no-op (no state change, no sync) when the current environment is not in
    /// the shared directory.
    /// </summary>
    public async Task UpdateCurrentFingerprintAsync(string? fingerprint)
    {
        var canonical = EnvironmentPaths.Canonicalize(_environment.DataPath);
        var state = GetState();
        var index = state.SharedEntries.FindIndex(
            e => e.DeletedUtc == null && EnvironmentPaths.Canonicalize(e.DataPath) == canonical);
        if (index < 0)
            return;

        state.SharedEntries[index] = state.SharedEntries[index] with
        {
            DistributionFingerprint = fingerprint,
            ModifiedUtc = DateTime.UtcNow
        };

        await SaveStateAsync(state);
        Changed?.Invoke();
        await SyncSharedDirectoryAsync(EnvironmentInitStatus.Ready);
    }

    /// <summary>
    /// The option matching the live <see cref="EnvironmentContext.DataPath"/> — always shown even if
    /// it is a hidden Policy entry. Synthesizes a fallback option (last path segment as display name)
    /// in the edge case where the current path matches none of Policy/Configured/User, so the picker
    /// always has something to show as "current."
    /// </summary>
    public EnvironmentOption Current
    {
        get
        {
            var match = GetOptions(includeHidden: true).FirstOrDefault(o => o.IsCurrent);
            if (match != null)
                return match;

            var path = _environment.DataPath;
            return new EnvironmentOption(
                DataPath: path,
                DisplayName: LastPathSegmentOrPath(path),
                Kind: EnvironmentKind.User,
                PolicyName: null,
                IsHidden: false,
                IsShareable: EnvironmentPaths.IsShareable(path),
                IsCurrent: true);
        }
    }

    /// <summary>
    /// True when the current user may add a new environment: authenticated, a System Administrator
    /// (of the environment they are currently logged into), and user environments are allowed — by
    /// policy (<see cref="EnvironmentCatalog.AllowUserEnvironments"/>) or because this machine has no
    /// policy catalog at all (an unmanaged machine must not be left unable to add any environment).
    /// </summary>
    public bool CanAdd =>
        _authService.IsAuthenticated() &&
        _authService.IsSystemAdmin() &&
        (_catalog.AllowUserEnvironments || _catalog.Entries.Count == 0);

    /// <summary>
    /// True when the picker should render a read-only label instead of a dropdown — at most one
    /// environment is visible and there is no way to add another.
    /// </summary>
    public bool IsLocked => GetOptions().Count <= 1 && !CanAdd;

    /// <summary>
    /// Validates and adds <paramref name="path"/>. A UNC path (<see cref="EnvironmentPaths.IsShareable"/>)
    /// becomes a Shared entry — <see cref="SharedDirectoryEntry.AddedBy"/> is the current user's UPN,
    /// falling back to their username — and is synced to the current environment's
    /// <c>config/environments.json</c> immediately (M4b). Any other absolute path becomes a local-only
    /// User entry, exactly as in M4a. Rejections never touch disk beyond the probe itself, and never
    /// modify stored state.
    /// </summary>
    /// <param name="distributionFingerprint">
    /// M5b: the newly-added environment's own distribution fingerprint, when already known (the
    /// setup wizard passes this right after writing a brand-new <c>environment.json</c> — there is
    /// no reason to wait for that environment's own next save to populate it). Null for every other
    /// caller (e.g. the picker's "Add environment…" dialog), exactly as before this parameter
    /// existed — a Shared entry is still created, just without a fingerprint until its first save.
    /// Ignored for a User entry, which never carries a fingerprint at all.
    /// </param>
    public Task<AddResult> AddAsync(string path, string? displayName, string? distributionFingerprint = null) =>
        _audit.RunAsync(AuditEvents.EnvironmentAdded, path,
            () => AddCoreAsync(path, displayName, distributionFingerprint),
            isSuccess: r => r.Outcome is AddOutcome.Added or AddOutcome.AddedNotInitialized,
            detailFrom: r => r.Outcome.ToString());

    private async Task<AddResult> AddCoreAsync(string path, string? displayName, string? distributionFingerprint)
    {
        if (!CanAdd)
            return new AddResult(AddOutcome.RejectedNotAllowed, "Adding environments is not available.");

        if (string.IsNullOrWhiteSpace(path))
            return new AddResult(AddOutcome.RejectedEmptyPath, "Path is required.");

        if (!EnvironmentPaths.IsAbsolute(path))
            return new AddResult(AddOutcome.RejectedRelativePath, "Path must be a fully qualified (absolute) path.");

        var canonical = EnvironmentPaths.Canonicalize(path);
        if (GetOptions(includeHidden: true).Any(o => EnvironmentPaths.Canonicalize(o.DataPath) == canonical))
            return new AddResult(AddOutcome.RejectedAlreadyListed, "This environment is already listed.");

        var probe = await EnvironmentProbe.ProbeAsync(path, ProbeTimeout);

        if (probe.TimedOut)
            return new AddResult(AddOutcome.RejectedTimedOut, "Timed out while checking that path. Check that it is reachable.");

        if (probe.Error != null)
            return new AddResult(AddOutcome.RejectedError, probe.Error);

        if (!probe.Exists)
            return new AddResult(AddOutcome.RejectedNotFound, "That folder does not exist or is not accessible.");

        var isShared = EnvironmentPaths.IsShareable(path);
        var cleanDisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName;
        var state = GetState();

        if (isShared)
        {
            state.SharedEntries.Add(new SharedDirectoryEntry(
                path, cleanDisplayName, CurrentUserIdentity(), DateTime.UtcNow, DeletedUtc: null,
                DistributionFingerprint: distributionFingerprint));
        }
        else
        {
            state.Entries.Add(new UserEnvironmentEntry
            {
                DataPath = path,
                DisplayName = cleanDisplayName,
                AddedUtc = DateTime.UtcNow
            });
        }

        await SaveStateAsync(state);
        Changed?.Invoke();

        if (isShared)
            await SyncSharedDirectoryAsync(EnvironmentInitStatus.Ready);

        return new AddResult(probe.Initialized ? AddOutcome.Added : AddOutcome.AddedNotInitialized);
    }

    /// <summary>
    /// Removes an entry. A Shared entry (M4b) is tombstoned — never deleted outright, so the removal
    /// propagates through <see cref="SyncSharedDirectoryAsync"/> instead of being resurrected by an
    /// environment that hasn't seen it yet — and synced immediately. A User entry is removed
    /// outright, exactly as in M4a. Policy and Configured entries are never in either list, so they
    /// cannot be removed through this method regardless of caller. Fails (returns false, no state
    /// change) when the caller is not a System Administrator, or when <paramref name="dataPath"/> is
    /// the current environment.
    /// </summary>
    public Task<bool> RemoveAsync(string dataPath) =>
        _audit.RunAsync(AuditEvents.EnvironmentDeleted, dataPath, () => RemoveCoreAsync(dataPath),
            isSuccess: ok => ok, detailFrom: ok => ok ? "removed from the environment list" : "not removed");

    private async Task<bool> RemoveCoreAsync(string dataPath)
    {
        if (!_authService.IsSystemAdmin())
            return false;

        var canonical = EnvironmentPaths.Canonicalize(dataPath);
        if (canonical == EnvironmentPaths.Canonicalize(_environment.DataPath))
            return false;

        var state = GetState();

        var sharedIndex = state.SharedEntries.FindIndex(
            e => e.DeletedUtc == null && EnvironmentPaths.Canonicalize(e.DataPath) == canonical);
        if (sharedIndex >= 0)
        {
            var now = DateTime.UtcNow;
            state.SharedEntries[sharedIndex] = state.SharedEntries[sharedIndex] with
            {
                DeletedUtc = now,
                ModifiedUtc = now,
                AddedBy = CurrentUserIdentity()
            };

            await SaveStateAsync(state);
            Changed?.Invoke();
            await SyncSharedDirectoryAsync(EnvironmentInitStatus.Ready);
            return true;
        }

        var removedCount = state.Entries.RemoveAll(e => EnvironmentPaths.Canonicalize(e.DataPath) == canonical);
        if (removedCount == 0)
            return false;

        await SaveStateAsync(state);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Hides a Policy or (M4b) non-tombstoned Shared entry locally (any logged-in user may do this —
    /// it is a personal preference, not an admin action). Returns false without any state change
    /// when not authenticated, or when <paramref name="dataPath"/> matches neither kind.
    /// </summary>
    public async Task<bool> HideAsync(string dataPath)
    {
        if (!_authService.IsAuthenticated())
            return false;

        var canonical = EnvironmentPaths.Canonicalize(dataPath);
        var state = GetState();

        var isPolicyEntry = _catalog.Entries.Any(e => EnvironmentPaths.Canonicalize(e.DataPath) == canonical);
        var isSharedEntry = state.SharedEntries.Any(
            e => e.DeletedUtc == null && EnvironmentPaths.Canonicalize(e.DataPath) == canonical);
        if (!isPolicyEntry && !isSharedEntry)
            return false;

        if (state.HiddenPaths.Any(p => string.Equals(p, canonical, StringComparison.OrdinalIgnoreCase)))
            return true; // already hidden — no-op, still a success from the caller's point of view

        state.HiddenPaths.Add(canonical);
        await SaveStateAsync(state);
        Changed?.Invoke();
        return true;
    }

    /// <summary>The current user's UPN, falling back to their username — used as Shared-entry AddedBy.</summary>
    private string? CurrentUserIdentity()
    {
        var currentUser = _authService.GetCurrentUser();
        return !string.IsNullOrEmpty(currentUser?.UPN) ? currentUser.UPN : currentUser?.Username;
    }

    /// <summary>
    /// Reads the current environment's <c>config/environments.json</c> (missing is treated as
    /// empty), validates its entries, merges them with this machine's local Shared-entry cache, and
    /// writes back whichever side(s) changed. The local cache is always eligible to be saved; the
    /// remote file is written only when <paramref name="status"/> is <see cref="EnvironmentInitStatus.Ready"/>
    /// — a <see cref="EnvironmentInitStatus.NotInitialized"/> environment is read-only, so
    /// <c>config/</c> is never created there. The remote file is also written only while a SystemAdmin is signed in
    /// (S02a): before login, and for anyone else, the shared file is merged into the local cache and never written
    /// (no temp file, no move). Never throws: any I/O or parse failure is caught,
    /// logged to Debug, and the method returns normally — this must never block login or surface an
    /// error to the user. Raises <see cref="Changed"/> only when the local cache actually changed.
    /// </summary>
    public async Task SyncSharedDirectoryAsync(EnvironmentInitStatus status)
    {
        var environmentPath = _environment.DataPath;
        // M4b.1 guard: IStorageService resolves every path against the LIVE EnvironmentContext.DataPath,
        // so if the environment is switched between this read and the eventual write (below), the
        // merged file would otherwise be written into the NEW environment instead of the one this
        // sync actually read from. Captured once, up front, and re-checked immediately before the
        // write.
        var startPath = EnvironmentPaths.Canonicalize(_environment.DataPath);
        IReadOnlyList<SharedDirectoryEntry> remoteEntries = Array.Empty<SharedDirectoryEntry>();
        var rejectedCount = 0;

        try
        {
            string? fileJson = null;
            try
            {
                fileJson = await _storage.ReadTextAsync(SharedDirectoryRelativePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[EnvironmentDirectoryService] Sync '{environmentPath}': failed to read {SharedDirectoryRelativePath}: {ex.Message}");
            }

            if (fileJson != null)
            {
                SharedDirectoryFile? file = null;
                try
                {
                    file = JsonSerializer.Deserialize<SharedDirectoryFile>(fileJson, SharedDirectoryJson.Options);
                }
                catch (JsonException ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[EnvironmentDirectoryService] Sync '{environmentPath}': malformed {SharedDirectoryRelativePath}: {ex.Message}");
                }

                if (file?.Entries != null)
                {
                    var filtered = SharedDirectoryValidator.Filter(file.Entries, out var warnings);
                    foreach (var warning in warnings)
                        System.Diagnostics.Debug.WriteLine($"[EnvironmentDirectoryService] Sync '{environmentPath}': {warning}");

                    rejectedCount = file.Entries.Count - filtered.Count;
                    remoteEntries = filtered;
                }
            }

            var state = GetState();
            var (merged, localChanged, remoteChanged) = SharedDirectoryMerger.Merge(
                state.SharedEntries, remoteEntries, DateTime.UtcNow, SharedDirectoryMerger.DefaultPurgeAfter);

            if (localChanged)
            {
                state.SharedEntries = merged.ToList();
                await SaveStateAsync(state);
            }

            var writeResult = "not needed";
            if (remoteChanged)
            {
                if (status != EnvironmentInitStatus.Ready)
                {
                    writeResult = "skipped (environment not initialized)";
                }
                else if (!_authService.IsSystemAdmin())
                {
                    // Before login, and for anyone who is not a SystemAdmin, the shared file is read-only: it decides
                    // which environments every user's picker offers, so only administrators may change it (the same
                    // rule as Add/Remove). The merge above still updates this machine's own cache.
                    writeResult = "skipped (not a system administrator)";
                }
                else if (EnvironmentPaths.Canonicalize(_environment.DataPath) != startPath)
                {
                    writeResult = "skipped (environment changed during sync)";
                    System.Diagnostics.Debug.WriteLine(
                        $"[EnvironmentDirectoryService] Sync '{startPath}': environment changed during sync — remote write skipped");
                }
                else
                {
                    try
                    {
                        var file = new SharedDirectoryFile { SchemaVersion = 1, Entries = merged.ToList() };
                        var json = JsonSerializer.Serialize(file, SharedDirectoryJson.Options);
                        await _storage.WriteTextAsync(SharedDirectoryTempRelativePath, json);
                        await _storage.MoveFileAsync(SharedDirectoryTempRelativePath, SharedDirectoryRelativePath);
                        writeResult = "succeeded";
                    }
                    catch (Exception ex)
                    {
                        writeResult = $"failed: {ex.Message}";
                        // Best effort: a leftover .tmp file is harmless but untidy; this method
                        // never throws regardless, so there is nothing to mask.
                        try { await _storage.DeleteFileAsync(SharedDirectoryTempRelativePath); } catch { /* best effort */ }
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"[EnvironmentDirectoryService] Sync '{environmentPath}': remote entries read={remoteEntries.Count}, " +
                $"rejected={rejectedCount}, localChanged={localChanged}, remoteChanged={remoteChanged}, write={writeResult}");

            if (localChanged)
                Changed?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EnvironmentDirectoryService] Sync '{environmentPath}' failed: {ex.Message}");
        }
    }

    /// <summary>Clears every locally-hidden Policy entry.</summary>
    public async Task UnhideAllAsync()
    {
        var state = GetState();
        if (state.HiddenPaths.Count == 0)
            return;

        state.HiddenPaths.Clear();
        await SaveStateAsync(state);
        Changed?.Invoke();
    }

    /// <summary>Per-operator, per-machine "Process on open" flag for poster statistics (default false).</summary>
    public bool ProcessStatisticsOnOpen => GetState().ProcessStatisticsOnOpen;

    /// <summary>Persists the per-operator "Process on open" flag for poster statistics (local file only; failure is logged, never thrown).</summary>
    public async Task SetProcessStatisticsOnOpenAsync(bool value)
    {
        try
        {
            var state = GetState();
            state.ProcessStatisticsOnOpen = value;
            await SaveStateAsync(state);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EnvironmentDirectoryService] Failed to save ProcessStatisticsOnOpen: {ex.Message}");
        }
    }

    /// <summary>
    /// Records the current environment as "last used" — called only after a SUCCESSFUL login (Login
    /// or UPN Login), never on mere selection in the picker. Best-effort: a write failure is logged
    /// to Debug and swallowed, since failing to remember the last-used environment must never block
    /// login.
    /// </summary>
    public async Task RecordSuccessfulLoginAsync()
    {
        try
        {
            var state = GetState();
            state.LastUsedDataPath = _environment.DataPath;
            await SaveStateAsync(state);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EnvironmentDirectoryService] Failed to record last-used environment: {ex.Message}");
        }
    }

    /// <summary>
    /// Switches the runtime environment: logs out if currently logged in, clears the team context,
    /// then calls <see cref="EnvironmentContext.SwitchTo"/>. Navigation (and the confirm-before-switch
    /// dialog, when logged in) is the caller's responsibility — this method performs the switch
    /// itself unconditionally once called.
    /// </summary>
    public void SwitchTo(string dataPath)
    {
        if (_authService.IsAuthenticated())
            _authService.Logout();

        _teamContext.ClearCurrentTeam();
        _environment.SwitchTo(dataPath);
        Changed?.Invoke();
    }

    private static string LastPathSegmentOrPath(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var segment = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(segment) ? path : segment;
    }
}
