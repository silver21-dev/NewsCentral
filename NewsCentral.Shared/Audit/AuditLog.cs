using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewsCentral.Audit;

/// <summary>
/// Append-only audit writer. Each event opens the day's file, appends one JSON line and closes it again.
/// The file is opened requesting AppendData and Synchronize only - never Write/WriteData - so it keeps working under
/// an append-only ACL (see scripts/Set-AuthoringAcl.ps1), where the author can add lines but not rewrite or delete them.
/// Writes are serialized within the process.
///
/// Path: <c>{AuditPath}\{yyyy-MM}\{yyyy-MM-dd}_{MACHINE}_{windowsUser}.jsonl</c> (UTC date).
/// The app never sets ACLs; the provisioning script does.
/// </summary>
public sealed class AuditLog : IAuditLog
{
    private const int MaxFieldLength = 200;

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // The complete, closed set of fields on a line. Anything not here cannot reach the file.
    private sealed class Line
    {
        [JsonPropertyName("utc")] public string Utc { get; set; } = "";
        [JsonPropertyName("machine")] public string Machine { get; set; } = "";
        [JsonPropertyName("windowsAccount")] public string WindowsAccount { get; set; } = "";
        [JsonPropertyName("appUser")] public string? AppUser { get; set; }
        [JsonPropertyName("attemptedUser")] public string? AttemptedUser { get; set; }
        [JsonPropertyName("environment")] public string Environment { get; set; } = "";
        [JsonPropertyName("event")] public string Event { get; set; } = "";
        [JsonPropertyName("target")] public string? Target { get; set; }
        [JsonPropertyName("outcome")] public string Outcome { get; set; } = "";
        [JsonPropertyName("detail")] public string? Detail { get; set; }
    }

    private readonly Func<Task<AuditSettings>> _settings;
    private readonly AuditContext _context;
    private readonly string _machine;
    private readonly string _windowsAccount;
    private readonly string _windowsUserForFile;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _writeFailing;

    public AuditLog(
        Func<Task<AuditSettings>> settings,
        AuditContext context,
        string? machine = null,
        string? windowsUser = null,
        Func<DateTime>? utcNow = null,
        Action<string>? log = null)
    {
        _settings = settings;
        _context = context;
        _machine = (machine ?? System.Environment.MachineName).ToUpperInvariant();
        var user = windowsUser ?? System.Environment.UserName;
        _windowsAccount = windowsUser is null && !string.IsNullOrEmpty(System.Environment.UserDomainName)
            ? $"{System.Environment.UserDomainName}\\{user}"
            : user;
        _windowsUserForFile = SanitizeForFileName(user);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _log = log;
    }

    public bool WriteFailing => _writeFailing;

    public event Action? StatusChanged;

    public async Task RecordAsync(AuditRecord record)
    {
        await _gate.WaitAsync();
        try
        {
            var settings = await _settings();
            if (!settings.Enabled)
            {
                SetFailing(false);   // nothing is being recorded, so there is nothing to warn about
                return;
            }

            var now = _utcNow();
            var path = FilePath(settings.Path, now);

            var attempted = Clean(record.AttemptedUser);
            var line = new Line
            {
                Utc = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
                Machine = _machine,
                WindowsAccount = _windowsAccount,
                AppUser = attempted is null ? Clean(record.AppUser ?? _context.AppUser) : null,
                AttemptedUser = attempted,
                Environment = Clean(settings.EnvironmentName) ?? "",
                Event = record.Event,
                Target = Clean(record.Target),
                Outcome = record.Outcome.ToString(),
                Detail = Clean(record.Detail)
            };

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, LineOptions) + "\n");
            AppendLine(path, bytes);
            SetFailing(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Audit log write failed ({ex.GetType().Name}): {ex.Message}");
            SetFailing(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>{AuditPath}\{yyyy-MM}\{yyyy-MM-dd}_{MACHINE}_{windowsUser}.jsonl for the given UTC time.</summary>
    public string FilePath(string auditRoot, DateTime utc) =>
        Path.Combine(
            auditRoot,
            utc.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
            $"{utc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}_{_machine}_{_windowsUserForFile}.jsonl");

    private void SetFailing(bool value)
    {
        if (_writeFailing == value) return;
        _writeFailing = value;
        try { StatusChanged?.Invoke(); } catch { /* a UI handler must never break auditing */ }
    }

    private static void AppendLine(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (OperatingSystem.IsWindows())
        {
            // AppendData | Synchronize only: no WriteData, no ReadData. OpenOrCreate, because Append mode would need to
            // read the length to seek to the end; a handle with only AppendData always writes at the end anyway.
            using var stream = new FileInfo(path).Create(
                FileMode.OpenOrCreate,
                FileSystemRights.AppendData | FileSystemRights.Synchronize,
                FileShare.ReadWrite,
                bufferSize: 4096,
                FileOptions.None,
                fileSecurity: null);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        else
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    /// <summary>Single-line, bounded text: control characters become spaces, long values are cut.</summary>
    internal static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var sb = new StringBuilder(Math.Min(value.Length, MaxFieldLength));
        foreach (var c in value.Trim())
        {
            if (sb.Length >= MaxFieldLength) { sb.Append('…'); break; }
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString();
    }

    internal static string SanitizeForFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(Array.IndexOf(invalid, c) >= 0 || c == '\\' || c == '/' || c == ' ' ? '_' : c);
        var cleaned = sb.ToString().Trim('.', '_');
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }
}
