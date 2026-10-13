using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Configuration;
using NewsService.Configuration;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

public sealed class ViewerTaskProvisionerTests
{
    private const string Exe = @"C:\Install\NewsViewer\NewsViewer.exe";
    private static readonly string Company = SolutionConstants.Company;
    private static string Launch => TaskDefinitionBuilder.TaskName(ViewerTaskKind.Launch, Company);
    private static string Content => TaskDefinitionBuilder.TaskName(ViewerTaskKind.ContentArrived, Company);
    private static string LegacyUnlock => TaskDefinitionBuilder.LegacyTaskNames(Company)[0];

    private sealed class FakeScheduler : ITaskSchedulerClient
    {
        public Dictionary<string, string> Tasks { get; } = new();
        public List<string> Calls { get; } = new();
        public HashSet<string> FailCreate { get; } = new();
        public HashSet<string> FailDelete { get; } = new();
        public string FailOutput { get; set; } = "ERROR: Access is denied.";

        public Task<TaskQueryResult> QueryAsync(string taskName, CancellationToken ct)
        {
            Calls.Add("query " + taskName);
            return Task.FromResult(Tasks.TryGetValue(taskName, out var xml)
                ? new TaskQueryResult(true, xml) : new TaskQueryResult(false, null));
        }

        public Task<SchtasksResult> CreateAsync(string taskName, string xml, CancellationToken ct)
        {
            Calls.Add("create " + taskName);
            if (FailCreate.Contains(taskName)) return Task.FromResult(new SchtasksResult(1, FailOutput));
            Tasks[taskName] = xml;
            return Task.FromResult(new SchtasksResult(0, "SUCCESS"));
        }

        public Task<SchtasksResult> DeleteAsync(string taskName, CancellationToken ct)
        {
            Calls.Add("delete " + taskName);
            if (FailDelete.Contains(taskName)) return Task.FromResult(new SchtasksResult(1, FailOutput));
            Tasks.Remove(taskName);
            return Task.FromResult(new SchtasksResult(0, "SUCCESS"));
        }

        public int Count(string verb) => Calls.Count(c => c.StartsWith(verb + " "));
    }

