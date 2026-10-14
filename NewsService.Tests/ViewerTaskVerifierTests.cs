using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Configuration;
using NewsService.Configuration;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

public sealed class ViewerTaskVerifierTests
{
    private const string Exe = @"C:\Install\NewsViewer\NewsViewer.exe";
    private static readonly string Company = SolutionConstants.Company;
    private static string Launch => TaskDefinitionBuilder.TaskName(ViewerTaskKind.Launch, Company);
    private static string Content => TaskDefinitionBuilder.TaskName(ViewerTaskKind.ContentArrived, Company);

    private sealed class FakeScheduler : ITaskQueryClient
    {
        public Dictionary<string, string> Tasks { get; } = new();
        public Task<TaskQueryResult> QueryAsync(string taskName, CancellationToken ct) =>
            Task.FromResult(Tasks.TryGetValue(taskName, out var xml) ? new TaskQueryResult(true, xml) : new TaskQueryResult(false, null));
    }

    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class Rig
    {
        public FakeScheduler Scheduler { get; } = new();
        public CapturingLogger<ViewerTaskVerifier> Log { get; } = new();
        public bool ExeExists { get; set; } = true;
        public ServiceConfiguration Config { get; } = new() { ViewerTasks = { NewsViewerPath = Exe } };
        public Dictionary<string, string?> Settings { get; } = new();

        public ViewerTaskVerifier Sut() => new(
            Config, new ConfigurationBuilder().AddInMemoryCollection(Settings).Build(),
            Scheduler, Log, baseDirectory: @"C:\Install\NewsService", fileExists: _ => ExeExists);

        /// <summary>What the registration script writes: the built definition, stamp included.</summary>
        public void RegisterBoth(string exe = Exe)
        {
            var inputs = new TaskDefinitionInputs(Company, exe, "Application", "NewsService");
            foreach (var kind in new[] { ViewerTaskKind.Launch, ViewerTaskKind.ContentArrived })
            {
                var built = TaskDefinitionBuilder.Build(kind, TaskDefinitionBuilder.LoadTemplate(kind), inputs);
                Scheduler.Tasks[built.TaskName] = built.Xml;
            }
        }
    }

    private static string WithoutStamp(string xml) => Regex.Replace(xml, @"\s*\[nc-hash:[0-9a-f]{64}\]", "");

    [Fact]
    public async Task TasksWithAMatchingStamp_VerifyClean()
    {
        var rig = new Rig();
        rig.RegisterBoth();

        Assert.Empty((await rig.Sut().VerifyAsync(default))!);
    }

    [Fact]
    public async Task TasksWithoutAStamp_FallBackToFieldComparison_AndPassWhenTheFieldsMatch()
    {
        var rig = new Rig();
        rig.RegisterBoth();
        foreach (var name in rig.Scheduler.Tasks.Keys.ToList())
            rig.Scheduler.Tasks[name] = WithoutStamp(rig.Scheduler.Tasks[name]);
        Assert.All(rig.Scheduler.Tasks.Values, x => Assert.Null(TaskDefinitionBuilder.ExtractHash(x)));

        Assert.Empty((await rig.Sut().VerifyAsync(default))!);
    }

    [Fact]
    public async Task TaskWithoutAStamp_AndAWrongCommand_ReportsFieldDrift()
    {
        var rig = new Rig();
        rig.RegisterBoth(@"D:\Elsewhere\NewsViewer.exe");
        foreach (var name in rig.Scheduler.Tasks.Keys.ToList())
            rig.Scheduler.Tasks[name] = WithoutStamp(rig.Scheduler.Tasks[name]);

        var drift = (await rig.Sut().VerifyAsync(default))!;

        Assert.Contains(drift, d => d.Contains("wrong command") && d.Contains(@"D:\Elsewhere\NewsViewer.exe"));
        Assert.DoesNotContain(drift, d => d.Contains("nc-hash mismatch"));
    }

    [Fact]
    public async Task TaskWithADifferentStamp_IsDrift_EvenWhenEveryComparedFieldMatches()
    {
        var rig = new Rig();
        rig.RegisterBoth();
        // A change the field comparison cannot see (principal, time limit, ...) shows up only as a different stamp.
        var xml = rig.Scheduler.Tasks[Launch];
        var stamp = TaskDefinitionBuilder.ExtractHash(xml)!;
        rig.Scheduler.Tasks[Launch] = xml.Replace(stamp, new string('0', 64));

        var drift = (await rig.Sut().VerifyAsync(default))!;

        var single = Assert.Single(drift);
        Assert.Contains("nc-hash mismatch", single);
        Assert.Contains(Launch, single);
    }

