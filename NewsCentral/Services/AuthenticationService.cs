using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Audit;
using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsCentral.Repositories;

namespace NewsCentral.Services;

public enum UpnLoginOutcome
{
    UpnNotDetected,
    NoAccountForUpn,
    Success,
    Disabled
}

/// <summary>
/// Result of <see cref="AuthenticationService.LoginWithUpnAsync"/>. <see cref="User"/> is set only
/// when <see cref="Outcome"/> is <see cref="UpnLoginOutcome.Success"/>; <see cref="Upn"/> is the
/// detected UPN when one was found, for use in error messages (null when detection itself failed).
/// </summary>
public sealed record UpnLoginResult(UpnLoginOutcome Outcome, User? User = null, string? Upn = null);

public class AuthenticationService
{
    private readonly JsonFileRepository<UsersCollection> _userRepo;
    private readonly WindowsIdentityService _windowsIdentityService;
    private readonly UiLanguageService _uiLanguageService;
    private readonly AppConfiguration _appConfiguration;
    private readonly ILogger<AuthenticationService> _logger;
    private readonly UserPreferenceStore _preferences;
    private User? _currentUser;

    public event Action? OnAuthenticationStateChanged;

    private readonly IAuditLog _audit;
    private readonly AuditContext _auditContext;

    public AuthenticationService(IStorageService storage, WindowsIdentityService windowsIdentityService, UiLanguageService uiLanguageService,
        IConfiguration configuration, ILogger<AuthenticationService> logger, UserPreferenceStore preferences,
        IAuditLog? audit = null, AuditContext? auditContext = null)
    {
        _appConfiguration = new AppConfiguration(configuration);
        _logger = logger;
        _userRepo = new JsonFileRepository<UsersCollection>(storage, "config", "");
        _windowsIdentityService = windowsIdentityService;
        _uiLanguageService = uiLanguageService;
        _preferences = preferences;
        _audit = audit ?? NullAuditLog.Instance;
        _auditContext = auditContext ?? new AuditContext();
    }

    private Task AuditLoginFailedAsync(string? attemptedUser, string reason) =>
        _audit.RecordAsync(new AuditRecord(AuditEvents.LoginFailed, null, AuditOutcome.Failure, reason, AttemptedUser: attemptedUser ?? "(none)"));

    private async Task AuditLoginSucceededAsync(User user)
    {
        _auditContext.AppUser = user.Username;
        await _audit.RecordAsync(new AuditRecord(AuditEvents.LoginSucceeded, user.UserID));
    }