    private sealed class FakeRunKey : IRunKeyAccessor
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = new();
        public string? GetValue(string name) { Calls.Add("get " + name); return Values.GetValueOrDefault(name); }
        public bool DeleteValue(string name) { Calls.Add("delete " + name); return Values.Remove(name); }
    }

    private sealed class FakeIdentity(bool system = true, bool elevated = true) : IProcessIdentity
    {
        public bool IsLocalSystem { get; } = system;
        public bool IsElevated { get; } = elevated;
    }

    private sealed class CapturingLogger : ILogger<ViewerTaskProvisioner>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
        public bool Has(LogLevel level) => Entries.Any(e => e.Level == level);
    }

    private sealed class Rig
    {
        public FakeScheduler Scheduler { get; } = new();
        public FakeRunKey RunKey { get; } = new();
        public CapturingLogger Log { get; } = new();
        public bool ExeExists { get; set; } = true;
        public ServiceConfiguration Config { get; } = new() { ViewerTasks = { NewsViewerPath = Exe } };
        public FakeIdentity Identity { get; set; } = new();
        public Dictionary<string, string?> Settings { get; } = new();

        public ViewerTaskProvisioner Sut() => new(
            Config, new ConfigurationBuilder().AddInMemoryCollection(Settings).Build(),
            Scheduler, RunKey, Identity, Log, baseDirectory: @"C:\Install\NewsService", fileExists: _ => ExeExists);
    }

    [Fact]
    public async Task MissingTasks_AreCreated_AndReportedAsRegistered()
    {
        var rig = new Rig();

        Assert.True(await rig.Sut().RunPassAsync(default));

        Assert.True(rig.Scheduler.Tasks.ContainsKey(Launch));
        Assert.True(rig.Scheduler.Tasks.ContainsKey(Content));
        Assert.Contains(rig.Log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("registered"));
    }

    [Fact]
    public async Task SecondPass_IsUnchanged_NoCreate()
    {
        var rig = new Rig();
        await rig.Sut().RunPassAsync(default);
        rig.Scheduler.Calls.Clear();
        rig.Log.Entries.Clear();

        Assert.True(await rig.Sut().RunPassAsync(default));

        Assert.Equal(0, rig.Scheduler.Count("create"));
        Assert.Contains(rig.Log.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("unchanged"));
        Assert.DoesNotContain(rig.Log.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public async Task HashMismatch_IsRecreated_AndReportedAsUpdated()
    {
        var rig = new Rig();
        await rig.Sut().RunPassAsync(default);
        rig.Config.ViewerTasks.NewsViewerPath = @"D:\Elsewhere\NewsViewer.exe";
        rig.Scheduler.Calls.Clear();
        rig.Log.Entries.Clear();

        Assert.True(await rig.Sut().RunPassAsync(default));

        Assert.Equal(2, rig.Scheduler.Count("create"));
        Assert.Contains(rig.Log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("updated"));
        Assert.Contains(@"D:\Elsewhere\NewsViewer.exe", rig.Scheduler.Tasks[Launch]);
    }

    [Fact]
    public async Task ExistingTaskWithoutStamp_IsReplacedOnce()
    {
        var rig = new Rig();
        rig.Scheduler.Tasks[Launch] = "<Task><RegistrationInfo><Description>by script</Description></RegistrationInfo></Task>";

        Assert.True(await rig.Sut().RunPassAsync(default));

        Assert.Contains("nc-hash", rig.Scheduler.Tasks[Launch]);
    }

    [Fact]
    public async Task ManageOff_MakesNoSchedulerAndNoRegistryCalls()
    {
        var rig = new Rig();
        rig.Config.ViewerTasks.Manage = false;
        rig.RunKey.Values["NewsViewer"] = "\"" + Exe + "\"";

        Assert.True(await rig.Sut().RunPassAsync(default));

        Assert.Empty(rig.Scheduler.Calls);
        Assert.Empty(rig.RunKey.Calls);
        Assert.Contains(rig.Log.Entries, e => e.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task MissingExe_RegistersNothing_DeletesNothing_KeepsTheRunValue_AndAsksForRetry()
    {
        var rig = new Rig { ExeExists = false };
        rig.RunKey.Values["NewsViewer"] = "\"" + Exe + "\"";
        rig.Scheduler.Tasks[LegacyUnlock] = "<Task/>";

        Assert.False(await rig.Sut().RunPassAsync(default));

        Assert.Empty(rig.Scheduler.Calls);
        Assert.Empty(rig.RunKey.Calls);
        Assert.True(rig.RunKey.Values.ContainsKey("NewsViewer"));
        Assert.Contains(rig.Log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(Exe));
    }

    [Fact]
    public async Task DefaultExePath_IsTheSiblingNewsViewerFolder()
    {
        var rig = new Rig();
        rig.Config.ViewerTasks.NewsViewerPath = "";
        string? probed = null;
        var sut = new ViewerTaskProvisioner(rig.Config, new ConfigurationBuilder().Build(), rig.Scheduler,
            rig.RunKey, rig.Identity, rig.Log, baseDirectory: @"C:\Install\NewsService",
            fileExists: p => { probed = p; return true; });

        await sut.RunPassAsync(default);

        Assert.Equal(@"C:\Install\NewsViewer\NewsViewer.exe", probed);
    }

    [Fact]
    public async Task OneCreateFails_NoLegacyCleanup_AndNoRunRemoval()
    {
        var rig = new Rig();
        rig.Scheduler.FailCreate.Add(Content);
        rig.Scheduler.Tasks[LegacyUnlock] = "<Task/>";
        rig.RunKey.Values["NewsViewer"] = "\"" + Exe + "\"";

        Assert.False(await rig.Sut().RunPassAsync(default));

        Assert.Equal(0, rig.Scheduler.Count("delete"));
        Assert.Empty(rig.RunKey.Calls);
        Assert.True(rig.RunKey.Values.ContainsKey("NewsViewer"));
        Assert.True(rig.Scheduler.Tasks.ContainsKey(LegacyUnlock));
    }

    [Fact]
    public async Task BothOk_LegacyAndOldDevTasksAreDeleted_WhenPresent()
    {
        var rig = new Rig();
        var legacy = TaskDefinitionBuilder.LegacyTaskNames(Company);
        rig.Scheduler.Tasks[legacy[0]] = "<Task/>";
        rig.Scheduler.Tasks[legacy[2]] = "<Task/>";

        Assert.True(await rig.Sut().RunPassAsync(default));

        Assert.Equal(2, rig.Scheduler.Count("delete"));   // the absent one is not deleted
        Assert.All(legacy, n => Assert.False(rig.Scheduler.Tasks.ContainsKey(n)));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Contoso\\NewsCentral\\NewsViewer\\NewsViewer.exe\"", true)]
    [InlineData("c:\\x\\NEWSVIEWER.EXE", true)]
    [InlineData("C:\\other\\tool.exe", false)]
    public async Task RunValue_IsRemovedOnlyWhenItPointsAtNewsViewerExe(string data, bool removed)
    {
        var rig = new Rig();
        rig.RunKey.Values["NewsViewer"] = data;

        Assert.True(await rig.Sut().RunPassAsync(default));

        Assert.Equal(!removed, rig.RunKey.Values.ContainsKey("NewsViewer"));
    }

    [Fact]
    public async Task OtherRunValues_AreNeverTouched()
    {
        var rig = new Rig();
        rig.RunKey.Values["SomethingElse"] = "C:\\NewsViewer.exe";
        rig.RunKey.Values["NewsViewer"] = "C:\\NewsViewer.exe";

        await rig.Sut().RunPassAsync(default);

        Assert.True(rig.RunKey.Values.ContainsKey("SomethingElse"));
        Assert.DoesNotContain(rig.RunKey.Calls, c => c.Contains("SomethingElse"));
    }

    [Fact]
    public async Task AccessDenied_WhenNotLocalSystemAndNotElevated_IsAWarning_NotAnError()
    {
        var rig = new Rig { Identity = new FakeIdentity(system: false, elevated: false) };
        rig.Scheduler.FailCreate.Add(Launch);

        Assert.False(await rig.Sut().RunPassAsync(default));

        Assert.True(rig.Log.Has(LogLevel.Warning));
        Assert.False(rig.Log.Has(LogLevel.Error));
    }

    [Fact]
    public async Task Failure_AsLocalSystem_IsAnError()
    {
        var rig = new Rig();   // LocalSystem
        rig.Scheduler.FailCreate.Add(Launch);

        Assert.False(await rig.Sut().RunPassAsync(default));

        Assert.Contains(rig.Log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(Launch) && e.Message.Contains("exit code 1"));
    }

    [Fact]
    public async Task InvalidEventSource_SkipsOnlyContentArrived_AndBlocksCleanup()
    {
        var rig = new Rig();
        rig.Settings["Logging:EventLog:SourceName"] = "Bad'Source";
        rig.RunKey.Values["NewsViewer"] = "C:\\NewsViewer.exe";

        Assert.False(await rig.Sut().RunPassAsync(default));

        Assert.True(rig.Scheduler.Tasks.ContainsKey(Launch));
        Assert.False(rig.Scheduler.Tasks.ContainsKey(Content));
        Assert.True(rig.RunKey.Values.ContainsKey("NewsViewer"));
        Assert.True(rig.Log.Has(LogLevel.Error));
    }

    [Fact]
    public async Task ConfiguredLogAndSource_FlowIntoTheContentArrivedSubscription()
    {
        var rig = new Rig();
        rig.Settings["Logging:EventLog:LogName"] = "NewsLog";
        rig.Settings["Logging:EventLog:SourceName"] = "NewsSrc";

        await rig.Sut().RunPassAsync(default);

        Assert.Contains("NewsLog", rig.Scheduler.Tasks[Content]);
        Assert.Contains("NewsSrc", rig.Scheduler.Tasks[Content]);
    }
}
