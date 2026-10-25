using NewsCentral.Audit;
using NewsCentral.Models;

namespace NewsCentral.Security;

/// <summary>Where the secure store lives right now: a root, or the reason it could not be determined.</summary>
public readonly record struct SecureRootResult(string? Root, string? Error);

/// <summary>
/// Loads and saves team signing keys from the secure store and migrates them from the old DataPath location.
/// Migration runs for every team when a system administrator logs in (<see cref="MigrateAllAsync"/>) and lazily,
/// once per session, the first time a team's key is loaded. When a migration fails the old file stays in place
/// and is used for the rest of the session; failures and conflicts are kept in <see cref="Issues"/> for the
/// administrator. The DataPath and SecurePath are re-read on every call, so an environment switch takes effect.
/// </summary>
public sealed class TeamKeyService
{
    private readonly Func<string> _dataPath;
    private readonly Func<Task<SecureRootResult>> _resolveSecureRoot;
    private readonly Func<string, ISecureStorage> _storageFactory;
    private readonly IAuditLog _audit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _attempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _legacyInUse = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, KeyMigrationResult> _problems = new(StringComparer.OrdinalIgnoreCase);

    public TeamKeyService(
        Func<string> dataPath,
        Func<Task<SecureRootResult>> resolveSecureRoot,
        Func<string, ISecureStorage>? storageFactory = null,
        IAuditLog? audit = null)
    {
        _dataPath = dataPath;
        _resolveSecureRoot = resolveSecureRoot;
        _storageFactory = storageFactory ?? (root => new LocalSecureStorage(root));
        _audit = audit ?? NullAuditLog.Instance;
    }

    /// <summary>Raised after each migration attempt that found something to migrate (not for NothingToMigrate).</summary>
    public event Action<KeyMigrationResult>? MigrationCompleted;

    /// <summary>Raised when <see cref="Issues"/> changes.</summary>
    public event Action? IssuesChanged;

    /// <summary>One line per team whose last migration failed or conflicted. Empty when all is well.</summary>
    public IReadOnlyList<string> Issues
    {
        get
        {
            lock (_problems)
            {
                return _problems.Values
                    .OrderBy(p => p.Team, StringComparer.OrdinalIgnoreCase)
                    .Select(p => p.Status == KeyMigrationStatus.Conflict
                        ? $"{p.Team}: key conflict - {p.Detail}"
                        : $"{p.Team}: key migration failed - {p.Detail}")
                    .ToList();
            }
        }
    }

    public async Task<string?> GetSecureRootAsync() => (await _resolveSecureRoot()).Root;

    public async Task<TeamKeyLoadResult> LoadAsync(string teamFolderName)
    {
        var resolved = await _resolveSecureRoot();
        if (resolved.Root is null)
            return TeamKeyLoadResult.Unreadable($"secure path unavailable ({resolved.Error ?? "unknown"})");

        var storage = _storageFactory(resolved.Root);
        var dataPath = _dataPath();
        var id = Id(dataPath, resolved.Root, teamFolderName);

        await EnsureMigratedAsync(dataPath, teamFolderName, storage, id);

        bool legacy;
        lock (_legacyInUse) legacy = _legacyInUse.Contains(id);
        if (legacy)
        {
            try
            {
                return TeamKeyStore.Parse(
                    await File.ReadAllBytesAsync(TeamKeyMigrator.LegacyPath(dataPath, teamFolderName)), legacy: true);
            }
            catch (Exception ex)
            {
                return TeamKeyLoadResult.Unreadable(TeamKeyStore.Describe(ex));
            }
        }

        return await new TeamKeyStore(storage).LoadAsync(teamFolderName);
    }

