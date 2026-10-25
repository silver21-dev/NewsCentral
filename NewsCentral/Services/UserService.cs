using NewsCentral.Audit;
using NewsCentral.Models;
using NewsCentral.Repositories;

namespace NewsCentral.Services;

/// <summary>
/// config/users.json cannot be written by the current Windows account. The message is user-facing: the page
/// shows it as is instead of an error.
/// </summary>
public sealed class UsersFileNotWritableException : InvalidOperationException
{
    public const string UserMessage = "Your password can only be changed by an administrator.";

    public UsersFileNotWritableException() : base(UserMessage) { }
}

public class UserService
{
    private readonly IStorageService _storage;
    private readonly JsonFileRepository<UsersCollection> _userRepo;
    private readonly AuthenticationService _authService;
    private readonly IAuditLog _audit;

    public UserService(IStorageService storage, AuthenticationService authService, IAuditLog? audit = null)
    {
        _storage     = storage;
        _authService = authService;
        _audit       = audit ?? NullAuditLog.Instance;

        // Primary JsonFileRepository constructor — no [Obsolete] warning
        _userRepo = new JsonFileRepository<UsersCollection>(_storage, "config", "");
    }

    // ── Authorization helpers ─────────────────────────────────────────────────

    // True if the user is a SystemAdmin or holds TeamAdmin in at least one team.
    private static bool IsTeamAdminOfAnyTeam(User user) =>
        user.IsSystemAdmin || user.TeamRoles.Any(tr => tr.Roles.Contains("TeamAdmin"));

    // True if the user may administer the given team: SystemAdmin (any team) or
    // TeamAdmin of that specific team.
    private static bool CurrentUserAdministersTeam(User user, string teamId) =>
        user.IsSystemAdmin ||
        user.TeamRoles.Any(tr => tr.TeamID == teamId && tr.Roles.Contains("TeamAdmin"));

    // ── Queries ──────────────────────────────────────────────────────────────

    public async Task<List<User>> GetAllUsersAsync()
    {
        var usersCollection = await _userRepo.GetByIdAsync("users");
        return usersCollection?.Users ?? new List<User>();
    }

    public async Task<User?> GetUserByIdAsync(string userId)
    {
        var users = await GetAllUsersAsync();
        return users.FirstOrDefault(u => u.UserID == userId);
    }

    public async Task<User?> GetUserByUsernameAsync(string username)
    {
        var users = await GetAllUsersAsync();
        return users.FirstOrDefault(
            u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
    }

    // ── UPN helpers ───────────────────────────────────────────────────────────

    public Task UpdateUserUPNAsync(string userId, string? upn) =>
        _audit.RunAsync(AuditEvents.UserUpnChanged, userId, () => UpdateUserUPNCoreAsync(userId, upn),
            detail: string.IsNullOrWhiteSpace(upn) ? "UPN cleared" : "UPN set");

    private async Task UpdateUserUPNCoreAsync(string userId, string? upn)
    {
        var usersCollection = await _userRepo.GetByIdAsync("users")
            ?? throw new InvalidOperationException("Users collection not found");

        var user = usersCollection.Users.FirstOrDefault(u => u.UserID == userId)
            ?? throw new InvalidOperationException("User not found");

        user.UPN = string.IsNullOrWhiteSpace(upn) ? null : upn.Trim();
        usersCollection.LastModified = DateTime.UtcNow;

        await _userRepo.UpdateAsync(usersCollection);
    }

    public async Task CreateUserWithUPNAsync(
        string username, string email, string displayName,
        string password, bool isSystemAdmin, string? upn)
    {
        await CreateUserAsync(username, email, displayName, password, isSystemAdmin);

        if (!string.IsNullOrWhiteSpace(upn))
        {
            var usersCollection = await _userRepo.GetByIdAsync("users");
            var newUser = usersCollection?.Users.FirstOrDefault(u => u.Username == username);

            if (newUser != null)
            {
                await _audit.RunAsync(AuditEvents.UserUpnChanged, newUser.UserID, async () =>
                {
                    newUser.UPN = upn.Trim();
                    usersCollection!.LastModified = DateTime.UtcNow;
                    await _userRepo.UpdateAsync(usersCollection);
                }, detail: "UPN set");
            }
        }
    }

    public async Task UpdateUserWithUPNAsync(
        string userId, string email, string displayName,
        bool isActive, string? upn)
    {
        await UpdateUserAsync(userId, email, displayName, isActive);
        await UpdateUserUPNAsync(userId, upn);
    }

    // ── CRUD ─────────────────────────────────────────────────────────────────

    public Task<User> CreateUserAsync(
        string username, string email, string displayName,
        string password, bool isSystemAdmin = false) =>
        _audit.RunAsync(AuditEvents.UserCreated, null,
            () => CreateUserCoreAsync(username, email, displayName, password, isSystemAdmin),
            targetFrom: u => u.UserID,
            detail: $"username={username}; systemAdmin={isSystemAdmin}");

    private async Task<User> CreateUserCoreAsync(
        string username, string email, string displayName,
        string password, bool isSystemAdmin)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null || (!currentUser.IsSystemAdmin && !IsTeamAdminOfAnyTeam(currentUser)))
            throw new UnauthorizedAccessException("Only SystemAdmin or TeamAdmin can create users");

