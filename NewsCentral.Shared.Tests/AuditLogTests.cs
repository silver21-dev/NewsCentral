using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NewsCentral.Audit;
using NewsCentral.Configuration;

namespace NewsCentral.Shared.Tests;

public abstract class AuditTestBase : IDisposable
{
    protected readonly string Root = Path.Combine(Path.GetTempPath(), "nc-audit-" + Guid.NewGuid().ToString("N"));
    protected string AuditRoot => Path.Combine(Root, "audit");

    protected AuditSettings Settings { get; set; }
    protected readonly AuditContext Context = new() { AppUser = "alice" };
    protected readonly List<string> Logged = new();
    protected DateTime Now = new(2026, 10, 25, 14, 30, 0, DateTimeKind.Utc);

    protected AuditTestBase()
    {
        Settings = new AuditSettings(true, AuditRoot, false, "Prod environment");
    }

    protected AuditLog NewLog(string user = "jdoe", string machine = "ws01") =>
        new(() => Task.FromResult(Settings), Context, machine, user, () => Now, Logged.Add);

    protected static string[] Lines(string file) =>
        File.ReadAllLines(file).Where(l => l.Length > 0).ToArray();

    protected string[] AllFiles() =>
        Directory.Exists(AuditRoot) ? Directory.GetFiles(AuditRoot, "*.jsonl", SearchOption.AllDirectories) : [];

    protected string OnlyFile() => Assert.Single(AllFiles());

