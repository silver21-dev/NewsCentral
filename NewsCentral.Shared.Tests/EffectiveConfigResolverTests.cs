using Microsoft.Extensions.Configuration;
using NewsCentral.Configuration;

namespace NewsCentral.Shared.Tests;

public sealed class EffectiveConfigResolverTests
{
    // ── ParseExeDirectory (spec §4.2) ─────────────────────────────────────────

    [Fact]
    public void ParseExeDirectory_QuotedPathWithArgs()
    {
        var dir = EffectiveConfigResolver.ParseExeDirectory(
            "\"C:\\Program Files\\NewsService\\NewsService.exe\" --run");
        Assert.Equal(@"C:\Program Files\NewsService", dir);
    }

    [Fact]
    public void ParseExeDirectory_UnquotedPathWithArgs()
    {
        var dir = EffectiveConfigResolver.ParseExeDirectory(
            @"C:\Apps\NewsViewer\NewsViewer.exe /silent");
        Assert.Equal(@"C:\Apps\NewsViewer", dir);
    }

    [Fact]
    public void ParseExeDirectory_QuotedPathWithSpaces()
    {
        var dir = EffectiveConfigResolver.ParseExeDirectory(
            "\"C:\\Program Files\\My App\\app.exe\"");
        Assert.Equal(@"C:\Program Files\My App", dir);
    }

    [Fact]
    public void ParseExeDirectory_Garbage_ReturnsNull()
    {
        Assert.Null(EffectiveConfigResolver.ParseExeDirectory("not a path at all"));
        Assert.Null(EffectiveConfigResolver.ParseExeDirectory(""));
        Assert.Null(EffectiveConfigResolver.ParseExeDirectory("   "));
    }

    // ── ResolveNewsViewerDir (spec §4.1) — no HKLM Run value any more ────────

    [Fact]
    public void ResolveNewsViewerDir_ConfiguredExePath_WinsOverTheSiblingFolder()
    {
        var dir = EffectiveConfigResolver.ResolveNewsViewerDir(
            @"C:\dev\NewsViewer\bin\Debug\NewsViewer.exe", @"C:\Install\NewsService", _ => true);
        Assert.Equal(@"C:\dev\NewsViewer\bin\Debug", dir);
    }

    [Fact]
    public void ResolveNewsViewerDir_NoConfiguredPath_UsesTheExistingSiblingFolder()
    {
        var dir = EffectiveConfigResolver.ResolveNewsViewerDir(
            "", @"C:\Install\NewsService", p => p == @"C:\Install\NewsViewer");
        Assert.Equal(@"C:\Install\NewsViewer", dir);
    }

    [Fact]
    public void ResolveNewsViewerDir_SiblingFolderMissing_OrNoNewsServiceDir_IsNotDiscovered()
    {
        Assert.Null(EffectiveConfigResolver.ResolveNewsViewerDir(null, @"C:\Install\NewsService", _ => false));
        Assert.Null(EffectiveConfigResolver.ResolveNewsViewerDir(null, null, _ => true));
    }

    [Fact]
    public void ResolveNewsViewerDir_UnparseableConfiguredPath_IsNotDiscovered()
    {
        Assert.Null(EffectiveConfigResolver.ResolveNewsViewerDir("not a path", @"C:\Install\NewsService", _ => true));
    }

    // ── Project (spec §8 step 5) — pure, two in-memory layers ─────────────────

    private static ConfigKeyDescriptor Desc(string key, bool secret = false) => new()
    {
        CanonicalKey = key,
        DisplayName = key,
        RegistrySubkeyPath = key.Replace(':', '\\'),
        RegistryType = RegistryValueType.RegSz,
        Default = "",
        ControlKind = secret ? ControlKind.Redacted : ControlKind.Text,
        IsSecret = secret,
        OverridableState = OverridableState.Overridable,
        ValueHint = ""
    };

