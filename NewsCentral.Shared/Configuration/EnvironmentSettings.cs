using System.Text.Json;

namespace NewsCentral.Configuration;

/// <summary>
/// Contents of {DataPath}\config\environment.json — the per-environment distribution settings
/// introduced in M3a. When present, this file is authoritative for distribution (no per-field
/// merge with machine settings); when absent, the effective settings come from this machine's
/// configuration instead (see <see cref="EnvironmentSettingsResolver.FromMachineConfiguration"/>).
/// No secrets live here — Azure values are identifiers only, authentication stays interactive.
/// </summary>
public sealed class EnvironmentSettings
{
    public int SchemaVersion { get; set; } = EnvironmentSettingsResolver.CurrentSchemaVersion;

    public string? DisplayName { get; set; }

    public DistributionSettings Distribution { get; set; } = new();

    /// <summary>
    /// Root of the secure store for team signing keys (<c>{SecurePath}\keys\{team}\team-signing.json</c>).
    /// Null or empty = <c>{DataPath}\secure</c> - see <see cref="EnvironmentSettingsResolver.EffectiveSecurePath"/>.
    /// Changing it does not move existing keys. NTFS ACLs on it are applied by scripts/Set-AuthoringAcl.ps1, never by the app.
    /// </summary>
    public string? SecurePath { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedUtc { get; set; }
}

public sealed class DistributionSettings
{
    public bool Enabled { get; set; }

    /// <summary>"Local" or "AzureBlob" — compared case-insensitively by the resolver.</summary>
    public string Mode { get; set; } = "Local";

    /// <summary>
    /// Local mode only. Empty/null falls back to {DataPath}\_distribution — see
    /// <see cref="EnvironmentSettingsResolver.Fingerprint"/>.
    /// </summary>
    public string? LocalPath { get; set; }

    public AzureBlobSettings AzureBlob { get; set; } = new();
}

/// <summary>
/// Identifiers only — no secrets. Authentication to Azure Blob remains interactive (MSAL);
/// nothing here is a credential.
/// </summary>
public sealed class AzureBlobSettings
{
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "newscentral";
}

/// <summary>
/// The single JSON convention for environment.json: camelCase, indented. Unknown properties are
/// ignored (System.Text.Json's default), so an older NewsCentral build never fails to read a file
/// written by a newer one that added fields it doesn't know about — SchemaVersion handles the
/// cases that actually need to be rejected (see EnvironmentSettingsResolver.Validate).
/// </summary>
public static class EnvironmentSettingsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
