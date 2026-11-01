using Microsoft.Extensions.Configuration;
using Microsoft.Win32;

namespace NewsCentral.Configuration;

/// <summary>Read status of one config layer (spec §4/§10).</summary>
public enum LayerStatus { Found, NotFound, Unreadable }

/// <summary>One resolved manifest row: descriptor + raw values from each layer (null = absent).</summary>
public sealed record ConfigRow(ConfigKeyDescriptor Descriptor, string? AppSettingsValue, string? RegistryValue);

/// <summary>One selector→rule mapping within a named attribute scheme (NewsService only).</summary>
public sealed record EntraSchemeMapping(string Scheme, string Selector, string SelectorValue, string? Rule);

/// <summary>
/// One configured group-team instance. Label is the registry subkey; the instance id and team
/// folder name are both derived from InclusionGroup at runtime.
/// </summary>
public sealed record EntraGroupTeamInstance(string Label, string? InclusionGroup, string? ExclusionGroup);

/// <summary>
/// Full resolution for one component: header metadata (§4) + config rows (§5) + structural
/// sub-blocks (§6). The two layers are carried separately — never merged — so the page can show
/// appsettings and registry side-by-side and apply precedence itself (spec §1/§8).
/// </summary>
public sealed record ComponentResolution
{
    public string ComponentName { get; init; } = "";
    public string? InstallDir { get; init; }
    public string? DiscoverySource { get; init; }
    public bool InstallDirExists { get; init; }
    public string? AppSettingsPath { get; init; }
    public bool AppSettingsExists { get; init; }
    public LayerStatus AppSettingsStatus { get; init; }
    public string HivePath { get; init; } = "";
    public bool HiveFound { get; init; }
    public LayerStatus RegistryStatus { get; init; }
    public IReadOnlyList<ConfigRow> Rows { get; init; } = System.Array.Empty<ConfigRow>();
    public IReadOnlyList<string> Teams { get; init; } = System.Array.Empty<string>();
    public IReadOnlyList<EntraSchemeMapping> EntraSchemeMappings { get; init; } = System.Array.Empty<EntraSchemeMapping>();
    public IReadOnlyList<EntraGroupTeamInstance> EntraGroupTeamInstances { get; init; } = System.Array.Empty<EntraGroupTeamInstance>();
    public string? EntraGlobalExclusionGroup { get; init; }

    /// <summary>NewsCentral only (M3b) — null unless the manifest has <c>HasEnvironmentCatalog</c>.</summary>
    public EnvironmentCatalog? EnvironmentCatalog { get; init; }
}

