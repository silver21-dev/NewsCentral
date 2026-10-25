using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NewsCentral.Audit;
using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsCentral.Repositories;
using NewsCentral.Services;

namespace NewsCentral.App.Tests;

/// <summary>
/// config/environments.json decides which environments every user's picker offers. Only a signed-in SystemAdmin may
/// write it; before login, and for everyone else, the sync merges it into the local cache and never writes it.
/// </summary>
public sealed class EnvironmentDirectorySyncTests : IDisposable
{
    private const string Password = "Passw0rd!";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nc-envsync-" + Guid.NewGuid().ToString("N"));
    private readonly string _sharedFile;
    private readonly AuthenticationService _auth;
    private readonly EnvironmentDirectoryService _directory;
    private readonly UserEnvironmentStore _store;

    // The shared directory only accepts UNC paths. Nothing is ever opened at these.
    private const string RemoteEntryPath = @"\\fileserver\news\remote-env";
    private const string LocalEntryPath = @"\\fileserver\news\local-env";

    public EnvironmentDirectorySyncTests()
    {
        var dataPath = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(dataPath, "config"));
        _sharedFile = Path.Combine(dataPath, "config", "environments.json");

        var environment = new EnvironmentContext(dataPath);
        var storage = new LocalStorageService(environment);

        User Make(string id, bool sysAdmin) => new()
        {
            UserID = id, Username = id, IsSystemAdmin = sysAdmin, IsActive = true,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, 4)
        };
        new JsonFileRepository<UsersCollection>(storage, "config", "")
            .CreateAsync(new UsersCollection { Users = { Make("admin", true), Make("author", false) } })
            .GetAwaiter().GetResult();

        // The shared file knows one environment; this machine's local cache knows a different one.
        var now = DateTime.UtcNow;
        File.WriteAllText(_sharedFile, JsonSerializer.Serialize(
            new SharedDirectoryFile { Entries = { new SharedDirectoryEntry(RemoteEntryPath, "Remote", "admin", now, null) } },
            SharedDirectoryJson.Options));

        _store = new UserEnvironmentStore(Path.Combine(_root, "local", "user-environments.json"));
        _store.SaveAsync(new UserEnvironmentState
        {
            SharedEntries = { new SharedDirectoryEntry(LocalEntryPath, "Local", "author", now, null) }
        }).GetAwaiter().GetResult();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:EnableLocalAccounts"] = "true"
        }).Build();
        _auth = new AuthenticationService(
            storage, new WindowsIdentityService(config), new UiLanguageService(), config,
            NullLogger<AuthenticationService>.Instance, new UserPreferenceStore(Path.Combine(_root, "prefs.json")));

        var appConfig = new AppConfiguration(config);
        var settings = new EnvironmentSettingsService(storage, environment, appConfig, _auth, EnvironmentCatalog.Empty);
        _directory = new EnvironmentDirectoryService(
            EnvironmentCatalog.Empty, appConfig, _store, environment, _auth, new TeamContextService(), storage, settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string[] LeftoverTempFiles() =>
        Directory.GetFiles(Path.GetDirectoryName(_sharedFile)!, "*.tmp");

    private bool LocalCacheKnows(string path) =>
        _store.Load().SharedEntries.Any(e => e.DeletedUtc == null &&
            EnvironmentPaths.Canonicalize(e.DataPath) == EnvironmentPaths.Canonicalize(path));

    private bool SharedFileMentions(string path) =>
        File.ReadAllText(_sharedFile).Contains(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task BeforeLogin_TheSharedFileIsReadIntoTheLocalCache_AndNeverWritten()
    {
        var before = await File.ReadAllBytesAsync(_sharedFile);

        await _directory.SyncSharedDirectoryAsync(EnvironmentInitStatus.Ready);

        Assert.Equal(before, await File.ReadAllBytesAsync(_sharedFile));
        Assert.Empty(LeftoverTempFiles());
        Assert.True(LocalCacheKnows(RemoteEntryPath));   // the read half still works
        Assert.True(LocalCacheKnows(LocalEntryPath));
    }

    [Fact]
    public async Task ASignedInAuthor_NeverWritesTheSharedFile()
    {
        Assert.NotNull(await _auth.LoginAsync("author", Password));
        var before = await File.ReadAllBytesAsync(_sharedFile);

        await _directory.SyncSharedDirectoryAsync(EnvironmentInitStatus.Ready);

        Assert.Equal(before, await File.ReadAllBytesAsync(_sharedFile));
        Assert.Empty(LeftoverTempFiles());
        Assert.False(SharedFileMentions(LocalEntryPath));
    }

    [Fact]
    public async Task ASystemAdmin_WritesTheMergedFileBack()
    {
        Assert.NotNull(await _auth.LoginAsync("admin", Password));

        await _directory.SyncSharedDirectoryAsync(EnvironmentInitStatus.Ready);

        Assert.True(SharedFileMentions(LocalEntryPath));
        Assert.True(SharedFileMentions(RemoteEntryPath));
        Assert.Empty(LeftoverTempFiles());
    }
}
