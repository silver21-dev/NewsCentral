using Microsoft.Extensions.Configuration;

namespace NewsCentral.Configuration;

public class AppConfiguration
{
    private readonly IConfiguration _configuration;

    public AppConfiguration(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    // Company defines the registry hive path and is a build-time constant, not configuration.
    public string Company => SolutionConstants.Company;

    // ── Authoring tier ───────────────────────────────────────────────────────
    // DataPath is the root for IStorageService / LocalStorageService.
    // When using Azure Files, mount the share as a drive and set DataPath to it.

    public string DataPath             => _configuration["DataPath"] ?? string.Empty;

    // ── Distribution tier (IBlobDistributionService) ─────────────────────────
    //
    // M3a: these five properties are now consumed only by
    // EnvironmentSettingsResolver.FromMachineConfiguration, as the defaults used when no
    // config/environment.json exists for the current DataPath. DistributionServiceRouter and
    // AzureBlobDistributionService read the resolved EnvironmentSettings instead of this class
    // directly. Kept here (not removed) because FromMachineConfiguration still needs them.
    //
    // EnableBlobDistribution = false → NullBlobDistributionService (logs only)
    // EnableBlobDistribution = true
    //   DistributionMode = "Local"    → LocalBlobDistributionService
    //   DistributionMode = "AzureBlob"→ AzureBlobDistributionService
    //
    // Recommended progression:
    //   Dev start  : EnableBlobDistribution=false
    //   Dev/test   : EnableBlobDistribution=true, DistributionMode=Local
    //   Production : EnableBlobDistribution=true, DistributionMode=AzureBlob

    public bool   EnableBlobDistribution  =>
        bool.TryParse(_configuration["Storage:EnableBlobDistribution"], out var b) && b;

    public string DistributionMode        =>
        _configuration["Storage:DistributionMode"] ?? "Local";

    /// <summary>
    /// Root folder for LocalBlobDistributionService.
    /// MUST differ from DataPath — the separation is the point.
    /// Falls back to DataPath\_distribution if not set.
    /// </summary>
    public string LocalDistributionPath   =>
        _configuration["Storage:LocalDistributionPath"] ?? string.Empty;

    public string AzureBlobContainerName =>
        _configuration["Storage:AzureBlobContainerName"] ?? "newscentral";

    // ── Azure Blob MSAL identity (interactive user session) ──────────────────
    public string AzureBlobTenantId    => _configuration["AzureBlob:TenantId"]    ?? string.Empty;
    public string AzureBlobClientId    => _configuration["AzureBlob:ClientId"]    ?? string.Empty;
    public string AzureBlobAccountName => _configuration["AzureBlob:AccountName"] ?? string.Empty;

    // Local (username/password) accounts are opt-in. REG_DWORD 0/1 reaches here as "False"/"True",
    // which bool.TryParse accepts; absent or unparseable is false.
    public bool EnableLocalAccounts =>
        bool.TryParse(_configuration["Authentication:EnableLocalAccounts"], out var b) && b;

    /// <summary>
    /// Policy override for auditing: the registry value Audit\Enabled (REG_DWORD 0/1, which arrives as
    /// "False"/"True"). Null when absent or unparseable, in which case the environment's own setting applies.
    /// Deliberately not defaulted in appsettings.json: "present" is what makes the UI toggle read "set by policy".
    /// </summary>
    public bool? AuditEnabledOverride =>
        bool.TryParse(_configuration["Audit:Enabled"], out var b) ? b : null;

    public string HmacSecretKey => _configuration["Hmac:SecretKey"] ?? string.Empty;
}
