using NewsCentral.Audit;
using NewsCentral.Models;

namespace NewsCentral.Security;

/// <summary>Where the secure store lives right now: a root, or the reason it could not be determined.</summary>
public readonly record struct SecureRootResult(string? Root, string? Error);

/// <summary>
/// Loads the environment signing keys and performs the rotation steps (generate, prepare next, discard next,
/// activate next, retire previous). Every action requires a system administrator, re-loads the file first, refuses
/// over an unreadable file, and refuses when the current key changed since the page loaded it. Every action is
/// audited with public-key fingerprints only. The SecurePath is re-read on every call, so an environment switch
/// takes effect.
/// </summary>
public sealed class EnvironmentKeyService
{
    public const string AuditTarget = "environment";

    private readonly Func<Task<SecureRootResult>> _resolveSecureRoot;
    private readonly Func<string, ISecureStorage> _storageFactory;
    private readonly IAuditLog _audit;
    private readonly Func<bool> _isSystemAdmin;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EnvironmentKeyService(
        Func<Task<SecureRootResult>> resolveSecureRoot,
        Func<bool> isSystemAdmin,
        Func<string, ISecureStorage>? storageFactory = null,
        IAuditLog? audit = null,
        Func<DateTime>? utcNow = null)
    {
        _resolveSecureRoot = resolveSecureRoot;
        _isSystemAdmin = isSystemAdmin;
        _storageFactory = storageFactory ?? (root => new LocalSecureStorage(root));
        _audit = audit ?? NullAuditLog.Instance;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<SigningKeyLoadResult> LoadAsync()
    {
        var resolved = await _resolveSecureRoot();
        if (resolved.Root is null)
            return SigningKeyLoadResult.Unreadable($"secure path unavailable ({resolved.Error ?? "unknown"})");

        return await new EnvironmentKeyStore(_storageFactory(resolved.Root)).LoadAsync();
    }

    /// <summary>
    /// The registry values the fleet must hold at this step. PublicKey is the key clients trust first (the next key
    /// once one is prepared); PublicKeyPrevious is the second key they still accept. Null means "not set" / "remove".
    /// </summary>
    public static (string? PublicKey, string? PublicKeyPrevious) GpoTarget(EnvironmentSigningKeys k) => (
        k.Next?.PublicKey ?? k.Current?.PublicKey,
        k.Next != null ? k.Current?.PublicKey : k.PreviousPublicKey);

    public Task<EnvironmentSigningKeys> GenerateAsync(string? expectedCurrentKeyId) =>
        ApplyAsync(AuditEvents.KeyGenerated, expectedCurrentKeyId, null, false,
            keys => keys.Current != null ? "an environment signing key already exists" : null,
            (keys, now) => keys.Current = NewPair(now),
            (before, after) => $"fingerprint={KeyFingerprint.Of(after.Current?.PublicKey)}");

    public Task<EnvironmentSigningKeys> PrepareNextAsync(string? expectedCurrentKeyId) =>
        ApplyAsync(AuditEvents.KeyPrepared, expectedCurrentKeyId, null, false,
            keys => keys.Current == null ? "there is no current key to rotate"
                : keys.Next != null ? "a next key is already prepared"
                : keys.PreviousPublicKey != null ? "retire the previous key before preparing another"
                : null,
            (keys, now) => keys.Next = NewPair(now),
            (before, after) => $"current={KeyFingerprint.Of(after.Current?.PublicKey)}; next={KeyFingerprint.Of(after.Next?.PublicKey)}");

    public Task<EnvironmentSigningKeys> DiscardNextAsync(string? expectedCurrentKeyId, string? expectedNextKeyId) =>
        ApplyAsync(AuditEvents.KeyDiscarded, expectedCurrentKeyId, expectedNextKeyId, true,
            keys => keys.Next == null ? "there is no next key to discard" : null,
            (keys, now) => keys.Next = null,
            (before, after) => $"discarded={KeyFingerprint.Of(before.Next?.PublicKey)}");

    public Task<EnvironmentSigningKeys> ActivateNextAsync(string? expectedCurrentKeyId, string? expectedNextKeyId) =>
        ApplyAsync(AuditEvents.KeyRotated, expectedCurrentKeyId, expectedNextKeyId, true,
            keys => keys.Next == null ? "there is no next key to activate"
                : keys.PreviousPublicKey != null ? "retire the previous key before activating another"
                : null,
            (keys, now) =>
            {
                keys.PreviousPublicKey = keys.Current?.PublicKey;
                keys.PreviousCreatedAt = keys.Current?.CreatedAt;
                keys.Current = keys.Next;
                keys.Next = null;
            },
            (before, after) => $"current={KeyFingerprint.Of(after.Current?.PublicKey)}; previous={KeyFingerprint.Of(after.PreviousPublicKey)}");

    public Task<EnvironmentSigningKeys> RetirePreviousAsync(string? expectedCurrentKeyId) =>
        ApplyAsync(AuditEvents.KeyRetired, expectedCurrentKeyId, null, false,
            keys => keys.PreviousPublicKey == null ? "there is no previous key to retire" : null,
            (keys, now) =>
            {
                keys.PreviousPublicKey = null;
                keys.PreviousCreatedAt = null;
            },
            (before, after) => $"retired={KeyFingerprint.Of(before.PreviousPublicKey)}; current={KeyFingerprint.Of(after.Current?.PublicKey)}");

    private static SigningKeyPair NewPair(DateTime now)
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        return new SigningKeyPair { PrivateKey = priv, PublicKey = pub, CreatedAt = now };
    }

