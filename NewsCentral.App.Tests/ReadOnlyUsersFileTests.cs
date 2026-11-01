using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NewsCentral.Models;
using NewsCentral.Repositories;
using NewsCentral.Services;

namespace NewsCentral.App.Tests;

/// <summary>
/// users.json is writable by administrators only. Login, the UI language and the password page must keep working
/// (or explain themselves) for an author whose Windows account can read it but not write it.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ReadOnlyUsersFileTests : IDisposable
{
    private const string Password = "Passw0rd!";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nc-app-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefsPath;
    private readonly EnvironmentContext _environment;
    private readonly LocalStorageService _storage;
    private readonly User _user;

    private string UsersFile => Path.Combine(_root, "data", "config", "users.json");

    public ReadOnlyUsersFileTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data", "config"));
        _prefsPath = Path.Combine(_root, "local", "user-preferences.json");
        _environment = new EnvironmentContext(Path.Combine(_root, "data"));
        _storage = new LocalStorageService(_environment);

        _user = new User
        {
            UserID = "u-1",
            Username = "author",
            UPN = "author@contoso.test",
            Email = "a@contoso.test",
            DisplayName = "Author",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, 4),
            IsActive = true
        };
        Seed(_user);
    }

    public void Dispose()
    {
        try { File.SetAttributes(UsersFile, FileAttributes.Normal); } catch { /* best effort */ }
        try { AllowRead(); } catch { /* best effort */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Seed(User user)
    {
        var repo = new JsonFileRepository<UsersCollection>(_storage, "config", "");
        repo.CreateAsync(new UsersCollection { Users = { user } }).GetAwaiter().GetResult();
    }

    private void MakeReadOnly() => File.SetAttributes(UsersFile, FileAttributes.ReadOnly);

    private AuthenticationService NewAuth(UserPreferenceStore? prefs = null, UiLanguageService? ui = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:EnableLocalAccounts"] = "true",
            ["Authentication:EnableAutoLogin"] = "true",
            ["Authentication:UseMockUPN"] = "true",
            ["Authentication:MockUPN"] = "author@contoso.test"
        }).Build();

        return new AuthenticationService(
            _storage, new WindowsIdentityService(config), ui ?? new UiLanguageService(), config,
            NullLogger<AuthenticationService>.Instance, prefs ?? new UserPreferenceStore(_prefsPath));
    }

    [Fact]
    public async Task LocalLogin_Succeeds_WhenUsersJsonIsReadOnly()
    {
        MakeReadOnly();
        var before = File.ReadAllText(UsersFile);

        var user = await NewAuth().LoginAsync("author", Password);

        Assert.NotNull(user);
        Assert.Equal("author", user!.Username);
        Assert.Equal(before, File.ReadAllText(UsersFile));   // nothing was written
    }

    [Fact]
    public async Task LocalLogin_StillRejectsAWrongPassword_WhenUsersJsonIsReadOnly()
    {
        MakeReadOnly();

        Assert.Null(await NewAuth().LoginAsync("author", "wrong-password"));
    }

    [Fact]
    public async Task LocalLogin_RecordsLastLogin_WhenWritable()
    {
        var auth = NewAuth();

        await auth.LoginAsync("author", Password);

        var stored = new JsonFileRepository<UsersCollection>(_storage, "config", "").GetByIdAsync("users").GetAwaiter().GetResult();
        Assert.NotNull(stored!.Users.Single().LastLogin);
    }

#if DEBUG
    // The mock-UPN shortcut that makes this testable exists in Debug builds only.
    [Fact]
    public async Task UpnLogin_Succeeds_WhenUsersJsonIsReadOnly()
    {
        MakeReadOnly();

        var result = await NewAuth().LoginWithUpnAsync();

        Assert.Equal(UpnLoginOutcome.Success, result.Outcome);
        Assert.Equal("author", result.User!.Username);
    }
