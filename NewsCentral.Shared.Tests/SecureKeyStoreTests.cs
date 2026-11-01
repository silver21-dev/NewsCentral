using System.Text;
using NewsCentral.Configuration;
using NewsCentral.Configuration.FleetExport;
using NewsCentral.Models;
using NewsCentral.Security;

namespace NewsCentral.Shared.Tests;

/// <summary>Storage that fails in controlled ways, wrapping a real one.</summary>
internal sealed class FaultyStorage : ISecureStorage
{
    private readonly ISecureStorage _inner;
    public FaultyStorage(ISecureStorage inner) => _inner = inner;

    public Exception? ReadFault { get; set; }
    public Exception? WriteFault { get; set; }
    public Func<byte[], byte[]>? CorruptOnWrite { get; set; }

    public string Root => _inner.Root;

    public Task<byte[]?> ReadBytesAsync(string relativePath) =>
        ReadFault is null ? _inner.ReadBytesAsync(relativePath) : Task.FromException<byte[]?>(ReadFault);

    public Task WriteBytesAsync(string relativePath, byte[] content) =>
        WriteFault is not null
            ? Task.FromException(WriteFault)
            : _inner.WriteBytesAsync(relativePath, CorruptOnWrite is null ? content : CorruptOnWrite(content));

    public Task DeleteAsync(string relativePath) => _inner.DeleteAsync(relativePath);
}

public abstract class SecureStoreTestBase : IDisposable
{
    protected readonly string Root = Path.Combine(Path.GetTempPath(), "nc-secure-" + Guid.NewGuid().ToString("N"));
    protected string SecurePath => Path.Combine(Root, "secure");

    /// <summary>A pair with a real P-256 key (the service tests use real keys; the store tests only need valid Base64).</summary>
    protected static SigningKeyPair Pair(string tag = "a", DateTime? createdAt = null) => new()
    {
        PrivateKey = "priv-" + tag,
        PublicKey = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("pub-" + tag)),   // valid Base64: the fingerprint decodes it
        CreatedAt = createdAt
    };

    protected string KeyFilePath => Path.Combine(SecurePath, "keys", "environment-signing.json");

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}

public sealed class EnvironmentKeyStoreTests : SecureStoreTestBase
{
    private EnvironmentKeyStore Store(ISecureStorage? s = null) => new(s ?? new LocalSecureStorage(SecurePath));

    [Fact]
    public async Task AbsentFile_IsNotConfigured()
    {
        var r = await Store().LoadAsync();

        Assert.Equal(KeyLoadOutcome.NotConfigured, r.Outcome);
        Assert.Null(r.Keys);
    }

    [Fact]
    public async Task ExistingFile_IsLoaded_AtTheKeysSubfolder_WithAllThreeSlots()
    {
        var created = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        await Store().SaveAsync(new EnvironmentSigningKeys
        {
            Current = Pair("c", created),
            Next = Pair("n", created),
            PreviousPublicKey = "cHJldg==",
            PreviousCreatedAt = created
        });

        var r = await Store().LoadAsync();

        Assert.Equal(KeyLoadOutcome.Loaded, r.Outcome);
        Assert.Equal("priv-c", r.Keys!.Current!.PrivateKey);
        Assert.Equal(created, r.Keys.Current.CreatedAt);
        Assert.Equal("priv-n", r.Keys.Next!.PrivateKey);
        Assert.Equal("cHJldg==", r.Keys.PreviousPublicKey);
        Assert.Equal(created, r.Keys.PreviousCreatedAt);
        Assert.True(File.Exists(KeyFilePath));
    }

    [Fact]
    public async Task Json_IsCamelCase_Indented_AndOmitsNulls()
    {
        await Store().SaveAsync(new EnvironmentSigningKeys { Current = Pair("c") });

        var text = await File.ReadAllTextAsync(KeyFilePath);

        Assert.Contains("\"current\"", text);
        Assert.Contains("\"privateKey\"", text);
        Assert.DoesNotContain("\"next\"", text);
        Assert.DoesNotContain("previousPublicKey", text);
        Assert.Contains("\n", text);   // indented
    }

