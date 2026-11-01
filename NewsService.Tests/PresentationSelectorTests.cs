using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using NewsCentral.Models.IndexFile;
using NewsViewer.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// NewsViewer display-time checks (docs/anti-tamper.md §8): a candidate with a tampered cached file, an
/// empty ImageHash or a bad ImagePath is ineligible and the selector falls through to the next one;
/// with none eligible it behaves as with no content.
/// </summary>
public sealed class PresentationSelectorTests : IDisposable
{
    private const string Team = "cz-its";
    private static readonly byte[] NewerBytes = [1, 1, 1];
    private static readonly byte[] OlderBytes = [2, 2, 2];

    private readonly string _cache =
        Path.Combine(Path.GetTempPath(), "nsvw-selector-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_cache, recursive: true); } catch { /* best effort */ }
    }

    private static string Hash(byte[] b) => "sha256:" + Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private string ImageFile(string name) => Path.Combine(_cache, Team, "images", "generated", name);

    private static PublishedAssignmentIndex Item(string id, string imagePath, string imageHash, int ageDays) => new()
    {
        AssignmentId             = "asg-" + id,
        PresentationId           = id,
        PresentationLastModified = DateTime.Now.AddDays(-ageDays),
        PublishedDate            = DateTime.Now.AddDays(-ageDays),
        ScheduleStart            = DateTime.Now.AddDays(-30),
        ScheduleEnd              = DateTime.Now.AddDays(30),
        DaysOfWeek               = "1,2,3,4,5,6,7",
        SourceTeamFolderName     = Team,
        DisplayTypes             = new DisplayTypeInfo { IsNewsOfWeek = true },
        Content                  = new ContentInfo { ImagePath = imagePath, ImageHash = imageHash }
    };

    /// <summary>Newer item "new" (newer.png) and older item "old" (older.png), both valid on disk.</summary>
    private (PublishedAssignmentIndex Newer, PublishedAssignmentIndex Older) Seed(
        Action<PublishedAssignmentIndex>? tweakNewer = null, Action<PublishedAssignmentIndex>? tweakOlder = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ImageFile("x"))!);
        File.WriteAllBytes(ImageFile("newer.png"), NewerBytes);
        File.WriteAllBytes(ImageFile("older.png"), OlderBytes);

        var newer = Item("new", $"{Team}/images/generated/newer.png", Hash(NewerBytes), ageDays: 1);
        var older = Item("old", $"{Team}/images/generated/older.png", Hash(OlderBytes), ageDays: 5);
        tweakNewer?.Invoke(newer);
        tweakOlder?.Invoke(older);

        var index = new TeamIndexFile { TeamFolderName = Team, TeamName = Team, IndexHash = "h1" };
        index.PublishedAssignments.Add(newer);
        index.PublishedAssignments.Add(older);
        File.WriteAllText(Path.Combine(_cache, Team, "index.json"), JsonSerializer.Serialize(index,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() }
            }));
        return (newer, older);
    }

    private PresentationSelector NewSelector(List<(string Id, string Reason)>? rejected = null, bool bypass = false) =>
        new(_cache, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Signing:RequireSignedIndex"] = "false" }).Build(), bypass,
            onRejected: rejected is null ? null : (id, reason) => rejected.Add((id, reason)));

    [Fact]
    public void ValidItems_NewestSelected_AsBefore()
    {
        Seed();
        var rejected = new List<(string, string)>();

        var (assignment, path) = NewSelector(rejected!).SelectActive([Team]);

        Assert.Equal("new", assignment?.PresentationId);
        Assert.Equal(ImageFile("newer.png"), path);
        Assert.Empty(rejected);
    }

    [Fact]
    public void TamperedFile_MakesItemIneligible_NextCandidateSelected_WarnedOnce()
    {
        Seed();
        File.WriteAllBytes(ImageFile("newer.png"), [9, 9, 9]);
        var rejected = new List<(string Id, string Reason)>();
        var selector = NewSelector(rejected);

        var (assignment, path) = selector.SelectActive([Team]);
        selector.SelectActive([Team]);   // a second pass in the same run must not warn again

        Assert.Equal("old", assignment?.PresentationId);
        Assert.Equal(ImageFile("older.png"), path);
        var warning = Assert.Single(rejected);
        Assert.Equal("new", warning.Id);
        Assert.Contains("does not match", warning.Reason);
    }

    [Fact]
    public void EmptyImageHash_MakesItemIneligible_NoFailOpen()
    {
        Seed(tweakNewer: a => a.Content.ImageHash = "");
        var rejected = new List<(string Id, string Reason)>();

        var (assignment, _) = NewSelector(rejected).SelectActive([Team]);

        Assert.Equal("old", assignment?.PresentationId);
        Assert.Contains("ImageHash", Assert.Single(rejected).Reason);
    }

    [Theory]
    [InlineData("cz-its/images/../../../Windows/Temp/x.png")]
    [InlineData(@"\\localhost\C$\Windows\Temp\x.png")]
    [InlineData("other/images/generated/newer.png")]
    [InlineData("cz-its/newer.png")]
    public void BadImagePath_MakesItemIneligible_NextCandidateSelected(string badPath)
    {
        Seed(tweakNewer: a => a.Content.ImagePath = badPath);
        var rejected = new List<(string Id, string Reason)>();

        var (assignment, path) = NewSelector(rejected).SelectActive([Team]);

        Assert.Equal("old", assignment?.PresentationId);
        Assert.Equal(ImageFile("older.png"), path);
        Assert.Contains("ImagePath", Assert.Single(rejected).Reason);
    }

    [Fact]
    public void AllIneligible_NothingSelected_BothWarned()
    {
        Seed(tweakNewer: a => a.Content.ImageHash = "", tweakOlder: a => a.Content.ImagePath = "../x.png");
        var rejected = new List<(string Id, string Reason)>();

        var (assignment, path) = NewSelector(rejected).SelectActive([Team]);

        Assert.Null(assignment);
        Assert.Null(path);
        Assert.Equal(new[] { "new", "old" }, rejected.Select(r => r.Id).OrderBy(x => x));
    }

    [Fact]
    public void MissingCachedFile_MakesItemIneligible()
    {
        Seed();
        File.Delete(ImageFile("newer.png"));

        var (assignment, _) = NewSelector().SelectActive([Team]);

        Assert.Equal("old", assignment?.PresentationId);
    }

    [Fact]
    public void BypassImageIntegrityCheck_SkipsOnlyTheHash_PathChecksStillApply()
    {
        Seed();
        File.WriteAllBytes(ImageFile("newer.png"), [9, 9, 9]);   // tampered, but the dev flag skips the hash

        Assert.Equal("new", NewSelector(bypass: true).SelectActive([Team]).Assignment?.PresentationId);

        Seed(tweakNewer: a => a.Content.ImagePath = "cz-its/images/../../x.png");
        Assert.Equal("old", NewSelector(bypass: true).SelectActive([Team]).Assignment?.PresentationId);
    }

    [Fact]
    public void WallpaperOnlyItem_StillNeverShownAsPoster()
    {
        Seed(tweakNewer: a => a.DisplayTypes = new DisplayTypeInfo { IsWallpaper = true });

        Assert.Equal("old", NewSelector().SelectActive([Team]).Assignment?.PresentationId);
    }
}
