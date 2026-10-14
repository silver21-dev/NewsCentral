using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using NewsCentral.Configuration;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Runs the generated script under Windows PowerShell 5.1 with its system-touching functions replaced
/// by mocks (injected at the "# ---- Main ----" marker), so the real control flow - messages, the
/// Verification section and the exit code - is exercised without registering anything.
/// </summary>
public sealed class GeneratedScriptBehaviourTests
{
    private const string Marker = "# ---- Main ----";
    private static readonly TaskDefinitionInputs Inputs = new(
        SolutionConstants.Company, TaskDefinitionInputsResolver.CanonicalViewerPath(), "Application", "NewsService");

    private static string Generate(TaskDefinitionInputs? inputs = null) =>
        TaskScriptGenerator.Generate(inputs ?? Inputs,
            new TaskScriptHeader("1.0", new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero), "BOX"),
            TaskDefinitionBuilder.LoadTemplateBytes);

    private static string? PowerShell =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") is var p && File.Exists(p) ? p : null;

    // Everything before the marker (header, baked values, functions), the mocks, then the main flow.
    private static string WithMocks(string script, string mocks)
    {
        var withoutRequires = string.Join("\r\n", script.Split("\r\n").Where(l => !l.StartsWith("#Requires")));
        var at = withoutRequires.IndexOf(Marker, StringComparison.Ordinal);
        Assert.True(at > 0, "marker present");
        return withoutRequires[..at] + mocks + "\r\n" + withoutRequires[at..];
    }

    private static string FunctionsOnly(string script, string tail)
    {
        var withoutRequires = string.Join("\r\n", script.Split("\r\n").Where(l => !l.StartsWith("#Requires")));
        return withoutRequires[..withoutRequires.IndexOf(Marker, StringComparison.Ordinal)] + "\r\n" + tail;
    }

    private static (int ExitCode, string Output) Run(string scriptText, params string[] args)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nc-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "s.ps1");
            File.WriteAllText(path, scriptText, Encoding.ASCII);
            var psi = new ProcessStartInfo(PowerShell!)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path }.Concat(args)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private const string CommonMocks = """
function Invoke-Schtasks { param([string[]]$Arguments) $script:SchtasksOutput = 'SUCCESS'; return 0 }
function Test-TaskExists { param($Name) return $false }
function Remove-LegacyRunValue { Write-Host 'mock: run value step' }
function Test-EventSourceExists { param($Name) return $true }
function Get-EventSourceLogName { param($Name) return $EventLogName }
function Get-ScheduledTask {
    param($TaskPath, $TaskName, $ErrorAction)
    if ($TaskName -eq '@@MISSING@@') { return $null }
    [pscustomobject]@{ Triggers = @(); Actions = @(); Principal = [pscustomobject]@{ GroupId = 'S-1-5-32-545'; RunLevel = 'Limited' } }
}
function Export-ScheduledTask {
    param($TaskName, $TaskPath, $ErrorAction)
    $xml = $script:Expected[($TaskPath.TrimEnd('\') + '\' + $TaskName)].Xml
    @@MUTATE@@
    return $xml
}
""";

    private static string Mocks(string missing = "", string mutate = "") =>
        CommonMocks.Replace("@@MISSING@@", missing.Length == 0 ? "no-such-task" : missing).Replace("@@MUTATE@@", mutate);

    [Fact]
    public void RealRun_VerifiesAndExitsZero_WhenEverythingMatches()
    {
        if (PowerShell is null) return;
        var (code, output) = Run(WithMocks(Generate(), Mocks()));

        Assert.Equal(0, code);
        Assert.Contains("Verification passed", output);
        Assert.Contains("principal S-1-5-32-545", output);
        Assert.Contains("event source 'NewsService': present", output);
        Assert.DoesNotContain("MISSING", output);
        Assert.DoesNotContain("MISMATCH", output);
    }

    [Fact]
    public void RealRun_ExitsNonZero_WhenATaskIsAbsentAtVerification()
    {
        if (PowerShell is null) return;
        var (code, output) = Run(WithMocks(Generate(), Mocks(missing: "NewsViewer Launch")));

        Assert.NotEqual(0, code);
        Assert.Contains("MISSING", output);
        Assert.Contains("VERIFICATION FAILED", output);
    }

    [Fact]
    public void RealRun_ExitsNonZero_WhenTheRegisteredCommandDiffers()
    {
        if (PowerShell is null) return;
        var mutate = "$xml = $xml.Replace('NewsViewer.exe', 'Other.exe')";
        var (code, output) = Run(WithMocks(Generate(), Mocks(mutate: mutate)));

        Assert.NotEqual(0, code);
        Assert.Contains("MISMATCH", output);
        Assert.Contains("wrong command", output);
    }

    [Fact]
    public void RealRun_ExitsNonZero_WhenTheTriggerSetDiffers()
    {
        if (PowerShell is null) return;
        var mutate = "$xml = [regex]::Replace($xml, '<LogonTrigger>.*?</LogonTrigger>', '', 'Singleline')";
        var (code, output) = Run(WithMocks(Generate(), Mocks(mutate: mutate)));

        Assert.NotEqual(0, code);
        Assert.Contains("wrong trigger set", output);
    }

    [Fact]
    public void RealRun_Verification_AcceptsAnExpandedCommandWhereATokenWasRegistered()
    {
        if (PowerShell is null) return;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var mutate = $"$xml = $xml.Replace('%ProgramFiles%', '{programFiles}')";
        var (code, output) = Run(WithMocks(Generate(), Mocks(mutate: mutate)));

        // The expanded form differs from the nc-hash-stamped text only inside the stamp-free fields compared here,
        // but the stamp itself is read from the Description, which the mutation leaves untouched.
        Assert.Equal(0, code);
        Assert.DoesNotContain("wrong command", output);
    }

    [Fact]
    public void WhatIf_SkipsVerification_WithANotice_AndExitsZero()
    {
        if (PowerShell is null) return;
        var (code, output) = Run(WithMocks(Generate(), Mocks(missing: "NewsViewer Launch")), "-WhatIf");

        Assert.Equal(0, code);
        Assert.Contains("Verification", output);
        Assert.Contains("skipped (-WhatIf", output);
        Assert.DoesNotContain("MISSING", output);
    }

    [Fact]
    public void RemovalMessages_DifferBetweenWhatIfAndARealRun_InInstallAndUninstall()
    {
        if (PowerShell is null) return;
        var script = Generate();

        // Real run, task absent: the reference wording, no "-WhatIf" noise.
        var (_, real) = Run(WithMocks(script, Mocks()));
        Assert.Contains($"not found legacy \\{SolutionConstants.Company}\\NewsCentral\\NewsViewer (nothing to remove)", real);
        Assert.Contains("not found old dev \\NewsCentral\\NewsViewer Launch (nothing to remove)", real);
        Assert.DoesNotContain("-WhatIf", real);
        Assert.DoesNotContain("absent, or", real);

        var (_, realUninstall) = Run(WithMocks(script, Mocks()), "-Uninstall");
        Assert.Contains($"not found \\{SolutionConstants.Company}\\NewsCentral\\NewsViewer Launch (nothing to remove)", realUninstall);
        Assert.DoesNotContain("-WhatIf", realUninstall);

        // -WhatIf with the tasks present: a distinct "would remove" line, in both paths.
        var present = Mocks().Replace("function Test-TaskExists { param($Name) return $false }", "function Test-TaskExists { param($Name) return $true }");
        var (_, whatIf) = Run(WithMocks(script, present), "-WhatIf");
        Assert.Contains("would remove legacy ", whatIf);
        Assert.Contains("(-WhatIf: nothing was changed)", whatIf);
        var (_, whatIfUninstall) = Run(WithMocks(script, present), "-Uninstall", "-WhatIf");
        Assert.Contains($"would remove \\{SolutionConstants.Company}\\NewsCentral\\NewsViewer Launch (-WhatIf", whatIfUninstall);
    }

    [Fact]
    public void RemovedMessage_UsesTheReferenceWording()
    {
        if (PowerShell is null) return;
        var (_, output) = Run(FunctionsOnly(Generate(), "Write-RemoveResult 'removed' '\\X\\Y' 'legacy '"));
        Assert.Contains("removed   legacy \\X\\Y", output);
    }
}

