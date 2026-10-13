using System.Text.RegularExpressions;
using System.Xml;
using NewsCentral.Configuration;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// The builder runs against the REAL embedded task definitions (deploy/scheduled-tasks), so a template
/// edit that breaks a substitution or an invariant (principal, parallelism, triggers) fails here.
/// </summary>
public sealed class TaskDefinitionBuilderTests
{
    private const string Exe = @"C:\Program Files\Contoso\NewsCentral\NewsViewer\NewsViewer.exe";
    private const string TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static TaskDefinitionInputs Inputs(string company = "Contoso", string exe = Exe,
        string log = "Application", string source = "NewsService") => new(company, exe, log, source);

    private static BuiltTaskDefinition Build(ViewerTaskKind kind, TaskDefinitionInputs? inputs = null) =>
        TaskDefinitionBuilder.Build(kind, TaskDefinitionBuilder.LoadTemplate(kind), inputs ?? Inputs());

    private static (XmlDocument Doc, XmlNamespaceManager Ns) Parse(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("t", TaskNs);
        return (doc, ns);
    }

    private static string Text(string xml, string xpath)
    {
        var (doc, ns) = Parse(xml);
        return doc.SelectSingleNode(xpath, ns)!.InnerText;
    }

    [Fact]
    public void Launch_SubstitutesUriCommandAndWorkingDirectory()
    {
        var built = Build(ViewerTaskKind.Launch);

        Assert.Equal(@"\Contoso\NewsCentral\NewsViewer Launch", built.TaskName);
        Assert.Equal(built.TaskName, Text(built.Xml, "//t:RegistrationInfo/t:URI"));
        Assert.Equal("\"" + Exe + "\"", Text(built.Xml, "//t:Exec/t:Command"));
        Assert.Equal(@"C:\Program Files\Contoso\NewsCentral\NewsViewer", Text(built.Xml, "//t:Exec/t:WorkingDirectory"));
    }

    [Fact]
    public void Launch_HasLogonUnlockAndConnectTriggers_AndTheUnchangedPrincipalAndSettings()
    {
        var built = Build(ViewerTaskKind.Launch);
        var (doc, ns) = Parse(built.Xml);

        Assert.Equal("PT15S", doc.SelectSingleNode("//t:Triggers/t:LogonTrigger/t:Delay", ns)!.InnerText);
        Assert.Null(doc.SelectSingleNode("//t:Triggers/t:LogonTrigger/t:UserId", ns));   // fires for any user
        Assert.Equal("LogonTrigger", doc.SelectSingleNode("//t:Triggers/*[1]", ns)!.LocalName);

        var states = doc.SelectNodes("//t:SessionStateChangeTrigger/t:StateChange", ns)!
            .Cast<XmlNode>().Select(n => n.InnerText).ToArray();
        Assert.Equal(new[] { "SessionUnlock", "RemoteConnect", "ConsoleConnect" }, states);

        Assert.Equal("S-1-5-32-545", Text(built.Xml, "//t:Principal/t:GroupId"));
        Assert.Equal("LeastPrivilege", Text(built.Xml, "//t:Principal/t:RunLevel"));
        Assert.Equal("Parallel", Text(built.Xml, "//t:Settings/t:MultipleInstancesPolicy"));
        Assert.Equal("PT0S", Text(built.Xml, "//t:Settings/t:ExecutionTimeLimit"));
        Assert.Equal("false", Text(built.Xml, "//t:Settings/t:StartWhenAvailable"));
    }