    public void Dispose()
    {
        try
        {
            foreach (var f in AllFiles()) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch { /* best effort */ }
    }
}

public sealed class AuditLogTests : AuditTestBase
{
    [Fact]
    public async Task Line_HasExactlyTheDocumentedFields_AndValues()
    {
        await NewLog().RecordAsync(new AuditRecord(AuditEvents.PresentationCreated, "pres-1", AuditOutcome.Success, "team=cz-its"));

        using var doc = JsonDocument.Parse(Assert.Single(Lines(OnlyFile())));
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();

        Assert.Equal(
            new[] { "appUser", "detail", "environment", "event", "machine", "outcome", "target", "utc", "windowsAccount" },
            names);
        var root = doc.RootElement;
        Assert.Equal("2026-10-25T14:30:00.000Z", root.GetProperty("utc").GetString());
        Assert.Equal("WS01", root.GetProperty("machine").GetString());
        Assert.Equal("jdoe", root.GetProperty("windowsAccount").GetString());
        Assert.Equal("alice", root.GetProperty("appUser").GetString());
        Assert.Equal("Prod environment", root.GetProperty("environment").GetString());
        Assert.Equal("PresentationCreated", root.GetProperty("event").GetString());
        Assert.Equal("pres-1", root.GetProperty("target").GetString());
        Assert.Equal("Success", root.GetProperty("outcome").GetString());
        Assert.Equal("team=cz-its", root.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task OptionalFields_AreOmitted_NotNull()
    {
        await NewLog().RecordAsync(new AuditRecord(AuditEvents.Logout));

        using var doc = JsonDocument.Parse(Assert.Single(Lines(OnlyFile())));
        Assert.False(doc.RootElement.TryGetProperty("target", out _));
        Assert.False(doc.RootElement.TryGetProperty("detail", out _));
        Assert.False(doc.RootElement.TryGetProperty("attemptedUser", out _));
    }

    [Fact]
    public async Task FailedLogin_CarriesAttemptedUser_AndNoAppUser()
    {
        await NewLog().RecordAsync(new AuditRecord(
            AuditEvents.LoginFailed, null, AuditOutcome.Failure, "invalid password", AttemptedUser: "mallory"));

        using var doc = JsonDocument.Parse(Assert.Single(Lines(OnlyFile())));
        Assert.Equal("mallory", doc.RootElement.GetProperty("attemptedUser").GetString());
        Assert.False(doc.RootElement.TryGetProperty("appUser", out _));
        Assert.Equal("Failure", doc.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task ExplicitAppUser_OverridesTheContext()
    {
        Context.AppUser = null;   // already signed out by the time the write runs

        await NewLog().RecordAsync(new AuditRecord(AuditEvents.Logout, AppUser: "alice"));

        using var doc = JsonDocument.Parse(Assert.Single(Lines(OnlyFile())));
        Assert.Equal("alice", doc.RootElement.GetProperty("appUser").GetString());
    }

    [Fact]
    public async Task NoFieldCanCarrySecrets_TheSetIsClosed_AndValuesAreSingleLineAndBounded()
    {
        var log = NewLog();
        await log.RecordAsync(new AuditRecord(AuditEvents.PasswordChanged, "u-1", AuditOutcome.Success,
            "line one\r\nline two " + new string('x', 500)));

        var file = OnlyFile();
        var lines = Lines(file);
        var line = Assert.Single(lines);   // the embedded newline did not split the record
        using var doc = JsonDocument.Parse(line);
        var forbidden = new[] { "password", "hash", "privateKey", "secret", "content", "key" };
        foreach (var p in doc.RootElement.EnumerateObject())
            Assert.DoesNotContain(forbidden, f => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
        Assert.True(doc.RootElement.GetProperty("detail").GetString()!.Length <= 201);
    }

    [Fact]
    public async Task FileName_FollowsTheDocumentedPattern_UtcDate()
    {
        Now = new DateTime(2026, 1, 2, 23, 59, 0, DateTimeKind.Utc);

        await NewLog(user: "jdoe", machine: "ws01").RecordAsync(new AuditRecord(AuditEvents.Logout));

        Assert.Equal(Path.Combine(AuditRoot, "2026-01", "2026-01-02_WS01_jdoe.jsonl"), OnlyFile());
    }

    [Fact]
    public async Task UserName_IsSanitizedForTheFileName()
    {
        await NewLog(user: "CONTOSO\\j doe:x").RecordAsync(new AuditRecord(AuditEvents.Logout));

        var name = Path.GetFileName(OnlyFile());
        Assert.Equal("2026-10-25_WS01_CONTOSO_j_doe_x.jsonl", name);
    }

    [Fact]
    public async Task EachEvent_IsAppendedAsOneMoreLine()
    {
        var log = NewLog();
        await log.RecordAsync(new AuditRecord(AuditEvents.LoginSucceeded));
        await log.RecordAsync(new AuditRecord(AuditEvents.PresentationCreated));
        await log.RecordAsync(new AuditRecord(AuditEvents.Logout));

        var events = Lines(OnlyFile()).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("event").GetString()).ToArray();
        Assert.Equal(new[] { "LoginSucceeded", "PresentationCreated", "Logout" }, events);
    }

    [Fact]
    public async Task ParallelWrites_ProduceOneValidLineEach()
    {
        var log = NewLog();

        await Task.WhenAll(Enumerable.Range(0, 60).Select(i =>
            log.RecordAsync(new AuditRecord(AuditEvents.PresentationUpdated, $"p-{i}"))));

        var lines = Lines(OnlyFile());
        Assert.Equal(60, lines.Length);
        Assert.All(lines, l => JsonDocument.Parse(l).Dispose());
        Assert.Equal(60, lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("target").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task Disabled_WritesNothing_NotEvenTheFolder()
    {
        Settings = Settings with { Enabled = false };

        await NewLog().RecordAsync(new AuditRecord(AuditEvents.Logout));

        Assert.False(Directory.Exists(AuditRoot));
    }

    [Fact]
    public async Task WriteFailure_SetsTheBannerState_DoesNotThrow_AndClearsOnTheNextSuccess()
    {
        // The audit root is an existing FILE, so creating the month folder under it fails.
        Directory.CreateDirectory(Root);
        var blocker = Path.Combine(Root, "blocker");
        File.WriteAllText(blocker, "x");
        Settings = Settings with { Path = blocker };
        var log = NewLog();
        var changes = 0;
        log.StatusChanged += () => changes++;

        await log.RecordAsync(new AuditRecord(AuditEvents.Logout));   // must not throw

        Assert.True(log.WriteFailing);
        Assert.Equal(1, changes);
        Assert.Single(Logged);

        Settings = Settings with { Path = AuditRoot };
        await log.RecordAsync(new AuditRecord(AuditEvents.Logout));

        Assert.False(log.WriteFailing);
        Assert.Equal(2, changes);
        Assert.Single(Lines(OnlyFile()));
    }

    [Fact]
    public async Task WriteFailure_NeverBlocksTheCallersAction()
    {
        Directory.CreateDirectory(Root);
        var blocker = Path.Combine(Root, "blocker");
        File.WriteAllText(blocker, "x");
        Settings = Settings with { Path = blocker };
        var ran = false;

        var result = await NewLog().RunAsync(AuditEvents.PresentationCreated, null, () => { ran = true; return Task.FromResult(42); });

        Assert.True(ran);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task AppendOnly_WorksWhenWriteDataIsDenied()
    {
        if (!OperatingSystem.IsWindows()) return;

        var log = NewLog();
        await log.RecordAsync(new AuditRecord(AuditEvents.LoginSucceeded));
        var file = OnlyFile();

        // What an append-only ACL does to the author: AppendData stays, WriteData is gone.
        var me = WindowsIdentity.GetCurrent().User!;
        var info = new FileInfo(file);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.WriteData, AccessControlType.Deny));
        info.SetAccessControl(security);

        // Sanity: a normal write really is refused now, so the next assertion proves something.
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(file, "x"));
        Assert.Throws<UnauthorizedAccessException>(() => File.AppendAllText(file, "x"));

        await log.RecordAsync(new AuditRecord(AuditEvents.Logout));

        Assert.False(log.WriteFailing);
        Assert.Equal(new[] { "LoginSucceeded", "Logout" },
            Lines(file).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("event").GetString()).ToArray());
    }

    [Fact]
    public void Clean_And_Sanitize_Helpers()
    {
        Assert.Null(AuditLog.Clean("   "));
        Assert.Equal("a b", AuditLog.Clean("a\tb"));
        Assert.Equal("unknown", AuditLog.SanitizeForFileName("..."));
        Assert.Equal("a_b", AuditLog.SanitizeForFileName("a/b"));
    }
}

public sealed class AuditToggleTests : AuditTestBase
{
    [Fact]
    public async Task Disabling_WritesAuditDisabledBeforeLoggingStops_AndNothingAfter()
    {
        var log = NewLog();
        await log.RecordAsync(new AuditRecord(AuditEvents.LoginSucceeded));

        await AuditToggle.ApplyAsync(log, wasEnabled: true, willBeEnabled: false,
            apply: () => { Settings = Settings with { Enabled = false }; return Task.CompletedTask; });
        await log.RecordAsync(new AuditRecord(AuditEvents.PresentationCreated));   // after: must add nothing

        var events = Lines(OnlyFile()).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("event").GetString()).ToArray();
        Assert.Equal(new[] { "LoginSucceeded", "AuditDisabled" }, events);
    }

    [Fact]
    public async Task Enabling_WritesAuditEnabledAfterLoggingStarts()
    {
        Settings = Settings with { Enabled = false };
        var log = NewLog();

        await AuditToggle.ApplyAsync(log, wasEnabled: false, willBeEnabled: true,
            apply: () =>
            {
                Assert.Empty(AllFiles());   // nothing yet: the line must come AFTER the setting takes effect
                Settings = Settings with { Enabled = true };
                return Task.CompletedTask;
            });

        var events = Lines(OnlyFile()).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("event").GetString()).ToArray();
        Assert.Equal(new[] { "AuditEnabled" }, events);
    }

    [Fact]
    public async Task UnchangedState_WritesNeitherEvent()
    {
        var log = NewLog();

        await AuditToggle.ApplyAsync(log, true, true, () => Task.CompletedTask);
        await AuditToggle.ApplyAsync(log, false, false, () => Task.CompletedTask);

        Assert.Empty(AllFiles());
    }

    [Fact]
    public async Task FailedDisable_IsRecordedAsNotApplied_AndRethrown()
    {
        var log = NewLog();

        await Assert.ThrowsAsync<IOException>(() => AuditToggle.ApplyAsync(log, true, false, () => throw new IOException("disk")));

        var docs = Lines(OnlyFile()).Select(l => JsonDocument.Parse(l).RootElement).ToArray();
        Assert.Equal(2, docs.Length);
        Assert.Equal("Failure", docs[1].GetProperty("outcome").GetString());
        Assert.Contains("remains enabled", docs[1].GetProperty("detail").GetString());
    }
}

public sealed class AuditRunAsyncTests : AuditTestBase
{
    [Fact]
    public async Task Success_IsRecorded_WithTargetFromTheResult()
    {
        var result = await NewLog().RunAsync(AuditEvents.PresentationCreated, null,
            () => Task.FromResult("pres-9"), targetFrom: r => r, detail: "team=x");

        Assert.Equal("pres-9", result);
        using var doc = JsonDocument.Parse(Assert.Single(Lines(OnlyFile())));
        Assert.Equal("pres-9", doc.RootElement.GetProperty("target").GetString());
        Assert.Equal("Success", doc.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Exception_IsRecordedAsFailure_AndRethrownUnchanged()
    {
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            NewLog().RunAsync<int>(AuditEvents.UserDeleted, "u-1", () => throw new UnauthorizedAccessException("nope")));

        Assert.Equal("nope", ex.Message);
        using var doc = JsonDocument.Parse(Assert.Single(Lines(OnlyFile())));
        Assert.Equal("Failure", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Contains("UnauthorizedAccessException", doc.RootElement.GetProperty("detail").GetString());
        Assert.Equal("u-1", doc.RootElement.GetProperty("target").GetString());
    }

    [Fact]
    public async Task FalseResult_IsRecordedAsFailure()
    {
        var ok = await NewLog().RunAsync(AuditEvents.UserDeleted, "u-1", () => Task.FromResult(false),
            isSuccess: r => r, detailFrom: r => r ? null : "user not found");

        Assert.False(ok);
        using var doc = JsonDocument.Parse(Assert.Single(Lines(OnlyFile())));
        Assert.Equal("Failure", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("user not found", doc.RootElement.GetProperty("detail").GetString());
    }
}

public sealed class AuditSettingsTests
{
    [Fact]
    public void Defaults_AreOn_AndUnderDataPath()
    {
        var s = AuditSettingsResolver.Resolve(new EnvironmentSettings(), "D:/data", policyEnabled: null);

        Assert.True(s.Enabled);
        Assert.False(s.LockedByPolicy);
        Assert.Equal(Path.Combine("D:/data", "audit"), s.Path);
        Assert.Equal("D:/data", s.EnvironmentName);
    }

    [Fact]
    public void EnvironmentFile_CanDisable_AndSetsPathAndName()
    {
        var settings = new EnvironmentSettings { DisplayName = "Prod", AuditPath = "E:/audit", Audit = { Enabled = false } };

        var s = AuditSettingsResolver.Resolve(settings, "D:/data", null);

        Assert.False(s.Enabled);
        Assert.Equal("E:/audit", s.Path);
        Assert.Equal("Prod", s.EnvironmentName);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RegistryOverride_Wins_AndLocks(bool policy, bool environment)
    {
        var settings = new EnvironmentSettings { Audit = { Enabled = environment } };

        var s = AuditSettingsResolver.Resolve(settings, "D:/data", policy);

        Assert.Equal(policy, s.Enabled);
        Assert.True(s.LockedByPolicy);
    }

    [Fact]
    public void MissingEnvironmentSettings_KeepAuditOn()
    {
        Assert.True(AuditSettingsResolver.Resolve(null, "D:/data", null).Enabled);
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    public void AppConfiguration_ReadsTheRegistryValue(string raw, bool expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Audit:Enabled"] = raw }).Build();

        Assert.Equal(expected, new AppConfiguration(config).AuditEnabledOverride);
    }

    [Fact]
    public void AppConfiguration_AbsentOrGarbage_IsNoOverride()
    {
        Assert.Null(new AppConfiguration(new ConfigurationBuilder().Build()).AuditEnabledOverride);
        var garbage = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Audit:Enabled"] = "maybe" }).Build();
        Assert.Null(new AppConfiguration(garbage).AuditEnabledOverride);
    }

    [Fact]
    public void EnvironmentJson_RoundTripsAuditSettings_AndOldFilesDefaultToOn()
    {
        var written = System.Text.Json.JsonSerializer.Serialize(
            new EnvironmentSettings { AuditPath = "E:/audit", Audit = { Enabled = false } }, EnvironmentSettingsJson.Options);
        var back = EnvironmentSettingsResolver.Resolve(written, new EnvironmentSettings());
        Assert.Equal("E:/audit", back.Settings!.AuditPath);
        Assert.False(back.Settings.Audit.Enabled);

        var old = EnvironmentSettingsResolver.Resolve("{\"schemaVersion\":1}", new EnvironmentSettings());
        Assert.True(old.Settings!.Audit.Enabled);
        Assert.Null(old.Settings.AuditPath);
    }

    [Fact]
    public void RelativeAuditPath_FailsValidation_AndIsPreservedByMergeAndPolicy()
    {
        Assert.NotEmpty(EnvironmentSettingsResolver.Validate(new EnvironmentSettings { AuditPath = "audit" }));

        var form = new EnvironmentSettings { AuditPath = "E:/audit", Audit = { Enabled = false } };
        var merged = EnvironmentSettingsResolver.MergeForSave(form, null, new EnvironmentSettings(), Array.Empty<string>());
        Assert.Equal("E:/audit", merged.AuditPath);
        Assert.False(merged.Audit.Enabled);
        var (applied, _) = EnvironmentSettingsResolver.ApplyPolicy(
            merged, new PolicyEnvironment("p", "D:/data", null, null, null, null, null, null, null, null, false));
        Assert.Equal("E:/audit", applied.AuditPath);
        Assert.False(applied.Audit.Enabled);
    }

    [Fact]
    public void Diff_ListsFieldNamesOnly()
    {
        var before = new EnvironmentSettings { DisplayName = "A" };
        var after = new EnvironmentSettings { DisplayName = "B", AuditPath = "E:/audit", Audit = { Enabled = false } };

        var changed = EnvironmentSettingsDiff.ChangedFields(before, after);

        Assert.Equal(new[] { "displayName", "auditPath", "audit.enabled" }, changed);
        Assert.Empty(EnvironmentSettingsDiff.ChangedFields(after, after));
        Assert.Empty(EnvironmentSettingsDiff.ChangedFields(new EnvironmentSettings { SecurePath = "" }, new EnvironmentSettings()));
    }
}
