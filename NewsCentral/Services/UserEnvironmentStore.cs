using System.Text.Json;
using NewsCentral.Configuration;

namespace NewsCentral.Services;

/// <summary>
/// Reads and writes <c>%LocalAppData%\{Company}\NewsCentral\user-environments.json</c> — this
/// machine+user's local environment list (<see cref="UserEnvironmentState"/>). Never shared between
/// users or machines (that is M4b); never watched or polled.
/// </summary>
public sealed class UserEnvironmentStore
{
    private readonly string _filePath;

    public UserEnvironmentStore()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SolutionConstants.Company,
            "NewsCentral");
        _filePath = Path.Combine(folder, "user-environments.json");
    }

    /// <summary>Test seam: a store backed by an explicit file, so tests never touch the real per-user file.</summary>
    public UserEnvironmentStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Loads the state synchronously — a missing file returns a fresh empty state; a corrupt or
    /// unreadable file does the same (logged at Debug) and is deliberately NOT overwritten here, so
    /// a bad file is only ever replaced by the next successful <see cref="SaveAsync"/>, not silently
    /// wiped just by being read. Synchronous because MauiProgram needs the state before the DI
    /// container (and therefore any async host) exists, to pick the startup DataPath.
    /// </summary>
    public UserEnvironmentState Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new UserEnvironmentState();

            var json = File.ReadAllText(_filePath);
            var state = UserEnvironmentStateJson.Deserialize(json);
            return state ?? new UserEnvironmentState();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UserEnvironmentStore] Failed to read {_filePath}: {ex.Message}");
            return new UserEnvironmentState();
        }
    }

    /// <summary>Atomic write: content.tmp then File.Move(overwrite) — a reader never sees a half-written file.</summary>
    public async Task SaveAsync(UserEnvironmentState state)
    {
        var folder = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(folder);

        var tempPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(state, UserEnvironmentStateJson.Options);
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }
}