    /// <summary>
    /// users.json may be read-only for authors (administrators own it), so recording the last login is a courtesy:
    /// an access or IO failure on this one write is logged and login carries on. Only the write is wrapped - the
    /// credential check has already happened and is never softened.
    /// </summary>
    private async Task TryRecordLastLoginAsync(UsersCollection usersCollection, User user)
    {
        try
        {
            user.LastLogin = DateTime.UtcNow;
            usersCollection.LastModified = DateTime.UtcNow;
            await _userRepo.UpdateAsync(usersCollection);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning("Could not record last login for {User}: users.json is not writable ({Reason}). Login continues.",
                user.Username, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Applies the signed-in user's UI language. The per-user local store wins (including an explicit "follow
    /// Windows"); only when this machine has no choice for the user is the legacy users.json value taken - a
    /// read-only migration, users.json is never written for this. The effective value is mirrored onto the
    /// in-memory user so the Settings page shows it.
    /// </summary>
    private void ApplyUserLanguage(User user)
    {
        var (found, language) = _preferences.GetPreferredLanguage(user.UserID);
        var effective = found ? language : user.PreferredUiLanguage;
        user.PreferredUiLanguage = effective;
        _uiLanguageService.Apply(effective);
    }

    /// <summary>
    /// Checks the current user's password without signing in again: no last-login write, no state change.
    /// False when local accounts are disabled, nobody is signed in, or the password is wrong.
    /// </summary>
    public async Task<bool> VerifyCurrentPasswordAsync(string password)
    {
        if (_currentUser == null || !IsLocalLoginEnabled)
            return false;

        var usersCollection = await _userRepo.GetByIdAsync("users");
        var user = usersCollection?.Users.FirstOrDefault(u => u.UserID == _currentUser.UserID && u.IsActive);
        return user != null && BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
    }

    /// <summary>True when Authentication:EnableLocalAccounts is on; Login.razor renders the local logon form only then.</summary>
    public bool IsLocalLoginEnabled => _appConfiguration.EnableLocalAccounts;

    /// <summary>
    /// Reads users.json for a sign-in. An access-denied read is reported as <see cref="UsersFileNotReadableException"/>
    /// so the login page can explain it instead of showing the raw .NET message.
    /// </summary>
    private async Task<UsersCollection?> LoadUsersForLoginAsync()
    {
        try
        {
            return await _userRepo.GetByIdAsync("users");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UsersFileNotReadableException(ex);
        }
    }

    public async Task<User?> LoginAsync(string username, string password)
    {
        // Enforced here, not only in the UI: a disabled setting refuses even if the page is bypassed.
        if (!IsLocalLoginEnabled)
        {
            _logger.LogWarning("Local account login refused: Authentication:EnableLocalAccounts is false.");
            await AuditLoginFailedAsync(username, "local accounts are disabled");
            return null;
        }

        System.Diagnostics.Debug.WriteLine($"=== LoginAsync: {username} ===");

        try
        {
            System.Diagnostics.Debug.WriteLine("Loading users from repository...");
            var usersCollection = await LoadUsersForLoginAsync();

            if (usersCollection == null)
            {
                System.Diagnostics.Debug.WriteLine("ERROR: No users collection found");
                throw new InvalidOperationException("No users found in system");
            }

            System.Diagnostics.Debug.WriteLine($"Users collection loaded: {usersCollection.Users.Count} users found");

            var user = usersCollection.Users.FirstOrDefault(u =>
                u.Username.Equals(username, StringComparison.OrdinalIgnoreCase) &&
                u.IsActive);

            if (user == null)
            {
                System.Diagnostics.Debug.WriteLine($"Login failed: User '{username}' not found or inactive");
                var availableUsers = string.Join(", ", usersCollection.Users.Select(u => $"{u.Username} (Active: {u.IsActive})"));
                System.Diagnostics.Debug.WriteLine($"Available users: {availableUsers}");
                await AuditLoginFailedAsync(username, "unknown or inactive user");
                return null;
            }

            System.Diagnostics.Debug.WriteLine($"User found: {user.Username}, verifying password...");

            if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            {
                System.Diagnostics.Debug.WriteLine($"Login failed: Invalid password for user '{username}'");
                await AuditLoginFailedAsync(username, "invalid password");
                return null;
            }

            System.Diagnostics.Debug.WriteLine("Password verified successfully");

            await TryRecordLastLoginAsync(usersCollection, user);

            _currentUser = user;
            ApplyUserLanguage(user);
            System.Diagnostics.Debug.WriteLine($"Login successful: {user.Username} (IsSystemAdmin: {user.IsSystemAdmin})");

            await AuditLoginSucceededAsync(user);
            OnAuthenticationStateChanged?.Invoke();

            return user;
        }
        catch (Exception ex)
        {
            await AuditLoginFailedAsync(username, $"error: {ex.GetType().Name}");
            System.Diagnostics.Debug.WriteLine($"Login error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// Explicit, user-initiated UPN login — the counterpart to <see cref="LoginAsync"/>. Never
    /// called automatically; Login.razor invokes it only from the "UPN Login" button click.
    /// </summary>
    public async Task<UpnLoginResult> LoginWithUpnAsync()
    {
        // Defense in depth: Login.razor only shows the UPN Login button when this is true, but the
        // service itself must still refuse if it is ever called while the feature is off.
        if (!_windowsIdentityService.IsAutoLoginEnabled())
        {
            System.Diagnostics.Debug.WriteLine("UPN login is disabled");
            await AuditLoginFailedAsync(null, "UPN login is disabled");
            return new UpnLoginResult(UpnLoginOutcome.Disabled);
        }

        var upn = _windowsIdentityService.GetCurrentUserUPN();

        if (string.IsNullOrEmpty(upn))
        {
            System.Diagnostics.Debug.WriteLine("Could not detect UPN");
            await AuditLoginFailedAsync(null, "UPN could not be detected");
            return new UpnLoginResult(UpnLoginOutcome.UpnNotDetected);
        }

        System.Diagnostics.Debug.WriteLine($"Attempting UPN login with UPN: {upn}");

        UsersCollection? usersCollection;
        try
        {
            usersCollection = await LoadUsersForLoginAsync();
        }
        catch (UsersFileNotReadableException)
        {
            await AuditLoginFailedAsync(upn, "users.json is not readable");
            throw;
        }

        if (usersCollection == null)
        {
            System.Diagnostics.Debug.WriteLine("No users collection found in system");
            await AuditLoginFailedAsync(upn, "no users collection");
            return new UpnLoginResult(UpnLoginOutcome.NoAccountForUpn, Upn: upn);
        }

        var user = usersCollection.Users.FirstOrDefault(u =>
            !string.IsNullOrEmpty(u.UPN) &&
            u.UPN.Equals(upn, StringComparison.OrdinalIgnoreCase) &&
            u.IsActive);

        if (user != null)
        {
            await TryRecordLastLoginAsync(usersCollection, user);

            _currentUser = user;
            ApplyUserLanguage(user);

            await AuditLoginSucceededAsync(user);
            OnAuthenticationStateChanged?.Invoke();

            System.Diagnostics.Debug.WriteLine($"✓ UPN login successful: {user.Username}");
            return new UpnLoginResult(UpnLoginOutcome.Success, User: user, Upn: upn);
        }

        System.Diagnostics.Debug.WriteLine($"No active user found with UPN: {upn}");
        await AuditLoginFailedAsync(upn, "no active user with this UPN");
        return new UpnLoginResult(UpnLoginOutcome.NoAccountForUpn, Upn: upn);
    }

    public void Logout()
    {
        // Recorded for the user being signed out; the actor is fixed here because the write completes asynchronously.
        if (_currentUser != null)
            _ = _audit.RecordAsync(new AuditRecord(AuditEvents.Logout, _currentUser.UserID, AppUser: _currentUser.Username));

        _auditContext.AppUser = null;
        _currentUser = null;
        _uiLanguageService.Apply(null);
        OnAuthenticationStateChanged?.Invoke();
    }

    /// <summary>
    /// L10N-1: self-service update of the CURRENT user's own UI language preference — any
    /// authenticated role, no admin rights needed. Stored in the per-user local store on this machine
    /// (<see cref="UserPreferenceStore"/>); users.json is not touched, so this works when users.json is read-only.
    /// </summary>
    public Task UpdatePreferredUiLanguageAsync(string? preferredLanguage)
    {
        if (_currentUser == null)
            throw new UnauthorizedAccessException("Not authenticated");

        var normalized = string.IsNullOrWhiteSpace(preferredLanguage) ? null : preferredLanguage.Trim();
        _preferences.SetPreferredLanguage(_currentUser.UserID, normalized);

        // Keep the in-memory session's own user object in sync.
        _currentUser.PreferredUiLanguage = normalized;
        return Task.CompletedTask;
    }

    public User? GetCurrentUser()
    {
        return _currentUser;
    }

    public bool IsAuthenticated()
    {
        return _currentUser != null;
    }

    public bool IsSystemAdmin()
    {
        return _currentUser?.IsSystemAdmin ?? false;
    }

    public bool HasRole(string teamID, string role)
    {
        if (_currentUser == null)
            return false;

        if (_currentUser.IsSystemAdmin)
            return true;

        var teamRole = _currentUser.TeamRoles
            .FirstOrDefault(tr => tr.TeamID == teamID);

        return teamRole?.Roles.Contains(role) ?? false;
    }

    public bool HasAnyRole(string teamID, params string[] roles)
    {
        if (_currentUser == null)
            return false;

        if (_currentUser.IsSystemAdmin)
            return true;

        var teamRole = _currentUser.TeamRoles
            .FirstOrDefault(tr => tr.TeamID == teamID);

        return teamRole?.Roles.Any(r => roles.Contains(r)) ?? false;
    }

    /// <summary>
    /// True if the current user is a SystemAdmin, or holds the TeamAdmin role
    /// in at least one team. Used to gate access to the Users page, where a
    /// TeamAdmin may manage users and assign roles within the team(s) they admin.
    /// </summary>
    public bool IsTeamAdminOfAnyTeam()
    {
        if (_currentUser == null)
            return false;

        if (_currentUser.IsSystemAdmin)
            return true;

        return _currentUser.TeamRoles.Any(tr => tr.Roles.Contains("TeamAdmin"));
    }
}