#endif

    [Fact]
    public async Task PreferredLanguage_RoundTripsThroughTheLocalStore_WithoutTouchingUsersJson()
    {
        MakeReadOnly();
        var before = File.ReadAllText(UsersFile);
        var auth = NewAuth();
        await auth.LoginAsync("author", Password);

        await auth.UpdatePreferredUiLanguageAsync("de");

        Assert.Equal("de", auth.GetCurrentUser()!.PreferredUiLanguage);
        Assert.Equal(before, File.ReadAllText(UsersFile));

        // A new session (restart): a fresh service and store over the same file picks it up.
        var ui = new UiLanguageService();
        var restarted = NewAuth(new UserPreferenceStore(_prefsPath), ui);
        var user = await restarted.LoginAsync("author", Password);
        Assert.Equal("de", user!.PreferredUiLanguage);
        Assert.Equal("de", ui.CurrentUiCulture.TwoLetterISOLanguageName);
    }

    [Fact]
    public async Task LegacyUsersJsonValue_IsPickedUp_ReadOnly()
    {
        _user.PreferredUiLanguage = "fr";
        var repo = new JsonFileRepository<UsersCollection>(_storage, "config", "");
        var collection = (await repo.GetByIdAsync("users"))!;
        collection.Users[0].PreferredUiLanguage = "fr";
        await repo.UpdateAsync(collection);
        MakeReadOnly();
        var before = File.ReadAllText(UsersFile);
        var ui = new UiLanguageService();

        var user = await NewAuth(ui: ui).LoginAsync("author", Password);

        Assert.Equal("fr", user!.PreferredUiLanguage);
        Assert.Equal("fr", ui.CurrentUiCulture.TwoLetterISOLanguageName);
        Assert.Equal(before, File.ReadAllText(UsersFile));   // the migration is read-only
        Assert.False(File.Exists(_prefsPath));               // and does not even write the local store
    }

    [Fact]
    public async Task LocalChoice_WinsOverTheLegacyValue_IncludingFollowWindows()
    {
        var repo = new JsonFileRepository<UsersCollection>(_storage, "config", "");
        var collection = (await repo.GetByIdAsync("users"))!;
        collection.Users[0].PreferredUiLanguage = "fr";
        await repo.UpdateAsync(collection);

        var auth = NewAuth();
        await auth.LoginAsync("author", Password);
        await auth.UpdatePreferredUiLanguageAsync(null);   // explicit "follow Windows"

        var restarted = NewAuth(new UserPreferenceStore(_prefsPath));
        var user = await restarted.LoginAsync("author", Password);

        Assert.Null(user!.PreferredUiLanguage);
    }

    [Fact]
    public async Task ChangePassword_WhenUsersJsonIsReadOnly_ThrowsTheExplanation_AndChangesNothing()
    {
        var auth = NewAuth();
        await auth.LoginAsync("author", Password);
        var users = new UserService(_storage, auth);
        MakeReadOnly();
        var before = File.ReadAllText(UsersFile);

        var ex = await Assert.ThrowsAsync<UsersFileNotWritableException>(() => users.ChangePasswordAsync("u-1", "New-Passw0rd!"));

        Assert.Equal("Your password can only be changed by an administrator.", ex.Message);
        Assert.Equal(before, File.ReadAllText(UsersFile));
    }

    [Fact]
    public async Task ChangePassword_WhenWritable_Works()
    {
        var auth = NewAuth();
        await auth.LoginAsync("author", Password);
        var users = new UserService(_storage, auth);

        Assert.True(await users.ChangePasswordAsync("u-1", "New-Passw0rd!"));
        Assert.True(await auth.VerifyCurrentPasswordAsync("New-Passw0rd!"));
        Assert.False(await auth.VerifyCurrentPasswordAsync(Password));
    }

    private static readonly FileSystemAccessRule DenyRead = new(
        WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);

    private void MakeUnreadable()
    {
        var info = new FileInfo(UsersFile);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(DenyRead);
        info.SetAccessControl(acl);
    }

    private void AllowRead()
    {
        var info = new FileInfo(UsersFile);
        var acl = info.GetAccessControl();
        acl.RemoveAccessRule(DenyRead);
        info.SetAccessControl(acl);
    }

    [Fact]
    public async Task LocalLogin_WhenUsersJsonCannotBeRead_ThrowsTheExplainableException_AndAuditsNothingSecret()
    {
        if (!OperatingSystem.IsWindows()) return;
        MakeUnreadable();

        var ex = await Assert.ThrowsAsync<UsersFileNotReadableException>(() => NewAuth().LoginAsync("author", Password));
        Assert.IsAssignableFrom<UnauthorizedAccessException>(ex.InnerException);
        Assert.DoesNotContain(Password, ex.ToString());
    }

    [Fact]
    public async Task UpnLogin_WhenUsersJsonCannotBeRead_ThrowsTheExplainableException()
    {
        if (!OperatingSystem.IsWindows()) return;
        MakeUnreadable();

        await Assert.ThrowsAsync<UsersFileNotReadableException>(() => NewAuth().LoginWithUpnAsync());
    }

    [Fact]
    public async Task VerifyCurrentPassword_DoesNotWriteUsersJson()
    {
        var auth = NewAuth();
        await auth.LoginAsync("author", Password);
        MakeReadOnly();
        var before = File.ReadAllText(UsersFile);

        Assert.True(await auth.VerifyCurrentPasswordAsync(Password));
        Assert.Equal(before, File.ReadAllText(UsersFile));
    }
}