    [Fact]
    public async Task MissingTask_AndMissingExe_AreReported()
    {
        var rig = new Rig { ExeExists = false };
        rig.RegisterBoth();
        rig.Scheduler.Tasks.Remove(Content);

        var drift = (await rig.Sut().VerifyAsync(default))!;

        Assert.Contains(drift, d => d.Contains(Content) && d.Contains("missing"));
        Assert.Contains(drift, d => d.Contains("executable not found") && d.Contains(Exe));
    }

    [Fact]
    public async Task DefaultExePath_IsTheSiblingNewsViewerFolder()
    {
        var rig = new Rig();
        rig.Config.ViewerTasks.NewsViewerPath = "";
        string? probed = null;
        var sut = new ViewerTaskVerifier(rig.Config, new ConfigurationBuilder().Build(), rig.Scheduler, rig.Log,
            baseDirectory: @"C:\Install\NewsService", fileExists: p => { probed = p; return true; });

        await sut.VerifyAsync(default);

        Assert.Equal(@"C:\Install\NewsViewer\NewsViewer.exe", probed);
    }

    [Fact]
    public void ObsoleteManageKey_ProducesOneWarningNamingTheReplacement()
    {
        var rig = new Rig();
        rig.Settings["ViewerTasks:Manage"] = "False";

        rig.Sut().WarnIfObsoleteManageKey();

        var warning = Assert.Single(rig.Log.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("ViewerTasks:Manage", warning.Message);
        Assert.Contains("ignored", warning.Message);
        Assert.Contains("ViewerTasks:Mode", warning.Message);
    }

    [Fact]
    public void NoObsoleteManageKey_NoWarning()
    {
        var rig = new Rig();
        rig.Sut().WarnIfObsoleteManageKey();
        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public void ManageValue_DoesNotInfluenceTheMode()
    {
        var rig = new Rig();
        rig.Settings["ViewerTasks:Manage"] = "False";
        Assert.Equal(ViewerTaskMode.VerifyOnly, rig.Sut().ResolveMode().Mode);
    }
}

public sealed class EventSourceGuardTests
{
    private sealed class FakeApi(bool? exists, string? log = null, bool throwOnCreate = false) : EventSourceGuard.IEventSourceApi
    {
        public List<(string Source, string Log)> Created { get; } = new();
        public bool? Exists(string source) => exists;
        public string? LogOf(string source) => log;
        public void Create(string source, string logName)
        {
            if (throwOnCreate) throw new UnauthorizedAccessException("denied");
            Created.Add((source, logName));
        }
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void MissingSource_IsCreatedExplicitly_InTheConfiguredLog()
    {
        var api = new FakeApi(exists: false);
        var log = new ViewerTaskVerifierTests.CapturingLogger<EventSourceGuardTests>();

        EventSourceGuard.EnsureSource(Config(("Logging:EventLog:SourceName", "Src"), ("Logging:EventLog:LogName", "NewsLog")), log, api);

        Assert.Equal([("Src", "NewsLog")], api.Created);
    }

    [Fact]
    public void Defaults_AreNewsServiceInApplication()
    {
        var api = new FakeApi(exists: false);
        EventSourceGuard.EnsureSource(Config(), new ViewerTaskVerifierTests.CapturingLogger<EventSourceGuardTests>(), api);
        Assert.Equal([("NewsService", "Application")], api.Created);
    }

    [Fact]
    public void ExistingSource_IsLeftAlone()
    {
        var api = new FakeApi(exists: true, log: "Application");
        EventSourceGuard.EnsureSource(Config(), new ViewerTaskVerifierTests.CapturingLogger<EventSourceGuardTests>(), api);
        Assert.Empty(api.Created);
    }

    [Fact]
    public void ExistingSourceInAnotherLog_WarnsAndDoesNotCreate()
    {
        var api = new FakeApi(exists: true, log: "Other");
        var log = new ViewerTaskVerifierTests.CapturingLogger<EventSourceGuardTests>();

        EventSourceGuard.EnsureSource(Config(), log, api);

        Assert.Empty(api.Created);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Other"));
    }

    [Fact]
    public void RegistrationDisabled_NeverTouchesTheSource()
    {
        var api = new FakeApi(exists: false);
        EventSourceGuard.EnsureSource(Config(("ViewerTasks:RegisterEventSource", "false")),
            new ViewerTaskVerifierTests.CapturingLogger<EventSourceGuardTests>(), api);
        Assert.Empty(api.Created);
    }

    [Fact]
    public void CreateFailure_IsAWarning_NeverAnException()
    {
        var api = new FakeApi(exists: false, throwOnCreate: true);
        var log = new ViewerTaskVerifierTests.CapturingLogger<EventSourceGuardTests>();

        EventSourceGuard.EnsureSource(Config(), log, api);

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Could not create"));
    }
}
