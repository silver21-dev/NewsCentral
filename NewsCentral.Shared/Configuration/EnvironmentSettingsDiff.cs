namespace NewsCentral.Configuration;

/// <summary>
/// Which settings differ between two <see cref="EnvironmentSettings"/>, by FIELD NAME only. The audit log records
/// these names and never the values (several are identifiers an auditor does not need, and none may be a secret).
/// </summary>
public static class EnvironmentSettingsDiff
{
    public static IReadOnlyList<string> ChangedFields(EnvironmentSettings? before, EnvironmentSettings after)
    {
        before ??= new EnvironmentSettings();
        var changed = new List<string>();

        void Check(string name, object? a, object? b)
        {
            if (!Equals(Normalize(a), Normalize(b))) changed.Add(name);
        }

        Check("displayName", before.DisplayName, after.DisplayName);
        Check("securePath", before.SecurePath, after.SecurePath);
        Check("auditPath", before.AuditPath, after.AuditPath);
        Check("audit.enabled", before.Audit.Enabled, after.Audit.Enabled);
        Check("distribution.enabled", before.Distribution.Enabled, after.Distribution.Enabled);
        Check("distribution.mode", before.Distribution.Mode, after.Distribution.Mode);
        Check("distribution.localPath", before.Distribution.LocalPath, after.Distribution.LocalPath);
        Check("distribution.azureBlob.tenantId", before.Distribution.AzureBlob.TenantId, after.Distribution.AzureBlob.TenantId);
        Check("distribution.azureBlob.clientId", before.Distribution.AzureBlob.ClientId, after.Distribution.AzureBlob.ClientId);
        Check("distribution.azureBlob.accountName", before.Distribution.AzureBlob.AccountName, after.Distribution.AzureBlob.AccountName);
        Check("distribution.azureBlob.containerName", before.Distribution.AzureBlob.ContainerName, after.Distribution.AzureBlob.ContainerName);
        return changed;
    }

    // null and "" are the same setting ("use the default").
    private static object? Normalize(object? value) => value is string s && s.Length == 0 ? null : value;
}
