using System.Text.Json;
using NewsCentral.Configuration;

namespace NewsCentral.Services;

/// <summary>
/// Per-user preferences kept on THIS machine, in <c>%LocalAppData%\{Company}\NewsCentral\user-preferences.json</c>
/// next to <c>user-environments.json</c>. Currently the UI language, keyed by the application user's id.
///
/// This replaces <c>User.PreferredUiLanguage</c> in users.json, which every author would otherwise need write
/// access to. users.json is only ever READ for this value (the legacy fallback in
/// <see cref="AuthenticationService"/>): an entry here, including an explicit "follow Windows", always wins
/// over the old users.json value, and nothing here writes users.json.
/// </summary>
public sealed class UserPreferenceStore
{
    private sealed class State
    {
        // userId -> language code; "" means "follow Windows" (an explicit choice that overrides the legacy value).
        public Dictionary<string, string> Languages { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly object _gate = new();

    public UserPreferenceStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SolutionConstants.Company,
            "NewsCentral",
            "user-preferences.json"))
    {
    }

    public UserPreferenceStore(string filePath) => _filePath = filePath;

    /// <summary>
    /// Found = false means this user has never chosen a language on this machine (the caller may fall back to the
    /// legacy users.json value). Found = true with a null Language means "follow Windows".
    /// </summary>
    public (bool Found, string? Language) GetPreferredLanguage(string userId)
    {
        lock (_gate)
        {
            var state = Load();
            if (!state.Languages.TryGetValue(userId, out var value))
                return (false, null);
            return (true, string.IsNullOrEmpty(value) ? null : value);
        }
    }

    /// <summary>Records the choice (null = follow Windows). Throws on a write failure; the caller decides how to report it.</summary>
    public void SetPreferredLanguage(string userId, string? language)
    {
        lock (_gate)
        {
            var state = Load();
            state.Languages[userId] = string.IsNullOrWhiteSpace(language) ? string.Empty : language.Trim();

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
            File.Move(temp, _filePath, overwrite: true);
        }
    }

    // A missing or unreadable file is an empty state. It is not overwritten by a read, only by the next successful Set.
    private State Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new State();
            return JsonSerializer.Deserialize<State>(File.ReadAllText(_filePath), Json) ?? new State();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UserPreferenceStore] Failed to read {_filePath}: {ex.Message}");
            return new State();
        }
    }
}