public sealed class EmittedPathTests
{
    private static string EmitTo(string dir, params string[] extraArgs)
    {
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), "{}");
        var target = Path.Combine(dir, "r.ps1");
        string[] args = ["--emit-task-script", target, .. extraArgs];
        Assert.Equal(0, EmitTaskScriptCommand.Run(args, new StringWriter(), new StringWriter(), dir));
        return File.ReadAllText(target);
    }

    [Fact]
    public void DefaultPath_IsTheUnexpandedToken_AndNeverTheBuildMachinesProgramFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nc-emit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var script = EmitTo(dir);
            Assert.Contains(@"'%ProgramFiles%\" + SolutionConstants.Company + @"\NewsCentral\NewsViewer\NewsViewer.exe'", script);
            Assert.Contains(@"'%ProgramFiles%\" + SolutionConstants.Company + @"\NewsCentral\NewsViewer'", script);   // working directory
            Assert.DoesNotContain(@"C:\Program Files", script);
            Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), script);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ExplicitPath_IsUsedLiterally_WithNoTokenSubstituted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nc-emit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var given = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Elsewhere\NewsViewer.exe";
            var script = EmitTo(dir, "--newsviewer-path", given);
            Assert.Contains("'" + given + "'", script);
            Assert.DoesNotContain("'%ProgramFiles%", script);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
