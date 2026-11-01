using System.Diagnostics;
using System.Security.AccessControl;
using NewsCentral.Security;

namespace NewsCentral.App.Tests;

/// <summary>
/// scripts/Set-AuthoringAcl.ps1: the refusals and -WhatIf, which change nothing and so run without elevation.
/// (The leading #Requires -RunAsAdministrator line is dropped from the copy under test, as the repo's other script
/// tests do. Applying the ACLs needs an elevated session and is covered by the manual test in the S02 spec.)
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class AuthoringAclScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nc-aclscript-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly string _script;
    private readonly string? _powershell;

    public AuthoringAclScriptTests()
    {
        _data = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(_data, "config"));
        File.WriteAllText(Path.Combine(_data, "config", "users.json"), "{}");

        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        _powershell = File.Exists(powershell) ? powershell : null;

        var source = FindScript();
        _script = Path.Combine(_root, "Set-AuthoringAcl.ps1");
        File.WriteAllLines(_script, File.ReadAllLines(source).Where(l => !l.StartsWith("#Requires")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static string FindScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "Set-AuthoringAcl.ps1");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("scripts/Set-AuthoringAcl.ps1 not found above " + AppContext.BaseDirectory);
    }

    private (int ExitCode, string Output) Run(string arguments)
    {
        var psi = new ProcessStartInfo(_powershell!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $"& '{_script}' {arguments}; exit $LASTEXITCODE" })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    private string Common =>
        $"-DataPath '{_data}' -AdminsGroup 'BUILTIN\\Administrators' -AuthorsGroup 'BUILTIN\\Users'";

    [Fact]
    public void WhatIf_ExitsZero_AndCreatesNothing()
    {
        if (_powershell is null) return;

        var (code, output) = Run(Common + " -WhatIf");

        Assert.Equal(0, code);
        Assert.Contains("What if", output);
        Assert.Contains("Grant AuthorsGroup Read", output);
        Assert.False(Directory.Exists(Path.Combine(_data, "secure")));
        Assert.False(Directory.Exists(Path.Combine(_data, "audit")));
    }

    [Fact]
    public void WhatIf_ShowsTheConfigFolderAcl_AndLeavesItUntouched()
    {
        if (_powershell is null || !OperatingSystem.IsWindows()) return;
        var config = Path.Combine(_data, "config");
        var before = new DirectoryInfo(config).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        var (code, output) = Run(Common + " -WhatIf");

        Assert.Equal(0, code);
        Assert.Contains(config, output);
        Assert.Contains("AuthorsGroup read-only (folder and files)", output);
        Assert.Equal(before, new DirectoryInfo(config).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    [Fact]
    public void Apply_MakesTheConfigFolderReadOnlyForAuthors_AndTheDriftCheckAgrees()
    {
        if (_powershell is null || !OperatingSystem.IsWindows()) return;
        var config = Path.Combine(_data, "config");
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().Name;   // keeps this test's own cleanup rights
        var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);

        // Give BUILTIN\Users the Modify the dev box inherits from the drive root, so there is something to remove.
        var info = new DirectoryInfo(config);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
        Assert.NotNull(AuthoringAclChecks.ConfigFolderWritableByBroadIdentity(AclExposureEvaluator.Read(config)));

        var (code, output) = Run($"-DataPath '{_data}' -AdminsGroup '{me}' -AuthorsGroup 'BUILTIN\\Users'");

        Assert.True(code == 0, output);
        var after = new DirectoryInfo(config).GetAccessControl();
        Assert.True(after.AreAccessRulesProtected);
        var usersRules = after.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Where(r => r.IdentityReference.Equals(users)).ToList();
        Assert.NotEmpty(usersRules);
        Assert.All(usersRules, r => Assert.Equal(0, (int)r.FileSystemRights & (int)AuthoringAclChecks.ConfigFolderWriteRights));
        Assert.Null(AuthoringAclChecks.ConfigFolderWritableByBroadIdentity(AclExposureEvaluator.Read(config)));
        Assert.DoesNotContain(
            AuthoringAclChecks.Check(_data, Path.Combine(_data, "secure"), Path.Combine(_data, "audit")),
            w => w.Area == AuthoringAclChecks.AreaConfig);
    }

    [Fact]
    public void UnknownGroup_Exits1_BeforeAnythingIsChanged()
    {
        if (_powershell is null) return;

        var (code, output) = Run($"-DataPath '{_data}' -AdminsGroup 'NO-SUCH-GROUP-{Guid.NewGuid():N}' -AuthorsGroup 'BUILTIN\\Users'");

        Assert.Equal(1, code);
        Assert.Contains("could not be resolved", output);
        Assert.False(Directory.Exists(Path.Combine(_data, "secure")));
    }

    [Fact]
    public void MissingUsersJson_Exits1()
    {
        if (_powershell is null) return;
        File.Delete(Path.Combine(_data, "config", "users.json"));

        var (code, output) = Run(Common);

        Assert.Equal(1, code);
        Assert.Contains("users.json", output);
    }

    [Fact]
    public void RelativeDataPath_Exits1()
    {
        if (_powershell is null) return;

        var (code, _) = Run("-DataPath 'relative\\path' -AdminsGroup 'BUILTIN\\Administrators' -AuthorsGroup 'BUILTIN\\Users'");

        Assert.Equal(1, code);
    }
}
