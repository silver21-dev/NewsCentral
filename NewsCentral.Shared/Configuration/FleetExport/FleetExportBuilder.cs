using System.Globalization;
using System.Text;

namespace NewsCentral.Configuration.FleetExport;

/// <summary>
/// M6: the effective distribution target for one environment — carries exactly the fields the
/// export needs (Enabled, Mode, LocalPath, and the Azure identifiers TenantId/AccountName/
/// ContainerName). Deliberately has NO ClientId or any credential field: the fleet authenticates
/// with its OWN app registration and certificate (docs/production-deployment.md), never the
/// authoring app's — so there is no field here for an authoring ClientId to leak through even by
/// mistake.
/// </summary>
public sealed record FleetExportDistribution(
    bool Enabled,
    string Mode,
    string? LocalPath,
    string TenantId,
    string AccountName,
    string ContainerName);

/// <summary>
/// M6/M4: one team's exportable identity - folder name and display name only. Signing keys are per environment
/// now (<see cref="FleetExportSigning"/>), never per team.
/// </summary>
public sealed record FleetExportTeam(string FolderName, string DisplayName);

/// <summary>
/// M4: the environment's PUBLIC signing keys as the GPO must hold them (from EnvironmentKeyService.GpoTarget, so the
/// export always matches Key Management). Deliberately has no private-key field: the key file's private keys must
/// never reach this export (see docs/security.md's authoring-tier-only trust boundary).
/// <see cref="PublicKeyPrevious"/> null means "remove it". <see cref="KeyUnreadableReason"/> set means the key file
/// exists but could not be read; the export then carries no key.
/// </summary>
public sealed record FleetExportSigning(string? PublicKey, string? PublicKeyPrevious, string? KeyUnreadableReason = null);

/// <summary>
/// M6: pure input to <see cref="FleetExportBuilder.Build"/> - no I/O, no secrets. The caller
/// (Environment Management) is responsible for resolving <see cref="EffectiveLocalDistributionPath"/>
/// (including the {DataPath}\_distribution fallback - see EnvironmentSettingsResolver.Fingerprint /
/// EnvironmentSettingsService.DescribeDistributionTarget) before building this.
/// </summary>
public sealed record FleetExportInput(
    string Company,
    string EnvironmentDisplayName,
    string DataPath,
    DateTime GeneratedAtUtc,
    FleetExportDistribution Distribution,
    string EffectiveLocalDistributionPath,
    FleetExportSigning Signing,
    IReadOnlyList<FleetExportTeam> Teams);

/// <summary>Result of <see cref="FleetExportBuilder.Build"/>.</summary>
public sealed class FleetExport
{
    public string PowerShell { get; init; } = string.Empty;
    public string Reg { get; init; } = string.Empty;
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Placeholders { get; init; } = Array.Empty<string>();
}

/// <summary>
/// M6: builds the device-fleet registry settings (NewsService + NewsViewer) for one environment,
/// as a Set-RegistryOverrides.ps1 invocation (dev/pilot machines) and a .reg file (GPO hand-off).
/// Pure — no I/O, no secrets. See docs/configuration.md for the registry surface this mirrors and
/// docs/production-deployment.md for why the fleet's own app registration/certificate are used
/// instead of anything from the authoring environment.
/// </summary>
public static class FleetExportBuilder
{
    public const string FleetClientIdPlaceholder = "<FLEET-CLIENT-ID>";
    public const string CertThumbprintPlaceholder = "<CERT-THUMBPRINT>";
    public const string SharePathPlaceholder = @"<\\server\share\path>";

    private static readonly string[] NotExportedSurface =
        { "Entra", "Delivery", "Telemetry", "Logging", "Hmac", "Display", "Ui" };