    [Fact]
    public void ContentArrived_SubstitutesSubscriptionFromConfiguredLogAndSource_AndPassesTheTriggerArgument()
    {
        var built = Build(ViewerTaskKind.ContentArrived, Inputs(log: "NewsLog", source: "NewsSrc"));

        Assert.Equal(@"\Contoso\NewsCentral\NewsViewer Content Arrived", built.TaskName);
        var subscription = Text(built.Xml, "//t:EventTrigger/t:Subscription");
        Assert.Contains("Path=\"NewsLog\"", subscription);
        Assert.Contains("Provider[@Name='NewsSrc']", subscription);
        Assert.Contains($"EventID={ServiceEventIds.ContentChanged}", subscription);
        Assert.Equal("--trigger content", Text(built.Xml, "//t:Exec/t:Arguments"));
        Assert.Equal("S-1-5-32-545", Text(built.Xml, "//t:Principal/t:GroupId"));
        Assert.Equal("Parallel", Text(built.Xml, "//t:Settings/t:MultipleInstancesPolicy"));
        Assert.Equal("PT0S", Text(built.Xml, "//t:Settings/t:ExecutionTimeLimit"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Description_CarriesTheHashStamp_AndOutputIsDeterministic(bool launch)
    {
        var kind = launch ? ViewerTaskKind.Launch : ViewerTaskKind.ContentArrived;
        var a = Build(kind);
        var b = Build(kind);

        Assert.Equal(a.Xml, b.Xml);
        Assert.Equal(a.Hash, b.Hash);
        Assert.Matches("^[0-9a-f]{64}$", a.Hash);
        Assert.EndsWith($" [nc-hash:{a.Hash}]", Text(a.Xml, "//t:RegistrationInfo/t:Description"));
        Assert.Equal(a.Hash, TaskDefinitionBuilder.ExtractHash(a.Xml));
        Assert.Single(Regex.Matches(a.Xml, Regex.Escape("[nc-hash:")));
    }

    [Fact]
    public void Hash_ChangesWithExePathCompanyAndEventSource()
    {
        var baseline = Build(ViewerTaskKind.ContentArrived).Hash;

        Assert.NotEqual(baseline, Build(ViewerTaskKind.ContentArrived, Inputs(exe: @"D:\other\NewsViewer.exe")).Hash);
        Assert.NotEqual(baseline, Build(ViewerTaskKind.ContentArrived, Inputs(company: "Fabrikam")).Hash);
        Assert.NotEqual(baseline, Build(ViewerTaskKind.ContentArrived, Inputs(source: "OtherSource")).Hash);
        Assert.NotEqual(baseline, Build(ViewerTaskKind.ContentArrived, Inputs(log: "OtherLog")).Hash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<Task><RegistrationInfo><Description>x</Description></RegistrationInfo></Task>")]
    [InlineData("<Description>no stamp here</Description>")]
    [InlineData("<Description>x [nc-hash:abc]</Description>")]
    [InlineData("<Description>x [nc-hash:zz00000000000000000000000000000000000000000000000000000000000000]</Description>")]
    [InlineData("<Description>[nc-hash:0000000000000000000000000000000000000000000000000000000000000000] trailing</Description>")]
    public void ExtractHash_MissingOrMalformed_IsNull(string? xml)
    {
        Assert.Null(TaskDefinitionBuilder.ExtractHash(xml));
    }

    [Fact]
    public void ExtractHash_Present_ReturnsTheValue_EvenWhenSchtasksReEscapesTheText()
    {
        var hash = new string('a', 64);
        var xml = $"<Task><RegistrationInfo><Description>Launches &apos;x&apos; [nc-hash:{hash}]</Description></RegistrationInfo></Task>";
        Assert.Equal(hash, TaskDefinitionBuilder.ExtractHash(xml));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("App\"lication")]
    [InlineData("App'lication")]
    [InlineData("App<lication")]
    [InlineData("App>lication")]
    [InlineData("App&lication")]
    public void InvalidEventLogOrSource_IsRejected_ForContentArrived(string bad)
    {
        Assert.False(TaskDefinitionBuilder.IsValidEventName(bad));
        var template = TaskDefinitionBuilder.LoadTemplate(ViewerTaskKind.ContentArrived);
        Assert.Throws<ArgumentException>(() =>
            TaskDefinitionBuilder.Build(ViewerTaskKind.ContentArrived, template, Inputs(log: bad)));
        Assert.Throws<ArgumentException>(() =>
            TaskDefinitionBuilder.Build(ViewerTaskKind.ContentArrived, template, Inputs(source: bad)));
    }

    [Fact]
    public void InvalidEventLogOrSource_DoesNotAffectTheLaunchTask()
    {
        var built = Build(ViewerTaskKind.Launch, Inputs(log: "bad\"", source: ""));
        Assert.Equal(@"\Contoso\NewsCentral\NewsViewer Launch", built.TaskName);
    }

    [Fact]
    public void LegacyTaskNames_AreTheScriptsNames()
    {
        Assert.Equal(
            new[] { @"\Contoso\NewsCentral\NewsViewer", @"\NewsCentral\NewsViewer Launch", @"\NewsCentral\NewsViewer Content Arrived" },
            TaskDefinitionBuilder.LegacyTaskNames("Contoso"));
    }
}
