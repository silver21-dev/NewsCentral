using System.Text.Json;
using System.Text.Json.Serialization;
using NewsCentral.Models;
using NewsCentral.Security;
using Xunit;

namespace NewsCentral.Shared.Tests;

/// <summary>
/// The optional notification fields on <see cref="SessionTelemetry"/> must not disturb the HMAC of an
/// ordinary poster record (older NewsService versions verify the old shape), and must themselves be signed.
/// </summary>
public sealed class SessionTelemetryTests
{
    private static readonly HmacService Hmac = new(new HmacOptions
    {
        SecretKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray())
    });

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // The record shape before the optional fields existed.
    private sealed class LegacySessionTelemetry : ISignable
    {
        public string SessionId { get; set; } = string.Empty;
        public string PresentationId { get; set; } = string.Empty;
        public string TeamId { get; set; } = string.Empty;
        public DateTime SessionStartTime { get; set; }
        public DateTime SessionEndTime { get; set; }
        public string CloseReason { get; set; } = string.Empty;
        public string? Signature { get; set; }
    }

    private static readonly DateTime Start = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OrdinaryPosterRecord_SignsIdenticallyToTheLegacyShape()
    {
        var now = new SessionTelemetry
        {
            SessionId = "s1", PresentationId = "p1", TeamId = "cz-its",
            SessionStartTime = Start, SessionEndTime = Start.AddSeconds(30), CloseReason = "Timeout"
        };
        var legacy = new LegacySessionTelemetry
        {
            SessionId = "s1", PresentationId = "p1", TeamId = "cz-its",
            SessionStartTime = Start, SessionEndTime = Start.AddSeconds(30), CloseReason = "Timeout"
        };

        Assert.Equal(Hmac.Sign(legacy), Hmac.Sign(now));
    }

    [Fact]
    public void OrdinaryPosterRecord_OmitsTheOptionalFields()
    {
        var json = JsonSerializer.Serialize(new SessionTelemetry { SessionId = "s1" }, Json);
        Assert.DoesNotContain("recordType", json);
        Assert.DoesNotContain("source", json);
        Assert.DoesNotContain("contentKey", json);
    }

    [Fact]
    public void NotificationRecord_RoundTripsAndVerifies()
    {
        var rec = new SessionTelemetry
        {
            SessionId = "s2", PresentationId = "p1", TeamId = "cz-its",
            RecordType = SessionTelemetry.NotificationRecordType, ContentKey = "p1|v1|sha256:ab",
            SessionStartTime = Start, SessionEndTime = Start.AddSeconds(12), CloseReason = "Dismissed"
        };
        rec.Signature = Hmac.Sign(rec);

        var back = JsonSerializer.Deserialize<SessionTelemetry>(JsonSerializer.Serialize(rec, Json), Json)!;

        Assert.Equal("notification", back.RecordType);
        Assert.Equal("p1|v1|sha256:ab", back.ContentKey);
        Assert.Equal("Dismissed", back.CloseReason);
        Assert.Equal(VerifyResult.Valid, Hmac.Verify(back));
    }

    [Fact]
    public void PosterRecordFromNotification_CarriesSource_AndTamperingIsDetected()
    {
        var rec = new SessionTelemetry
        {
            SessionId = "s3", PresentationId = "p1", TeamId = "cz-its", Source = SessionTelemetry.NotificationSource,
            SessionStartTime = Start, SessionEndTime = Start.AddSeconds(5), CloseReason = "UserClose"
        };
        rec.Signature = Hmac.Sign(rec);

        var back = JsonSerializer.Deserialize<SessionTelemetry>(JsonSerializer.Serialize(rec, Json), Json)!;
        Assert.Equal(VerifyResult.Valid, Hmac.Verify(back));

        back.Source = null;   // stripping the source must invalidate the record
        Assert.Equal(VerifyResult.Invalid, Hmac.Verify(back));
    }
}
