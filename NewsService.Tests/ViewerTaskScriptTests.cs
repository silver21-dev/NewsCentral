using NewsCentral.Configuration;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

public sealed class ViewerTaskModeResolverTests
{
    [Theory]
    [InlineData("VerifyOnly", "VerifyOnly")]
    [InlineData("verifyonly", "VerifyOnly")]
    [InlineData(" VERIFYONLY ", "VerifyOnly")]
    [InlineData("Off", "Off")]
    [InlineData("off", "Off")]
    [InlineData("OFF", "Off")]
    public void ValidValues_AreCaseInsensitive_AndHaveNoError(string raw, string expected)
    {
        var result = ViewerTaskModeResolver.Resolve(raw);
        Assert.Equal(expected, result.Mode.ToString());
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("Manage")]   // the removed mode
    [InlineData("Disabled")]
    [InlineData("1")]
    [InlineData("true")]
    public void UnrecognisedValue_FallsBackToVerifyOnly_WithAnErrorNamingValueAndValidSet(string raw)
    {
        var result = ViewerTaskModeResolver.Resolve(raw);
        Assert.Equal(ViewerTaskMode.VerifyOnly, result.Mode);
        Assert.NotNull(result.Error);
        Assert.Contains($"'{raw}'", result.Error);
        Assert.Contains("VerifyOnly, Off", result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbsentValue_DefaultsToVerifyOnly(string? raw)
    {
        var result = ViewerTaskModeResolver.Resolve(raw);
        Assert.Equal(ViewerTaskMode.VerifyOnly, result.Mode);
        Assert.Null(result.Error);
    }
}

public sealed class TaskScriptGeneratorTests
{
    private static readonly TaskDefinitionInputs Inputs =
        new("Contoso", @"C:\Program Files\Contoso\NewsCentral\NewsViewer\NewsViewer.exe", "Application", "NewsService");

    private static readonly TaskScriptHeader Header =
        new("1.2.3", new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero), "BOX01");

    private static string Generate(TaskDefinitionInputs? inputs = null, TaskScriptHeader? header = null) =>
        TaskScriptGenerator.Generate(inputs ?? Inputs, header ?? Header, TaskDefinitionBuilder.LoadTemplateBytes);

    private static string ShippedXml(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "deploy", "scheduled-tasks", file)))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "deploy", "scheduled-tasks", file);
    }

    private static byte[] EmbeddedBytes(string script, string variable)
    {
        var lines = script.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith($"${variable} = @(", StringComparison.Ordinal));
        Assert.True(start >= 0, $"{variable} not found");
        var b64 = new System.Text.StringBuilder();
        for (var i = start + 1; !lines[i].StartsWith(") -join", StringComparison.Ordinal); i++)
            b64.Append(lines[i].Trim().TrimEnd(',').Trim('\''));
        return Convert.FromBase64String(b64.ToString());
    }

    [Fact]
    public void IdenticalInputs_GiveByteIdenticalScript()
    {
        Assert.Equal(Generate(), Generate());
        Assert.Equal(
            System.Text.Encoding.ASCII.GetBytes(Generate()),
            System.Text.Encoding.ASCII.GetBytes(Generate()));
    }

    [Fact]
    public void Script_IsAscii_BakesInCompanyAndNames_AndCarriesNoSecret()
    {
        var script = Generate();
        Assert.All(script, ch => Assert.True(ch < 0x80, $"non-ASCII char U+{(int)ch:X4}"));
        Assert.Contains("'Contoso'", script);
        Assert.Contains(@"'\Contoso\NewsCentral\NewsViewer Launch'", script);
        Assert.Contains(@"'\Contoso\NewsCentral\NewsViewer Content Arrived'", script);
        Assert.Contains(@"'\Contoso\NewsCentral\NewsViewer'", script);
        Assert.Contains("#Requires -RunAsAdministrator", script);
        Assert.Contains("SupportsShouldProcess", script);
        Assert.Contains("# Generator: NewsService 1.2.3", script);
        Assert.Contains("# Machine:   BOX01", script);
        Assert.DoesNotContain("SecretKey", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ClientSecret", script, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("NewsViewer-Launch.xml", "LaunchXmlBase64")]
    [InlineData("NewsViewer-ContentArrived.xml", "ContentXmlBase64")]
    public void EmbeddedXml_RoundTripsToTheShippedFileBytes_BomIncluded(string file, string variable)
    {
        var decoded = EmbeddedBytes(Generate(), variable);
        var shipped = File.ReadAllBytes(ShippedXml(file));

        Assert.Equal(shipped, decoded);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, decoded.Take(2).ToArray()); // UTF-16 LE BOM
    }

    [Fact]
    public void NonAsciiValues_BecomeCharEscapes()
    {
        var script = Generate(Inputs with { Company = "Caf\u00E9" });
        Assert.Contains("('Caf' + [char]0x00E9)", script);
        Assert.All(script, ch => Assert.True(ch < 0x80));
    }

    [Fact]
    public void ApostropheInAValue_IsDoubledInTheLiteral() =>
        Assert.Equal("'O''Neil'", TaskScriptGenerator.Literal("O'Neil"));

    [Theory]
    [InlineData("")]
    [InlineData(@"Bad\Name")]
    public void InvalidCompany_Throws(string company) =>
        Assert.Throws<ArgumentException>(() => Generate(Inputs with { Company = company }));

    [Fact]
    public void InvalidEventSource_Throws() =>
        Assert.Throws<ArgumentException>(() => Generate(Inputs with { EventSourceName = "a'b" }));

    [Fact]
    public void Script_RequiresAdministrator_AndWritesTheStamp()
    {
        var script = Generate();
        Assert.StartsWith("#Requires -RunAsAdministrator", script);
        Assert.Contains("' [nc-hash:'", script);
        Assert.Contains("build time", script);
    }

    private static string Squash(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    [Fact]
    public void ScriptStamp_EqualsTheHashTheServiceExpects_AndWhatIfLeavesNoTempFiles()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) return;   // Windows PowerShell 5.1 ships with every supported Windows

        var dir = Path.Combine(Path.GetTempPath(), "nc-stamp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // #Requires -RunAsAdministrator would stop a non-elevated test run before it does anything.
            var script = string.Join("\r\n", Generate().Split("\r\n").Where(l => !l.StartsWith("#Requires")));
            var path = Path.Combine(dir, "r.ps1");
            File.WriteAllText(path, script, System.Text.Encoding.ASCII);
            var before = Directory.GetFiles(Path.GetTempPath(), "NewsViewerTask*.xml").Length;

            var psi = new System.Diagnostics.ProcessStartInfo(powershell)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path, "-WhatIf", "-Verbose" }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();

            Assert.Equal(0, p.ExitCode);
            foreach (var kind in new[] { ViewerTaskKind.Launch, ViewerTaskKind.ContentArrived })
            {
                var expected = TaskDefinitionBuilder.Build(kind, TaskDefinitionBuilder.LoadTemplate(kind), Inputs);
                // The host wraps long verbose lines when output is redirected: compare without whitespace.
                Assert.Contains(Squash($"nc-hash {expected.TaskName} {expected.Hash}"), Squash(output));
                Assert.Contains(expected.TaskName, output);   // the -WhatIf preview names the task
                Assert.Contains("Legacy HKLM Run value", output);
            }
            Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "NewsViewerTask*.xml").Length);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void EmitCommand_WritesScriptWithCompany_AndExitsZero()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nc-emit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), "{}");
            var target = Path.Combine(dir, "out", "r.ps1");
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var code = EmitTaskScriptCommand.Run(
                ["--emit-task-script", target, "--newsviewer-path", @"C:\X\NewsViewer.exe"],
                stdout, stderr, dir);

            Assert.Equal(0, code);
            Assert.Equal("", stderr.ToString());
            var script = File.ReadAllText(target);
            Assert.Contains($"'{SolutionConstants.Company}'", script);
            Assert.Contains(@"'C:\X\NewsViewer.exe'", script);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void EmitCommand_IgnoresTheDevelopmentOverlay_AndUsesTheCanonicalViewerPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nc-emit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"),
                """{"ViewerTasks":{"NewsViewerPath":"C:\\Base\\NewsViewer.exe"},"Logging":{"EventLog":{"SourceName":"BaseSource","LogName":"BaseLog"}}}""");
            File.WriteAllText(Path.Combine(dir, "appsettings.Development.json"),
                """{"Logging":{"EventLog":{"SourceName":"DevSource"}}}""");
            var target = Path.Combine(dir, "r.ps1");
            var stdout = new StringWriter(); var err = new StringWriter();

            var code = EmitTaskScriptCommand.Run(["--emit-task-script", target], stdout, err, dir);

            Assert.True(code == 0, err.ToString());
            var script = File.ReadAllText(target);
            Assert.Contains("'BaseSource'", script);
            Assert.Contains("'BaseLog'", script);
            Assert.DoesNotContain("DevSource", script);
            Assert.DoesNotContain(@"Base\NewsViewer", script);   // not even appsettings.json supplies the exe path
            Assert.Contains("'" + EmitTaskScriptCommand.DefaultViewerPath() + "'", script);
            Assert.Contains("appsettings.Development.json are not consulted", stdout.ToString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void EmitCommand_WithNoPath_PrintsUsage_AndExitsNonZero()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nc-emit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), "{}");
            var stderr = new StringWriter();
            var code = EmitTaskScriptCommand.Run(["--emit-task-script"], new StringWriter(), stderr, dir);
            Assert.NotEqual(0, code);
            Assert.Contains("Usage: NewsService.exe --emit-task-script <path>", stderr.ToString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void EmitCommand_WithoutAppsettings_FailsNonZeroWithReason()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nc-emit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var stderr = new StringWriter();
            var code = EmitTaskScriptCommand.Run(["--emit-task-script", Path.Combine(dir, "r.ps1")], new StringWriter(), stderr, dir);
            Assert.NotEqual(0, code);
            Assert.Contains("--emit-task-script failed", stderr.ToString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

public sealed class TaskDriftComparerTests
{
    private static readonly TaskDefinitionInputs Inputs =
        new("Contoso", @"C:\Install\NewsViewer\NewsViewer.exe", "Application", "NewsService");

    private static string Expected(ViewerTaskKind kind, TaskDefinitionInputs? inputs = null) =>
        TaskDefinitionBuilder.Build(kind, TaskDefinitionBuilder.LoadTemplate(kind), inputs ?? Inputs).Xml;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void IdenticalDefinition_HasNoDrift(int k) =>
        Assert.Empty(TaskDriftComparer.Compare("t", Expected((ViewerTaskKind)k), Expected((ViewerTaskKind)k)));

    [Fact]
    public void WrongCommandPath_IsReported()
    {
        var actual = Expected(ViewerTaskKind.Launch, Inputs with { ExePath = @"D:\Elsewhere\NewsViewer.exe" });
        var drift = TaskDriftComparer.Compare("t", Expected(ViewerTaskKind.Launch), actual);
        Assert.Contains(drift, d => d.Contains("wrong command") && d.Contains(@"D:\Elsewhere\NewsViewer.exe"));
    }

    [Fact]
    public void MissingTrigger_IsReported()
    {
        var expected = Expected(ViewerTaskKind.Launch);
        var start = expected.IndexOf("<LogonTrigger", StringComparison.Ordinal);
        var end = expected.IndexOf("</LogonTrigger>", StringComparison.Ordinal) + "</LogonTrigger>".Length;
        Assert.True(start >= 0 && end > start, "Launch template has a LogonTrigger");

        var drift = TaskDriftComparer.Compare("t", expected, expected.Remove(start, end - start));
        Assert.Contains(drift, d => d.Contains("wrong trigger set"));
    }

    [Fact]
    public void WrongEventSubscription_IsReported()
    {
        var actual = Expected(ViewerTaskKind.ContentArrived, Inputs with { EventSourceName = "Other" });
        var drift = TaskDriftComparer.Compare("t", Expected(ViewerTaskKind.ContentArrived), actual);
        Assert.Contains(drift, d => d.Contains("wrong event subscription"));
    }

    [Fact]
    public void UnreadableDefinition_IsReported() =>
        Assert.Single(TaskDriftComparer.Compare("t", Expected(ViewerTaskKind.Launch), "not xml"));
}