    [Fact]
    public async Task ParseError_IsUnreadable_NotNotConfigured()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFilePath)!);
        File.WriteAllText(KeyFilePath, "{ not json");

        var r = await Store().LoadAsync();

        Assert.Equal(KeyLoadOutcome.Unreadable, r.Outcome);
        Assert.Contains("parsed", r.Reason);
    }

    [Fact]
    public async Task JsonNull_IsUnreadable()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFilePath)!);
        File.WriteAllText(KeyFilePath, "null");

        Assert.Equal(KeyLoadOutcome.Unreadable, (await Store().LoadAsync()).Outcome);
    }

    [Fact]
    public async Task AccessDenied_IsUnreadable_WithReason()
    {
        var storage = new FaultyStorage(new LocalSecureStorage(SecurePath)) { ReadFault = new UnauthorizedAccessException() };

        var r = await Store(storage).LoadAsync();

        Assert.Equal(KeyLoadOutcome.Unreadable, r.Outcome);
        Assert.Equal("access denied", r.Reason);
    }

    [Fact]
    public async Task IoError_IsUnreadable()
    {
        var storage = new FaultyStorage(new LocalSecureStorage(SecurePath)) { ReadFault = new IOException("disk gone") };

        var r = await Store(storage).LoadAsync();

        Assert.Equal(KeyLoadOutcome.Unreadable, r.Outcome);
        Assert.Contains("disk gone", r.Reason);
    }

    [Fact]
    public void Fingerprint_IsDerivedFromTheCurrentPublicKeyOnly()
    {
        var f = EnvironmentKeyStore.Fingerprint(new EnvironmentSigningKeys
        {
            Current = new SigningKeyPair { PrivateKey = "SECRET", PublicKey = "cHVi" },
            Next = Pair("other")
        });

        Assert.Equal(16, f!.Length);
        Assert.DoesNotContain("SECRET", f);
        Assert.Equal(KeyFingerprint.Of("cHVi"), f);
        Assert.Null(EnvironmentKeyStore.Fingerprint(new EnvironmentSigningKeys { Current = new SigningKeyPair { PrivateKey = "SECRET" } }));
        Assert.Null(EnvironmentKeyStore.Fingerprint(new EnvironmentSigningKeys { Next = Pair("n") }));
    }
}

public sealed class SigningKeyGateTests
{
    [Fact]
    public void Unreadable_Blocks_NeverUnsigned()
    {
        var d = SigningKeyGate.Decide(SigningKeyLoadResult.Unreadable("access denied"));

        Assert.Equal(SigningAction.Block, d.Action);
        Assert.Equal("access denied", d.BlockReason);
    }

    [Fact]
    public void NotConfigured_KeepsTodaysBehaviour_Unsigned()
    {
        Assert.Equal(SigningAction.PublishUnsigned, SigningKeyGate.Decide(SigningKeyLoadResult.NotConfigured()).Action);
    }

    [Fact]
    public void LoadedWithCurrentPrivateKey_SignsWithCurrent()
    {
        var d = SigningKeyGate.Decide(SigningKeyLoadResult.Loaded(new EnvironmentSigningKeys
        {
            Current = new SigningKeyPair { PrivateKey = "p", PublicKey = "q" }
        }));

        Assert.Equal(SigningAction.Sign, d.Action);
        Assert.Equal("p", d.Key!.PrivateKey);
    }

