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
    protected string DataPath => Path.Combine(Root, "data");
    protected string SecurePath => Path.Combine(Root, "secure");

    protected static TeamSigningKeys Keys(string tag = "a") => new()
    {
        PrivateKey = "priv-" + tag,
        PublicKey = "pub-" + tag
    };

    protected string WriteLegacy(string team, TeamSigningKeys keys)
    {
        var path = TeamKeyMigrator.LegacyPath(DataPath, team);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, TeamKeyStore.Serialize(keys));
        return path;
    }

    protected string NewPath(string team) =>
        Path.Combine(SecurePath, "keys", team, "team-signing.json");

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}

public sealed class TeamKeyStoreTests : SecureStoreTestBase
{
    private TeamKeyStore Store(ISecureStorage? s = null) => new(s ?? new LocalSecureStorage(SecurePath));

    [Fact]
    public async Task AbsentFile_IsNotConfigured()
    {
        var r = await Store().LoadAsync("cz-its");

        Assert.Equal(KeyLoadOutcome.NotConfigured, r.Outcome);
        Assert.Null(r.Keys);
    }

    [Fact]
    public async Task ExistingFile_IsLoaded_AtTheKeysSubfolder()
    {
        await Store().SaveAsync("cz-its", Keys("x"));

        var r = await Store().LoadAsync("cz-its");

        Assert.Equal(KeyLoadOutcome.Loaded, r.Outcome);
        Assert.Equal("priv-x", r.Keys!.PrivateKey);
        Assert.True(File.Exists(NewPath("cz-its")));
    }

    [Fact]
    public async Task ParseError_IsUnreadable_NotNotConfigured()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(NewPath("cz-its"))!);
        File.WriteAllText(NewPath("cz-its"), "{ not json");

        var r = await Store().LoadAsync("cz-its");

        Assert.Equal(KeyLoadOutcome.Unreadable, r.Outcome);
        Assert.Contains("parsed", r.Reason);
    }

    [Fact]
    public async Task JsonNull_IsUnreadable()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(NewPath("cz-its"))!);
        File.WriteAllText(NewPath("cz-its"), "null");

        Assert.Equal(KeyLoadOutcome.Unreadable, (await Store().LoadAsync("cz-its")).Outcome);
    }

    [Fact]
    public async Task AccessDenied_IsUnreadable_WithReason()
    {
        var storage = new FaultyStorage(new LocalSecureStorage(SecurePath)) { ReadFault = new UnauthorizedAccessException() };

        var r = await Store(storage).LoadAsync("cz-its");

        Assert.Equal(KeyLoadOutcome.Unreadable, r.Outcome);
        Assert.Equal("access denied", r.Reason);
    }

    [Fact]
    public async Task IoError_IsUnreadable()
    {
        var storage = new FaultyStorage(new LocalSecureStorage(SecurePath)) { ReadFault = new IOException("disk gone") };

        var r = await Store(storage).LoadAsync("cz-its");

        Assert.Equal(KeyLoadOutcome.Unreadable, r.Outcome);
        Assert.Contains("disk gone", r.Reason);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public async Task UnsafeTeamName_IsUnreadable(string team)
    {
        Assert.Equal(KeyLoadOutcome.Unreadable, (await Store().LoadAsync(team)).Outcome);
    }

    [Fact]
    public void Fingerprint_IsDerivedFromThePublicKeyOnly()
    {
        var f = TeamKeyStore.Fingerprint(new TeamSigningKeys { PrivateKey = "SECRET", PublicKey = "pub" });

        Assert.Equal(16, f!.Length);
        Assert.DoesNotContain("SECRET", f);
        Assert.Null(TeamKeyStore.Fingerprint(new TeamSigningKeys { PrivateKey = "SECRET" }));
    }
}

public sealed class SigningKeyGateTests
{
    [Fact]
    public void Unreadable_Blocks_NeverUnsigned()
    {
        var d = SigningKeyGate.Decide(TeamKeyLoadResult.Unreadable("access denied"));

        Assert.Equal(SigningAction.Block, d.Action);
        Assert.Equal("access denied", d.BlockReason);
    }

