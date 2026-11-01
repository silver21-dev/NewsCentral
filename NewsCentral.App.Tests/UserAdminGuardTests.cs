using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NewsCentral.Audit;
using NewsCentral.Models;
using NewsCentral.Repositories;
using NewsCentral.Services;

namespace NewsCentral.App.Tests;

/// <summary>Promote/demote and the last-active-SystemAdmin guard. "Active SystemAdmin" = IsSystemAdmin and IsActive.</summary>
public sealed class UserAdminGuardTests : IDisposable
{
    private const string Password = "Passw0rd!";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nc-adminguard-" + Guid.NewGuid().ToString("N"));
    private readonly string _usersFile;
    private readonly LocalStorageService _storage;
    private readonly AuditContext _context = new();
    private readonly AuthenticationService _auth;
    private readonly UserService _users;

    public UserAdminGuardTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data", "config"));
        _usersFile = Path.Combine(_root, "data", "config", "users.json");
        _storage = new LocalStorageService(new EnvironmentContext(Path.Combine(_root, "data")));

        var settings = new AuditSettings(true, Path.Combine(_root, "audit"), false, "Test env");
        var audit = new AuditLog(() => Task.FromResult(settings), _context, "ws01", "jdoe");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:EnableLocalAccounts"] = "true"
        }).Build();
        _auth = new AuthenticationService(
            _storage, new WindowsIdentityService(config), new UiLanguageService(), config,
            NullLogger<AuthenticationService>.Instance, new UserPreferenceStore(Path.Combine(_root, "prefs.json")),
            audit, _context);
        _users = new UserService(_storage, _auth, audit);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // "id" is both the user id and the username. Admins listed as inactive cannot sign in, so tests log in as the first one.
    private async Task Seed(params (string Id, bool Admin, bool Active)[] seed)
    {
        await new JsonFileRepository<UsersCollection>(_storage, "config", "").CreateAsync(new UsersCollection
        {
            Users = seed.Select(s => new User
            {
                UserID = s.Id, Username = s.Id, IsSystemAdmin = s.Admin, IsActive = s.Active,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, 4), TeamRoles = new List<TeamRole>()
            }).ToList()
        });
    }

    private async Task<User> Reload(string id) => (await _users.GetUserByIdAsync(id))!;

    private List<(string Event, string Outcome, string Detail)> Events()
    {
        var dir = Path.Combine(_root, "audit");
        if (!Directory.Exists(dir)) return new();
        return Directory.GetFiles(dir, "*.jsonl", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Select(e => (e.GetProperty("event").GetString()!, e.GetProperty("outcome").GetString()!,
                e.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : ""))
            .ToList();
    }

    private async Task AssertRefused(Func<Task> call, string expectedEvent)
    {
        var before = await File.ReadAllBytesAsync(_usersFile);
        await Assert.ThrowsAsync<InvalidOperationException>(call);
        Assert.Equal(before, await File.ReadAllBytesAsync(_usersFile));
        Assert.Contains(Events(), e => e.Event == expectedEvent && e.Outcome == "Failure");
    }

    [Fact]
    public async Task Promote_PersistsFlag_AndAuditsIt()
    {
        await Seed(("admin", true, true), ("bob", false, true));
        Assert.NotNull(await _auth.LoginAsync("admin", Password));

        await _users.UpdateUserAsync("bob", "b@x", "Bob", true, true);

        Assert.True((await Reload("bob")).IsSystemAdmin);
        Assert.Contains(Events(), e => e.Event == "UserUpdated" && e.Outcome == "Success" && e.Detail.Contains("systemAdmin=True"));
    }

    [Fact]
    public async Task UpdateWithUpn_PersistsFlag()
    {
        await Seed(("admin", true, true), ("bob", false, true));
        Assert.NotNull(await _auth.LoginAsync("admin", Password));

        await _users.UpdateUserWithUPNAsync("bob", "b@x", "Bob", true, true, "b@c.t");

        var bob = await Reload("bob");
        Assert.True(bob.IsSystemAdmin);
        Assert.Equal("b@c.t", bob.UPN);
    }

    [Fact]
    public async Task DemoteAnotherAdmin_WhileAThirdActiveAdminExists_IsAllowed()
    {
        await Seed(("admin", true, true), ("other", true, true), ("third", true, true));
        Assert.NotNull(await _auth.LoginAsync("admin", Password));

        await _users.UpdateUserAsync("other", "o@x", "Other", true, false);

        Assert.False((await Reload("other")).IsSystemAdmin);
    }

    [Fact]
    public async Task DemoteTheOnlyOtherAdmin_WhileCallerIsActive_IsAllowed()
    {
        await Seed(("admin", true, true), ("other", true, true));
        Assert.NotNull(await _auth.LoginAsync("admin", Password));

        await _users.UpdateUserAsync("other", "o@x", "Other", true, false);

        Assert.False((await Reload("other")).IsSystemAdmin);
        Assert.True((await Reload("admin")).IsSystemAdmin);
    }

    [Fact]
    public async Task CallerCannotDemoteThemselves_EvenWhenOtherAdminsExist()
    {
        await Seed(("admin", true, true), ("other", true, true));
        Assert.NotNull(await _auth.LoginAsync("admin", Password));

        await AssertRefused(() => _users.UpdateUserAsync("admin", "a@x", "Admin", true, false), "UserUpdated");
    }

    [Fact]
    public async Task CallerCannotDeactivateThemselves_EvenWhenOtherAdminsExist()
    {
        await Seed(("admin", true, true), ("other", true, true));
        Assert.NotNull(await _auth.LoginAsync("admin", Password));

        await AssertRefused(() => _users.UpdateUserAsync("admin", "a@x", "Admin", false, true), "UserUpdated");
    }

    [Fact]
    public async Task InactiveAdminDoesNotCount_DeactivatingTheOnlyActiveOneIsRefused()
    {
        // Caller is no longer active in users.json, B is inactive: A is the only active admin.
        await Seed(("caller", true, true), ("a", true, true), ("b", true, false));
        Assert.NotNull(await _auth.LoginAsync("caller", Password));
        await Seed2Inactive("caller");

        await AssertRefused(() => _users.UpdateUserAsync("a", "a@x", "A", false, true), "UserUpdated");
        Assert.True((await Reload("a")).IsActive);
    }

    [Fact]
    public async Task ZeroActiveAdmins_ViaDeactivate_IsRefused()
    {
        // The caller is no longer active in users.json (another admin deactivated them), so "sole" is the last active admin.
        await Seed(("caller", true, true), ("sole", true, true));
        Assert.NotNull(await _auth.LoginAsync("caller", Password));
        // Another admin deactivated the caller after sign-in; the live session still works.
        await Seed2Inactive("caller");

        await AssertRefused(() => _users.UpdateUserAsync("sole", "s@x", "Sole", false, true), "UserUpdated");
        Assert.True((await Reload("sole")).IsActive);
    }

    [Fact]
    public async Task ZeroActiveAdmins_ViaDemote_IsRefused()
    {
        await Seed(("caller", true, true), ("sole", true, true));
        Assert.NotNull(await _auth.LoginAsync("caller", Password));
        await Seed2Inactive("caller");

        await AssertRefused(() => _users.UpdateUserAsync("sole", "s@x", "Sole", true, false), "UserUpdated");
        Assert.True((await Reload("sole")).IsSystemAdmin);
    }

    [Fact]
    public async Task ZeroActiveAdmins_ViaDelete_IsRefused()
    {
        await Seed(("caller", true, true), ("sole", true, true));
        Assert.NotNull(await _auth.LoginAsync("caller", Password));
        await Seed2Inactive("caller");

        await AssertRefused(() => _users.DeleteUserAsync("sole"), "UserDeleted");
        Assert.NotNull(await _users.GetUserByIdAsync("sole"));
    }

    [Fact]
    public async Task DeletingANonAdmin_IsAllowed()
    {
        await Seed(("admin", true, true), ("bob", false, true));
        Assert.NotNull(await _auth.LoginAsync("admin", Password));

        Assert.True(await _users.DeleteUserAsync("bob"));
    }

    [Fact]
    public async Task NonSystemAdmin_IsStillUnauthorized()
    {
        await Seed(("admin", true, true), ("bob", false, true));
        Assert.NotNull(await _auth.LoginAsync("bob", Password));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _users.UpdateUserAsync("bob", "b@x", "Bob", true, true));
    }

    // Marks a user inactive in users.json behind the service's back; the signed-in session is unaffected.
    private async Task Seed2Inactive(string id)
    {
        var repo = new JsonFileRepository<UsersCollection>(_storage, "config", "");
        var collection = (await repo.GetByIdAsync("users"))!;
        collection.Users.First(u => u.UserID == id).IsActive = false;
        await repo.UpdateAsync(collection);
    }
}
