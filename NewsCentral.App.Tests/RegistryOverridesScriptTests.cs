using System.Diagnostics;
using NewsCentral.Security;

namespace NewsCentral.App.Tests;

/// <summary>
/// scripts/Set-RegistryOverrides.ps1, signing parameters: validation happens before any registry access, so the
/// refusals and -WhatIf run without elevation (the leading #Requires -RunAsAdministrator line is dropped from the copy
/// under test, as the other script tests do). Run under Windows PowerShell 5.1.
/// </summary>
public sealed class RegistryOverridesScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nc-regscript-" + Guid.NewGuid().ToString("N"));
    private readonly string _company = "NcTest" + Guid.NewGuid().ToString("N")[..10];
    private readonly string _script;
    private readonly string? _powershell;

    public RegistryOverridesScriptTests()
    {
        Directory.CreateDirectory(_root);
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        _powershell = OperatingSystem.IsWindows() && File.Exists(powershell) ? powershell : null;

        _script = Path.Combine(_root, "Set-RegistryOverrides.ps1");
        File.WriteAllLines(_script, File.ReadAllLines(FindScript()).Where(l => !l.StartsWith("#Requires")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        if (OperatingSystem.IsWindows())
        {
            try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree($@"Software\{_company}", throwOnMissingSubKey: false); }
            catch { /* not elevated: nothing was created */ }
        }
    }

    private static string FindScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "Set-RegistryOverrides.ps1");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("scripts/Set-RegistryOverrides.ps1 not found above " + AppContext.BaseDirectory);
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

    private string Common(string component = "NewsService") => $"-Company '{_company}' -ComponentName {component}";

    private bool CompanyKeyExists() =>
        OperatingSystem.IsWindows() && Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"Software\{_company}") != null;

    [Fact]
    public void InvalidBase64Key_Exits1_AndTheRegistryIsUntouched()
    {
        if (_powershell is null) return;

        // Without -WhatIf on purpose: validation must fail before any write, so this is safe without elevation.
        var (code, output) = Run(Common() + " -SigningPublicKey 'not-a-key'");

        Assert.Equal(1, code);
        Assert.Contains("not valid Base64", output);
        Assert.False(CompanyKeyExists());
    }

    [Fact]
    public void KeyOfTheWrongLength_Exits1_AndTheRegistryIsUntouched()
    {
        if (_powershell is null) return;
        var (privateKey, _) = SigningKeyTool.GenerateKeyPair();   // valid Base64, but a PKCS#8 private key: not 91 bytes

        var (code, output) = Run(Common() + $" -SigningPublicKey '{privateKey}'");

        Assert.Equal(1, code);
        Assert.Contains("91 bytes", output);
        Assert.DoesNotContain(privateKey, output.Replace($"-SigningPublicKey does not decode", ""));   // never echo key material
        Assert.False(CompanyKeyExists());
    }

    [Fact]
    public void InvalidPreviousKey_Exits1_EvenWithAValidCurrentKey()
    {
        if (_powershell is null) return;
        var (_, pub) = SigningKeyTool.GenerateKeyPair();

        var (code, output) = Run(Common() + $" -SigningPublicKey '{pub}' -SigningPublicKeyPrevious 'garbage!' -WhatIf");

        Assert.Equal(1, code);
        Assert.Contains("SigningPublicKeyPrevious", output);
        Assert.DoesNotContain("What if", output);   // nothing was planned either
    }

    [Fact]
    public void EmptyCurrentKey_Exits1()
    {
        if (_powershell is null) return;

        var (code, output) = Run(Common() + " -SigningPublicKey '' -WhatIf");

        Assert.Equal(1, code);
        Assert.Contains("-SigningPublicKey is empty", output);
    }

    [Fact]
    public void ValidKey_WithWhatIf_Exits0_ShowsThePublicKeyWrite_AndChangesNothing()
    {
        if (_powershell is null) return;
        var (_, pub) = SigningKeyTool.GenerateKeyPair();

        var (code, output) = Run(Common("NewsViewer") + $" -RequireSignedIndex $true -SigningPublicKey '{pub}' -SigningPublicKeyPrevious '' -WhatIf");

        Assert.Equal(0, code);
        Assert.Contains("What if", output);
        Assert.Contains(@"Signing\PublicKey", output);
        Assert.Contains(pub, output);
        Assert.Contains(@"Signing\RequireSignedIndex", output);
        Assert.False(CompanyKeyExists());
    }

    [Fact]
    public void ValidKey_WithWhatIf_ShowsTheOldPerTeamSubkeyRemoval_WhenOneExists()
    {
        if (_powershell is null || !OperatingSystem.IsWindows()) return;

        // Creating the old-layout subkey needs elevation; without it only the PublicKey write can be checked.
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                $@"Software\{_company}\NewsCentral\NewsService\Signing\cz-its");
            key!.SetValue("PublicKey", "old-per-team-key");
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        var (_, pub) = SigningKeyTool.GenerateKeyPair();

        var (code, output) = Run(Common() + $" -SigningPublicKey '{pub}' -WhatIf");

        Assert.Equal(0, code);
        Assert.Contains("Remove old per-team signing subkey", output);
        Assert.Contains("cz-its", output);
        Assert.Contains(pub, output);
        // -WhatIf: the subkey is still there.
        Assert.NotNull(Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            $@"Software\{_company}\NewsCentral\NewsService\Signing\cz-its"));
    }

    [Fact]
    public void TheOldPerTeamParameters_AreGone()
    {
        if (_powershell is null) return;

        var (_, output) = Run(Common() + " -TeamPublicKeys @{ 'cz-its' = 'x' } -WhatIf");
        // A binding error stops the script before it runs; the harness's trailing exit reports $LASTEXITCODE (unset),
        // so the proof here is the parameter-not-found message and that nothing was planned.
        Assert.Contains("TeamPublicKeys", output);
        Assert.Contains("parameter", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("What if", output);
    }

    [Fact]
    public void SigningParameters_AreRefusedForNewsCentral()
    {
        if (_powershell is null) return;
        var (_, pub) = SigningKeyTool.GenerateKeyPair();

        var (code, output) = Run(Common("NewsCentral") + $" -SigningPublicKey '{pub}' -WhatIf");

        Assert.Equal(1, code);
        Assert.Contains("not valid for -ComponentName NewsCentral", output);
    }
}