    /// <summary>
    /// Writes the team's keys to the secure store. Refuses when the existing key cannot be read: generating over
    /// an unreadable key would orphan every device that trusts it. Throws on any failure.
    /// </summary>
    public async Task SaveAsync(string teamFolderName, TeamSigningKeys keys)
    {
        // KeyGenerated when the team had no key, KeyRotated when it replaces one. Only the public-key
        // fingerprint is recorded - never any key material.
        var existing = await LoadAsync(teamFolderName);
        var rotating = existing.Outcome == KeyLoadOutcome.Loaded && !string.IsNullOrEmpty(existing.Keys?.PublicKey);
        var eventName = rotating ? AuditEvents.KeyRotated : AuditEvents.KeyGenerated;
        var fingerprint = TeamKeyStore.Fingerprint(keys);
        var detail = $"fingerprint={fingerprint ?? "none"}";

        try
        {
            if (existing.Outcome == KeyLoadOutcome.Unreadable)
                throw new InvalidOperationException(
                    $"The existing signing key for {teamFolderName} is not readable ({existing.Reason}); it was not replaced.");

            var resolved = await _resolveSecureRoot();
            if (resolved.Root is null)
                throw new InvalidOperationException($"The secure path is unavailable ({resolved.Error ?? "unknown"}).");

            await new TeamKeyStore(_storageFactory(resolved.Root)).SaveAsync(teamFolderName, keys);

            lock (_legacyInUse) _legacyInUse.Remove(Id(_dataPath(), resolved.Root, teamFolderName));
        }
        catch (Exception ex)
        {
            await _audit.RecordAsync(new AuditRecord(eventName, teamFolderName, AuditOutcome.Failure, AuditLogExtensions.Describe(ex)));
            throw;
        }

        await _audit.RecordAsync(new AuditRecord(eventName, teamFolderName, AuditOutcome.Success, detail));
    }

    /// <summary>Migrates every team now (system administrator login). Always attempts, even if tried before.</summary>
    public async Task<IReadOnlyList<KeyMigrationResult>> MigrateAllAsync(IEnumerable<string> teamFolderNames)
    {
        var resolved = await _resolveSecureRoot();
        var results = new List<KeyMigrationResult>();
        var dataPath = _dataPath();

        foreach (var team in teamFolderNames)
        {
            if (resolved.Root is null)
            {
                var failed = new KeyMigrationResult(team, KeyMigrationStatus.Failed,
                    $"secure path unavailable ({resolved.Error ?? "unknown"})", null);
                if (File.Exists(TeamKeyMigrator.LegacyPath(dataPath, team)))
                {
                    await RecordAsync(Id(dataPath, "?", team), failed);
                    results.Add(failed);
                }
                continue;
            }

            var storage = _storageFactory(resolved.Root);
            await _gate.WaitAsync();
            try
            {
                var result = await TeamKeyMigrator.MigrateAsync(dataPath, team, storage);
                await RecordAsync(Id(dataPath, resolved.Root, team), result);
                results.Add(result);
            }
            finally { _gate.Release(); }
        }

        return results;
    }

    private async Task EnsureMigratedAsync(string dataPath, string team, ISecureStorage storage, string id)
    {
        await _gate.WaitAsync();
        try
        {
            lock (_attempted)
                if (_attempted.Contains(id)) return;

            await RecordAsync(id, await TeamKeyMigrator.MigrateAsync(dataPath, team, storage));
        }
        finally { _gate.Release(); }
    }

    private async Task RecordAsync(string id, KeyMigrationResult result)
    {
        lock (_attempted) _attempted.Add(id);

        lock (_legacyInUse)
        {
            if (result.Status == KeyMigrationStatus.Failed) _legacyInUse.Add(id);
            else _legacyInUse.Remove(id);
        }

        bool changed;
        lock (_problems)
        {
            if (result.Status is KeyMigrationStatus.Failed or KeyMigrationStatus.Conflict)
            {
                changed = !_problems.TryGetValue(result.Team, out var old) || old.Detail != result.Detail || old.Status != result.Status;
                _problems[result.Team] = result;
            }
            else
            {
                changed = _problems.Remove(result.Team);
            }
        }

        if (result.Status != KeyMigrationStatus.NothingToMigrate)
        {
            MigrationCompleted?.Invoke(result);

            // Fingerprint only. A failed or conflicting migration is recorded as a failure with its reason.
            var ok = result.Status == KeyMigrationStatus.Migrated;
            var detail = result.Fingerprint is null ? null : $"fingerprint={result.Fingerprint}";
            if (!ok) detail = $"{result.Status}: {result.Detail}" + (detail is null ? "" : $"; {detail}");
            await _audit.RecordAsync(new AuditRecord(
                AuditEvents.KeyMigrated, result.Team, ok ? AuditOutcome.Success : AuditOutcome.Failure, detail));
        }
        if (changed)
            IssuesChanged?.Invoke();
    }

    private static string Id(string dataPath, string root, string team) => $"{dataPath}|{root}|{team}";
}
