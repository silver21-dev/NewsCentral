namespace NewsCentral.Models;

/// <summary>
/// Schema for {CacheRootPath}\resolved-teams.json. NewsService writes it each poll cycle;
/// NewsViewer reads it and unions the entries with the registry static team list.
///
/// Lists ONLY dynamic, Entra-resolved teams. Dynamic-team index verification is the same as for static teams
/// (the environment's registry key, selected by the index KeyId), so this file carries only
/// team identity + grace state — no signature fields. It is a local-tier file protected by
/// cache ACLs. Serializes with the existing camelCase + JsonStringEnumConverter options used
/// across NewsService/NewsViewer; no new serializer options are introduced here.
/// </summary>
public sealed class ResolvedTeamsFile
{
    public DateTime GeneratedUtc { get; set; }
    public List<ResolvedTeamEntry> Teams { get; set; } = new();
}

public sealed class ResolvedTeamEntry
{
    public string TeamFolderName { get; set; } = "";
    public DateTime LastConfirmedUtc { get; set; }
    public ResolvedTeamState State { get; set; } = ResolvedTeamState.Active;

    /// <summary>
    /// Which Entra source produced this entry. Combined with <see cref="SourceId"/> — not alone —
    /// this drives grace in <see cref="NewsCentral.Configuration.EntraResolvedTeamsMerger"/>; see
    /// <see cref="SourceId"/> for why grace is per instance, not per source. Defaults to
    /// <see cref="ResolvedTeamSource.Attribute"/> so pre-feature resolved-teams.json files
    /// (which carry no Source field — every dynamic team was attribute-derived) deserialize
    /// correctly. NewsViewer ignores this field (it reads only <see cref="TeamFolderName"/>).
    /// </summary>
    public ResolvedTeamSource Source { get; set; } = ResolvedTeamSource.Attribute;

    /// <summary>
    /// Identifies WHICH instance of <see cref="Source"/> produced this entry, so several
    /// configured instances of the same source each carry an independent grace window.
    /// Empty string = the legacy single-instance source. Defaults to "" so pre-feature
    /// resolved-teams.json files (which carry no sourceId) deserialize correctly.
    /// NewsViewer ignores this field (it reads only <see cref="TeamFolderName"/>).
    /// </summary>
    public string SourceId { get; set; } = "";
}

public enum ResolvedTeamState
{
    Active,
    Grace
}

/// <summary>
/// The Entra source that resolved a dynamic team: from device extensionAttributes
/// (<see cref="Attribute"/>) or from group membership (<see cref="Group"/>).
/// </summary>
public enum ResolvedTeamSource
{
    Attribute,
    Group
}