    [Fact]
    public void NotConfigured_KeepsTodaysBehaviour_Unsigned()
    {
        Assert.Equal(SigningAction.PublishUnsigned, SigningKeyGate.Decide(TeamKeyLoadResult.NotConfigured()).Action);
    }

    [Fact]
    public void LoadedWithPrivateKey_Signs()
    {
        var d = SigningKeyGate.Decide(TeamKeyLoadResult.Loaded(new TeamSigningKeys { PrivateKey = "p", PublicKey = "q" }));

        Assert.Equal(SigningAction.Sign, d.Action);
        Assert.Equal("p", d.Keys!.PrivateKey);
    }

    [Fact]
    public void LoadedWithoutPrivateKey_IsAsToday_Unsigned()
    {
        Assert.Equal(SigningAction.PublishUnsigned,
            SigningKeyGate.Decide(TeamKeyLoadResult.Loaded(new TeamSigningKeys { PublicKey = "q" })).Action);
    }
}

public sealed class TeamKeyMigratorTests : SecureStoreTestBase
{
    [Fact]
    public async Task Migrates_CopyVerified_ThenOldDeleted()
    {
        var old = WriteLegacy("cz-its", Keys("a"));
        var source = File.ReadAllBytes(old);

        var r = await TeamKeyMigrator.MigrateAsync(DataPath, "cz-its", new LocalSecureStorage(SecurePath));

        Assert.Equal(KeyMigrationStatus.Migrated, r.Status);
        Assert.False(File.Exists(old));
        Assert.Equal(source, File.ReadAllBytes(NewPath("cz-its")));
        Assert.NotNull(r.Fingerprint);
    }

    [Fact]
    public async Task NoOldFile_NothingToMigrate()
    {
        var r = await TeamKeyMigrator.MigrateAsync(DataPath, "cz-its", new LocalSecureStorage(SecurePath));

        Assert.Equal(KeyMigrationStatus.NothingToMigrate, r.Status);
    }

    [Fact]
    public async Task WriteFailure_KeepsOldFile()
    {
        var old = WriteLegacy("cz-its", Keys("a"));
        var storage = new FaultyStorage(new LocalSecureStorage(SecurePath)) { WriteFault = new UnauthorizedAccessException() };

        var r = await TeamKeyMigrator.MigrateAsync(DataPath, "cz-its", storage);

        Assert.Equal(KeyMigrationStatus.Failed, r.Status);
        Assert.Contains("access denied", r.Detail);
        Assert.True(File.Exists(old));
        Assert.False(File.Exists(NewPath("cz-its")));
    }