    [Fact]
    public void NextIsNeverUsedForSigning()
    {
        var withNext = SigningKeyGate.Decide(SigningKeyLoadResult.Loaded(new EnvironmentSigningKeys
        {
            Current = new SigningKeyPair { PrivateKey = "cur", PublicKey = "q" },
            Next = new SigningKeyPair { PrivateKey = "next", PublicKey = "r" }
        }));
        Assert.Equal("cur", withNext.Key!.PrivateKey);

        // Only a Next, no Current: nothing to sign with, so the index is not signed with Next either.
        var onlyNext = SigningKeyGate.Decide(SigningKeyLoadResult.Loaded(new EnvironmentSigningKeys
        {
            Next = new SigningKeyPair { PrivateKey = "next", PublicKey = "r" }
        }));
        Assert.Equal(SigningAction.PublishUnsigned, onlyNext.Action);
        Assert.Null(onlyNext.Key);
    }

    [Fact]
    public void LoadedWithoutPrivateKey_IsAsToday_Unsigned()
    {
        Assert.Equal(SigningAction.PublishUnsigned,
            SigningKeyGate.Decide(SigningKeyLoadResult.Loaded(new EnvironmentSigningKeys
            {
                Current = new SigningKeyPair { PublicKey = "q" }
            })).Action);
    }
}

public sealed class SecurePathSettingsTests
{
    [Fact]
    public void Default_IsDataPathSecure()
    {
        Assert.Equal(Path.Combine("D:/data", "secure"),
            EnvironmentSettingsResolver.EffectiveSecurePath(new EnvironmentSettings(), "D:/data"));
    }

    [Fact]
    public void Explicit_Wins()
    {
        Assert.Equal("E:/keys",
            EnvironmentSettingsResolver.EffectiveSecurePath(new EnvironmentSettings { SecurePath = "E:/keys" }, "D:/data"));
    }

    [Fact]
    public void EnvironmentWithoutFile_FallsBackToTheDefault()
    {
        var resolved = EnvironmentSettingsResolver.Resolve(null, new EnvironmentSettings());

        Assert.Equal(EnvironmentSettingsSource.MachineDefaults, resolved.Source);
        Assert.Equal(Path.Combine("D:/data", "secure"),
            EnvironmentSettingsResolver.EffectiveSecurePath(resolved.Settings, "D:/data"));
        Assert.Equal(Path.Combine("D:/data", "secure"), EnvironmentSettingsResolver.EffectiveSecurePath(null, "D:/data"));
    }

    [Fact]
    public void FileWithoutTheField_ResolvesToNull_ThenDefault()
    {
        var resolved = EnvironmentSettingsResolver.Resolve("{\"schemaVersion\":1}", new EnvironmentSettings());

        Assert.Equal(EnvironmentSettingsSource.EnvironmentFile, resolved.Source);
        Assert.Null(resolved.Settings!.SecurePath);
    }

    [Fact]
    public void FileWithTheField_RoundTrips()
    {
        var resolved = EnvironmentSettingsResolver.Resolve("{\"schemaVersion\":1,\"securePath\":\"E:/keys\"}", new EnvironmentSettings());

        Assert.Equal("E:/keys", resolved.Settings!.SecurePath);
    }

    [Fact]
    public void RelativeSecurePath_FailsValidation()
    {
        Assert.NotEmpty(EnvironmentSettingsResolver.Validate(new EnvironmentSettings { SecurePath = "keys" }));
        Assert.Empty(EnvironmentSettingsResolver.Validate(new EnvironmentSettings { SecurePath = "E:/keys" }));
    }

    [Fact]
    public void MergeForSave_And_ApplyPolicy_PreserveSecurePath()
    {
        var form = new EnvironmentSettings { SecurePath = "E:/keys" };
        var merged = EnvironmentSettingsResolver.MergeForSave(form, null, new EnvironmentSettings(), Array.Empty<string>());
        Assert.Equal("E:/keys", merged.SecurePath);

        var (applied, _) = EnvironmentSettingsResolver.ApplyPolicy(merged, new PolicyEnvironment("p", "D:/data", null, null, null, null, null, null, null, null, false));
        Assert.Equal("E:/keys", applied.SecurePath);
    }
}
