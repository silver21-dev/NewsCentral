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
            // Invoked the way a caller that is not "-File" sees it: the exit code is whatever $LASTEXITCODE holds when
            // the script returns, so a script that falls off the end after a failing native probe reports 1.
            var command = "& '" + path + "' " + string.Join(' ', args) + "; exit $LASTEXITCODE";
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private const string CommonMocks = """
function Invoke-Schtasks { param([string[]]$Arguments) $script:SchtasksOutput = 'SUCCESS'; return 0 }
# Faithful to the real probe: a native command that exits 1 leaves $LASTEXITCODE = 1, which is exactly what
# the script used to leak as its exit code when it fell off the end.
function Test-TaskExists { param($Name) & cmd.exe /c exit 1; return $false }
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
        var present = Mocks().Replace("& cmd.exe /c exit 1; return $false", "& cmd.exe /c exit 1; return $true");
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

public sealed class ExitCodeAndNormalisationTests
{
    private static readonly string Company = SolutionConstants.Company;

    private static string? PowerShell =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") is var p && File.Exists(p) ? p : null;

    private static BuiltTaskDefinition Build(ViewerTaskKind kind, string exe) =>
        TaskDefinitionBuilder.Build(kind, TaskDefinitionBuilder.LoadTemplate(kind),
            new TaskDefinitionInputs(Company, exe, "Application", "NewsService"));

    [Fact]
    public void TokenAndExpandedForms_HashEqual_AndDifferentPathsDoNot()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var tail = Company + @"\NewsCentral\NewsViewer\NewsViewer.exe";
        string[] same =
        [
            @"%ProgramFiles%\" + tail,
            programFiles + @"\" + tail,
            programFiles.ToUpperInvariant() + @"\" + tail.ToLowerInvariant(),
            programFiles.Replace('\\', '/') + "/" + tail.Replace('\\', '/'),
            "%ProgramFiles%/" + tail.Replace('\\', '/'),
        ];
        foreach (var kind in new[] { ViewerTaskKind.Launch, ViewerTaskKind.ContentArrived })
        {
            var hashes = same.Select(p => Build(kind, p).Hash).Distinct().ToList();
            Assert.Single(hashes);
            Assert.NotEqual(hashes[0], Build(kind, @"%ProgramFiles%\Other\NewsViewer.exe").Hash);
        }
    }

    [Fact]
    public void TheRegisteredXml_KeepsThePathAsWritten_EvenThoughTheHashIsNormalised()
    {
        var built = Build(ViewerTaskKind.Launch, @"%ProgramFiles%\" + Company + @"\NewsCentral\NewsViewer\NewsViewer.exe");
        Assert.Contains("%ProgramFiles%", built.Xml);
        Assert.DoesNotContain(@"%programfiles%\" + Company.ToLowerInvariant(), built.Xml);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\X\\NewsViewer.exe\"")]
    [InlineData("  c:/Program Files/X/NewsViewer.exe/ ")]
    [InlineData("%ProgramFiles%\\X\\NewsViewer.exe")]
    [InlineData("C:\\Program Files\\X\\NEWSVIEWER.EXE\\")]
    public void NormaliseIsEqualInCSharpAndInThePowerShellScript(string input)
    {
        var ps = PowerShell;
        if (ps is null) return;
        var script = TaskScriptGenerator.Generate(
            new TaskDefinitionInputs(Company, TaskDefinitionInputsResolver.CanonicalViewerPath(), "Application", "NewsService"),
            new TaskScriptHeader("1", DateTimeOffset.UnixEpoch, "B"), TaskDefinitionBuilder.LoadTemplateBytes);
        var functions = script[..script.IndexOf("# ---- Main ----", StringComparison.Ordinal)]
            .Replace("#Requires -RunAsAdministrator", "");
        var dir = Path.Combine(Path.GetTempPath(), "nc-norm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "n.ps1");
            File.WriteAllText(file, functions + "\r\nWrite-Output ('<<' + (Get-NormalizedPath $env:NC_INPUT) + '>>')\r\n", System.Text.Encoding.ASCII);
            var psi = new System.Diagnostics.ProcessStartInfo(ps)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            psi.Environment["NC_INPUT"] = input;
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", file }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            Assert.Contains("<<" + TaskPathNormalizer.Normalize(input) + ">>", output);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TaskDescriptions_NameTheRightComponent()
    {
        foreach (var kind in new[] { ViewerTaskKind.Launch, ViewerTaskKind.ContentArrived })
        {
            var xml = TaskDefinitionBuilder.LoadTemplate(kind);
            Assert.DoesNotContain("maintained by NewsService", xml);
            Assert.DoesNotContain("repaired by NewsService", xml);
            Assert.Contains("Registered by the installer or the registration script, and verified by NewsService.", xml);
        }
    }

    [Fact]
    public void ReferenceScript_HasExplicitExitCodes_AndTheGeneratedBannerNoLongerNotesADivergence()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scripts", "Register-NewsViewerTasks.ps1"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "scripts", "Register-NewsViewerTasks.ps1"));
        Assert.Contains("exit 1", text);
        Assert.Contains("exit 0", text);
        Assert.Contains("VERIFICATION FAILED", text);

        var generated = TaskScriptGenerator.Generate(
            new TaskDefinitionInputs(Company, TaskDefinitionInputsResolver.CanonicalViewerPath(), "Application", "NewsService"),
            new TaskScriptHeader("1", DateTimeOffset.UnixEpoch, "B"), TaskDefinitionBuilder.LoadTemplateBytes);
        Assert.DoesNotContain("DIFFERENCE FROM THE REFERENCE", generated);
    }

    [Fact]
    public void GeneratedScript_ExitsExplicitly_OnEveryTerminalPath()
    {
        var generated = TaskScriptGenerator.Generate(
            new TaskDefinitionInputs(Company, TaskDefinitionInputsResolver.CanonicalViewerPath(), "Application", "NewsService"),
            new TaskScriptHeader("1", DateTimeOffset.UnixEpoch, "B"), TaskDefinitionBuilder.LoadTemplateBytes);
        var main = generated[generated.IndexOf("# ---- Main ----", StringComparison.Ordinal)..];
        Assert.DoesNotContain("\r\n    return\r\n", main);   // the uninstall path exits 0 rather than falling out
        Assert.Contains("exit 0", main);
        Assert.Contains("exit 1", main);
    }
}
