using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using NewsCentral.Security;
using NewsService.Configuration;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// The publish-folder write-exposure check. The regression guard is the ordinary case: a read-only
/// Users ACE (ReadAndExecute + Synchronize) must NOT trip it, because a false positive now stops publishing.
/// </summary>
public sealed class ImagePublisherAclTests : IDisposable
{
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier AuthUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    private static readonly SecurityIdentifier Interactive = new(WellKnownSidType.InteractiveSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System_ = new(WellKnownSidType.LocalSystemSid, null);

    private const FileSystemRights ReadOnly = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;

    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var d in _dirs) { try { Directory.Delete(d, recursive: true); } catch { /* best effort */ } }
    }

    // ---- pure decision --------------------------------------------------------------------------

    private static AclRuleView Allow(SecurityIdentifier sid, FileSystemRights rights) => new(sid, (int)rights, true);
    private static AclRuleView Deny(SecurityIdentifier sid, FileSystemRights rights) => new(sid, (int)rights, false);

    [Fact]
    public void TheOrdinaryInheritedLayout_DoesNotTripTheCheck()
    {
        // C:\Windows\Web: administrators and SYSTEM full, Users read-only (the rule that tripped the old check),
        // plus the generic read/execute child-inherit rule icacls shows as (GR,GE).
        var rules = new[]
        {
            Allow(System_, FileSystemRights.FullControl),
            Allow(Admins, FileSystemRights.FullControl),
            Allow(Users, ReadOnly),
            new AclRuleView(Users, unchecked((int)0xA0000000), true),   // GENERIC_READ | GENERIC_EXECUTE
        };

        Assert.Null(PublishFolderAcl.FindWriteExposure(rules));
    }

    [Fact]
    public void TheOldMaskWouldHaveFiredOnTheOrdinaryCase_SoTheRegressionGuardIsReal()
    {
        const FileSystemRights oldMask = FileSystemRights.Write | FileSystemRights.Modify | FileSystemRights.FullControl;
        Assert.NotEqual(0, (int)(ReadOnly & oldMask));
        Assert.Equal(0, (int)(ReadOnly & PublishFolderAcl.WriteRights));
    }

    [Theory]
    [InlineData("Users")]
    [InlineData("Everyone")]
    [InlineData("Authenticated Users")]
    [InlineData("INTERACTIVE")]
    public void AWriteGrantToEachWatchedIdentity_TripsTheCheck(string name)
    {
        var sid = name switch { "Users" => Users, "Everyone" => Everyone, "Authenticated Users" => AuthUsers, _ => Interactive };

        var finding = PublishFolderAcl.FindWriteExposure([Allow(Admins, FileSystemRights.FullControl), Allow(sid, FileSystemRights.Modify)]);

        Assert.NotNull(finding);
        Assert.Equal(name, finding!.Identity);
        Assert.Equal(sid.Value, finding.Sid);
    }

    [Theory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.WriteAttributes)]
    [InlineData(FileSystemRights.WriteExtendedAttributes)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    public void EachWriteSpecificRight_Trips(FileSystemRights right) =>
        Assert.NotNull(PublishFolderAcl.FindWriteExposure([Allow(Users, ReadOnly | right)]));

    [Theory]
    [InlineData(0x40000000)]   // GENERIC_WRITE
    [InlineData(0x10000000)]   // GENERIC_ALL
    public void AnInheritOnlyGenericWrite_Trips(int generic) =>
        Assert.NotNull(PublishFolderAcl.FindWriteExposure([new AclRuleView(Users, generic, true)]));

    [Fact]
    public void AnAllowWriteCoveredByAMatchingDeny_DoesNotTrip()
    {
        var rules = new[] { Allow(Users, FileSystemRights.Modify), Deny(Users, FileSystemRights.Modify) };
        Assert.Null(PublishFolderAcl.FindWriteExposure(rules));
    }

    [Fact]
    public void ADenyThatCoversOnlyPartOfTheAllow_StillTrips_NamingWhatRemains()
    {
        var rules = new[] { Allow(Users, FileSystemRights.WriteData | FileSystemRights.Delete), Deny(Users, FileSystemRights.WriteData) };
        var finding = PublishFolderAcl.FindWriteExposure(rules);

        Assert.NotNull(finding);
        Assert.Contains("Delete", finding!.Rights);
        Assert.DoesNotContain("WriteData", finding.Rights);
    }

    [Fact]
    public void ADenyWriteAlone_DoesNotTrip() =>
        Assert.Null(PublishFolderAcl.FindWriteExposure([Allow(Users, ReadOnly), Deny(Users, FileSystemRights.Write)]));

    [Fact]
    public void ADenyForAnotherIdentity_DoesNotCancelTheAllow() =>
        Assert.NotNull(PublishFolderAcl.FindWriteExposure([Allow(Users, FileSystemRights.Modify), Deny(Everyone, FileSystemRights.Modify)]));

    [Fact]
    public void AdministratorsAndSystemWriteRights_AreNotWatched() =>
        Assert.Null(PublishFolderAcl.FindWriteExposure([Allow(Admins, FileSystemRights.FullControl), Allow(System_, FileSystemRights.FullControl)]));

    [Fact]
    public void TheRealWindowsWebFolderOnThisMachine_IsNotFlagged()
    {
        // The supported location inherits its ACL from %SystemRoot%\Web. If this machine's own copy of that
        // ACL tripped the check, every healthy default install would be refused.
        var web = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web");
        if (!Directory.Exists(web)) return;
        Assert.Null(PublishFolderAcl.FindWriteExposure(PublishFolderAcl.Read(web)));

        var publish = Path.Combine(web, "NewsCentral");
        if (Directory.Exists(publish)) Assert.Null(PublishFolderAcl.FindWriteExposure(PublishFolderAcl.Read(publish)));
    }

    [Fact]
    public void RemediationIsTailoredToThePath()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Contains("restore it", PublishFolderAcl.Remediation(Path.Combine(windows, "Web", "NewsCentral")));
        var elsewhere = PublishFolderAcl.Remediation(@"C:\ProgramData\NewsCentral\Published");
        Assert.Contains("installer must set an explicit ACL", elsewhere);
        Assert.Contains(@"%SystemRoot%\Web\NewsCentral", elsewhere);
    }

    // ---- the refusal path, on a real folder --------------------------------------------------------

    private sealed class CapturingLogger : ILogger<ImagePublisher>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private (ImagePublisher Sut, CapturingLogger Log, string Root, string Source, string Hash) Rig()
    {
        var root = Path.Combine(Path.GetTempPath(), "nsvc-acl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _dirs.Add(root);
        var srcDir = Path.Combine(Path.GetTempPath(), "nsvc-aclsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcDir);
        _dirs.Add(srcDir);
        var bytes = "image bytes"u8.ToArray();
        var source = Path.Combine(srcDir, "a.jpg");
        File.WriteAllBytes(source, bytes);
        var log = new CapturingLogger();
        var sut = new ImagePublisher(new ServiceConfiguration { Delivery = new DeliverySection { PublishedImagePath = root } }, log);
        return (sut, log, root, source, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static void Grant(string dir, SecurityIdentifier sid, FileSystemRights rights)
    {
        var info = new DirectoryInfo(dir);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(sid, rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [Fact]
    public void ARealFolderWithAReadOnlyUsersAce_PublishesNormally_AndLogsNoError()
    {
        var (sut, log, root, source, hash) = Rig();
        Grant(root, Users, ReadOnly);   // the same rights the inherited C:\Windows\Web ACE carries

        var path = sut.Publish(source, hash, "lockscreen");

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData("Users")]
    [InlineData("Everyone")]
    [InlineData("Authenticated Users")]
    [InlineData("INTERACTIVE")]
    public void AWritableFolder_RefusesToPublish_NamingThePathAndTheIdentity_WithoutThrowing(string name)
    {
        var (sut, log, root, source, hash) = Rig();
        var sid = name switch { "Users" => Users, "Everyone" => Everyone, "Authenticated Users" => AuthUsers, _ => Interactive };
        Grant(root, sid, FileSystemRights.Modify);

        var path = sut.Publish(source, hash, "lockscreen");   // must not throw

        Assert.Null(path);
        Assert.Empty(Directory.GetFiles(root));
        var error = Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(root, error.Message);
        Assert.Contains(name, error.Message);
        Assert.Contains("installer must set an explicit ACL", error.Message);   // outside %SystemRoot%\Web
    }

    [Fact]
    public void TheError_IsChangeGated_LoggedOnceAcrossPublishAndSweep_AndAClearedFolderResumes()
    {
        var (sut, log, root, source, hash) = Rig();
        Grant(root, Users, FileSystemRights.Modify);

        for (var i = 0; i < 3; i++)
        {
            Assert.Null(sut.Publish(source, hash, "lockscreen"));
            sut.SweepExcept(null, "lockscreen");
        }
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error);

        // Administrator fixes the ACL: the next publish works and the clearing is reported once.
        var info = new DirectoryInfo(root);
        var security = info.GetAccessControl();
        security.RemoveAccessRuleAll(new FileSystemAccessRule(Users, FileSystemRights.Modify, AccessControlType.Allow));
        info.SetAccessControl(security);

        Assert.NotNull(sut.Publish(source, hash, "lockscreen"));
        Assert.Single(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("no longer user-writable"));
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void ARefusedFolder_IsNotSweptAndKeepsItsPreviousFiles()
    {
        var (sut, _, root, _, _) = Rig();
        var previous = Path.Combine(root, "lockscreen-0123456789abcdef.jpg");
        File.WriteAllText(previous, "previous state");
        Grant(root, Users, FileSystemRights.Modify);

        sut.SweepExcept(null, "lockscreen");

        Assert.True(File.Exists(previous));
    }

    [Fact]
    public void StartupVerification_ReportsOnce_AndNeverThrows()
    {
        var (sut, log, root, _, _) = Rig();
        Grant(root, Everyone, FileSystemRights.Write);

        sut.VerifyPublishFolder();
        sut.VerifyPublishFolder();

        Assert.Single(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Everyone"));
    }
}
