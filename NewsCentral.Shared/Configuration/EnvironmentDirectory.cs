using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewsCentral.Configuration;

/// <summary>
/// One user-added environment entry in <see cref="UserEnvironmentState"/>. <see cref="DisplayName"/>
/// is the name the user gave it when adding it (optional — falls back to the last path segment in
/// <see cref="EnvironmentListBuilder"/> when absent).
/// </summary>
public sealed class UserEnvironmentEntry
{
    public string DataPath { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public DateTime AddedUtc { get; set; }
}

/// <summary>
/// The full contents of <c>%LocalAppData%\{Company}\NewsCentral\user-environments.json</c> — this
/// machine+user's local environment list, the Shared entries this machine has learned of (M4b, via
/// <c>config/environments.json</c> sync), hidden-entry preferences, and last-used path for startup
/// selection.
/// </summary>
public sealed class UserEnvironmentState
{
    public int SchemaVersion { get; set; } = 1;
    public string? LastUsedDataPath { get; set; }
    public List<UserEnvironmentEntry> Entries { get; set; } = new();

    /// <summary>
    /// The union of Shared-directory entries (<see cref="SharedDirectoryEntry"/>) this machine has
    /// learned of from any environment it has opened — including tombstones, so a deletion already
    /// known to this machine is never resurrected by a later merge. M4b.
    /// </summary>
    public List<SharedDirectoryEntry> SharedEntries { get; set; } = new();

    /// <summary>
    /// Canonical (<see cref="EnvironmentPaths.Canonicalize"/>) DataPaths of Policy or Shared entries
    /// hidden locally. Renamed from <c>HiddenPolicyPaths</c> in M4b when Shared entries became
    /// hideable too; <see cref="UserEnvironmentStateJson.Deserialize"/> folds an old file's
    /// <c>hiddenPolicyPaths</c> key into this property on read, so existing local state survives the
    /// rename unchanged.
    /// </summary>
    public List<string> HiddenPaths { get; set; } = new();

    /// <summary>
    /// Per operator, per machine: start a poster-statistics pass automatically when the Assignments page
    /// has loaded. Default false. Local convenience only; never shared and not an appsettings or registry key.
    /// </summary>
    public bool ProcessStatisticsOnOpen { get; set; }

    /// <summary>Landing pad for the pre-M4b JSON key — never read directly; see <see cref="UserEnvironmentStateJson.Deserialize"/>.</summary>
    [JsonPropertyName("hiddenPolicyPaths")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? HiddenPolicyPathsLegacy { get; set; }
}

public static class UserEnvironmentStateJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Deserializes <paramref name="json"/> and folds a legacy <c>hiddenPolicyPaths</c> key into
    /// <see cref="UserEnvironmentState.HiddenPaths"/> when the new key is absent/empty — a one-time
    /// read-side migration. The legacy field is always cleared afterward, so a subsequent save never
    /// writes the old key back out (see its <c>JsonIgnore</c>).
    /// </summary>
    public static UserEnvironmentState? Deserialize(string json)
    {
        var state = JsonSerializer.Deserialize<UserEnvironmentState>(json, Options);
        if (state == null)
            return null;

        if (state.HiddenPaths.Count == 0 && state.HiddenPolicyPathsLegacy is { Count: > 0 } legacy)
            state.HiddenPaths = legacy;

        state.HiddenPolicyPathsLegacy = null;
        return state;
    }
}

/// <summary>Which of the four sources produced an <see cref="EnvironmentOption"/>.</summary>
public enum EnvironmentKind
{
    /// <summary>A Group Policy-defined entry from <see cref="EnvironmentCatalog"/>. Cannot be removed.</summary>
    Policy,

    /// <summary>This machine's own appsettings/registry <c>DataPath</c> (<see cref="AppConfiguration.DataPath"/>).</summary>
    Configured,

    /// <summary>
    /// An entry from the shared <c>config/environments.json</c> directory (M4b), learned by this
    /// machine via <see cref="SharedDirectoryMerger"/>. Removable (as a tombstone) only by a System
    /// Administrator; hideable by anyone.
    /// </summary>
    Shared,