    [Fact]
    public async Task CopyThatDiffersFromSource_FailsVerification_KeepsOldFile_RemovesBadCopy()
    {
        var old = WriteLegacy("cz-its", Keys("a"));
        var storage = new FaultyStorage(new LocalSecureStorage(SecurePath))
        {
            // Still valid JSON, so only the byte comparison can catch it.
            CorruptOnWrite = b => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b) + " ")
        };

        var r = await TeamKeyMigrator.MigrateAsync(DataPath, "cz-its", storage);

        Assert.Equal(KeyMigrationStatus.Failed, r.Status);
        Assert.Contains("differs", r.Detail);
        Assert.True(File.Exists(old));
        Assert.False(File.Exists(NewPath("cz-its")));
    }

    [Fact]
    public async Task CopyThatDoesNotParse_FailsVerification_KeepsOldFile()
    {
        var old = WriteLegacy("cz-its", Keys("a"));
        var storage = new FaultyStorage(new LocalSecureStorage(SecurePath)) { CorruptOnWrite = _ => "garbage"u8.ToArray() };

        var r = await TeamKeyMigrator.MigrateAsync(DataPath, "cz-its", storage);

        Assert.Equal(KeyMigrationStatus.Failed, r.Status);
        Assert.True(File.Exists(old));
    }

    [Fact]
    public async Task SecureStoreNotReadable_Fails_KeepsOldFile()
    {
        var old = WriteLegacy("cz-its", Keys("a"));
        var storage = new FaultyStorage(new LocalSecureStorage(SecurePath)) { ReadFault = new UnauthorizedAccessException() };

        var r = await TeamKeyMigrator.MigrateAsync(DataPath, "cz-its", storage);

        Assert.Equal(KeyMigrationStatus.Failed, r.Status);
        Assert.True(File.Exists(old));
    }

    [Fact]
    public async Task BothExistAndDiffer_IsConflict_NothingOverwrittenOrDeleted()
    {
        var old = WriteLegacy("cz-its", Keys("old"));
        await new TeamKeyStore(new LocalSecureStorage(SecurePath)).SaveAsync("cz-its", Keys("new"));
        var newBytes = File.ReadAllBytes(NewPath("cz-its"));

        var r = await TeamKeyMigrator.MigrateAsync(DataPath, "cz-its", new LocalSecureStorage(SecurePath));

        Assert.Equal(KeyMigrationStatus.Conflict, r.Status);
        Assert.True(File.Exists(old));
        Assert.Equal(newBytes, File.ReadAllBytes(NewPath("cz-its")));
    }

    [Fact]
    public async Task BothExistAndIdentical_FinishesTheJob_DeletesOld()
    {
        var old = WriteLegacy("cz-its", Keys("a"));
        Directory.CreateDirectory(Path.GetDirectoryName(NewPath("cz-its"))!);
        File.Copy(old, NewPath("cz-its"));

        var r = await TeamKeyMigrator.MigrateAsync(DataPath, "cz-its", new LocalSecureStorage(SecurePath));

        Assert.Equal(KeyMigrationStatus.Migrated, r.Status);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public async Task InvalidOldFile_IsNotCopied()
    {
        var old = TeamKeyMigrator.LegacyPath(DataPath, "cz-its");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, "{ broken");

        var r = await TeamKeyMigrator.MigrateAsync(DataPath, "cz-its", new LocalSecureStorage(SecurePath));

        Assert.Equal(KeyMigrationStatus.Failed, r.Status);
        Assert.True(File.Exists(old));
        Assert.False(File.Exists(NewPath("cz-its")));
    }
}

public sealed class TeamKeyServiceTests : SecureStoreTestBase
{
    private TeamKeyService Service(Func<string, ISecureStorage>? factory = null) =>
        new(() => DataPath, () => Task.FromResult(new SecureRootResult(SecurePath, null)), factory);

