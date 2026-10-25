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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _attempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _legacyInUse = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, KeyMigrationResult> _problems = new(StringComparer.OrdinalIgnoreCase);

    public TeamKeyService(
        Func<string> dataPath,
        Func<Task<SecureRootResult>> resolveSecureRoot,
        Func<string, ISecureStorage>? storageFactory = null)
    {
        _dataPath = dataPath;
        _resolveSecureRoot = resolveSecureRoot;
        _storageFactory = storageFactory ?? (root => new LocalSecureStorage(root));
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
        var existing = await LoadAsync(teamFolderName);
        if (existing.Outcome == KeyLoadOutcome.Unreadable)
            throw new InvalidOperationException(
                $"The existing signing key for {teamFolderName} is not readable ({existing.Reason}); it was not replaced.");

        var resolved = await _resolveSecureRoot();
        if (resolved.Root is null)
            throw new InvalidOperationException($"The secure path is unavailable ({resolved.Error ?? "unknown"}).");

        await new TeamKeyStore(_storageFactory(resolved.Root)).SaveAsync(teamFolderName, keys);

        lock (_legacyInUse) _legacyInUse.Remove(Id(_dataPath(), resolved.Root, teamFolderName));
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
                    Record(Id(dataPath, "?", team), failed);
                    results.Add(failed);
                }
                continue;
            }

            var storage = _storageFactory(resolved.Root);
            await _gate.WaitAsync();
            try
            {
                var result = await TeamKeyMigrator.MigrateAsync(dataPath, team, storage);
                Record(Id(dataPath, resolved.Root, team), result);
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

            Record(id, await TeamKeyMigrator.MigrateAsync(dataPath, team, storage));
        }
        finally { _gate.Release(); }
    }

    private void Record(string id, KeyMigrationResult result)
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
            MigrationCompleted?.Invoke(result);
        if (changed)
            IssuesChanged?.Invoke();
    }

    private static string Id(string dataPath, string root, string team) => $"{dataPath}|{root}|{team}";
}