    /// <summary>A path the user added themselves, stored in <see cref="UserEnvironmentState"/>.</summary>
    User
}

/// <summary>
/// One row the environment picker can show. Built entirely from stored data by
/// <see cref="EnvironmentListBuilder.Build"/> — never probes the filesystem.
/// </summary>
public sealed record EnvironmentOption(
    string DataPath,
    string DisplayName,
    EnvironmentKind Kind,
    string? PolicyName,
    bool IsHidden,
    bool IsShareable,
    bool IsCurrent);

/// <summary>
/// Builds the ordered, de-duplicated list of environments to show in the picker from the three
/// sources — Policy, Configured, User — applying precedence, visibility and display-name fallback.
/// Pure: never touches the filesystem.
/// </summary>
public static class EnvironmentListBuilder
{
    /// <summary>
    /// Builds the visible (or, with <paramref name="includeHidden"/>, full) environment list.
    ///
    /// Precedence: Policy &gt; Configured &gt; Shared &gt; User, by canonical DataPath — each source
    /// is processed in that order and a later source's entry is dropped if an earlier source already
    /// claimed the same canonical path. User entries are listed only when <paramref name="catalog"/>
    /// allows them (<see cref="EnvironmentCatalog.AllowUserEnvironments"/>) or when the catalog has no
    /// entries at all (an unmanaged machine); Shared entries (M4b) follow that identical rule — see
    /// the "shared" note below.
    ///
    /// A Policy or Shared entry hidden via <see cref="UserEnvironmentState.HiddenPaths"/> is excluded
    /// unless <paramref name="includeHidden"/> is true, or it is the current environment
    /// (<paramref name="currentDataPath"/>) — the current environment is always shown even if hidden.
    /// A Shared entry whose <see cref="SharedDirectoryEntry.DeletedUtc"/> is set (a tombstone) is
    /// never shown, under any circumstance — tombstones exist only to propagate; see
    /// <see cref="SharedDirectoryMerger"/>.
    ///
    /// Ordering: Policy (by name, ordinal-ignore-case), then Configured, then Shared (by display
    /// name, ordinal-ignore-case), then User (by display name, ordinal-ignore-case).
    /// </summary>
    public static IReadOnlyList<EnvironmentOption> Build(
        EnvironmentCatalog catalog,
        string? configuredDataPath,
        UserEnvironmentState state,
        string? currentDataPath,
        bool includeHidden)
    {
        var currentCanonical = string.IsNullOrWhiteSpace(currentDataPath)
            ? null
            : EnvironmentPaths.Canonicalize(currentDataPath);

        var hiddenPaths = new HashSet<string>(state.HiddenPaths, StringComparer.OrdinalIgnoreCase);

        var seenPaths = new HashSet<string>();
        var policyOptions = new List<EnvironmentOption>();

        foreach (var policy in catalog.Entries)
        {
            var canonical = EnvironmentPaths.Canonicalize(policy.DataPath);
            if (!seenPaths.Add(canonical))
                continue; // shouldn't happen — EnvironmentCatalogReader already dedupes — defensive only.

            var isCurrent = currentCanonical != null && canonical == currentCanonical;
            var isHidden = hiddenPaths.Contains(canonical);

            if (isHidden && !includeHidden && !isCurrent)
                continue;

            var displayName = !string.IsNullOrWhiteSpace(policy.DisplayName) ? policy.DisplayName! : policy.Name;

            policyOptions.Add(new EnvironmentOption(
                DataPath: policy.DataPath,
                DisplayName: displayName,
                Kind: EnvironmentKind.Policy,
                PolicyName: policy.Name,
                IsHidden: isHidden,
                IsShareable: EnvironmentPaths.IsShareable(policy.DataPath),
                IsCurrent: isCurrent));
        }

        policyOptions = policyOptions
            .OrderBy(o => o.PolicyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        EnvironmentOption? configuredOption = null;
        if (!string.IsNullOrWhiteSpace(configuredDataPath))
        {
            var canonical = EnvironmentPaths.Canonicalize(configuredDataPath);
            if (seenPaths.Add(canonical))
            {
                var isCurrent = currentCanonical != null && canonical == currentCanonical;
                configuredOption = new EnvironmentOption(
                    DataPath: configuredDataPath,
                    DisplayName: LastPathSegmentOrPath(configuredDataPath),
                    Kind: EnvironmentKind.Configured,
                    PolicyName: null,
                    IsHidden: false,
                    IsShareable: EnvironmentPaths.IsShareable(configuredDataPath),
                    IsCurrent: isCurrent);
            }
        }

        var userOptionsAllowed = catalog.AllowUserEnvironments || catalog.Entries.Count == 0;

        var sharedOptions = new List<EnvironmentOption>();
        if (userOptionsAllowed)
        {
            foreach (var entry in state.SharedEntries)
            {
                if (entry.DeletedUtc != null) // tombstoned — never shown, exists only to propagate
                    continue;

                if (string.IsNullOrWhiteSpace(entry.DataPath))
                    continue;

                var canonical = EnvironmentPaths.Canonicalize(entry.DataPath);
                if (!seenPaths.Add(canonical))
                    continue;

                var isCurrent = currentCanonical != null && canonical == currentCanonical;
                var isHidden = hiddenPaths.Contains(canonical);

                if (isHidden && !includeHidden && !isCurrent)
                    continue;

                var displayName = !string.IsNullOrWhiteSpace(entry.DisplayName)
                    ? entry.DisplayName!
                    : LastPathSegmentOrPath(entry.DataPath);

                sharedOptions.Add(new EnvironmentOption(
                    DataPath: entry.DataPath,
                    DisplayName: displayName,
                    Kind: EnvironmentKind.Shared,
                    PolicyName: null,
                    IsHidden: isHidden,
                    IsShareable: EnvironmentPaths.IsShareable(entry.DataPath),
                    IsCurrent: isCurrent));
            }

            sharedOptions = sharedOptions
                .OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var userOptions = new List<EnvironmentOption>();

        if (userOptionsAllowed)
        {
            foreach (var entry in state.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.DataPath))
                    continue;

                var canonical = EnvironmentPaths.Canonicalize(entry.DataPath);
                if (!seenPaths.Add(canonical))
                    continue;

                var isCurrent = currentCanonical != null && canonical == currentCanonical;
                var displayName = !string.IsNullOrWhiteSpace(entry.DisplayName)
                    ? entry.DisplayName!
                    : LastPathSegmentOrPath(entry.DataPath);

                userOptions.Add(new EnvironmentOption(
                    DataPath: entry.DataPath,
                    DisplayName: displayName,
                    Kind: EnvironmentKind.User,
                    PolicyName: null,
                    IsHidden: false,
                    IsShareable: EnvironmentPaths.IsShareable(entry.DataPath),
                    IsCurrent: isCurrent));
            }

            userOptions = userOptions
                .OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var result = new List<EnvironmentOption>(policyOptions);
        if (configuredOption != null)
            result.Add(configuredOption);
        result.AddRange(sharedOptions);
        result.AddRange(userOptions);
        return result;
    }

    private static string LastPathSegmentOrPath(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var segment = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(segment) ? path : segment;
    }
}

/// <summary>
/// Picks which environment to start in, from the visible list only — never falls back to an
/// environment that isn't shown. Pure: never touches the filesystem.
/// </summary>
public static class EnvironmentStartupSelector
{
    /// <summary>
    /// Selection order: (1) <paramref name="lastUsed"/>, if it matches a visible entry's canonical
    /// path; (2) the Policy entry named "Default" (explicit or implicit), if visible; (3)
    /// <paramref name="configuredDataPath"/>, if visible; (4) the first visible entry. Returns null
    /// when <paramref name="visible"/> is empty — the caller (MauiProgram) falls back to
    /// <see cref="AppConfiguration.DataPath"/> directly in that case, which is outside this pure
    /// function's concern.
    /// </summary>
    public static string? Select(
        IReadOnlyList<EnvironmentOption> visible,
        string? lastUsed,
        EnvironmentCatalog catalog,
        string? configuredDataPath)
    {
        if (visible.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(lastUsed))
        {
            var canonicalLastUsed = EnvironmentPaths.Canonicalize(lastUsed);
            var match = visible.FirstOrDefault(
                o => EnvironmentPaths.Canonicalize(o.DataPath) == canonicalLastUsed);
            if (match != null)
                return match.DataPath;
        }

        var defaultPolicy = visible.FirstOrDefault(
            o => o.Kind == EnvironmentKind.Policy &&
                 string.Equals(o.PolicyName, "Default", StringComparison.OrdinalIgnoreCase));
        if (defaultPolicy != null)
            return defaultPolicy.DataPath;

        if (!string.IsNullOrWhiteSpace(configuredDataPath))
        {
            var canonicalConfigured = EnvironmentPaths.Canonicalize(configuredDataPath);
            var configuredMatch = visible.FirstOrDefault(
                o => EnvironmentPaths.Canonicalize(o.DataPath) == canonicalConfigured);
            if (configuredMatch != null)
                return configuredMatch.DataPath;
        }

        return visible[0].DataPath;
    }
}