    public static FleetExport Build(FleetExportInput input)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));

        var warnings = new List<string>();
        var placeholders = new List<string>();

        var isAzure = string.Equals(input.Distribution.Mode, "AzureBlob", StringComparison.OrdinalIgnoreCase);

        var sharePath = string.Empty;
        if (!isAzure)
        {
            if (IsUncPath(input.EffectiveLocalDistributionPath))
            {
                sharePath = input.EffectiveLocalDistributionPath;
            }
            else
            {
                sharePath = SharePathPlaceholder;
                placeholders.Add(SharePathPlaceholder);
                warnings.Add(
                    $"The distribution path {input.EffectiveLocalDistributionPath} is local to this computer; " +
                    "devices need a UNC path to the same folder.");
            }
        }
        else
        {
            placeholders.Add(FleetClientIdPlaceholder);
            placeholders.Add(CertThumbprintPlaceholder);
        }

        if (!input.Distribution.Enabled)
        {
            warnings.Add(
                "Distribution is disabled for this environment; devices will receive nothing until it is enabled.");
        }

        // An unreadable key is not a missing key: the file exists but could not be read, so the warning must say so
        // (and must not suggest generating one). In both cases the export omits the key lines but still requires
        // signed indexes (fail-closed).
        var signing = input.Signing;
        if (signing.KeyUnreadableReason is not null)
        {
            signing = new FleetExportSigning(null, null, signing.KeyUnreadableReason);
            warnings.Add(
                $"Signing key not readable ({signing.KeyUnreadableReason}): this export contains no signing key; " +
                "devices will show no content until it is fixed and the export regenerated");
        }
        else if (string.IsNullOrWhiteSpace(signing.PublicKey))
        {
            signing = new FleetExportSigning(null, null);
            warnings.Add(
                "No environment signing key: devices will show no content (fail-closed). Generate one in Key Management.");
        }

        var headerLines = BuildHeaderLines(input, warnings, placeholders);

        return new FleetExport
        {
            PowerShell = BuildPowerShell(input, isAzure, sharePath, signing, headerLines),
            Reg = BuildReg(input, isAzure, sharePath, signing, headerLines),
            Warnings = warnings,
            Placeholders = placeholders.Distinct(StringComparer.Ordinal).ToList()
        };
    }

    private static bool IsUncPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.StartsWith(@"\\", StringComparison.Ordinal);

    // ── Shared header content ───────────────────────────────────────────────

    private static List<string> BuildHeaderLines(
        FleetExportInput input, IReadOnlyList<string> warnings, IReadOnlyList<string> placeholders)
    {
        var lines = new List<string>
        {
            "NewsCentral — Fleet settings export",
            $"Environment: {input.EnvironmentDisplayName}",
            $"DataPath: {input.DataPath}",
            $"Generated: {input.GeneratedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)} (UTC)",
            "",
            "Fleet settings for NewsService and NewsViewer.",
            ""
        };

        if (placeholders.Count > 0)
        {
            lines.Add("PLACEHOLDERS - replace before use:");
            foreach (var placeholder in placeholders)
                lines.Add($"  {placeholder}");
            lines.Add("");
        }

        if (warnings.Count > 0)
        {
            lines.Add("WARNINGS:");
            foreach (var warning in warnings)
                lines.Add($"  - {warning}");
            lines.Add("");
        }

        lines.Add("NOT EXPORTED - configure by hand per docs/configuration.md if needed:");
        lines.Add($"  {string.Join(", ", NotExportedSurface)}");
        lines.Add("The fleet authenticates with its OWN app registration and certificate - the");
        lines.Add("authoring app's ClientId and any auth material are never exported.");

        return lines;
    }

    private static string CommentBlock(IEnumerable<string> lines, string prefix) =>
        string.Join(Environment.NewLine, lines.Select(line =>
            string.IsNullOrEmpty(line) ? prefix.TrimEnd() : $"{prefix}{line}"));

    // ── PowerShell output ────────────────────────────────────────────────────

    private static string BuildPowerShell(
        FleetExportInput input, bool isAzure, string sharePath,
        FleetExportSigning signing, List<string> headerLines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#Requires -RunAsAdministrator");
        sb.AppendLine(CommentBlock(headerLines, "# "));
        sb.AppendLine("#");
        sb.AppendLine("# Run from the repository's scripts\\ folder (or adjust the path below) - this");
        sb.AppendLine("# calls .\\Set-RegistryOverrides.ps1, which must sit alongside this file.");
        sb.AppendLine();

        sb.AppendLine("$NewsServiceParams = @{");
        sb.AppendLine($"    Company       = {PsQuote(input.Company)}");
        sb.AppendLine("    ComponentName = 'NewsService'");
        if (isAzure)
        {
            sb.AppendLine("    StorageMode                = 'Azure'");
            sb.AppendLine($"    AzureTenantId              = {PsQuote(input.Distribution.TenantId)}");
            sb.AppendLine($"    AzureClientId              = {PsQuote(FleetClientIdPlaceholder)}");
            sb.AppendLine($"    AzureAccountName           = {PsQuote(input.Distribution.AccountName)}");
            sb.AppendLine($"    AzureContainerName         = {PsQuote(input.Distribution.ContainerName)}");
            sb.AppendLine("    AzureAuthMode              = 'Certificate'");
            sb.AppendLine($"    AzureCertificateThumbprint = {PsQuote(CertThumbprintPlaceholder)}");
        }
        else
        {
            sb.AppendLine("    StorageMode = 'Share'");
            sb.AppendLine($"    SharePath   = {PsQuote(sharePath)}");
        }
        sb.AppendLine("    RequireSignedIndex = $true");
        AppendSigningParams(sb, signing);
        sb.AppendLine("}");
        sb.AppendLine(".\\Set-RegistryOverrides.ps1 @NewsServiceParams");
        sb.AppendLine();

        sb.AppendLine("$NewsViewerParams = @{");
        sb.AppendLine($"    Company       = {PsQuote(input.Company)}");
        sb.AppendLine("    ComponentName = 'NewsViewer'");
        sb.AppendLine("    RequireSignedIndex = $true");
        AppendSigningParams(sb, signing);
        sb.AppendLine("}");
        sb.AppendLine(".\\Set-RegistryOverrides.ps1 @NewsViewerParams");
        sb.AppendLine();

        sb.AppendLine("# Example only - NOT written by this export (teams\\ stays authoritative via GPO,");
        sb.AppendLine("# or run this script again with -Teams):");
        var teamList = string.Join(",", input.Teams.Select(t => PsQuote(t.FolderName)));
        sb.AppendLine(
            $"# .\\Set-RegistryOverrides.ps1 -Company {PsQuote(input.Company)} " +
            $"-ComponentName NewsService -Teams @({teamList})");

        return sb.ToString();
    }

    private static void AppendSigningParams(StringBuilder sb, FleetExportSigning signing)
    {
        if (string.IsNullOrWhiteSpace(signing.PublicKey))
            return;   // no key (or unreadable): omit the key lines; RequireSignedIndex stays true (fail-closed)

        sb.AppendLine($"    SigningPublicKey = {PsQuote(signing.PublicKey)}");
        // '' removes the value (Set-RegistryOverrides.ps1), so an old previous key does not linger after Retire.
        sb.AppendLine($"    SigningPublicKeyPrevious = {PsQuote(signing.PublicKeyPrevious ?? string.Empty)}");
    }

    /// <summary>Single-quoted PowerShell string literal — embedded <c>'</c> doubled.</summary>
    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    // ── .reg output ──────────────────────────────────────────────────────────

    private static string BuildReg(
        FleetExportInput input, bool isAzure, string sharePath,
        FleetExportSigning signing, List<string> headerLines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
        sb.AppendLine();
        sb.AppendLine(CommentBlock(headerLines, "; "));
        sb.AppendLine();

        var serviceBase = $@"HKEY_LOCAL_MACHINE\SOFTWARE\{input.Company}\NewsCentral\NewsService";
        var viewerBase = $@"HKEY_LOCAL_MACHINE\SOFTWARE\{input.Company}\NewsCentral\NewsViewer";

        if (isAzure)
        {
            sb.AppendLine($"[{serviceBase}\\Repository]");
            sb.AppendLine("\"StorageMode\"=\"Azure\"");
            sb.AppendLine();
            sb.AppendLine($"[{serviceBase}\\AzureBlob]");
            sb.AppendLine($"\"TenantId\"=\"{RegEscape(input.Distribution.TenantId)}\"");
            sb.AppendLine($"\"ClientId\"=\"{RegEscape(FleetClientIdPlaceholder)}\"");
            sb.AppendLine($"\"AccountName\"=\"{RegEscape(input.Distribution.AccountName)}\"");
            sb.AppendLine($"\"ContainerName\"=\"{RegEscape(input.Distribution.ContainerName)}\"");
            sb.AppendLine("\"AuthMode\"=\"Certificate\"");
            sb.AppendLine($"\"CertificateThumbprint\"=\"{RegEscape(CertThumbprintPlaceholder)}\"");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine($"[{serviceBase}\\Repository]");
            sb.AppendLine("\"StorageMode\"=\"Share\"");
            sb.AppendLine($"\"SharePath\"=\"{RegEscape(sharePath)}\"");
            sb.AppendLine();
        }

        AppendSigningSection(sb, serviceBase, signing);
        AppendTeamsExampleComment(sb, serviceBase, input.Teams);

        AppendSigningSection(sb, viewerBase, signing);
        AppendTeamsExampleComment(sb, viewerBase, input.Teams);

        return sb.ToString();
    }

    private static void AppendSigningSection(StringBuilder sb, string componentBase, FleetExportSigning signing)
    {
        sb.AppendLine($"[{componentBase}\\Signing]");
        sb.AppendLine("\"RequireSignedIndex\"=\"true\"");
        if (!string.IsNullOrWhiteSpace(signing.PublicKey))
        {
            sb.AppendLine($"\"PublicKey\"=\"{RegEscape(signing.PublicKey)}\"");
            // "=-" deletes the value on import, so an old previous key does not linger after Retire.
            sb.AppendLine(string.IsNullOrWhiteSpace(signing.PublicKeyPrevious)
                ? "\"PublicKeyPrevious\"=-"
                : $"\"PublicKeyPrevious\"=\"{RegEscape(signing.PublicKeyPrevious)}\"");
        }
        sb.AppendLine();
    }

    private static void AppendTeamsExampleComment(StringBuilder sb, string componentBase, IReadOnlyList<FleetExportTeam> teams)
    {
        sb.AppendLine("; Example only - NOT written by this export (teams\\ stays authoritative via GPO):");
        sb.AppendLine($"; [{componentBase}\\teams]");
        foreach (var team in teams)
            sb.AppendLine($"; \"{RegEscape(team.FolderName)}\"=\"\"");
        sb.AppendLine();
    }

    /// <summary>.reg value-string escaping: backslashes doubled, then quotes escaped.</summary>
    private static string RegEscape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
