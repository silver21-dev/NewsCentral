using System.Xml;

namespace NewsService.Services;

/// <summary>
/// Semantic comparison of a registered task against the definition NewsService would register.
/// Used by VerifyOnly when the nc-hash stamp does not match (tasks registered by the generated
/// script have none): the command, working directory, event subscription and trigger set.
/// </summary>
internal static class TaskDriftComparer
{
    private const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static IReadOnlyList<string> Compare(string taskName, string expectedXml, string? actualXml)
    {
        if (string.IsNullOrWhiteSpace(actualXml))
            return [$"task '{taskName}': the registered definition could not be read"];

        XmlDocument expected, actual;
        try
        {
            expected = Load(expectedXml);
            actual = Load(actualXml);
        }
        catch (XmlException ex)
        {
            return [$"task '{taskName}': the registered definition could not be parsed ({ex.Message})"];
        }

        var drift = new List<string>();
        Check(drift, taskName, "command", expected, actual, "//t:Exec/t:Command", unquote: true, expand: true);
        Check(drift, taskName, "working directory", expected, actual, "//t:Exec/t:WorkingDirectory", unquote: false, expand: true);
        Check(drift, taskName, "event subscription", expected, actual, "//t:EventTrigger/t:Subscription", unquote: false, expand: false);

        var expectedTriggers = Triggers(expected);
        var actualTriggers = Triggers(actual);
        if (!expectedTriggers.SequenceEqual(actualTriggers))
            drift.Add($"task '{taskName}': wrong trigger set - expected [{string.Join(", ", expectedTriggers)}], found [{string.Join(", ", actualTriggers)}]");
        return drift;
    }

    private static void Check(List<string> drift, string taskName, string what,
        XmlDocument expected, XmlDocument actual, string xpath, bool unquote, bool expand)
    {
        var want = Text(expected, xpath, unquote);
        var have = Text(actual, xpath, unquote);
        // Paths compare in their normalised form (a token and the literal it expands to are the same file);
        // the message shows the values as registered.
        var same = expand && want is not null && have is not null
            ? string.Equals(TaskPathNormalizer.Normalize(want), TaskPathNormalizer.Normalize(have), StringComparison.Ordinal)
            : string.Equals(want, have, StringComparison.OrdinalIgnoreCase);
        if (!same)
            drift.Add($"task '{taskName}': wrong {what} - expected '{want ?? "(none)"}', found '{have ?? "(none)"}'");
    }

    private static string? Text(XmlDocument doc, string xpath, bool unquote)
    {
        var text = doc.SelectSingleNode(xpath, Ns(doc))?.InnerText.Trim();
        return unquote ? text?.Trim((char)34) : text;
    }

    // One entry per trigger: the element name, plus the state-change kind for session triggers.
    private static List<string> Triggers(XmlDocument doc)
    {
        var ns = Ns(doc);
        var list = new List<string>();
        var triggers = doc.SelectSingleNode("//t:Triggers", ns);
        if (triggers is null) return list;
        foreach (XmlNode node in triggers.ChildNodes)
        {
            if (node.NodeType != XmlNodeType.Element) continue;
            var state = node.SelectSingleNode("t:StateChange", ns)?.InnerText.Trim();
            list.Add(state is null ? node.LocalName : node.LocalName + ":" + state);
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static XmlDocument Load(string xml)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);
        return doc;
    }

    private static XmlNamespaceManager Ns(XmlDocument doc)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("t", TaskNamespace);
        return ns;
    }
}
