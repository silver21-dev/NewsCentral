using System.Text.Json;

namespace NewsCentral.Configuration;

public enum EnvironmentSettingsSource
{
    /// <summary>A valid config/environment.json was found and is authoritative.</summary>
    EnvironmentFile,

    /// <summary>No config/environment.json exists — effective settings come from this machine.</summary>
    MachineDefaults,

    /// <summary>
    /// config/environment.json exists but is unparseable, has an unsupported schemaVersion, or
    /// fails validation. Deliberately never falls back to machine defaults, which could publish
    /// to the wrong target — distribution becomes unavailable instead.
    /// </summary>
    Invalid,

    /// <summary>
    /// Reading config/environment.json itself threw (e.g. an unreachable UNC DataPath) — distinct
    /// from <see cref="Invalid"/>, which means the file was readable but malformed. M5a:
    /// NewsCentral.Services.EnvironmentSettingsService is the only producer of this value; the
    /// router treats it like Invalid for distribution purposes, and the UI shows a distinct message
    /// ("could not be read" vs. "is invalid") since the two causes call for different fixes.
    /// </summary>
    Unreachable
}

/// <summary>
/// <see cref="EnvironmentSettingsResolver.Resolve"/>'s result. <see cref="Settings"/> is null only
/// when <see cref="Source"/> is <see cref="EnvironmentSettingsSource.Invalid"/>.
/// <see cref="DistributionFingerprint"/> is not populated by <see cref="EnvironmentSettingsResolver.Resolve"/>
/// itself (that method has no DataPath to compute it from) — callers that have one combine it in,
/// typically via <c>result with { DistributionFingerprint = Fingerprint(result.Settings, dataPath) }</c>.
///
/// <see cref="PolicyEnvironmentName"/> and <see cref="PolicyFields"/> (M3b) are populated only by
/// NewsCentral.Services.EnvironmentSettingsService, after overlaying a matching
/// <see cref="PolicyEnvironment"/> via <see cref="EnvironmentSettingsResolver.ApplyPolicy"/> — never
/// by <see cref="Resolve"/> itself, which has no catalog to consult. Both default to "no policy
/// applied" (null / empty) so existing positional construction of this record is unaffected.
/// </summary>
public sealed record EffectiveEnvironmentSettings(
    EnvironmentSettingsSource Source,
    EnvironmentSettings? Settings,
    string? Error,
    string? DistributionFingerprint)
{
    /// <summary>
    /// The matching policy entry's name when one applied, "{Name} (implicit Default)" for the
    /// implicit Default entry, or null when no policy entry matched this DataPath.
    /// </summary>
    public string? PolicyEnvironmentName { get; init; }

    /// <summary>
    /// JSON-path-like names of exactly the fields the policy entry overrode (e.g.
    /// "distribution.localPath") — see <see cref="EnvironmentSettingsResolver.ApplyPolicy"/>. Empty
    /// when <see cref="PolicyEnvironmentName"/> is null.
    /// </summary>
    public IReadOnlyList<string> PolicyFields { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The settings BEFORE the policy overlay (M5a.1) — the file values for
    /// <see cref="EnvironmentSettingsSource.EnvironmentFile"/>, this machine's defaults for
    /// <see cref="EnvironmentSettingsSource.MachineDefaults"/>, null for
    /// <see cref="EnvironmentSettingsSource.Invalid"/> / <see cref="EnvironmentSettingsSource.Unreachable"/>.
    /// Populated only by NewsCentral.Services.EnvironmentSettingsService, which is the only place
    /// that has both a parsed file AND a policy catalog to overlay. Save flows must build the file
    /// they write from THIS, via <see cref="EnvironmentSettingsResolver.MergeForSave"/> — never from
    /// <see cref="Settings"/>, which already carries the policy overlay and would otherwise leak
    /// policy-controlled values into the stored file.
    /// </summary>
    public EnvironmentSettings? StoredSettings { get; init; }
}

/// <summary>
/// Pure, static, no I/O — parsing, validation and fingerprinting for per-environment distribution
/// settings. Reading/writing config/environment.json and caching live in
/// NewsCentral.Services.EnvironmentSettingsService (M3a commit 2); this type never touches disk.
/// </summary>
public static class EnvironmentSettingsResolver
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Resolves the effective settings from the raw file contents (null when the file is absent)
    /// and this machine's defaults. Never throws — a malformed or invalid file produces
    /// <see cref="EnvironmentSettingsSource.Invalid"/>, never a fallback to <paramref name="machineDefaults"/>.
    /// </summary>
    public static EffectiveEnvironmentSettings Resolve(string? fileJson, EnvironmentSettings machineDefaults)
    {
        if (fileJson == null)
            return new EffectiveEnvironmentSettings(
                EnvironmentSettingsSource.MachineDefaults, machineDefaults, Error: null, DistributionFingerprint: null);

        EnvironmentSettings? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<EnvironmentSettings>(fileJson, EnvironmentSettingsJson.Options);
        }
        catch (JsonException ex)
        {
            return new EffectiveEnvironmentSettings(
                EnvironmentSettingsSource.Invalid, Settings: null, Error: ex.Message, DistributionFingerprint: null);
        }

        if (parsed == null)
            return new EffectiveEnvironmentSettings(
                EnvironmentSettingsSource.Invalid, Settings: null,
                Error: "environment.json parsed to an empty document.", DistributionFingerprint: null);

        var errors = Validate(parsed);
        if (errors.Count > 0)
            return new EffectiveEnvironmentSettings(
                EnvironmentSettingsSource.Invalid, Settings: null,
                Error: string.Join("; ", errors), DistributionFingerprint: null);

        return new EffectiveEnvironmentSettings(
            EnvironmentSettingsSource.EnvironmentFile, parsed, Error: null, DistributionFingerprint: null);
    }

    /// <summary>
    /// Validation errors for a parsed <see cref="EnvironmentSettings"/>. Empty means valid.
    /// A bad SchemaVersion short-circuits — the rest of the document's shape is meaningless
    /// against a schema this build doesn't understand.
    /// </summary>
    public static IReadOnlyList<string> Validate(EnvironmentSettings settings)
    {
        var errors = new List<string>();

        if (settings.SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add(settings.SchemaVersion > CurrentSchemaVersion
                ? $"environment.json was written by a newer version of NewsCentral (schemaVersion {settings.SchemaVersion})."
                : $"environment.json has an unsupported schemaVersion ({settings.SchemaVersion}); expected {CurrentSchemaVersion}.");
            return errors;
        }

        if (!string.IsNullOrWhiteSpace(settings.SecurePath) && !Path.IsPathRooted(settings.SecurePath))
            errors.Add("SecurePath must be an absolute path (or left blank for {DataPath}\\secure).");

        if (settings.Distribution.Enabled)
        {
            var mode = settings.Distribution.Mode;
            var isLocal = string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase);
            var isAzure = string.Equals(mode, "AzureBlob", StringComparison.OrdinalIgnoreCase);

            if (!isLocal && !isAzure)
            {
                errors.Add($"Distribution.Mode must be \"Local\" or \"AzureBlob\" (was \"{mode}\").");
            }
            else if (isAzure)
            {
                var az = settings.Distribution.AzureBlob;
                if (string.IsNullOrWhiteSpace(az.AccountName))
                    errors.Add("Distribution.AzureBlob.AccountName is required when Mode is \"AzureBlob\".");
                if (string.IsNullOrWhiteSpace(az.ClientId))
                    errors.Add("Distribution.AzureBlob.ClientId is required when Mode is \"AzureBlob\".");
                if (string.IsNullOrWhiteSpace(az.ContainerName))
                    errors.Add("Distribution.AzureBlob.ContainerName is required when Mode is \"AzureBlob\".");
            }
            // Local mode: LocalPath may be empty — falls back to DataPath\_distribution (see Fingerprint).
        }

        return errors;
    }

    /// <summary>
    /// Where the secure key store lives: the configured SecurePath, else <c>{dataPath}\secure</c>. Also the
    /// answer for an environment without environment.json (machine defaults carry no SecurePath).
    /// </summary>
    public static string EffectiveSecurePath(EnvironmentSettings? settings, string dataPath) =>
        string.IsNullOrWhiteSpace(settings?.SecurePath)
            ? Path.Combine(dataPath, "secure")
            : settings.SecurePath;

    /// <summary>
    /// A stable identifier for where distribution publishes to, or null when distribution is
    /// disabled. Two settings with the same effective target (even differently cased or with a
    /// trailing separator, for Local) produce equal fingerprints — this is what later milestones
    /// compare to detect two environments pointed at the same place.
    /// </summary>
    public static string? Fingerprint(EnvironmentSettings settings, string dataPath)
    {
        if (!settings.Distribution.Enabled)
            return null;

        if (string.Equals(settings.Distribution.Mode, "AzureBlob", StringComparison.OrdinalIgnoreCase))
        {
            var az = settings.Distribution.AzureBlob;
            return $"blob:{az.AccountName}/{az.ContainerName}".ToLowerInvariant();
        }

        var root = string.IsNullOrWhiteSpace(settings.Distribution.LocalPath)
            ? Path.Combine(dataPath, "_distribution")
            : settings.Distribution.LocalPath;

        return $"local:{Path.GetFullPath(root)}".ToLowerInvariant();
    }

    /// <summary>
    /// Applies a matching policy entry's per-field overrides (M3b) onto <paramref name="baseSettings"/>,
    /// which is never mutated — a fresh, deep copy is built and only the fields the policy entry
    /// actually sets (non-null on <see cref="PolicyEnvironment"/>) are replaced on it.
    /// <paramref name="baseSettings"/> is typically the M3a result (environment file, or machine
    /// defaults when absent); if that result was already <see cref="EnvironmentSettingsSource.Invalid"/>,
    /// the caller must not call this at all — policy never rescues a corrupt file.
    /// </summary>
    /// <returns>
    /// The overlaid settings, plus the JSON-path-like names of exactly the fields policy overrode
    /// (environment.json's own field names, e.g. <c>"distribution.localPath"</c>), in a fixed order.
    /// </returns>
    public static (EnvironmentSettings Settings, IReadOnlyList<string> PolicyFields) ApplyPolicy(
        EnvironmentSettings baseSettings, PolicyEnvironment policy)
    {
        var fields = new List<string>();

        var result = new EnvironmentSettings
        {
            SchemaVersion = baseSettings.SchemaVersion,
            DisplayName = baseSettings.DisplayName,
            SecurePath = baseSettings.SecurePath,
            Distribution = new DistributionSettings
            {
                Enabled = baseSettings.Distribution.Enabled,
                Mode = baseSettings.Distribution.Mode,
                LocalPath = baseSettings.Distribution.LocalPath,
                AzureBlob = new AzureBlobSettings
                {
                    TenantId = baseSettings.Distribution.AzureBlob.TenantId,
                    ClientId = baseSettings.Distribution.AzureBlob.ClientId,
                    AccountName = baseSettings.Distribution.AzureBlob.AccountName,
                    ContainerName = baseSettings.Distribution.AzureBlob.ContainerName
                }
            },
            ModifiedBy = baseSettings.ModifiedBy,
            ModifiedUtc = baseSettings.ModifiedUtc
        };

        if (policy.DisplayName != null)
        {
            result.DisplayName = policy.DisplayName;
            fields.Add("displayName");
        }

        if (policy.EnableBlobDistribution.HasValue)
        {
            result.Distribution.Enabled = policy.EnableBlobDistribution.Value;
            fields.Add("distribution.enabled");
        }

        if (policy.DistributionMode != null)
        {
            result.Distribution.Mode = policy.DistributionMode;
            fields.Add("distribution.mode");
        }

        if (policy.LocalDistributionPath != null)
        {
            result.Distribution.LocalPath = policy.LocalDistributionPath;
            fields.Add("distribution.localPath");
        }

        if (policy.AzureBlobContainerName != null)
        {
            result.Distribution.AzureBlob.ContainerName = policy.AzureBlobContainerName;
            fields.Add("distribution.azureBlob.containerName");
        }

        if (policy.AzureTenantId != null)
        {
            result.Distribution.AzureBlob.TenantId = policy.AzureTenantId;
            fields.Add("distribution.azureBlob.tenantId");
        }

        if (policy.AzureClientId != null)
        {
            result.Distribution.AzureBlob.ClientId = policy.AzureClientId;
            fields.Add("distribution.azureBlob.clientId");
        }

        if (policy.AzureAccountName != null)
        {
            result.Distribution.AzureBlob.AccountName = policy.AzureAccountName;
            fields.Add("distribution.azureBlob.accountName");
        }

        return (result, fields);
    }

    /// <summary>
    /// Builds the settings to actually WRITE on Save (M5a.1): starts from <paramref name="form"/>
    /// (the editor's current values) but replaces every field named in <paramref name="policyFields"/>
    /// with that field's value from <paramref name="stored"/> (the pre-policy-overlay settings this
    /// environment already had — see <see cref="EffectiveEnvironmentSettings.StoredSettings"/>), or
    /// from <paramref name="machineDefaults"/> when <paramref name="stored"/> is null (there was no
    /// stored environment.json to preserve yet). This is what prevents a policy-locked field — shown
    /// read-only in the editor at its EFFECTIVE (policy) value — from being written into
    /// environment.json: policy keeps overriding it on every read regardless, so persisting it would
    /// only cause drift the moment the policy entry is ever removed. Never mutates
    /// <paramref name="form"/>, <paramref name="stored"/>, or <paramref name="machineDefaults"/>.
    /// Uses the exact same field names as <see cref="ApplyPolicy"/> produces in its PolicyFields list.
    /// </summary>
    public static EnvironmentSettings MergeForSave(
        EnvironmentSettings form,
        EnvironmentSettings? stored,
        EnvironmentSettings machineDefaults,
        IReadOnlyList<string> policyFields)
    {
        var baseline = stored ?? machineDefaults;

        var result = new EnvironmentSettings
        {
            SchemaVersion = form.SchemaVersion,
            DisplayName = form.DisplayName,
            SecurePath = form.SecurePath,
            Distribution = new DistributionSettings
            {
                Enabled = form.Distribution.Enabled,
                Mode = form.Distribution.Mode,
                LocalPath = form.Distribution.LocalPath,
                AzureBlob = new AzureBlobSettings
                {
                    TenantId = form.Distribution.AzureBlob.TenantId,
                    ClientId = form.Distribution.AzureBlob.ClientId,
                    AccountName = form.Distribution.AzureBlob.AccountName,
                    ContainerName = form.Distribution.AzureBlob.ContainerName
                }
            },
            ModifiedBy = form.ModifiedBy,
            ModifiedUtc = form.ModifiedUtc
        };

        if (policyFields.Contains("displayName"))
            result.DisplayName = baseline.DisplayName;

        if (policyFields.Contains("distribution.enabled"))
            result.Distribution.Enabled = baseline.Distribution.Enabled;

        if (policyFields.Contains("distribution.mode"))
            result.Distribution.Mode = baseline.Distribution.Mode;

        if (policyFields.Contains("distribution.localPath"))
            result.Distribution.LocalPath = baseline.Distribution.LocalPath;

        if (policyFields.Contains("distribution.azureBlob.containerName"))
            result.Distribution.AzureBlob.ContainerName = baseline.Distribution.AzureBlob.ContainerName;

        if (policyFields.Contains("distribution.azureBlob.tenantId"))
            result.Distribution.AzureBlob.TenantId = baseline.Distribution.AzureBlob.TenantId;

        if (policyFields.Contains("distribution.azureBlob.clientId"))
            result.Distribution.AzureBlob.ClientId = baseline.Distribution.AzureBlob.ClientId;

        if (policyFields.Contains("distribution.azureBlob.accountName"))
            result.Distribution.AzureBlob.AccountName = baseline.Distribution.AzureBlob.AccountName;

        return result;
    }

    /// <summary>
    /// Maps this machine's existing appsettings.json/registry values (Storage:*, AzureBlob:*) into
    /// an EnvironmentSettings shape — the defaults used when no environment.json exists, and what
    /// "Save to environment" writes verbatim.
    /// </summary>
    public static EnvironmentSettings FromMachineConfiguration(AppConfiguration config) => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        DisplayName = null,
        Distribution = new DistributionSettings
        {
            Enabled = config.EnableBlobDistribution,
            Mode = config.DistributionMode,
            LocalPath = string.IsNullOrEmpty(config.LocalDistributionPath) ? null : config.LocalDistributionPath,
            AzureBlob = new AzureBlobSettings
            {
                TenantId = config.AzureBlobTenantId,
                ClientId = config.AzureBlobClientId,
                AccountName = config.AzureBlobAccountName,
                ContainerName = config.AzureBlobContainerName
            }
        },
        ModifiedBy = null,
        ModifiedUtc = null
    };
}
