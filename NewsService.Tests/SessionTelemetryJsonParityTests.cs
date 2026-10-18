using System.Text.Json;
using NewsCentral.Models;
using NewsCentral.Security;
using NewsCentral.Statistics;
using NewsViewer.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Parity guard: the files NewsViewer's real TelemetryWriter produces must read back field-for-field with
/// the option set the statistics engine uses, and with NewsService's own JsonDefaults (the NewsViewer one
/// is internal to that assembly, so the real writer stands in for it).
/// </summary>
public sealed class SessionTelemetryJsonParityTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "nsvc-parity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static readonly DateTime Start = new(2026, 10, 12, 8, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddSeconds(34);
    private const string Pres = "4dd97030-61df-42d2-a89d-f57a7e171b2d";

    [Fact]
    public void EngineOptions_MatchNewsServiceOptions()
    {
        var engine = PosterStatisticsEngine.SessionJsonOptions;
        var service = JsonDefaults.Options;
        Assert.Equal(service.WriteIndented, engine.WriteIndented);
        Assert.Equal(service.PropertyNamingPolicy, engine.PropertyNamingPolicy);
        Assert.Equal(service.PropertyNameCaseInsensitive, engine.PropertyNameCaseInsensitive);
        Assert.Equal(service.Converters.Select(c => c.GetType()), engine.Converters.Select(c => c.GetType()));
    }

    [Fact]
    public void WriterOutput_ReadsFieldForField_WithEngineOptions()
    {
        var writer = new TelemetryWriter(_root, new HmacService(new HmacOptions
        {
            SecretKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray())
        }));

        writer.WriteSession(Pres, "cz-its", Start, End, "UserClose");                           // source null
        writer.WriteSession(Pres, "cz-its", Start, End, "UrlLaunch", SessionTelemetry.NotificationSource);
        writer.WriteNotification(Pres, "cz-its", "key|v1|sha256:abc", Start, End, "Expanded");

        var files = Directory.GetFiles(Path.Combine(_root, "uploads"), "session-*.json");
        Assert.Equal(3, files.Length);

        var seen = new Dictionary<string, SessionTelemetry>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var viaEngine = JsonSerializer.Deserialize<SessionTelemetry>(text, PosterStatisticsEngine.SessionJsonOptions)!;
            var viaService = JsonSerializer.Deserialize<SessionTelemetry>(text, JsonDefaults.Options)!;

            Assert.Equal(
                JsonSerializer.Serialize(viaService, JsonDefaults.Options),
                JsonSerializer.Serialize(viaEngine, JsonDefaults.Options));

            Assert.Equal(Pres, viaEngine.PresentationId);
            Assert.Equal("cz-its", viaEngine.TeamId);
            Assert.Equal(Start, viaEngine.SessionStartTime);
            Assert.Equal(End, viaEngine.SessionEndTime);
            Assert.Equal(DateTimeKind.Utc, viaEngine.SessionStartTime.Kind);
            Assert.Equal($"session-{viaEngine.SessionId}.json", Path.GetFileName(file));
            Assert.False(string.IsNullOrEmpty(viaEngine.Signature));
            seen[viaEngine.RecordType is not null ? "record" : viaEngine.Source is not null ? "source" : "plain"] = viaEngine;
        }

        var plain = seen["plain"];
        Assert.Equal("UserClose", plain.CloseReason);
        Assert.Null(plain.RecordType);
        Assert.Null(plain.Source);
        Assert.Null(plain.ContentKey);

        var fromNotification = seen["source"];
        Assert.Equal("UrlLaunch", fromNotification.CloseReason);
        Assert.Null(fromNotification.RecordType);

        var notification = seen["record"];
        Assert.Equal("Expanded", notification.CloseReason);
        Assert.Equal("key|v1|sha256:abc", notification.ContentKey);
        Assert.Null(notification.Source);
    }
}
