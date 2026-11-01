using System.Reflection;
using NewsCentral.Configuration;
using NewsService.Configuration;
using NewsViewer.Configuration;

namespace NewsService.Tests;

/// <summary>
/// Drift guard (spec §9): one-directional POCO ⊆ manifest. Every public settable scalar property of
/// the bindable config POCOs (walking nested section objects) must have a manifest entry. The manifest
/// is deliberately a superset — it also carries registry-only / ad-hoc keys (e.g. Signing:RequireSignedIndex)
/// and structural hives that have no POCO property, so the reverse direction is intentionally not asserted.
/// NewsCentral is exempt — AppConfiguration is a get-only IConfiguration wrapper, not reflection-bindable.
/// Lives here (not in NewsCentral.Shared.Tests) because reflecting ServiceConfiguration/ViewerConfiguration
/// needs references to the Windows-targeted NewsService/NewsViewer assemblies.
/// </summary>
public sealed class ManifestCoverageTests
{
    [Fact]
    public void ServiceConfiguration_ScalarKeys_AllInManifest() =>
        AssertPocoSubsetOfManifest(typeof(ServiceConfiguration), ConfigManifests.NewsService);

    [Fact]
    public void ViewerConfiguration_ScalarKeys_AllInManifest() =>
        AssertPocoSubsetOfManifest(typeof(ViewerConfiguration), ConfigManifests.NewsViewer);

    // ── Reverse direction: manifest ⊆ POCO ──────────────────────────────────────
    //
    // The forward tests above catch a POCO property with no manifest row (an operator can't see
    // a live setting). This direction catches the opposite drift: a manifest row that no longer
    // resolves to any POCO property — an orphaned key left behind by a rename/removal. On the
    // ConfigurationReview page an orphaned row renders blank in both the appsettings and registry
    // columns, which looks exactly like a legitimate, simply-unset value — actively misleading
    // rather than merely absent. This is what would have caught the Entra:GroupTeam:* rows
    // orphaned by the M3 rename to Entra:GroupTeams (M5).
    //
    // Scope: only OverridableState.Overridable rows are checked — DefinesPath (Company) and
    // EnvironmentSourced rows are non-POCO by construction, not by omission. A handful of
    // Overridable rows are *intentionally* ad-hoc reads or host-owned keys with no POCO backing;
    // those are named in NonPocoBackedKeys below rather than forcing the resolver to special-case
    // them structurally.

    [Fact]
    public void ServiceConfiguration_ManifestKeys_AllResolveToPocoScalars() =>
        AssertManifestSubsetOfPoco(typeof(ServiceConfiguration), ConfigManifests.NewsService);

    [Fact]
    public void ViewerConfiguration_ManifestKeys_AllResolveToPocoScalars() =>
        AssertManifestSubsetOfPoco(typeof(ViewerConfiguration), ConfigManifests.NewsViewer);

    /// <summary>
    /// Manifest rows deliberately without a matching POCO property: ad-hoc <c>IConfiguration</c>
    /// reads or keys owned by a framework binder rather than by our POCOs. Explicit allow-list —
    /// keep it short; a new entry here should be able to point at the ad-hoc read site.
    /// </summary>
    private static readonly HashSet<string> NonPocoBackedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Signing:RequireSignedIndex",          // configuration.GetValue<bool>(...) ad-hoc read (SyncService / PresentationSelector), not bound to a POCO
        "Signing:PublicKey",                    // SigningKeyConfigurationReader.GetPublicKeys ad-hoc read (SyncService / PresentationSelector), not bound to a POCO
        "Signing:PublicKeyPrevious",            // same
        "Logging:LogLevel:Default",             // standard .NET logging key, bound by the generic host's own logging configuration, not ServiceConfiguration
        "Logging:EventLog:LogLevel:Default",    // same — EventLog provider logging, host-owned
    };

    private static void AssertPocoSubsetOfManifest(Type pocoType, ComponentManifest manifest)
    {
        var manifestKeys = manifest.Keys
            .Select(k => k.CanonicalKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var pocoKeys = new List<string>();
        CollectScalarKeys(pocoType, prefix: "", pocoKeys);

        var missing = pocoKeys.Where(k => !manifestKeys.Contains(k)).ToList();

        Assert.True(missing.Count == 0,
            $"Manifest '{manifest.ComponentName}' is missing POCO scalar keys: {string.Join(", ", missing)}");
    }

    private static void AssertManifestSubsetOfPoco(Type pocoType, ComponentManifest manifest)
    {
        var orphaned = manifest.Keys
            .Where(k => k.OverridableState == OverridableState.Overridable)
            .Where(k => !NonPocoBackedKeys.Contains(k.CanonicalKey))
            .Where(k => !ResolvesToPocoScalar(pocoType, k.CanonicalKey))
            .Select(k => k.CanonicalKey)
            .ToList();

        Assert.True(orphaned.Count == 0,
            $"Manifest '{manifest.ComponentName}' has descriptor rows with no matching POCO scalar property " +
            $"(rename/removal left them behind — see NonPocoBackedKeys if this is intentional): {string.Join(", ", orphaned)}");
    }

    /// <summary>Walks <paramref name="canonicalKey"/>'s colon-delimited segments as nested property
    /// names on <paramref name="pocoType"/>; true only if every segment resolves and the final
    /// segment is a settable scalar.</summary>
    private static bool ResolvesToPocoScalar(Type pocoType, string canonicalKey)
    {
        var segments = canonicalKey.Split(':');
        var current = pocoType;

        for (var i = 0; i < segments.Length; i++)
        {
            var prop = current.GetProperty(segments[i], BindingFlags.Public | BindingFlags.Instance);
            if (prop is null) return false;
            if (prop.GetMethod is not { IsPublic: true }) return false;
            if (prop.SetMethod is not { IsPublic: true }) return false;

            var t          = prop.PropertyType;
            var underlying = Nullable.GetUnderlyingType(t) ?? t;
            var isLast     = i == segments.Length - 1;

            if (isLast) return IsScalar(underlying);
            if (!underlying.IsClass || !IsOwnType(underlying)) return false;

            current = underlying;
        }

        return false;
    }

    private static readonly string[] OwnRoots = { "NewsCentral", "NewsService", "NewsViewer" };

    private static void CollectScalarKeys(Type type, string prefix, List<string> keys)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;      // skip indexers
            if (p.GetMethod is not { IsPublic: true }) continue;
            if (p.SetMethod is not { IsPublic: true }) continue;  // settable scalars/sections only

            var t          = p.PropertyType;
            var underlying = Nullable.GetUnderlyingType(t) ?? t;
            var key        = string.IsNullOrEmpty(prefix) ? p.Name : $"{prefix}:{p.Name}";

            if (IsScalar(underlying)) { keys.Add(key); continue; }
            if (IsCollection(t)) continue;                        // Dictionary/List → structural hive, skip
            if (underlying.IsClass && IsOwnType(underlying))
                CollectScalarKeys(underlying, key, keys);         // nested section object
        }
    }

    private static bool IsScalar(Type t) => t == typeof(string) || t.IsPrimitive || t.IsEnum;

    private static bool IsCollection(Type t) =>
        t != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(t);

    private static bool IsOwnType(Type t) =>
        t.Namespace is not null && OwnRoots.Any(r => t.Namespace.StartsWith(r, StringComparison.Ordinal));
}
