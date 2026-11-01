namespace NewsCentral.Configuration;

/// <summary>Registry storage type for a config value (spec §7.1: REG_SZ / DWORD / n/a).</summary>
public enum RegistryValueType { RegSz, Dword, None }

/// <summary>
/// Type-appropriate read-only rendering for a value (spec §1/§7.1). Not editability —
/// a toggle communicates a bool better than the string "True"; a dropdown shows an enum's set.
/// </summary>
public enum ControlKind { Text, Toggle, Number, Dropdown, Path, Redacted, Structural }

/// <summary>Overridable flag (spec §5.2).</summary>
public enum OverridableState
{
    /// <summary>appsettings key with a registry mapping — registry wins.</summary>
    Overridable,
    /// <summary>Defines the hive path (Company) — cannot itself be a registry value.</summary>
    DefinesPath,
    /// <summary>No appsettings counterpart; settable only via registry.</summary>
    RegistryOnly,
    /// <summary>Sourced from a machine environment variable — not in appsettings, not registry-overridable.</summary>
    EnvironmentSourced
}

/// <summary>
/// One row of the config manifest — the hand-authored source of truth for a single key
/// (spec §7.1). Carries everything the page needs to render the row without touching live config.
/// </summary>
public sealed record ConfigKeyDescriptor
{
    /// <summary>Colon-delimited configuration key, e.g. <c>AzureBlob:ClientSecret</c>.</summary>
    public required string CanonicalKey { get; init; }

    /// <summary>Human label shown in the Value column (canonical key shown as secondary text).</summary>
    public required string DisplayName { get; init; }

    /// <summary>Registry subkey relative to the hive (e.g. <c>AzureBlob\ClientSecret</c>); null for path-defining keys.</summary>
    public string? RegistrySubkeyPath { get; init; }

    public RegistryValueType RegistryType { get; init; }

    /// <summary>Code default (spec §7.1). Rendered as <c>(empty)</c> when the empty string.</summary>
    public required string Default { get; init; }

    public ControlKind ControlKind { get; init; }

    /// <summary>Valid set for a dropdown; empty otherwise.</summary>
    public string[] Options { get; init; } = System.Array.Empty<string>();

    /// <summary>When true, the value is redacted in both layer columns (presence still shown).</summary>
    public bool IsSecret { get; init; }

    public OverridableState OverridableState { get; init; }

    /// <summary>
    /// Machine environment variable backing this row (<see cref="OverridableState.EnvironmentSourced"/>);
    /// null for ordinary config keys. The page shows PRESENCE only — the value is never read for display.
    /// </summary>
    public string? EnvironmentVariableName { get; init; }

    /// <summary>The ⓘ tooltip text (spec §7.1) — copied verbatim from the manifest tables.</summary>
    public required string ValueHint { get; init; }
}

/// <summary>
/// Per-component manifest: the full known key surface plus flags for the structural hives
/// rendered as sub-blocks (spec §6/§7).
/// </summary>
public sealed record ComponentManifest
{
    public required string ComponentName { get; init; }
    public required IReadOnlyList<ConfigKeyDescriptor> Keys { get; init; }
    public bool HasTeamsHive { get; init; }
    public bool HasEntraAttributeSchemes { get; init; }
    public bool HasEntraGroupTeams { get; init; }

    /// <summary>NewsCentral only (M3b) — projects a <see cref="EnvironmentCatalog"/> sub-block.</summary>
    public bool HasEnvironmentCatalog { get; init; }
}
