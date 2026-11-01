using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;
using Xunit;

namespace NewsCentral.Shared.Tests;

/// <summary>
/// Canonical JSON stability guard. ECDSA index verification re-serializes the deserialized index
/// and verifies over those bytes, so signer and verifier must emit byte-identical output. The
/// committed fixture was signed on .NET 9; this test must keep passing on every later runtime.
/// </summary>
public sealed class CanonicalJsonFixtureTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "CanonicalJson");

    /// <summary>
    /// A failure here means canonical JSON output changed across runtime versions: index signatures
    /// are no longer portable between signer and verifier builds. Every team's index would return
    /// Invalid and be skipped silently on devices. This is a RELEASE-BLOCKING condition — do not
    /// regenerate the fixture to make it pass; find what changed in serialization.
    /// </summary>
    [Fact]
    public void CanonicalJson_SignedOnNet9_StillVerifies()
    {
        var json = File.ReadAllText(Path.Combine(FixtureDir, "net9-signed-index.json"));
        var publicKey = File.ReadAllText(Path.Combine(FixtureDir, "net9-signed-index.publickey.txt")).Trim();

        // Same options NewsService / NewsViewer use to read index.json.
        var verifierOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var index = JsonSerializer.Deserialize<TeamIndexFile>(json, verifierOptions)!;

        // The fixture predates KeyId (and still carries the retired signingPublicKey property, which is ignored),
        // so it is checked at the canonicalization layer: same bytes in, same signature out.
        Assert.Equal(VerifyResult.Valid, new EcdsaSignatureService().Verify(index, publicKey));
    }

    /// <summary>
    /// One-off generator for the fixture. Not part of the suite. To regenerate (only ever on
    /// .NET 9): remove the Skip, run this test, restore the Skip, commit the three files.
    /// </summary>
    [Fact(Skip = "One-off fixture generator; run manually on .NET 9 only.")]
    public void Generate_Net9SignedIndexFixture()
    {
        Assert.True(Environment.Version.Major == 9,
            $"Fixture must be generated on the .NET 9 runtime, but this is {Environment.Version} " +
            $"({RuntimeInformation.FrameworkDescription}). Regenerating on a newer runtime would " +
            "hollow out the canonical JSON guard.");

        var (privateKey, publicKey) = SigningKeyTool.GenerateKeyPair();
        var persist = IndexPersistenceOptions();

        var index = new TeamIndexFile
        {
            TeamFolderName = "cz-its",
            TeamName = "CZ ITS – Žluťoučký tým",
            GeneratedAt = new DateTime(2026, 10, 12, 8, 30, 15, 987, DateTimeKind.Utc).AddTicks(1234),
            Version = "1.0.0",
            PublishedAssignments =
            {
                new PublishedAssignmentIndex
                {
                    AssignmentId = "11111111-1111-1111-1111-111111111111",
                    PresentationId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    ScheduleId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                    PresentationName = "Zpráva týdne – Příliš žluťoučký kůň ☕ 新闻",
                    PresentationDescription = "Quotes \"and\" <angle> & ünïcödé",
                    PresentationVersion = 3,
                    PresentationLastModified = new DateTime(2026, 10, 1, 12, 0, 0, 500, DateTimeKind.Utc).AddTicks(4321),
                    ScheduleStart = new DateTime(2026, 10, 13, 9, 0, 0, 250, DateTimeKind.Unspecified).AddTicks(777),
                    ScheduleEnd = new DateTime(2026, 10, 20, 17, 30, 0, DateTimeKind.Unspecified),
                    DaysOfWeek = "Mon,Tue,Wed",
                    PublishedDate = new DateTime(2026, 10, 2, 7, 15, 30, 999, DateTimeKind.Utc),
                    PublishedBy = "admin",
                    DisplayTypes = new DisplayTypeInfo { IsNewsOfWeek = true, IsWallpaper = false, IsLogonScreen = true },
                    Content = new ContentInfo
                    {
                        ImagePath = "cz-its/images/generated/poster_a_v1.jpg",
                        ImageUrl = null,
                        ImageHash = "abc123",
                        ImageSizeBytes = 123456,
                        ImageLastModified = new DateTime(2026, 10, 1, 11, 59, 59, 600, DateTimeKind.Utc),
                        MoreInfoUrl = "https://example.com/more?a=1&b=2"
                    },
                    SourceTeamFolderName = "cz-its",
                    SourceTeamName = "CZ ITS",
                    PosterText = null,
                    DisplayDurationSeconds = 15,
                    Priority = 0
                },
                new PublishedAssignmentIndex
                {
                    AssignmentId = "22222222-2222-2222-2222-222222222222",
                    PresentationId = "cccccccc-cccc-cccc-cccc-cccccccccccc",
                    ScheduleId = "dddddddd-dddd-dddd-dddd-dddddddddddd",
                    PresentationName = "Wallpaper",
                    PresentationVersion = 1,
                    PresentationLastModified = new DateTime(2026, 9, 30, 6, 0, 0, DateTimeKind.Utc),
                    ScheduleStart = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Unspecified),
                    ScheduleEnd = new DateTime(2026, 12, 31, 23, 59, 59, 999, DateTimeKind.Unspecified),
                    DaysOfWeek = "Mon,Tue,Wed,Thu,Fri,Sat,Sun",
                    PublishedDate = new DateTime(2026, 9, 30, 6, 5, 0, DateTimeKind.Utc),
                    PublishedBy = "editor",
                    DisplayTypes = new DisplayTypeInfo { IsWallpaper = true },
                    Content = new ContentInfo
                    {
                        ImagePath = "cz-its/images/generated/poster_c_v1.jpg",
                        ImageUrl = "https://blob.example.com/cz-its/poster_c_v1.jpg",
                        ImageHash = "def456",
                        ImageSizeBytes = 999,
                        ImageLastModified = new DateTime(2026, 9, 30, 6, 0, 0, DateTimeKind.Utc),
                        MoreInfoUrl = string.Empty
                    },
                    SourceTeamFolderName = "de-prod",
                    SourceTeamName = "DE Prod",
                    PosterText = "Překvapení ✓",
                    DisplayDurationSeconds = 30,
                    Priority = 2,
                    UseVirtualDesktop = true,
                    VirtualDesktopBackgroundColor = "#112233"
                }
            },
            Statistics = new IndexStatistics
            {
                TotalPublishedAssignments = 2, ActiveAssignments = 1, UpcomingAssignments = 1, ExpiredAssignments = 0
            }
        };
        index.IndexHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(index.PublishedAssignments, persist))));

        // Mirror IndexGenerationService: normalize through the persistence options (truncating
        // DateTimes to whole seconds) BEFORE signing, so the signed payload matches the file.
        index = JsonSerializer.Deserialize<TeamIndexFile>(JsonSerializer.Serialize(index, persist), persist)!;
        index.Signature = new EcdsaSignatureService().Sign(index, privateKey);

        var dir = SourceFixtureDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "net9-signed-index.json"),
            JsonSerializer.Serialize(index, persist), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "net9-signed-index.publickey.txt"),
            publicKey, new UTF8Encoding(false));

        var meta = new
        {
            generatedUtc = DateTime.UtcNow.ToString("o"),
            runtimeVersion = Environment.Version.ToString(),
            frameworkDescription = RuntimeInformation.FrameworkDescription,
            sdkVersion = "10.0.401",
            targetFramework = "net9.0",
            purpose = "Canonical JSON stability guard across runtime versions. Regenerate only on .NET 9."
        };
        File.WriteAllText(Path.Combine(dir, "net9-signed-index.meta.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        // privateKey goes out of scope here; it is never written anywhere.
    }

    private static string SourceFixtureDir([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Fixtures", "CanonicalJson");

    // COUPLED COPY: must match JsonConfiguration.GetIndexJsonOptions() and SmartDateTimeConverter in
    // NewsCentral/Configuration/JsonConfiguration.cs (the MAUI project cannot be referenced from
    // here). Used only by the generator; keep both locations in step.
    private static JsonSerializerOptions IndexPersistenceOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(), new SmartDateTimeConverter() }
    };

    private sealed class SmartDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var dateString = reader.GetString();
            if (string.IsNullOrEmpty(dateString)) return DateTime.MinValue;
            if (dateString.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
                return DateTime.Parse(dateString).ToUniversalTime();
            if (dateString.Contains("+") || dateString.LastIndexOf('-') > 8)
                return DateTime.Parse(dateString).ToUniversalTime();
            return DateTime.SpecifyKind(DateTime.Parse(dateString), DateTimeKind.Unspecified);
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            switch (value.Kind)
            {
                case DateTimeKind.Utc:
                    writer.WriteStringValue(value.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                    break;
                case DateTimeKind.Unspecified:
                    writer.WriteStringValue(value.ToString("yyyy-MM-ddTHH:mm:ss"));
                    break;
                case DateTimeKind.Local:
                    writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
                    break;
            }
        }
    }
}
