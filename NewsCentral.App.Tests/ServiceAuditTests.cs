using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NewsCentral.Audit;
using NewsCentral.Models;
using NewsCentral.Repositories;
using NewsCentral.Services;

namespace NewsCentral.App.Tests;

/// <summary>Authentication and user-management services write their audit events themselves, so every UI path is covered.</summary>
public sealed class ServiceAuditTests : IDisposable
{
    private const string Password = "Passw0rd!";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nc-svcaudit-" + Guid.NewGuid().ToString("N"));
    private readonly AuditContext _context = new();
    private readonly LocalStorageService _storage;
    private readonly AuditLog _audit;
    private readonly AuthenticationService _auth;
    private readonly UserService _users;

    public ServiceAuditTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data", "config"));
        _storage = new LocalStorageService(new EnvironmentContext(Path.Combine(_root, "data")));

        var admin = new User
        {
            UserID = "u-admin", Username = "admin", IsSystemAdmin = true, IsActive = true,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, 4)
        };
        new JsonFileRepository<UsersCollection>(_storage, "config", "")
            .CreateAsync(new UsersCollection { Users = { admin } }).GetAwaiter().GetResult();

        var settings = new AuditSettings(true, Path.Combine(_root, "audit"), false, "Test env");
        _audit = new AuditLog(() => Task.FromResult(settings), _context, "ws01", "jdoe");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:EnableLocalAccounts"] = "true"
        }).Build();
        _auth = new AuthenticationService(
            _storage, new WindowsIdentityService(config), new UiLanguageService(), config,
            NullLogger<AuthenticationService>.Instance, new UserPreferenceStore(Path.Combine(_root, "prefs.json")),
            _audit, _context);
        _users = new UserService(_storage, _auth, _audit);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private List<JsonElement> Events()
    {
        var dir = Path.Combine(_root, "audit");
        if (!Directory.Exists(dir)) return new();
        return Directory.GetFiles(dir, "*.jsonl", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .ToList();
    }

    private static string Ev(JsonElement e) => e.GetProperty("event").GetString()!;

    [Fact]
    public async Task Login_Failure_Success_AndLogout_AreRecorded_WithoutThePassword()
    {
        await _auth.LoginAsync("admin", "wrong");
        await _auth.LoginAsync("nobody", "whatever");
        await _auth.LoginAsync("admin", Password);
        _auth.Logout();
        await Task.Delay(200);   // Logout records fire-and-forget (its signature is synchronous)

        var events = Events();
        Assert.Equal(new[] { "LoginFailed", "LoginFailed", "LoginSucceeded", "Logout" }, events.Select(Ev).ToArray());

        Assert.Equal("admin", events[0].GetProperty("attemptedUser").GetString());
        Assert.Equal("Failure", events[0].GetProperty("outcome").GetString());
        Assert.Equal("nobody", events[1].GetProperty("attemptedUser").GetString());
        Assert.Equal("admin", events[2].GetProperty("appUser").GetString());
        Assert.Equal("u-admin", events[2].GetProperty("target").GetString());
        Assert.Equal("admin", events[3].GetProperty("appUser").GetString());

        var raw = string.Join("\n", Directory.GetFiles(Path.Combine(_root, "audit"), "*.jsonl", SearchOption.AllDirectories).SelectMany(File.ReadAllLines));
        Assert.DoesNotContain(Password, raw);
        Assert.DoesNotContain("wrong", raw);
        Assert.DoesNotContain("$2", raw);   // no bcrypt hash
    }

    [Fact]
    public async Task UserLifecycle_IsRecorded_AndFailuresAreRecordedAsFailures()
    {
        await _auth.LoginAsync("admin", Password);

        var created = await _users.CreateUserAsync("bob", "b@x", "Bob", "Sup3r-secret!");
        await _users.UpdateUserAsync(created.UserID, "b2@x", "Bobby", true);
        await _users.UpdateUserUPNAsync(created.UserID, "bob@contoso.test");
        await _users.AssignUserToTeamAsync(created.UserID, "t-1", "cz-its", new List<string> { "ContentAuthor" });
        await _users.UpdateTeamRolesAsync(created.UserID, "t-1", new List<string> { "ContentAuthor", "Approver" });
        await _users.RemoveUserFromTeamAsync(created.UserID, "t-1");
        await _users.ChangePasswordAsync(created.UserID, "An0ther-secret!");
        await _users.DeleteUserAsync(created.UserID);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _users.CreateUserAsync("admin", "a@x", "Dup", "pw-123456"));

        var events = Events().Skip(1).ToList();   // skip LoginSucceeded
        Assert.Equal(
            new[]
            {
                "UserCreated", "UserUpdated", "UserUpnChanged", "UserTeamAssigned", "UserRolesChanged",
                "UserTeamRemoved", "PasswordChanged", "UserDeleted", "UserCreated"
            },
            events.Select(Ev).ToArray());
        Assert.All(events.Take(8), e => Assert.Equal("Success", e.GetProperty("outcome").GetString()));
        Assert.Equal("Failure", events[8].GetProperty("outcome").GetString());
        Assert.Equal(created.UserID, events[0].GetProperty("target").GetString());
        Assert.Equal("admin", events[0].GetProperty("appUser").GetString());

        var raw = File.ReadAllText(Directory.GetFiles(Path.Combine(_root, "audit"), "*.jsonl", SearchOption.AllDirectories).Single());
        Assert.DoesNotContain("Sup3r-secret!", raw);
        Assert.DoesNotContain("An0ther-secret!", raw);
        Assert.DoesNotContain("$2", raw);
    }

    [Fact]
    public async Task UnauthorizedOperation_IsRecordedAsFailure_AndStillThrows()
    {
        // Nobody is signed in.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _users.DeleteUserAsync("u-admin"));

        var e = Assert.Single(Events());
        Assert.Equal("UserDeleted", Ev(e));
        Assert.Equal("Failure", e.GetProperty("outcome").GetString());
    }
}
