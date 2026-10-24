using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
    private User? _currentUser;

    public event Action? OnAuthenticationStateChanged;

    public AuthenticationService(IStorageService storage, WindowsIdentityService windowsIdentityService, UiLanguageService uiLanguageService,
        IConfiguration configuration, ILogger<AuthenticationService> logger)
    {
        _appConfiguration = new AppConfiguration(configuration);
        _logger = logger;
        _userRepo = new JsonFileRepository<UsersCollection>(storage, "config", "");
        _windowsIdentityService = windowsIdentityService;
        _uiLanguageService = uiLanguageService;
    }

    /// <summary>True when Authentication:EnableLocalAccounts is on; Login.razor renders the local logon form only then.</summary>
    public bool IsLocalLoginEnabled => _appConfiguration.EnableLocalAccounts;

    public async Task<User?> LoginAsync(string username, string password)
    {
        // Enforced here, not only in the UI: a disabled setting refuses even if the page is bypassed.
        if (!IsLocalLoginEnabled)
        {
            _logger.LogWarning("Local account login refused: Authentication:EnableLocalAccounts is false.");
            return null;
        }

        System.Diagnostics.Debug.WriteLine($"=== LoginAsync: {username} ===");

        try
        {
            System.Diagnostics.Debug.WriteLine("Loading users from repository...");
            var usersCollection = await _userRepo.GetByIdAsync("users");

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
                return null;
            }

            System.Diagnostics.Debug.WriteLine($"User found: {user.Username}, verifying password...");

            if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            {
                System.Diagnostics.Debug.WriteLine($"Login failed: Invalid password for user '{username}'");
                return null;
            }

            System.Diagnostics.Debug.WriteLine("Password verified successfully");

            // Update last login
            user.LastLogin = DateTime.UtcNow;
            usersCollection.LastModified = DateTime.UtcNow;
            await _userRepo.UpdateAsync(usersCollection);

            _currentUser = user;
            _uiLanguageService.Apply(user.PreferredUiLanguage);
            System.Diagnostics.Debug.WriteLine($"Login successful: {user.Username} (IsSystemAdmin: {user.IsSystemAdmin})");

            OnAuthenticationStateChanged?.Invoke();

            return user;
        }
        catch (Exception ex)
        {
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
            return new UpnLoginResult(UpnLoginOutcome.Disabled);
        }

        var upn = _windowsIdentityService.GetCurrentUserUPN();

        if (string.IsNullOrEmpty(upn))
        {
            System.Diagnostics.Debug.WriteLine("Could not detect UPN");
            return new UpnLoginResult(UpnLoginOutcome.UpnNotDetected);
        }

        System.Diagnostics.Debug.WriteLine($"Attempting UPN login with UPN: {upn}");

        var usersCollection = await _userRepo.GetByIdAsync("users");

        if (usersCollection == null)
        {
            System.Diagnostics.Debug.WriteLine("No users collection found in system");
            return new UpnLoginResult(UpnLoginOutcome.NoAccountForUpn, Upn: upn);
        }

        var user = usersCollection.Users.FirstOrDefault(u =>
            !string.IsNullOrEmpty(u.UPN) &&
            u.UPN.Equals(upn, StringComparison.OrdinalIgnoreCase) &&
            u.IsActive);

        if (user != null)
        {
            user.LastLogin = DateTime.UtcNow;
            usersCollection.LastModified = DateTime.UtcNow;
            await _userRepo.UpdateAsync(usersCollection);

            _currentUser = user;
            _uiLanguageService.Apply(user.PreferredUiLanguage);

            OnAuthenticationStateChanged?.Invoke();

            System.Diagnostics.Debug.WriteLine($"✓ UPN login successful: {user.Username}");
            return new UpnLoginResult(UpnLoginOutcome.Success, User: user, Upn: upn);
        }

        System.Diagnostics.Debug.WriteLine($"No active user found with UPN: {upn}");
        return new UpnLoginResult(UpnLoginOutcome.NoAccountForUpn, Upn: upn);
    }

    public void Logout()
    {
        _currentUser = null;
        _uiLanguageService.Apply(null);
        OnAuthenticationStateChanged?.Invoke();
    }

    /// <summary>
    /// L10N-1: self-service update of the CURRENT user's own UI language preference — any
    /// authenticated role, no admin rights needed. Touches only <see cref="User.PreferredUiLanguage"/>;
    /// every other field in <c>users.json</c> is left exactly as-is.
    /// </summary>
    public async Task UpdatePreferredUiLanguageAsync(string? preferredLanguage)
    {
        if (_currentUser == null)
            throw new UnauthorizedAccessException("Not authenticated");

        var usersCollection = await _userRepo.GetByIdAsync("users")
            ?? throw new InvalidOperationException("Users collection not found");

        var user = usersCollection.Users.FirstOrDefault(u => u.UserID == _currentUser.UserID)
            ?? throw new InvalidOperationException("User not found");

        var normalized = string.IsNullOrWhiteSpace(preferredLanguage) ? null : preferredLanguage.Trim();
        user.PreferredUiLanguage = normalized;
        usersCollection.LastModified = DateTime.UtcNow;
        usersCollection.ModifiedBy = _currentUser.UserID;

        await _userRepo.UpdateAsync(usersCollection);

        // Keep the in-memory session's own user object in sync — never re-fetch the whole user
        // record just to reflect the ONE field we just changed.
        _currentUser.PreferredUiLanguage = normalized;
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