    private async Task<EnvironmentSigningKeys> ApplyAsync(
        string eventName,
        string? expectedCurrentKeyId,
        string? expectedNextKeyId,
        bool checkNext,
        Func<EnvironmentSigningKeys, string?> refusal,
        Action<EnvironmentSigningKeys, DateTime> mutate,
        Func<EnvironmentSigningKeys, EnvironmentSigningKeys, string> detail)
    {
        if (!_isSystemAdmin())
            throw new UnauthorizedAccessException("Only a system administrator can manage the signing key.");

        await _gate.WaitAsync();
        try
        {
            try
            {
                var resolved = await _resolveSecureRoot();
                if (resolved.Root is null)
                    throw new InvalidOperationException($"The secure path is unavailable ({resolved.Error ?? "unknown"}).");

                var store = new EnvironmentKeyStore(_storageFactory(resolved.Root));
                var load = await store.LoadAsync();
                if (load.Outcome == KeyLoadOutcome.Unreadable)
                    throw new InvalidOperationException(
                        $"The existing environment signing key is not readable ({load.Reason}); it was not replaced.");

                var before = load.Keys ?? new EnvironmentSigningKeys();
                if (KeyFingerprint.Of(before.Current?.PublicKey) != expectedCurrentKeyId
                    || (checkNext && KeyFingerprint.Of(before.Next?.PublicKey) != expectedNextKeyId))
                    throw new InvalidOperationException("The key was changed by someone else; reload the page.");

                var why = refusal(before);
                if (why != null)
                    throw new InvalidOperationException($"Not done: {why}.");

                // Mutate a copy so a failed write leaves nothing half-changed in memory.
                var after = EnvironmentKeyStore.Parse(EnvironmentKeyStore.Serialize(before)).Keys!;
                mutate(after, _utcNow());
                await store.SaveAsync(after);

                await _audit.RecordAsync(new AuditRecord(eventName, AuditTarget, AuditOutcome.Success, detail(before, after)));
                return after;
            }
            catch (Exception ex)
            {
                // The reason never contains key material: it is one of the fixed messages above or an IO error text.
                await _audit.RecordAsync(new AuditRecord(eventName, AuditTarget, AuditOutcome.Failure, AuditLogExtensions.Describe(ex)));
                throw;
            }
        }
        finally { _gate.Release(); }
    }
}
