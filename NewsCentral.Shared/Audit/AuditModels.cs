namespace NewsCentral.Audit;

public enum AuditOutcome
{
    Success,
    Failure
}

/// <summary>Names of the audited events. The string is the <c>event</c> field of the audit line.</summary>
public static class AuditEvents
{
    public const string LoginSucceeded = nameof(LoginSucceeded);
    public const string LoginFailed = nameof(LoginFailed);
    public const string Logout = nameof(Logout);

    public const string UserCreated = nameof(UserCreated);
    public const string UserUpdated = nameof(UserUpdated);
    public const string UserDeleted = nameof(UserDeleted);
    public const string UserRolesChanged = nameof(UserRolesChanged);
    public const string UserTeamAssigned = nameof(UserTeamAssigned);
    public const string UserTeamRemoved = nameof(UserTeamRemoved);
    public const string UserUpnChanged = nameof(UserUpnChanged);
    public const string PasswordChanged = nameof(PasswordChanged);

    public const string KeyGenerated = nameof(KeyGenerated);
    public const string KeyRotated = nameof(KeyRotated);
    public const string KeyMigrated = nameof(KeyMigrated);

    public const string PresentationCreated = nameof(PresentationCreated);
    public const string PresentationDuplicated = nameof(PresentationDuplicated);
    public const string PresentationUpdated = nameof(PresentationUpdated);
    public const string PresentationDeleted = nameof(PresentationDeleted);

    public const string AssignmentPublished = nameof(AssignmentPublished);
    public const string AssignmentDeleted = nameof(AssignmentDeleted);
    public const string IndexRegenerationFailed = nameof(IndexRegenerationFailed);

    public const string EnvironmentAdded = nameof(EnvironmentAdded);
    public const string EnvironmentDeleted = nameof(EnvironmentDeleted);
    public const string EnvironmentInitialized = nameof(EnvironmentInitialized);
    public const string EnvironmentSettingsChanged = nameof(EnvironmentSettingsChanged);

    public const string AuditEnabled = nameof(AuditEnabled);
    public const string AuditDisabled = nameof(AuditDisabled);
}

/// <summary>
/// One event to record. <see cref="Target"/> is the team, presentation id, user id or setting name the event is about;
/// <see cref="Detail"/> a short reason. Never put passwords, hashes, private keys or full content in either.
/// For a failed login, <see cref="AttemptedUser"/> carries the name that was tried and the app user is empty.
/// <see cref="AppUser"/> overrides the current app user when the actor must be fixed before the call returns (logout).
/// </summary>
public sealed record AuditRecord(
    string Event,
    string? Target = null,
    AuditOutcome Outcome = AuditOutcome.Success,
    string? Detail = null,
    string? AttemptedUser = null,
    string? AppUser = null);

/// <summary>The settings the writer needs right now, resolved from the environment and the policy override.</summary>
/// <param name="Enabled">Effective value: the policy registry value when present, else the environment setting.</param>
/// <param name="Path">Root audit folder (never null; defaults to {DataPath}\audit).</param>
/// <param name="LockedByPolicy">True when the registry decides <paramref name="Enabled"/> (the UI shows "set by policy").</param>
/// <param name="EnvironmentName">The environment's display name, or its DataPath when it has none.</param>
public sealed record AuditSettings(bool Enabled, string Path, bool LockedByPolicy, string EnvironmentName);

/// <summary>Who is acting right now. Set by the authentication service on login and logout.</summary>
public sealed class AuditContext
{
    private volatile string? _appUser;

    public string? AppUser
    {
        get => _appUser;
        set => _appUser = value;
    }
}

public interface IAuditLog
{
    /// <summary>
    /// Appends one line. Never throws and never blocks the caller's action: a write failure is logged and sets
    /// <see cref="WriteFailing"/>. Does nothing when audit is disabled.
    /// </summary>
    Task RecordAsync(AuditRecord record);

    /// <summary>True from a failed write until a later write succeeds. Drives the main layout's banner.</summary>
    bool WriteFailing { get; }

    /// <summary>Raised when <see cref="WriteFailing"/> changes.</summary>
    event Action? StatusChanged;
}

/// <summary>An <see cref="IAuditLog"/> that records nothing, for callers constructed without one.</summary>
public sealed class NullAuditLog : IAuditLog
{
    public static readonly NullAuditLog Instance = new();

    public Task RecordAsync(AuditRecord record) => Task.CompletedTask;
    public bool WriteFailing => false;
    public event Action? StatusChanged { add { } remove { } }
}