    [Fact]
    public async Task FirstLoad_MigratesLazily_AndReturnsTheKey()
    {
        var old = WriteLegacy("cz-its", Keys("a"));
        var svc = Service();

        var r = await svc.LoadAsync("cz-its");

        Assert.Equal(KeyLoadOutcome.Loaded, r.Outcome);
        Assert.False(r.FromLegacyLocation);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(NewPath("cz-its")));
        Assert.Empty(svc.Issues);
    }

    [Fact]
    public async Task FailedMigration_UsesTheOldFileForTheSession_AndReportsIt()
    {
        var old = WriteLegacy("cz-its", Keys("a"));
        var svc = Service(root => new FaultyStorage(new LocalSecureStorage(root)) { WriteFault = new UnauthorizedAccessException() });

        var r = await svc.LoadAsync("cz-its");

        Assert.Equal(KeyLoadOutcome.Loaded, r.Outcome);
        Assert.True(r.FromLegacyLocation);
        Assert.Equal("priv-a", r.Keys!.PrivateKey);
        Assert.True(File.Exists(old));
        Assert.Single(svc.Issues);
        Assert.Contains("cz-its", svc.Issues[0]);
    }

    [Fact]
    public async Task Conflict_UsesTheNewLocationFile()
    {
        WriteLegacy("cz-its", Keys("old"));
        await new TeamKeyStore(new LocalSecureStorage(SecurePath)).SaveAsync("cz-its", Keys("new"));
        var svc = Service();

        var r = await svc.LoadAsync("cz-its");

        Assert.Equal("priv-new", r.Keys!.PrivateKey);
        Assert.False(r.FromLegacyLocation);
        Assert.Contains("conflict", svc.Issues.Single());
    }

    [Fact]
    public async Task MigrateAll_MigratesEveryTeam_AndRaisesTheEventPerTeam()
    {
        WriteLegacy("cz-its", Keys("a"));
        WriteLegacy("de-prod", Keys("b"));
        var svc = Service();
        var migrated = new List<string>();
        svc.MigrationCompleted += r => migrated.Add($"{r.Team}:{r.Status}");

        var results = await svc.MigrateAllAsync(["cz-its", "de-prod", "empty-team"]);

        Assert.Equal(3, results.Count);
        Assert.Equal(["cz-its:Migrated", "de-prod:Migrated"], migrated);
        Assert.True(File.Exists(NewPath("de-prod")));
    }

    [Fact]
    public async Task UnreadableSecureStore_LoadIsUnreadable_NotNotConfigured()
    {
        var svc = Service(root => new FaultyStorage(new LocalSecureStorage(root)) { ReadFault = new UnauthorizedAccessException() });

        var r = await svc.LoadAsync("cz-its");

        Assert.Equal(KeyLoadOutcome.Unreadable, r.Outcome);
    }

    [Fact]
    public async Task SecureRootUnavailable_LoadIsUnreadable()
    {
        var svc = new TeamKeyService(() => DataPath, () => Task.FromResult(new SecureRootResult(null, "settings invalid")));

        var r = await svc.LoadAsync("cz-its");

        Assert.Equal(KeyLoadOutcome.Unreadable, r.Outcome);
        Assert.Contains("settings invalid", r.Reason);
    }

    [Fact]
    public async Task Save_RefusesToReplaceAnUnreadableKey()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(NewPath("cz-its"))!);
        File.WriteAllText(NewPath("cz-its"), "{ corrupt");
        var svc = Service();

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveAsync("cz-its", Keys("n")));

        Assert.Equal("{ corrupt", File.ReadAllText(NewPath("cz-its")));
    }

    [Fact]
    public async Task Save_WritesToTheSecureStoreOnly()
    {
        var svc = Service();

        await svc.SaveAsync("cz-its", Keys("n"));

        Assert.True(File.Exists(NewPath("cz-its")));
        Assert.False(File.Exists(TeamKeyMigrator.LegacyPath(DataPath, "cz-its")));
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

public sealed class FleetExportUnreadableKeyTests
{
    private static FleetExportInput Input(params FleetExportTeam[] teams) => new(
        Company: "Contoso", EnvironmentDisplayName: "Prod", DataPath: "D:/data", GeneratedAtUtc: DateTime.UtcNow,
        Distribution: new FleetExportDistribution(true, "Local", null, "", "", ""),
        EffectiveLocalDistributionPath: @"\\srv\share", Teams: teams);

    [Fact]
    public void UnreadableKey_IsReportedAsNotReadable_NotAsNoKey()
    {
        var export = FleetExportBuilder.Build(Input(
            new FleetExportTeam("cz-its", "CZ ITS", null, null, KeyUnreadableReason: "access denied"),
            new FleetExportTeam("de-prod", "DE", null, null)));

        Assert.Contains(export.Warnings, w => w.StartsWith("Key not readable: cz-its") && w.Contains("access denied"));
        Assert.DoesNotContain(export.Warnings, w => w.StartsWith("No signing key: cz-its"));
        Assert.Contains(export.Warnings, w => w.StartsWith("No signing key: de-prod"));
    }

    [Fact]
    public void UnreadableKey_ContributesNoKeyMaterial()
    {
        var export = FleetExportBuilder.Build(Input(
            new FleetExportTeam("cz-its", "CZ ITS", null, null, KeyUnreadableReason: "access denied")));

        Assert.DoesNotContain("TeamPublicKeys", export.PowerShell);
    }
}
