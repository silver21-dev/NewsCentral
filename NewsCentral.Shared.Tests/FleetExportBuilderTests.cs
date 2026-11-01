using System.Text.RegularExpressions;
using NewsCentral.Configuration.FleetExport;
using NewsCentral.Security;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class FleetExportBuilderTests
{
    private static FleetExportInput MakeInput(
        FleetExportDistribution? distribution = null,
        string? effectiveLocalDistributionPath = null,
        IReadOnlyList<FleetExportTeam>? teams = null,
        FleetExportSigning? signing = null) => new(
        Company: "Contoso",
        EnvironmentDisplayName: "Pilot",
        DataPath: @"C:\Download\NewsCentral",
        GeneratedAtUtc: new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
        Distribution: distribution ?? new FleetExportDistribution(true, "Local", null, "", "", "newscentral"),
        EffectiveLocalDistributionPath: effectiveLocalDistributionPath ?? @"\\fileserver\newscentral",
        Signing: signing ?? new FleetExportSigning("pub-current", null),
        Teams: teams ?? new List<FleetExportTeam>
        {
            new("cz-its", "CZ ITS")
        });

    [Fact]
    public void Build_LocalMode_UncPath_ExportsSharePath_NoPathWarning()
    {
        var input = MakeInput(effectiveLocalDistributionPath: @"\\fileserver\newscentral");

        var result = FleetExportBuilder.Build(input);

        Assert.Contains("StorageMode = 'Share'", result.PowerShell);
        Assert.Contains(@"SharePath   = '\\fileserver\newscentral'", result.PowerShell);
        Assert.Contains("\"StorageMode\"=\"Share\"", result.Reg);
        Assert.DoesNotContain(FleetExportBuilder.SharePathPlaceholder, result.PowerShell);
        Assert.DoesNotContain(FleetExportBuilder.SharePathPlaceholder, result.Reg);
        Assert.DoesNotContain(FleetExportBuilder.SharePathPlaceholder, result.Placeholders);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("UNC path"));
    }

    [Fact]
    public void Build_LocalMode_LocalPath_EmitsPlaceholderAndWarning()
    {
        var input = MakeInput(effectiveLocalDistributionPath: @"C:\Download\NewsCentral\_distribution");

        var result = FleetExportBuilder.Build(input);

        Assert.Contains(FleetExportBuilder.SharePathPlaceholder, result.PowerShell);
        Assert.Contains(FleetExportBuilder.SharePathPlaceholder, result.Reg);
        Assert.Contains(FleetExportBuilder.SharePathPlaceholder, result.Placeholders);
        Assert.Contains(result.Warnings, w => w.Contains("is local to this computer") && w.Contains("UNC path"));
    }

    [Fact]
    public void Build_AzureBlob_ExportsIdentifiers_ClientIdAndThumbprintArePlaceholders()
    {
        var distribution = new FleetExportDistribution(true, "AzureBlob", null, "tenant-guid-123", "stgacct1", "container1");
        var input = MakeInput(distribution: distribution);

        var result = FleetExportBuilder.Build(input);

        Assert.Contains("tenant-guid-123", result.PowerShell);
        Assert.Contains("stgacct1", result.PowerShell);
        Assert.Contains("container1", result.PowerShell);
        Assert.Contains("'Certificate'", result.PowerShell);
        Assert.Contains(FleetExportBuilder.FleetClientIdPlaceholder, result.PowerShell);
        Assert.Contains(FleetExportBuilder.CertThumbprintPlaceholder, result.PowerShell);

        Assert.Contains("tenant-guid-123", result.Reg);
        Assert.Contains("stgacct1", result.Reg);
        Assert.Contains("container1", result.Reg);
        Assert.Contains("\"AuthMode\"=\"Certificate\"", result.Reg);
        Assert.Contains(FleetExportBuilder.FleetClientIdPlaceholder, result.Reg);
        Assert.Contains(FleetExportBuilder.CertThumbprintPlaceholder, result.Reg);

        Assert.Contains(FleetExportBuilder.FleetClientIdPlaceholder, result.Placeholders);
        Assert.Contains(FleetExportBuilder.CertThumbprintPlaceholder, result.Placeholders);

        // The authoring app's ClientId is never even part of the input type, so it structurally
        // cannot leak into the export - FleetExportDistribution carries no ClientId property.
        var distributionProperties = typeof(FleetExportDistribution).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain("ClientId", distributionProperties);
    }

    [Fact]
    public void Build_DistributionDisabled_WarnsButStillProducesOutput()
    {
        var distribution = new FleetExportDistribution(false, "Local", null, "", "", "newscentral");
        var input = MakeInput(distribution: distribution);

        var result = FleetExportBuilder.Build(input);

        Assert.Contains(result.Warnings, w => w.Contains("Distribution is disabled"));
        Assert.False(string.IsNullOrWhiteSpace(result.PowerShell));
        Assert.False(string.IsNullOrWhiteSpace(result.Reg));
    }

    // ── M4: the environment signing key (flat) ───────────────────────────────

    [Fact]
    public void Build_KeyWithPrevious_EmitsFlatValuesForBothComponents_AndRequiresSignedIndex()
    {
        var result = FleetExportBuilder.Build(MakeInput(signing: new FleetExportSigning("pub-current", "pub-previous")));

        Assert.Equal(2, CountOccurrences(result.PowerShell, "SigningPublicKey = 'pub-current'"));
        Assert.Equal(2, CountOccurrences(result.PowerShell, "SigningPublicKeyPrevious = 'pub-previous'"));
        Assert.Equal(2, CountOccurrences(result.PowerShell, "RequireSignedIndex = $true"));
        Assert.Equal(2, CountOccurrences(result.Reg, "\"RequireSignedIndex\"=\"true\""));
        Assert.Equal(2, CountOccurrences(result.Reg, "\"PublicKey\"=\"pub-current\""));
        Assert.Equal(2, CountOccurrences(result.Reg, "\"PublicKeyPrevious\"=\"pub-previous\""));
        AssertNoPerTeamLayoutOrFalse(result);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("signing key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_KeyWithoutPrevious_MarksPreviousForRemoval()
    {
        var result = FleetExportBuilder.Build(MakeInput(signing: new FleetExportSigning("pub-current", null)));

        Assert.Equal(2, CountOccurrences(result.PowerShell, "SigningPublicKeyPrevious = ''"));
        Assert.Equal(2, CountOccurrences(result.Reg, "\"PublicKeyPrevious\"=-"));
        Assert.Equal(2, CountOccurrences(result.Reg, "\"PublicKey\"=\"pub-current\""));
        AssertNoPerTeamLayoutOrFalse(result);
    }

    [Fact]
    public void Build_NoKey_OmitsKeyLines_KeepsRequireSignedIndex_AndWarns()
    {
        var result = FleetExportBuilder.Build(MakeInput(signing: new FleetExportSigning(null, null)));

        Assert.DoesNotContain("SigningPublicKey", result.PowerShell);
        Assert.DoesNotContain("\"PublicKey\"", result.Reg);
        Assert.DoesNotContain("PublicKeyPrevious", result.Reg);
        Assert.Equal(2, CountOccurrences(result.PowerShell, "RequireSignedIndex = $true"));
        Assert.Equal(2, CountOccurrences(result.Reg, "\"RequireSignedIndex\"=\"true\""));
        Assert.Contains(result.Warnings, w =>
            w == "No environment signing key: devices will show no content (fail-closed). Generate one in Key Management.");
        AssertNoPerTeamLayoutOrFalse(result);
    }

    [Fact]
    public void Build_UnreadableKey_OmitsKeyLines_KeepsRequireSignedIndex_AndWarnsWithReason()
    {
        var result = FleetExportBuilder.Build(MakeInput(signing: new FleetExportSigning("ignored", "ignored", "access denied")));

        Assert.DoesNotContain("ignored", result.PowerShell);
        Assert.DoesNotContain("ignored", result.Reg);
        Assert.DoesNotContain("SigningPublicKey", result.PowerShell);
        Assert.Equal(2, CountOccurrences(result.PowerShell, "RequireSignedIndex = $true"));
        Assert.Equal(2, CountOccurrences(result.Reg, "\"RequireSignedIndex\"=\"true\""));
        var warning = Assert.Single(result.Warnings, w => w.StartsWith("Signing key not readable"));
        Assert.Equal(
            "Signing key not readable (access denied): this export contains no signing key; devices will show " +
            "no content until it is fixed and the export regenerated", warning);
        Assert.DoesNotContain(result.Warnings, w => w.StartsWith("No environment signing key"));
    }

    [Fact]
    public void Build_NeverContainsPrivateKeyMaterial_AndNoBase64LongerThanASpki()
    {
        var (priv, pub) = SigningKeyTool.GenerateKeyPair();
        var prevPub = SigningKeyTool.GenerateKeyPair().PublicKeyBase64;
        var result = FleetExportBuilder.Build(MakeInput(signing: new FleetExportSigning(pub, prevPub)));

        foreach (var text in new[] { result.PowerShell, result.Reg })
        {
            Assert.DoesNotContain(priv, text);
            Assert.DoesNotContain("privateKey", text, StringComparison.OrdinalIgnoreCase);
            // The longest Base64 run is an SPKI (91 bytes = 124 chars); a PKCS#8 private key is longer.
            var longest = Regex.Matches(text, "[A-Za-z0-9+/]{40,}={0,2}")
                .Select(m => m.Value.Length).DefaultIfEmpty(0).Max();
            Assert.True(longest <= pub.Length, $"found a Base64 run of {longest} chars, longer than an SPKI ({pub.Length})");
        }
        Assert.DoesNotContain("PrivateKey", typeof(FleetExportSigning).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("PrivateKey", typeof(FleetExportTeam).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public void Build_TeamsCarryNoKeyData()
    {
        Assert.Equal(new[] { "FolderName", "DisplayName" },
            typeof(FleetExportTeam).GetProperties().Select(p => p.Name).ToArray());
    }

    private static void AssertNoPerTeamLayoutOrFalse(FleetExport result)
    {
        Assert.DoesNotContain("TeamPublicKeys", result.PowerShell);
        Assert.DoesNotContain("TeamPreviousPublicKeys", result.PowerShell);
        Assert.DoesNotContain("RequireSignedIndex = $false", result.PowerShell);
        Assert.DoesNotContain("\"RequireSignedIndex\"=\"false\"", result.Reg);
        // No per-team Signing subkey: a section header would read ...\Signing\<team>].
        Assert.DoesNotMatch(@"\\Signing\\", result.Reg);
    }

    [Fact]
    public void Build_Reg_EscapesBackslashesAndQuotes_HeaderLineExact_AllValuesRegSz()
    {
        var input = MakeInput(effectiveLocalDistributionPath: @"\\server\share\path",
            signing: new FleetExportSigning("pub\"with\"quotes", null));

        var result = FleetExportBuilder.Build(input);

        Assert.StartsWith("Windows Registry Editor Version 5.00", result.Reg);
        var firstLine = result.Reg.Split('\n')[0].TrimEnd('\r');
        Assert.Equal("Windows Registry Editor Version 5.00", firstLine);

        // \\server\share\path -> each backslash doubled.
        Assert.Contains(@"""SharePath""=""\\\\server\\share\\path""", result.Reg);

        // An embedded quote is escaped as \".
        Assert.Contains(@"\""with\""", result.Reg);

        // Every value line uses the quoted "name"="value" form (REG_SZ) or the "=-" removal - never dword:/hex:.
        foreach (var line in result.Reg.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('"') && trimmed.Contains('='))
            {
                Assert.DoesNotContain("dword:", trimmed);
                Assert.DoesNotContain("hex:", trimmed);
            }
        }
    }

    [Fact]
    public void Build_PowerShell_ApostropheInTeamName_EscapedAndRoundTrips()
    {
        var teams = new List<FleetExportTeam> { new("o'brien-team", "O'Brien Team") };
        var input = MakeInput(teams: teams);

        var result = FleetExportBuilder.Build(input);

        Assert.Contains("'o''brien-team'", result.PowerShell);

        // Textual round-trip check (no System.Management.Automation reference in this project):
        // a single-quoted PowerShell string with an embedded ' must be written as ''.
        var quoted = "'o''brien-team'";
        var unescaped = quoted.Trim('\'').Replace("''", "'");
        Assert.Equal("o'brien-team", unescaped);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