    [Fact]
    public void Project_ResolvesPerKeyValues_AndStructuralBlocks()
    {
        var manifest = new ComponentManifest
        {
            ComponentName = "Test",
            HasTeamsHive = true,
            Keys = new[]
            {
                Desc("Company"),
                Desc("Service:PollIntervalSeconds"),
                Desc("Signing:PublicKey"),
                Desc("Signing:PublicKeyPrevious"),
                Desc("AzureBlob:ClientSecret", secret: true)   // absent in both layers
            }
        };

        var app = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Company"] = "Contoso",
            ["Service:PollIntervalSeconds"] = "60"
        }).Build();

        var reg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Company"] = "Contoso",
            ["Service:PollIntervalSeconds"] = "90",
            ["teams:0"] = "alpha",
            ["teams:1"] = "beta",
            ["Signing:RequireSignedIndex"] = "true",
            ["Signing:PublicKey"] = "PKCUR",
            ["Signing:PublicKeyPrevious"] = "PKPREV",
            // Leftover per-team values from the old model: ignored, and must not throw.
            ["Signing:team-x:PublicKey"] = "PKX",
            ["Signing:team-x:PublicKeyPrevious"] = "PKXP",
        }).Build();

        var result = EffectiveConfigResolver.Project(manifest, app, reg);

        // Per-key value resolution
        var company = result.Rows.Single(r => r.Descriptor.CanonicalKey == "Company");
        Assert.Equal("Contoso", company.AppSettingsValue);
        Assert.Equal("Contoso", company.RegistryValue);

        var poll = result.Rows.Single(r => r.Descriptor.CanonicalKey == "Service:PollIntervalSeconds");
        Assert.Equal("60", poll.AppSettingsValue);
        Assert.Equal("90", poll.RegistryValue);

        // Absent in both layers → null
        var secret = result.Rows.Single(r => r.Descriptor.CanonicalKey == "AzureBlob:ClientSecret");
        Assert.Null(secret.AppSettingsValue);
        Assert.Null(secret.RegistryValue);

        // teams
        Assert.Equal(new[] { "alpha", "beta" }, result.Teams);

        // Signing keys are ordinary flat manifest rows (registry layer only here)
        var cur = result.Rows.Single(r => r.Descriptor.CanonicalKey == "Signing:PublicKey");
        Assert.Null(cur.AppSettingsValue);
        Assert.Equal("PKCUR", cur.RegistryValue);
        var prev = result.Rows.Single(r => r.Descriptor.CanonicalKey == "Signing:PublicKeyPrevious");
        Assert.Equal("PKPREV", prev.RegistryValue);
        Assert.DoesNotContain(result.Rows, r => r.Descriptor.CanonicalKey.Contains("team-x"));
    }

    [Fact]
    public void Project_OmitsStructuralBlocks_WhenFlagsFalse()
    {
        var manifest = new ComponentManifest
        {
            ComponentName = "NoStructure",
            HasTeamsHive = false,
            Keys = new[] { Desc("Company") }
        };

        var empty = new ConfigurationBuilder().Build();
        var reg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["teams:0"] = "alpha",
            ["Signing:team-x:PublicKey"] = "PKX",
            ["Entra:AttributeSchemes:fat:Selector"] = "extensionAttribute1",
            ["Entra:AttributeSchemes:fat:Mappings:FAT"] = "rule-fat",
            ["Entra:GroupTeams:ExclusionGroup"] = "Kill Switch",
            ["Entra:GroupTeams:Instances:prague:InclusionGroup"] = "Prague ITS"
        }).Build();

        var result = EffectiveConfigResolver.Project(manifest, empty, reg);

        Assert.Empty(result.Teams);
        Assert.Empty(result.EntraSchemeMappings);
        Assert.Empty(result.EntraGroupTeamInstances);
        Assert.Null(result.EntraGlobalExclusionGroup);
    }

    // ── Entra structural projection (M4) ──────────────────────────────────────

    [Fact]
    public void Project_AttributeSchemes_ProjectsSchemeSelectorValueAndRulePerMapping()
    {
        var manifest = new ComponentManifest
        {
            ComponentName = "Test",
            HasEntraAttributeSchemes = true,
            Keys = new[] { Desc("Company") }
        };

        var empty = new ConfigurationBuilder().Build();
        var reg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Entra:AttributeSchemes:fat:Selector"] = "extensionAttribute1",
            ["Entra:AttributeSchemes:fat:Mappings:FAT"] = "extensionAttribute2-extensionAttribute4",
            ["Entra:AttributeSchemes:fat:Mappings:VDE"] = "extensionAttribute3",
            ["Entra:AttributeSchemes:vde-only:Selector"] = "extensionAttribute7",
            ["Entra:AttributeSchemes:vde-only:Mappings:VDE"] = "extensionAttribute8",
        }).Build();

        var result = EffectiveConfigResolver.Project(manifest, empty, reg);

        Assert.Equal(3, result.EntraSchemeMappings.Count);

        var fatFat = result.EntraSchemeMappings.Single(m => m.Scheme == "fat" && m.SelectorValue == "FAT");
        Assert.Equal("extensionAttribute1", fatFat.Selector);
        Assert.Equal("extensionAttribute2-extensionAttribute4", fatFat.Rule);

        var fatVde = result.EntraSchemeMappings.Single(m => m.Scheme == "fat" && m.SelectorValue == "VDE");
        Assert.Equal("extensionAttribute1", fatVde.Selector);
        Assert.Equal("extensionAttribute3", fatVde.Rule);

        var vdeOnly = result.EntraSchemeMappings.Single(m => m.Scheme == "vde-only");
        Assert.Equal("extensionAttribute7", vdeOnly.Selector);
        Assert.Equal("VDE", vdeOnly.SelectorValue);
        Assert.Equal("extensionAttribute8", vdeOnly.Rule);
    }

    [Fact]
    public void Project_GroupTeams_ProjectsInstancesAndGlobalExclusion()
    {
        var manifest = new ComponentManifest
        {
            ComponentName = "Test",
            HasEntraGroupTeams = true,
            Keys = new[] { Desc("Company") }
        };

        var empty = new ConfigurationBuilder().Build();
        var reg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Entra:GroupTeams:ExclusionGroup"] = "Fleet Kill Switch",
            ["Entra:GroupTeams:Instances:prague-its:InclusionGroup"] = "NewsCentral Prague ITS",
            ["Entra:GroupTeams:Instances:prague-its:ExclusionGroup"] = "Excluded Devices",
            ["Entra:GroupTeams:Instances:brno-qa:InclusionGroup"] = "NewsCentral Brno QA",
            // brno-qa has no per-instance ExclusionGroup key at all.
        }).Build();

        var result = EffectiveConfigResolver.Project(manifest, empty, reg);

        Assert.Equal("Fleet Kill Switch", result.EntraGlobalExclusionGroup);
        Assert.Equal(2, result.EntraGroupTeamInstances.Count);

        var prague = result.EntraGroupTeamInstances.Single(i => i.Label == "prague-its");
        Assert.Equal("NewsCentral Prague ITS", prague.InclusionGroup);
        Assert.Equal("Excluded Devices", prague.ExclusionGroup);

        var brno = result.EntraGroupTeamInstances.Single(i => i.Label == "brno-qa");
        Assert.Equal("NewsCentral Brno QA", brno.InclusionGroup);
        Assert.Null(brno.ExclusionGroup);
    }

    // ── Environment catalog projection (M3b) ──────────────────────────────────

    [Fact]
    public void Project_PopulatesEnvironmentCatalog_WhenManifestHasEnvironmentCatalog()
    {
        var manifest = new ComponentManifest
        {
            ComponentName = "Test",
            HasEnvironmentCatalog = true,
            Keys = new[] { Desc("Company") }
        };

        var empty = new ConfigurationBuilder().Build();
        var reg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Environments:Dev:DataPath"] = "C:\\Download\\NewsCentral"
        }).Build();

        var result = EffectiveConfigResolver.Project(manifest, empty, reg);

        Assert.NotNull(result.EnvironmentCatalog);
        Assert.Single(result.EnvironmentCatalog!.Entries);
        Assert.Equal("Dev", result.EnvironmentCatalog!.Entries[0].Name);
    }

    [Fact]
    public void Project_LeavesEnvironmentCatalogNull_WhenManifestFlagIsFalse()
    {
        var manifest = new ComponentManifest
        {
            ComponentName = "NewsService",
            HasEnvironmentCatalog = false,
            Keys = new[] { Desc("Company") }
        };

        var empty = new ConfigurationBuilder().Build();
        var reg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Present but irrelevant — NewsService's manifest doesn't project this hive.
            ["Environments:Dev:DataPath"] = "C:\\Download\\NewsCentral"
        }).Build();

        var result = EffectiveConfigResolver.Project(manifest, empty, reg);

        Assert.Null(result.EnvironmentCatalog);
    }

    // ── Resolve install-dir override (spec §4) ────────────────────────────────

    [Fact]
    public void Resolve_WithValidOverride_ReadsThatDirsAppsettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ncr-override-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"),
                "{ \"Company\": \"TestCo\", \"Service\": { \"PollIntervalSeconds\": \"77\" } }");

            var result = EffectiveConfigResolver.Resolve(ConfigManifests.NewsService, dir);

            Assert.Equal(dir, result.InstallDir);
            Assert.Equal("manual override (session)", result.DiscoverySource);
            Assert.Equal(Path.Combine(dir, "appsettings.json"), result.AppSettingsPath);
            Assert.True(result.AppSettingsExists);
            Assert.Equal(LayerStatus.Found, result.AppSettingsStatus);

            // Values are read from THAT directory's appsettings.json.
            Assert.Equal("TestCo",
                result.Rows.Single(r => r.Descriptor.CanonicalKey == "Company").AppSettingsValue);
            Assert.Equal("77",
                result.Rows.Single(r => r.Descriptor.CanonicalKey == "Service:PollIntervalSeconds").AppSettingsValue);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Resolve_WithEmptyOrNullOverride_FallsBackToDiscovery()
    {
        // NewsCentral discovery is deterministic: AppContext.BaseDirectory.
        var viaNull  = EffectiveConfigResolver.Resolve(ConfigManifests.NewsCentral, null);
        var viaEmpty = EffectiveConfigResolver.Resolve(ConfigManifests.NewsCentral, "   ");

        Assert.Equal("AppContext.BaseDirectory", viaNull.DiscoverySource);
        Assert.Equal("AppContext.BaseDirectory", viaEmpty.DiscoverySource);
        Assert.Equal(AppContext.BaseDirectory, viaNull.InstallDir);
        Assert.Equal(AppContext.BaseDirectory, viaEmpty.InstallDir);
    }

    [Fact]
    public void Resolve_WithNonExistentOverride_ResolvesEmpty_WithoutThrowing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ncr-missing-" + Guid.NewGuid().ToString("N"));

        var result = EffectiveConfigResolver.Resolve(ConfigManifests.NewsService, missing);

        Assert.Equal(missing, result.InstallDir);
        Assert.False(result.InstallDirExists);
        Assert.Equal(Path.Combine(missing, "appsettings.json"), result.AppSettingsPath);
        Assert.False(result.AppSettingsExists);
        Assert.Equal(LayerStatus.NotFound, result.AppSettingsStatus);

        // Rows still present (manifest surface); all *appsettings* values absent. The registry layer
        // is located by the compile-time SolutionConstants.Company constant and is machine-global —
        // it is independent of the (non-existent) install-dir override, so structural data sourced from
        // it (Teams etc.) is not asserted here; this test covers the appsettings layer.
        Assert.NotEmpty(result.Rows);
        Assert.All(result.Rows, row => Assert.Null(row.AppSettingsValue));
    }
}
