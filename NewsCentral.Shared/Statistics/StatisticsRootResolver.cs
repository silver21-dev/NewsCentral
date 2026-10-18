using NewsCentral.Configuration;

namespace NewsCentral.Statistics;

/// <param name="DistRoot">The local distribution root, or null when statistics are not available.</param>
/// <param name="NotAvailableReason">Set exactly when <paramref name="DistRoot"/> is null.</param>
public sealed record StatsRootResolution(string? DistRoot, string? NotAvailableReason);

/// <summary>
/// Decides whether poster statistics can run for an environment and where its flat inbox lives. The
/// local-root rule is the one DistributionServiceRouter and EnvironmentSettingsResolver.Fingerprint use:
/// Distribution.LocalPath, else {DataPath}\_distribution. Pure, no I/O.
/// </summary>
public static class StatisticsRootResolver
{
    public static StatsRootResolution Resolve(EffectiveEnvironmentSettings effective, string dataPath)
    {
        if (effective.Source is EnvironmentSettingsSource.Invalid or EnvironmentSettingsSource.Unreachable
            || effective.Settings is null)
            return new(null, $"Environment settings are unavailable: {effective.Error}");

        var distribution = effective.Settings.Distribution;

        if (!distribution.Enabled)
            return new(null, "Distribution is disabled for this environment.");

        if (string.Equals(distribution.Mode, "AzureBlob", StringComparison.OrdinalIgnoreCase))
            return new(null, "Statistics are not available for Azure Blob distribution (the telemetry inbox is not a local folder).");

        var root = string.IsNullOrWhiteSpace(distribution.LocalPath)
            ? Path.Combine(dataPath, "_distribution")
            : distribution.LocalPath;

        return new(root, null);
    }
}
