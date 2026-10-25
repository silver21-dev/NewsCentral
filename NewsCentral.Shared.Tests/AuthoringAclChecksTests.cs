using System.Security.AccessControl;
using System.Security.Principal;
using NewsCentral.Security;

namespace NewsCentral.Shared.Tests;

public sealed class AuthoringAclChecksTests : IDisposable
{
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System_ = new(WellKnownSidType.LocalSystemSid, null);
    // A group the provisioning script would grant (not one of the broad identities the check watches).
    private static readonly SecurityIdentifier AuthorsGroup = new("S-1-5-21-1-2-3-1001");

    private static AclRuleView Allow(SecurityIdentifier s, FileSystemRights r) => new(s, (int)r, true);
    private static AclRuleView Deny(SecurityIdentifier s, FileSystemRights r) => new(s, (int)r, false);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nc-aclchk-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── keys ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FileSystemRights.ReadData)]
    [InlineData(FileSystemRights.Read)]
    [InlineData(FileSystemRights.ReadAndExecute)]
    [InlineData(FileSystemRights.Modify)]
    [InlineData(FileSystemRights.FullControl)]
    public void Keys_ReadByUsers_IsFlagged(FileSystemRights granted)
    {
        var finding = AuthoringAclChecks.KeysReadableByBroadIdentity(new[] { Allow(Admins, FileSystemRights.FullControl), Allow(Users, granted) });

        Assert.Equal("Users", finding?.Identity);
    }

    [Fact]
    public void Keys_GenericRead_AndEveryone_AreFlagged()
    {
        Assert.NotNull(AuthoringAclChecks.KeysReadableByBroadIdentity(new[] { new AclRuleView(Users, unchecked((int)0x80000000), true) }));
        Assert.Equal("Everyone", AuthoringAclChecks.KeysReadableByBroadIdentity(new[] { Allow(Everyone, FileSystemRights.ReadData) })?.Identity);
    }

    [Fact]
    public void Keys_ProvisionedLayout_IsNotFlagged()
    {
        var rules = new[]
        {
            Allow(System_, FileSystemRights.FullControl),
            Allow(Admins, FileSystemRights.FullControl),
            Allow(AuthorsGroup, FileSystemRights.Modify)   // a named signer group is not a broad identity
        };

        Assert.Null(AuthoringAclChecks.KeysReadableByBroadIdentity(rules));
    }

    [Fact]
    public void Keys_DenyCancelsTheAllow()
    {
        Assert.Null(AuthoringAclChecks.KeysReadableByBroadIdentity(new[]
        {
            Allow(Users, FileSystemRights.ReadAndExecute), Deny(Users, FileSystemRights.ReadData)
        }));
    }

    [Fact]
    public void Keys_WriteOnlyGrant_IsNotAReadExposure()
    {
        Assert.Null(AuthoringAclChecks.KeysReadableByBroadIdentity(new[] { Allow(Users, FileSystemRights.WriteExtendedAttributes) }));
    }

    // ── users.json ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    [InlineData(FileSystemRights.Modify)]
    public void UsersFile_WritableByUsers_IsFlagged(FileSystemRights granted)
    {
        Assert.Equal("Users", AuthoringAclChecks.UsersFileWritableByBroadIdentity(new[] { Allow(Users, granted) })?.Identity);
    }

    [Fact]
    public void UsersFile_ReadOnlyForUsers_IsNotFlagged()
    {
        var rules = new[]
        {
            Allow(Admins, FileSystemRights.FullControl),
            Allow(Users, FileSystemRights.Read),
            Allow(AuthorsGroup, FileSystemRights.Read)
        };

        Assert.Null(AuthoringAclChecks.UsersFileWritableByBroadIdentity(rules));
    }

    [Fact]
    public void UsersFile_GenericWriteFromAnInheritOnlyAce_IsFlagged()
    {
        Assert.NotNull(AuthoringAclChecks.UsersFileWritableByBroadIdentity(new[] { new AclRuleView(Everyone, 0x40000000, true) }));
    }

    // ── audit ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    public void Audit_TamperRights_ForUsers_AreFlagged(FileSystemRights granted)
    {
        Assert.Equal("Users", AuthoringAclChecks.AuditTamperableByBroadIdentity(new[] { Allow(Users, granted) })?.Identity);
    }

    [Fact]
    public void Audit_AppendOnlyAndListing_IsNotFlagged()
    {
        // What the provisioning script grants - here (wrongly, for the test) even to Users: append and list are fine.
        var rules = new[]
        {
            Allow(Users, FileSystemRights.AppendData | FileSystemRights.ReadAttributes | FileSystemRights.Synchronize | FileSystemRights.ListDirectory)
        };

        Assert.Null(AuthoringAclChecks.AuditTamperableByBroadIdentity(rules));
    }

    [Fact]
    public void Audit_ProvisionedLayout_IsNotFlagged()
    {
        var rules = new[]
        {
            Allow(System_, FileSystemRights.FullControl),
            Allow(Admins, FileSystemRights.FullControl),
            Allow(AuthorsGroup, FileSystemRights.ListDirectory | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories),
            Allow(AuthorsGroup, FileSystemRights.AppendData | FileSystemRights.ReadAttributes | FileSystemRights.Synchronize)
        };

        Assert.Null(AuthoringAclChecks.AuditTamperableByBroadIdentity(rules));
    }

    [Fact]
    public void Audit_InheritedCreatorStyleUsersAce_OverReports_Acknowledged()
    {
        // The evaluator ignores inheritance flags, so Users holding CreateFiles (the WriteData bit) on an ordinary,
        // un-provisioned folder is reported. That is the documented, accepted over-report.
        Assert.NotNull(AuthoringAclChecks.AuditTamperableByBroadIdentity(new[] { Allow(Users, FileSystemRights.CreateFiles) }));
    }

    // ── file system ───────────────────────────────────────────────────────────

    [Fact]
    public void Check_ReportsEachArea_WhenUsersAreGrantedAccess_OnRealFolders()
    {
        if (!OperatingSystem.IsWindows()) return;

        var data = Path.Combine(_root, "data");
        var secure = Path.Combine(data, "secure");
        var audit = Path.Combine(data, "audit");
        var keysTeam = Path.Combine(secure, "keys", "cz-its");
        Directory.CreateDirectory(keysTeam);
        Directory.CreateDirectory(audit);
        Directory.CreateDirectory(Path.Combine(data, "config"));
        var usersFile = Path.Combine(data, "config", "users.json");
        File.WriteAllText(usersFile, "{}");

        GrantUsers(new DirectoryInfo(keysTeam), FileSystemRights.ReadData);
        GrantUsers(new FileInfo(usersFile), FileSystemRights.WriteData);
        GrantUsers(new DirectoryInfo(Path.Combine(data, "config")), FileSystemRights.CreateFiles);
        GrantUsers(new DirectoryInfo(audit), FileSystemRights.Delete);

        var warnings = AuthoringAclChecks.Check(data, secure, audit);

        Assert.Equal(
            new[] { AuthoringAclChecks.AreaKeys, AuthoringAclChecks.AreaUsers, AuthoringAclChecks.AreaConfig, AuthoringAclChecks.AreaAudit },
            warnings.Select(w => w.Area).ToArray());
        Assert.Contains("cz-its", warnings[0].Path);
        Assert.All(warnings, w => Assert.Contains("Set-AuthoringAcl.ps1", w.Message));
    }

    [Fact]
    public void Check_MissingAssets_ProduceNoWarnings_AndDoNotThrow()
    {
        var warnings = AuthoringAclChecks.Check(
            Path.Combine(_root, "nope"), Path.Combine(_root, "nope", "secure"), Path.Combine(_root, "nope", "audit"));

        Assert.Empty(warnings);
    }

    // â”€â”€ config folder â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [InlineData(FileSystemRights.CreateFiles)]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(FileSystemRights.Modify)]
    public void ConfigFolder_WritableByAuthenticatedUsersOrUsers_IsFlagged(FileSystemRights granted)
    {
        var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        Assert.Equal("Users", AuthoringAclChecks.ConfigFolderWritableByBroadIdentity(new[] { Allow(Users, granted) })?.Identity);
        Assert.NotNull(AuthoringAclChecks.ConfigFolderWritableByBroadIdentity(new[] { Allow(authenticatedUsers, granted) }));
    }

    [Fact]
    public void ConfigFolder_AsTheScriptProvisionsIt_IsNotFlagged()
    {
        var rules = new[]
        {
            Allow(System_, FileSystemRights.FullControl),
            Allow(Admins, FileSystemRights.FullControl),
            Allow(AuthorsGroup, FileSystemRights.Read),
            Allow(Users, FileSystemRights.Read)
        };

        Assert.Null(AuthoringAclChecks.ConfigFolderWritableByBroadIdentity(rules));
    }

    [Fact]
    public void ConfigFolder_AllowThenDenyOnTheSameIdentity_IsNotFlagged()
    {
        var rules = new[] { Allow(Users, FileSystemRights.Modify), Deny(Users, FileSystemRights.Modify) };

        Assert.Null(AuthoringAclChecks.ConfigFolderWritableByBroadIdentity(rules));
    }

    private static void GrantUsers(FileSystemInfo target, FileSystemRights rights)
    {
        var rule = new FileSystemAccessRule(Users, rights, AccessControlType.Allow);
        if (target is DirectoryInfo d)
        {
            var s = d.GetAccessControl();
            s.AddAccessRule(rule);
            d.SetAccessControl(s);
        }
        else
        {
            var f = (FileInfo)target;
            var s = f.GetAccessControl();
            s.AddAccessRule(rule);
            f.SetAccessControl(s);
        }
    }
}
