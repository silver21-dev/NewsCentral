using System.Text.Json.Serialization;
using NewsCentral.Security;

namespace NewsCentral.Models;

public class SessionTelemetry : ISignable
{
    /// <summary><see cref="RecordType"/> of a "News arrived!" notification record.</summary>
    public const string NotificationRecordType = "notification";

    /// <summary><see cref="Source"/> of a poster display that was opened by expanding a notification.</summary>
    public const string NotificationSource = "notification";

    public string SessionId { get; set; } = string.Empty;
    public string PresentationId { get; set; } = string.Empty;
    public string TeamId { get; set; } = string.Empty;
    public DateTime SessionStartTime { get; set; }
    public DateTime SessionEndTime { get; set; }
    public string CloseReason { get; set; } = string.Empty;

    // The three fields below are OPTIONAL and omitted from the JSON when null, so the canonical (signed)
    // form of an ordinary poster record is byte-identical to what it was before they existed: existing
    // signatures stay valid and an older reader simply never sees them.

    /// <summary>Null = a poster display session; "notification" = a "News arrived!" notification record
    /// (SessionStartTime = shown, SessionEndTime = outcome, CloseReason = Expanded | Dismissed | TimedOut).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecordType { get; set; }

    /// <summary>Null = launched directly; "notification" = the poster was opened from a notification.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>Content key (presentation id | version | image hash) of a notification record.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentKey { get; set; }

    public string? Signature { get; set; }
}
