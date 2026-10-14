using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using NewsCentral.Configuration;

namespace NewsService.Services;

/// <summary>The two NewsViewer scheduled tasks NewsService maintains.</summary>
internal enum ViewerTaskKind { Launch, ContentArrived }

/// <summary>Inputs the task templates are specialised with.</summary>
internal sealed record TaskDefinitionInputs(string Company, string ExePath, string EventLogName, string EventSourceName);

/// <summary>A specialised task: its full name, the final XML text and the SHA-256 (hex) it is stamped with.</summary>
internal sealed record BuiltTaskDefinition(string TaskName, string Xml, string Hash);

/// <summary>
/// Turns the embedded task templates (deploy/scheduled-tasks/*.xml — the only task definitions)
/// into the XML registered with schtasks. It applies exactly the substitutions
/// scripts/Register-NewsViewerTasks.ps1 applies; no task XML is generated in code. Pure and
/// deterministic: identical inputs give byte-identical output.
/// </summary>
internal static class TaskDefinitionBuilder
{
    private const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const string HashMarkerPrefix = "[nc-hash:";

    // Same rule as the script: the values are embedded in an XPath query inside XML.
    private static readonly Regex ValidEventName = new(@"^[^'""<>&]+$", RegexOptions.Compiled);

    // The stamp sits at the end of the Description element. Matched on the raw text schtasks returns
    // (it may re-escape the rest of the Description), so no XML parsing of the queried document.
    private static readonly Regex HashStamp = new(
        @"<Description>[^<]*?\[nc-hash:([0-9a-fA-F]{64})\]\s*</Description>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string TemplateResourceName(ViewerTaskKind kind) => kind switch
    {
        ViewerTaskKind.Launch => "NewsService.TaskDefinitions.NewsViewer-Launch.xml",
        ViewerTaskKind.ContentArrived => "NewsService.TaskDefinitions.NewsViewer-ContentArrived.xml",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string TaskName(ViewerTaskKind kind, string company) => kind switch
    {
        ViewerTaskKind.Launch => $@"\{company}\{SolutionConstants.SolutionName}\NewsViewer Launch",
        ViewerTaskKind.ContentArrived => $@"\{company}\{SolutionConstants.SolutionName}\NewsViewer Content Arrived",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>The legacy Workstation Unlock task and the old dev-box tasks (same names as the script).</summary>
    public static IReadOnlyList<string> LegacyTaskNames(string company) =>
    [
        $@"\{company}\{SolutionConstants.SolutionName}\NewsViewer",
        $@"\{SolutionConstants.SolutionName}\NewsViewer Launch",
        $@"\{SolutionConstants.SolutionName}\NewsViewer Content Arrived",
    ];

    public static bool IsValidEventName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && ValidEventName.IsMatch(value);

    /// <summary>The exact bytes of the embedded template file (BOM included) - what the script generator embeds.</summary>
    public static byte[] LoadTemplateBytes(ViewerTaskKind kind)
    {
        using var stream = OpenTemplate(kind);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static Stream OpenTemplate(ViewerTaskKind kind)
    {
        var name = TemplateResourceName(kind);
        return typeof(TaskDefinitionBuilder).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded task definition '{name}' not found.");
    }

    /// <summary>Loads a template from the NewsService assembly's embedded resources.</summary>
    public static string LoadTemplate(ViewerTaskKind kind)
    {
        using var stream = OpenTemplate(kind);
        // The templates are UTF-16 LE with BOM; XmlDocument honours the BOM / declaration.
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(stream);
        return doc.OuterXml;
    }

    /// <exception cref="ArgumentException">Inputs the task cannot be built from (invalid event log/source for Content Arrived).</exception>
    public static BuiltTaskDefinition Build(ViewerTaskKind kind, string templateXml, TaskDefinitionInputs inputs)
    {
        if (string.IsNullOrWhiteSpace(inputs.Company)) throw new ArgumentException("Company is empty.", nameof(inputs));
        if (string.IsNullOrWhiteSpace(inputs.ExePath)) throw new ArgumentException("ExePath is empty.", nameof(inputs));
        if (kind == ViewerTaskKind.ContentArrived
            && (!IsValidEventName(inputs.EventLogName) || !IsValidEventName(inputs.EventSourceName)))
        {
            throw new ArgumentException(
                "Event log name and source must be non-empty and must not contain quotes, <, > or &.", nameof(inputs));
        }

        var taskName = TaskName(kind, inputs.Company);
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(templateXml);

        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("t", TaskNamespace);

        Node(doc, ns, "//t:RegistrationInfo/t:URI").InnerText = taskName;
        Node(doc, ns, "//t:Exec/t:Command").InnerText = "\"" + inputs.ExePath + "\"";
        Node(doc, ns, "//t:Exec/t:WorkingDirectory").InnerText = Path.GetDirectoryName(inputs.ExePath) ?? string.Empty;

        if (kind == ViewerTaskKind.ContentArrived)
        {
            Node(doc, ns, "//t:EventTrigger/t:Subscription").InnerText =
                $"<QueryList><Query Id=\"0\" Path=\"{inputs.EventLogName}\"><Select Path=\"{inputs.EventLogName}\">" +
                $"*[System[Provider[@Name='{inputs.EventSourceName}'] and EventID={ServiceEventIds.ContentChanged}]]" +
                "</Select></Query></QueryList>";
        }

        // The hash covers the final XML with the stamp absent; the stamp is then appended to the Description.
        // Command and WorkingDirectory enter the hash in their normalised form (TaskPathNormalizer), so a
        // %ProgramFiles% token and the literal path it expands to hash equally. The registered XML keeps the
        // text as written. The generated script does exactly the same.
        var commandNode = Node(doc, ns, "//t:Exec/t:Command");
        var workDirNode = Node(doc, ns, "//t:Exec/t:WorkingDirectory");
        var writtenCommand = commandNode.InnerText;
        var writtenWorkDir = workDirNode.InnerText;
        commandNode.InnerText = TaskPathNormalizer.Normalize(writtenCommand);
        workDirNode.InnerText = TaskPathNormalizer.Normalize(writtenWorkDir);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(doc))));
        commandNode.InnerText = writtenCommand;
        workDirNode.InnerText = writtenWorkDir;
        var description = Node(doc, ns, "//t:RegistrationInfo/t:Description");
        description.InnerText = description.InnerText + " " + HashMarkerPrefix + hash + "]";

        return new BuiltTaskDefinition(taskName, Serialize(doc), hash);
    }

    /// <summary>The <c>nc-hash</c> of a queried task XML; null when missing or malformed (= mismatch).</summary>
    public static string? ExtractHash(string? taskXml)
    {
        if (string.IsNullOrEmpty(taskXml)) return null;
        var match = HashStamp.Match(taskXml);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static XmlNode Node(XmlDocument doc, XmlNamespaceManager ns, string xpath) =>
        doc.SelectSingleNode(xpath, ns) ?? throw new InvalidOperationException($"Task template has no node '{xpath}'.");

    private static string Serialize(XmlDocument doc)
    {
        using var sw = new StringWriter();
        using (var writer = XmlWriter.Create(sw, new XmlWriterSettings { Indent = false }))
            doc.Save(writer);
        return sw.ToString();
    }
}