        if (isSystemAdmin && !currentUser.IsSystemAdmin)
            throw new UnauthorizedAccessException("Only SystemAdmin can create SystemAdmin users");

        var existing = await GetUserByUsernameAsync(username);
        if (existing != null)
            throw new InvalidOperationException($"Username '{username}' already exists");

        var user = new User
        {
            UserID        = Guid.NewGuid().ToString(),
            Username      = username,
            Email         = email,
            DisplayName   = displayName,
            PasswordHash  = BCrypt.Net.BCrypt.HashPassword(password),
            IsSystemAdmin = isSystemAdmin,
            IsActive      = true,
            DateCreated   = DateTime.UtcNow,
            TeamRoles     = new List<TeamRole>()
        };

        var usersCollection = await _userRepo.GetByIdAsync("users")
            ?? throw new InvalidOperationException("Users collection not found");

        usersCollection.Users.Add(user);
        usersCollection.LastModified = DateTime.UtcNow;
        usersCollection.ModifiedBy   = currentUser.UserID;

        await _userRepo.UpdateAsync(usersCollection);
        return user;
    }

    public Task<User> UpdateUserAsync(
        string userId, string email, string displayName, bool isActive) =>
        _audit.RunAsync(AuditEvents.UserUpdated, userId,
            () => UpdateUserCoreAsync(userId, email, displayName, isActive),
            detail: $"active={isActive}");

    private async Task<User> UpdateUserCoreAsync(
        string userId, string email, string displayName, bool isActive)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null || (!currentUser.IsSystemAdmin && !IsTeamAdminOfAnyTeam(currentUser)))
            throw new UnauthorizedAccessException("Only SystemAdmin or TeamAdmin can update users");

        var usersCollection = await _userRepo.GetByIdAsync("users")
            ?? throw new InvalidOperationException("Users collection not found");

        var user = usersCollection.Users.FirstOrDefault(u => u.UserID == userId)
            ?? throw new InvalidOperationException($"User {userId} not found");

        if (user.IsSystemAdmin && !currentUser.IsSystemAdmin)
            throw new UnauthorizedAccessException("Only SystemAdmin can modify a SystemAdmin user");

        user.Email       = email;
        user.DisplayName = displayName;
        user.IsActive    = isActive;

        usersCollection.LastModified = DateTime.UtcNow;
        usersCollection.ModifiedBy   = currentUser.UserID;

        await _userRepo.UpdateAsync(usersCollection);
        return user;
    }

    // The password itself is never recorded - only that it changed.
    public Task<bool> ChangePasswordAsync(string userId, string newPassword) =>
        _audit.RunAsync(AuditEvents.PasswordChanged, userId, () => ChangePasswordCoreAsync(userId, newPassword),
            isSuccess: ok => ok, detail: null, detailFrom: ok => ok ? null : "user not found");

    private async Task<bool> ChangePasswordCoreAsync(string userId, string newPassword)
    {
        var currentUser = _authService.GetCurrentUser()
            ?? throw new UnauthorizedAccessException("Not authenticated");

        if (currentUser.UserID != userId && !currentUser.IsSystemAdmin)
            throw new UnauthorizedAccessException("Cannot change another user's password");

        var usersCollection = await _userRepo.GetByIdAsync("users");
        if (usersCollection == null) return false;

        var user = usersCollection.Users.FirstOrDefault(u => u.UserID == userId);
        if (user == null) return false;

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        usersCollection.LastModified = DateTime.UtcNow;
        usersCollection.ModifiedBy   = currentUser.UserID;

        try
        {
            await _userRepo.UpdateAsync(usersCollection);
        }
        catch (UnauthorizedAccessException)
        {
            // users.json is writable by administrators only: not a failure to report, a rule to explain.
            throw new UsersFileNotWritableException();
        }
        return true;
    }

    public Task<bool> DeleteUserAsync(string userId) =>
        _audit.RunAsync(AuditEvents.UserDeleted, userId, () => DeleteUserCoreAsync(userId),
            isSuccess: ok => ok, detailFrom: ok => ok ? null : "user not found");

    private async Task<bool> DeleteUserCoreAsync(string userId)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null || !currentUser.IsSystemAdmin)
            throw new UnauthorizedAccessException("Only SystemAdmin can delete users");

        if (currentUser.UserID == userId)
            throw new InvalidOperationException("Cannot delete your own account");

        var usersCollection = await _userRepo.GetByIdAsync("users");
        if (usersCollection == null) return false;

        var removed = usersCollection.Users.RemoveAll(u => u.UserID == userId);
        if (removed == 0) return false;

        usersCollection.LastModified = DateTime.UtcNow;
        usersCollection.ModifiedBy   = currentUser.UserID;

        await _userRepo.UpdateAsync(usersCollection);
        return true;
    }

    // ── Team role management ──────────────────────────────────────────────────

    public Task<bool> AssignUserToTeamAsync(
        string userId, string teamId, string teamFolderName, List<string> roles) =>
        _audit.RunAsync(AuditEvents.UserTeamAssigned, userId,
            () => AssignUserToTeamCoreAsync(userId, teamId, teamFolderName, roles),
            isSuccess: ok => ok, detail: $"team={teamFolderName}; roles={string.Join(",", roles)}");

    private async Task<bool> AssignUserToTeamCoreAsync(
        string userId, string teamId, string teamFolderName, List<string> roles)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null || !CurrentUserAdministersTeam(currentUser, teamId))
            throw new UnauthorizedAccessException("You don't have permission to assign users to this team");

        if (!currentUser.IsSystemAdmin && roles.Contains("TeamAdmin"))
            throw new UnauthorizedAccessException("Only SystemAdmin can grant TeamAdmin role");

        var usersCollection = await _userRepo.GetByIdAsync("users");
        if (usersCollection == null) return false;

        var user = usersCollection.Users.FirstOrDefault(u => u.UserID == userId);
        if (user == null) return false;

        user.TeamRoles.RemoveAll(tr => tr.TeamID == teamId);
        user.TeamRoles.Add(new TeamRole
        {
            TeamID         = teamId,
            TeamFolderName = teamFolderName,
            Roles          = roles
        });

        usersCollection.LastModified = DateTime.UtcNow;
        usersCollection.ModifiedBy   = currentUser.UserID;

        await _userRepo.UpdateAsync(usersCollection);
        return true;
    }

    public Task<bool> RemoveUserFromTeamAsync(string userId, string teamId) =>
        _audit.RunAsync(AuditEvents.UserTeamRemoved, userId, () => RemoveUserFromTeamCoreAsync(userId, teamId),
            isSuccess: ok => ok, detail: $"teamId={teamId}");

    private async Task<bool> RemoveUserFromTeamCoreAsync(string userId, string teamId)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null || !CurrentUserAdministersTeam(currentUser, teamId))
            throw new UnauthorizedAccessException("You don't have permission to remove users from this team");

        var usersCollection = await _userRepo.GetByIdAsync("users");
        if (usersCollection == null) return false;

        var user = usersCollection.Users.FirstOrDefault(u => u.UserID == userId);
        if (user == null) return false;

        var existingRole = user.TeamRoles.FirstOrDefault(tr => tr.TeamID == teamId);
        if (!currentUser.IsSystemAdmin && existingRole != null && existingRole.Roles.Contains("TeamAdmin"))
            throw new UnauthorizedAccessException("Only SystemAdmin can remove a TeamAdmin from a team");

        var removed = user.TeamRoles.RemoveAll(tr => tr.TeamID == teamId);
        if (removed == 0) return false;

        usersCollection.LastModified = DateTime.UtcNow;
        usersCollection.ModifiedBy   = currentUser.UserID;

        await _userRepo.UpdateAsync(usersCollection);
        return true;
    }

    public Task<bool> UpdateTeamRolesAsync(
        string userId, string teamId, List<string> roles) =>
        _audit.RunAsync(AuditEvents.UserRolesChanged, userId, () => UpdateTeamRolesCoreAsync(userId, teamId, roles),
            isSuccess: ok => ok, detail: $"teamId={teamId}; roles={string.Join(",", roles)}");

    private async Task<bool> UpdateTeamRolesCoreAsync(
        string userId, string teamId, List<string> roles)
    {
        var currentUser = _authService.GetCurrentUser()
            ?? throw new UnauthorizedAccessException("Not authenticated");

        var usersCollection = await _userRepo.GetByIdAsync("users");
        if (usersCollection == null) return false;

        var user = usersCollection.Users.FirstOrDefault(u => u.UserID == userId);
        if (user == null) return false;

        var teamRole = user.TeamRoles.FirstOrDefault(tr => tr.TeamID == teamId);
        if (teamRole == null) return false;

        if (!currentUser.IsSystemAdmin)
        {
            if (!CurrentUserAdministersTeam(currentUser, teamId))
                throw new UnauthorizedAccessException("You don't have permission to modify roles for this team");

            if (roles.Contains("TeamAdmin") && !teamRole.Roles.Contains("TeamAdmin"))
                throw new UnauthorizedAccessException("Only SystemAdmin can grant TeamAdmin role");

            if (!roles.Contains("TeamAdmin") && teamRole.Roles.Contains("TeamAdmin"))
                throw new UnauthorizedAccessException("Only SystemAdmin can revoke TeamAdmin role");
        }

        teamRole.Roles = roles;
        usersCollection.LastModified = DateTime.UtcNow;
        usersCollection.ModifiedBy   = currentUser.UserID;

        await _userRepo.UpdateAsync(usersCollection);
        return true;
    }
}
