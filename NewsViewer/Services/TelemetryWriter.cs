using System.Text.Json;
using NewsCentral.Models;
using NewsCentral.Security;

namespace NewsViewer.Services;

public sealed class TelemetryWriter
{
    private readonly string _uploadsPath;
    private readonly HmacService _hmac;

    public TelemetryWriter(string cacheRootPath, HmacService hmac)
    {
        _uploadsPath = Path.Combine(cacheRootPath, "uploads");
        _hmac        = hmac;
    }

    /// <summary>A poster display session. <paramref name="source"/> is "notification" when the poster was
    /// opened by expanding the "News arrived!" notification, otherwise null.</summary>
    public void WriteSession(
        string presentationId,
        string teamId,
        DateTime startTime,
        DateTime endTime,
        string closeReason,
        string? source = null) =>
        Write(new SessionTelemetry
        {
            PresentationId   = presentationId,
            TeamId           = teamId,
            SessionStartTime = startTime,
            SessionEndTime   = endTime,
            CloseReason      = closeReason,
            Source           = source
        });

    /// <summary>One shown "News arrived!" notification: shown time, outcome and the time it took.</summary>
    public void WriteNotification(
        string presentationId,
        string teamId,
        string contentKey,
        DateTime shownUtc,
        DateTime outcomeUtc,
        string outcome) =>
        Write(new SessionTelemetry
        {
            RecordType       = SessionTelemetry.NotificationRecordType,
            PresentationId   = presentationId,
            TeamId           = teamId,
            ContentKey       = contentKey,
            SessionStartTime = shownUtc,
            SessionEndTime   = outcomeUtc,
            CloseReason      = outcome
        });

    // Same session-file mechanism for both record kinds: HMAC-signed when a key is configured, written to
    // uploads\session-*.json, forwarded (or not) by NewsService's existing Telemetry:UploadEnabled gate.
    private void Write(SessionTelemetry record)
    {
        try
        {
            Directory.CreateDirectory(_uploadsPath);

            record.SessionId = Guid.NewGuid().ToString();
            record.Signature = _hmac.Sign(record);

            var path = Path.Combine(_uploadsPath, $"session-{record.SessionId}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(record, JsonDefaults.Options));
        }
        catch { }
    }
}
