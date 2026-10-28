using NewsCentral.Configuration;

namespace NewsCentral.Audit;

public static class AuditLogExtensions
{
    /// <summary>
    /// Runs <paramref name="action"/> and records the outcome: Success when it returns (and <paramref name="isSuccess"/>,
    /// if given, agrees), Failure with <paramref name="detail"/> (if any) followed by the exception type and message when it
    /// throws - the exception is rethrown unchanged. Recording never alters the action's result or exception.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        this IAuditLog audit,
        string eventName,
        string? target,
        Func<Task<T>> action,
        Func<T, string?>? targetFrom = null,
        Func<T, bool>? isSuccess = null,
        string? detail = null,
        Func<T, string?>? detailFrom = null)
    {
        T result;
        try
        {
            result = await action();
        }
        catch (Exception ex)
        {
            // The caller's detail comes first so AuditLog's length cut only shortens the exception text.
            await audit.RecordAsync(new AuditRecord(eventName, target, AuditOutcome.Failure,
                string.IsNullOrEmpty(detail) ? Describe(ex) : $"{detail}; {Describe(ex)}"));
            throw;
        }

        var ok = isSuccess?.Invoke(result) ?? true;
        await audit.RecordAsync(new AuditRecord(
            eventName,
            targetFrom?.Invoke(result) ?? target,
            ok ? AuditOutcome.Success : AuditOutcome.Failure,
            ok ? detailFrom?.Invoke(result) ?? detail : detailFrom?.Invoke(result) ?? detail ?? "not applied"));
        return result;
    }

    public static Task RunAsync(
        this IAuditLog audit, string eventName, string? target, Func<Task> action, string? detail = null) =>
        audit.RunAsync<bool>(eventName, target, async () => { await action(); return true; }, detail: detail);

    /// <summary>"ExceptionType: message", the short reason recorded for a failed operation.</summary>
    public static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}

/// <summary>
/// The order rule for switching audit on and off: AuditDisabled is written BEFORE logging stops, AuditEnabled AFTER
/// it starts. The caller's <c>apply</c> is what actually changes the effective setting.
/// </summary>
public static class AuditToggle
{
    public static async Task ApplyAsync(IAuditLog audit, bool wasEnabled, bool willBeEnabled, Func<Task> apply)
    {
        var turningOff = wasEnabled && !willBeEnabled;
        var turningOn = !wasEnabled && willBeEnabled;

        if (turningOff)
            await audit.RecordAsync(new AuditRecord(AuditEvents.AuditDisabled));

        try
        {
            await apply();
        }
        catch (Exception ex)
        {
            // The disabling line was already written but the setting did not change: say so.
            if (turningOff)
                await audit.RecordAsync(new AuditRecord(
                    AuditEvents.AuditDisabled, Outcome: AuditOutcome.Failure,
                    Detail: $"not applied, audit remains enabled ({AuditLogExtensions.Describe(ex)})"));
            throw;
        }

        if (turningOn)
            await audit.RecordAsync(new AuditRecord(AuditEvents.AuditEnabled));
    }
}

/// <summary>Resolves the effective audit settings: policy registry value over the environment value, default path under DataPath.</summary>
public static class AuditSettingsResolver
{
    public static string EffectiveAuditPath(EnvironmentSettings? settings, string dataPath) =>
        string.IsNullOrWhiteSpace(settings?.AuditPath)
            ? Path.Combine(dataPath, "audit")
            : settings.AuditPath;

    /// <param name="policyEnabled">The registry value (Audit\Enabled) when present, else null.</param>
    public static AuditSettings Resolve(EnvironmentSettings? settings, string dataPath, bool? policyEnabled)
    {
        var name = string.IsNullOrWhiteSpace(settings?.DisplayName) ? dataPath : settings!.DisplayName!;
        return new AuditSettings(
            Enabled: policyEnabled ?? settings?.Audit.Enabled ?? true,
            Path: EffectiveAuditPath(settings, dataPath),
            LockedByPolicy: policyEnabled.HasValue,
            EnvironmentName: name);
    }
}
