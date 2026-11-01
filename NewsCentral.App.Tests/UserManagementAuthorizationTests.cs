using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NewsCentral.Audit;
using NewsCentral.Models;
using NewsCentral.Repositories;
using NewsCentral.Services;

namespace NewsCentral.App.Tests;

/// <summary>users.json is writable by environment administrators only, so every UserService write needs a SystemAdmin.</summary>
public sealed class UserManagementAuthorizationTests : IDisposable
{
    private const string Password = "Passw0rd!";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nc-userauth-" + Guid.NewGuid().ToString("N"));
    private readonly string _usersFile;
    private readonly AuditContext _context = new();
    private readonly AuthenticationService _auth;
    private readonly UserService _users;

    public UserManagementAuthorizationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data", "config"));
        _usersFile = Path.Combine(_root, "data", "config", "users.json");
        var storage = new LocalStorageService(new EnvironmentContext(Path.Combine(_root, "data")));

        User Make(string id, bool sysAdmin, params TeamRole[] roles) => new()
        {
            UserID = id, Username = id, IsSystemAdmin = sysAdmin, IsActive = true,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, 4), TeamRoles = roles.ToList()
        };
        new JsonFileRepository<UsersCollection>(storage, "config", "").CreateAsync(new UsersCollection
        {
            Users =
            {
                Make("admin", true),
                Make("tadmin", false, new TeamRole { TeamID = "t-1", TeamFolderName = "cz-its", Roles = { "TeamAdmin" } }),
                Make("author", false, new TeamRole { TeamID = "t-1", TeamFolderName = "cz-its", Roles = { "ContentAuthor" } })
            }
        }).GetAwaiter().GetResult();

        var settings = new AuditSettings(true, Path.Combine(_root, "audit"), false, "Test env");
        var audit = new AuditLog(() => Task.FromResult(settings), _context, "ws01", "jdoe");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:EnableLocalAccounts"] = "true"
        }).Build();
        _auth = new AuthenticationService(
            storage, new WindowsIdentityService(config), new UiLanguageService(), config,
            NullLogger<AuthenticationService>.Instance, new UserPreferenceStore(Path.Combine(_root, "prefs.json")),
            audit, _context);
        _users = new UserService(storage, _auth, audit);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private List<(string Event, string Outcome)> Events()
    {
        var dir = Path.Combine(_root, "audit");
        if (!Directory.Exists(dir)) return new();
        return Directory.GetFiles(dir, "*.jsonl", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Select(e => (e.GetProperty("event").GetString()!, e.GetProperty("outcome").GetString()!))
            .ToList();
    }

    public static IEnumerable<object[]> Writes() => new (string Name, Func<UserService, Task> Call, string Event)[]
    {
        ("create",           s => s.CreateUserAsync("bob", "b@x", "Bob", "pw-123456"),                        "UserCreated"),
        ("create+upn",       s => s.CreateUserWithUPNAsync("bob", "b@x", "Bob", "pw-123456", false, "b@c.t"), "UserCreated"),
        ("update",           s => s.UpdateUserAsync("author", "a@x", "Author", true, false),                          "UserUpdated"),
        ("update+upn",       s => s.UpdateUserWithUPNAsync("author", "a@x", "Author", true, false, "a@c.t"),          "UserUpdated"),
        ("upn",              s => s.UpdateUserUPNAsync("author", "a@c.t"),                                     "UserUpnChanged"),
        ("assign",           s => s.AssignUserToTeamAsync("author", "t-1", "cz-its", new List<string> { "ContentAuthor" }), "UserTeamAssigned"),
        ("remove",           s => s.RemoveUserFromTeamAsync("author", "t-1"),                                  "UserTeamRemoved"),
        ("roles",            s => s.UpdateTeamRolesAsync("author", "t-1", new List<string> { "ContentApprover" }), "UserRolesChanged"),
        ("delete",           s => s.DeleteUserAsync("author"),                                                 "UserDeleted"),
    }.Select(w => new object[] { w.Name, w.Call, w.Event });

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task EveryWrite_RefusesATeamAdmin_AuditsFailure_AndLeavesUsersJsonUntouched(
        string name, Func<UserService, Task> call, string expectedEvent)
    {
        Assert.False(string.IsNullOrEmpty(name));
        Assert.NotNull(await _auth.LoginAsync("tadmin", Password));
        var before = await File.ReadAllBytesAsync(_usersFile);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => call(_users));

        Assert.Equal(before, await File.ReadAllBytesAsync(_usersFile));
        var failure = Assert.Single(Events(), e => e.Event == expectedEvent);
        Assert.Equal("Failure", failure.Outcome);
    }

    [Fact]
    public async Task AContentAuthor_IsRefusedToo()
    {
        Assert.NotNull(await _auth.LoginAsync("author", Password));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _users.UpdateTeamRolesAsync("author", "t-1", new List<string> { "TeamAdmin" }));
    }

    [Fact]
    public async Task ASystemAdmin_CanPerformEveryWrite()
    {
        Assert.NotNull(await _auth.LoginAsync("admin", Password));

        var bob = await _users.CreateUserAsync("bob", "b@x", "Bob", "pw-123456");
        await _users.CreateUserWithUPNAsync("carol", "c@x", "Carol", "pw-123456", false, "c@c.t");
        await _users.UpdateUserAsync(bob.UserID, "b2@x", "Bobby", true, false);
        await _users.UpdateUserWithUPNAsync(bob.UserID, "b2@x", "Bobby", true, false, "b@c.t");
        await _users.UpdateUserUPNAsync(bob.UserID, null);
        Assert.True(await _users.AssignUserToTeamAsync(bob.UserID, "t-1", "cz-its", new List<string> { "ContentAuthor", "TeamAdmin" }));
        Assert.True(await _users.UpdateTeamRolesAsync(bob.UserID, "t-1", new List<string> { "ContentApprover" }));
        Assert.True(await _users.RemoveUserFromTeamAsync(bob.UserID, "t-1"));
        Assert.True(await _users.DeleteUserAsync(bob.UserID));

        Assert.DoesNotContain(Events(), e => e.Outcome == "Failure");
        Assert.NotNull(await _users.GetUserByUsernameAsync("carol"));
    }
}