/// <summary>
/// Resolves a component's config across the two live layers (appsettings.json + registry), reading
/// each separately (spec §8). I/O (<see cref="DiscoverInstallDir"/>, <see cref="BuildLayers"/>) is
/// split from the pure <see cref="Project"/> so the projection is unit-testable. Returns RAW values —
/// redaction is a formatting concern (<see cref="ConfigValueFormatter"/>), not done here.
/// </summary>
public static class EffectiveConfigResolver
{
    /// <summary>
    /// Extracts the executable's directory from a command-line value (spec §4.2). Quoted → the path
    /// between the first pair of quotes; otherwise up to and including the last case-insensitive
    /// ".exe"; then the directory name. Null if unresolvable. Pure managed — NativeAOT-safe.
    /// </summary>
    public static string? ParseExeDirectory(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;

        var trimmed = commandLine.TrimStart();
        string exePath;

        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end < 0) return null;
            exePath = trimmed.Substring(1, end - 1);
        }
        else
        {
            var idx = trimmed.LastIndexOf(".exe", System.StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            exePath = trimmed[..(idx + 4)];
        }

        try
        {
            var dir = Path.GetDirectoryName(exePath);
            return string.IsNullOrEmpty(dir) ? null : dir;
        }
        catch { return null; }
    }

    /// <summary>
    /// NewsViewer's install directory (spec §4.1), pure: the folder of NewsService's
    /// <c>ViewerTasks:NewsViewerPath</c> when set, otherwise the install layout's sibling
    /// <c>..\NewsViewer</c> next to the NewsService install directory — accepted only if it exists.
    /// Null when neither resolves.
    /// </summary>
    public static string? ResolveNewsViewerDir(
        string? configuredExePath, string? newsServiceDir, Func<string, bool> directoryExists)
    {
        if (!string.IsNullOrWhiteSpace(configuredExePath))
            return ParseExeDirectory(configuredExePath);

        if (string.IsNullOrWhiteSpace(newsServiceDir)) return null;
        try
        {
            var sibling = Path.GetFullPath(Path.Combine(newsServiceDir, "..", "NewsViewer"));
            return directoryExists(sibling) ? sibling : null;
        }
        catch { return null; }
    }

    /// <summary>Discovers a component's install directory (spec §4.1). Null on any failure.</summary>
    public static string? DiscoverInstallDir(string component)
    {
        try
        {
            switch (component)
            {
                case "NewsCentral":
                    return AppContext.BaseDirectory;

                case "NewsService":
                    if (!OperatingSystem.IsWindows()) return null;
                    using (var k = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Services\NewsService"))
                    {
                        return k?.GetValue("ImagePath") is string img ? ParseExeDirectory(img) : null;
                    }

                case "NewsViewer":
                    // No HKLM Run value any more: NewsViewer's folder is what NewsService launches via the
                    // scheduled tasks — ViewerTasks:NewsViewerPath, else the sibling-folder install layout.
                    if (!OperatingSystem.IsWindows()) return null;
                    string? configuredExe = null;
                    var company = SolutionConstants.Company;
                    if (!string.IsNullOrEmpty(company))
                    {
                        using var k = Registry.LocalMachine.OpenSubKey(
                            $@"Software\{company}\{SolutionConstants.SolutionName}\NewsService\ViewerTasks");
                        configuredExe = k?.GetValue("NewsViewerPath") as string;
                    }
                    return ResolveNewsViewerDir(configuredExe, DiscoverInstallDir("NewsService"), Directory.Exists);

                default:
                    return null;
            }
        }
        catch { return null; }
    }

    /// <summary>Human-readable discovery source for the header block (spec §4.1).</summary>
    public static string DiscoverySourceLabel(string component) => component switch
    {
        "NewsCentral" => "AppContext.BaseDirectory",
        "NewsService" => @"HKLM\SYSTEM\CurrentControlSet\Services\NewsService\ImagePath",
        "NewsViewer"  => @"NewsService ViewerTasks\NewsViewerPath, else ..\NewsViewer beside the NewsService install",
        _ => "unknown"
    };

    /// <summary>
    /// Reads the two layers separately (spec §8): appsettings.json from the install dir, and the
    /// registry hive located via the compile-time <see cref="SolutionConstants.Company"/> constant —
    /// the same value every component resolves its hive from, so the tool and the runtime provably
    /// agree on which hive is read. Never merges them.
    /// </summary>
    public static (IConfiguration Appsettings, LayerStatus AppStatus,
                   IConfiguration Registry, LayerStatus RegStatus, bool HiveFound)
        BuildLayers(string component, string? installDir)
    {
        // ── appsettings layer ────────────────────────────────────────────────
        IConfiguration appLayer;
        LayerStatus appStatus;
        var appPath = installDir is null ? null : Path.Combine(installDir, "appsettings.json");

        if (appPath is null || !SafeFileExists(appPath))
        {
            appLayer = Empty();
            appStatus = LayerStatus.NotFound;
        }
        else
        {
            try
            {
                appLayer = new ConfigurationBuilder().AddJsonFile(appPath, optional: true).Build();
                appStatus = LayerStatus.Found;
            }
            catch
            {
                appLayer = Empty();
                appStatus = LayerStatus.Unreadable;
            }
        }

        // ── registry layer (hive located via the compile-time Company constant) ─────
        var company = SolutionConstants.Company;
        IConfiguration regLayer;
        LayerStatus regStatus;
        bool hiveFound;

        if (string.IsNullOrEmpty(company))
        {
            regLayer = Empty();
            regStatus = LayerStatus.NotFound;
            hiveFound = false;
        }
        else
        {
            (hiveFound, regStatus) = ProbeHive(company, component);
            try
            {
                regLayer = new ConfigurationBuilder()
                    .AddRegistryOverrides(company, SolutionConstants.SolutionName, component)
                    .Build();
            }
            catch
            {
                regLayer = Empty();
                regStatus = LayerStatus.Unreadable;
            }
        }

        return (appLayer, appStatus, regLayer, regStatus, hiveFound);
    }

    /// <summary>
    /// PURE projection (spec §8 step 5): per manifest key, pair the raw appsettings and registry
    /// values (null = absent), and project the structural teams / Entra attribute-scheme
    /// / Entra group-team blocks when the manifest flags them. No I/O — the two layers are supplied
    /// by the caller. Returns RAW values throughout — redaction is a formatting concern
    /// (<see cref="ConfigValueFormatter"/>), not done here; none of the Entra structural values are
    /// secret.
    /// </summary>
    public static ComponentResolution Project(
        ComponentManifest manifest, IConfiguration appsettingsLayer, IConfiguration registryLayer)
    {
        var rows = manifest.Keys
            .Select(d => new ConfigRow(d, appsettingsLayer[d.CanonicalKey], registryLayer[d.CanonicalKey]))
            .ToList();

        var teams = manifest.HasTeamsHive
            ? registryLayer.GetSection("teams").GetChildren()
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToList()
            : new List<string>();

        // Entra:AttributeSchemes:{scheme}:Selector / Mappings:{selectorValue} → one row per mapping.
        // A scheme with a Selector but no Mappings children emits nothing — the page renders schemes
        // via their mappings.
        var schemeMappings = manifest.HasEntraAttributeSchemes
            ? registryLayer.GetSection("Entra:AttributeSchemes").GetChildren()
                .SelectMany(scheme =>
                {
                    var selector = scheme["Selector"] ?? "";
                    return scheme.GetSection("Mappings").GetChildren()
                        .Select(m => new EntraSchemeMapping(scheme.Key, selector, m.Key, m.Value));
                })
                .ToList()
            : new List<EntraSchemeMapping>();

        var groupInstances = manifest.HasEntraGroupTeams
            ? registryLayer.GetSection("Entra:GroupTeams:Instances").GetChildren()
                .Select(i => new EntraGroupTeamInstance(i.Key, i["InclusionGroup"], i["ExclusionGroup"]))
                .ToList()
            : new List<EntraGroupTeamInstance>();

        var globalExclusion = manifest.HasEntraGroupTeams
            ? registryLayer["Entra:GroupTeams:ExclusionGroup"]
            : null;

        var environmentCatalog = manifest.HasEnvironmentCatalog
            ? EnvironmentCatalogReader.Read(registryLayer)
            : null;

        return new ComponentResolution
        {
            ComponentName = manifest.ComponentName,
            Rows = rows,
            Teams = teams,
            EntraSchemeMappings = schemeMappings,
            EntraGroupTeamInstances = groupInstances,
            EntraGlobalExclusionGroup = globalExclusion,
            EnvironmentCatalog = environmentCatalog
        };
    }

    /// <summary>
    /// Convenience: discover → build layers → project, carrying header metadata through (spec §8).
    /// When <paramref name="installDirOverride"/> is non-empty it is used directly and auto-discovery
    /// is skipped (spec §4 override); the appsettings path is still derived as
    /// <c>{override}\appsettings.json</c>. A non-existent override resolves to not-found/empty layers
    /// without throwing. Empty/null → behavior is unchanged (auto-discovery). Pure — no persistence.
    /// </summary>
    public static ComponentResolution Resolve(ComponentManifest manifest, string? installDirOverride = null)
    {
        var component = manifest.ComponentName;
        var overridden = !string.IsNullOrWhiteSpace(installDirOverride);
        var installDir = overridden ? installDirOverride!.Trim() : DiscoverInstallDir(component);

        var (app, appStatus, reg, regStatus, hiveFound) = BuildLayers(component, installDir);
        var projected = Project(manifest, app, reg);

        var appPath = installDir is null ? null : Path.Combine(installDir, "appsettings.json");
        // The hive is located by the compile-time Company constant, not by appsettings — so the
        // displayed hive path matches the one the components actually resolve at runtime.
        var hivePath = $@"HKLM\Software\{SolutionConstants.Company}\{SolutionConstants.SolutionName}\{component}";

        return projected with
        {
            InstallDir = installDir,
            DiscoverySource = overridden ? "manual override (session)" : DiscoverySourceLabel(component),
            InstallDirExists = installDir is not null && SafeDirExists(installDir),
            AppSettingsPath = appPath,
            AppSettingsExists = appPath is not null && SafeFileExists(appPath),
            AppSettingsStatus = appStatus,
            HivePath = hivePath,
            HiveFound = hiveFound,
            RegistryStatus = regStatus
        };
    }

    private static (bool Found, LayerStatus Status) ProbeHive(string company, string component)
    {
        if (!OperatingSystem.IsWindows()) return (false, LayerStatus.NotFound);
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                $@"Software\{company}\{SolutionConstants.SolutionName}\{component}");
            return k is not null ? (true, LayerStatus.Found) : (false, LayerStatus.NotFound);
        }
        catch (System.Security.SecurityException) { return (false, LayerStatus.Unreadable); }
        catch (UnauthorizedAccessException) { return (false, LayerStatus.Unreadable); }
    }

    private static IConfiguration Empty() => new ConfigurationBuilder().Build();

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    private static bool SafeDirExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }
}
