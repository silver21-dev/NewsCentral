using NewsCentral.Models;
using NewsCentral.Models.IndexFile;
using NewsCentral.Publishing;
using NewsCentral.Repositories;
using NewsCentral.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NewsCentral.Configuration;

namespace NewsCentral.Services;

public class IndexGenerationService
{
    private readonly IStorageService _storage;
    private readonly IBlobDistributionService _blobDistribution;
    private readonly IServiceProvider _serviceProvider;
    private readonly AuthenticationService _authService;
    private readonly EcdsaSignatureService _ecdsa;
    private readonly TeamKeyService _keys;
    private readonly BackgroundIndexRegeneration _background;

    private const string IndexFileName = "index.json";

    public IndexGenerationService(
        IStorageService storage,
        IBlobDistributionService blobDistribution,
        IServiceProvider serviceProvider,
        AuthenticationService authService,
        EcdsaSignatureService ecdsa,
        TeamKeyService keys,
        BackgroundIndexRegeneration background)
    {
        _background       = background;
        _storage          = storage;
        _blobDistribution = blobDistribution;
        _serviceProvider  = serviceProvider;
        _authService      = authService;
        _ecdsa            = ecdsa;
        _keys             = keys;
    }

    /// <summary>
    /// Regenerates a team's index after another operation (a delete or an edit) and never throws. An unreadable
    /// signing key is reported to the user and the audit log; the existing index stays. True when it was regenerated.
    /// </summary>
    public Task<bool> RegenerateInBackgroundAsync(string teamFolderName) =>
        _background.RunAsync(teamFolderName, () => GenerateAndSaveIndexAsync(teamFolderName));

    // ── Index path ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the relative path for a team's index file.
    /// e.g. "team-alpha/index.json"
    /// NOTE: Previously returned a full OS path; now returns a relative path
    /// consistent with IStorageService conventions.
    /// </summary>
    public string GetIndexFilePath(string teamFolderName) =>
        $"{teamFolderName}/{IndexFileName}";

    // ── Generation ───────────────────────────────────────────────────────────

    public async Task<TeamIndexFile> GenerateIndexForTeamAsync(string teamFolderName)
    {
        System.Diagnostics.Debug.WriteLine($"=== GenerateIndexForTeam: {teamFolderName} ===");

        var teamService       = _serviceProvider.GetRequiredService<TeamService>();
        var assignmentService = _serviceProvider.GetRequiredService<AssignmentService>();

        var team = await teamService.GetTeamByFolderNameAsync(teamFolderName);
        if (team == null)
            throw new InvalidOperationException($"Team not found: {teamFolderName}");

        var index = new TeamIndexFile
        {
            TeamFolderName       = teamFolderName,
            TeamName             = team.Name,
            GeneratedAt          = DateTime.UtcNow,
            Version              = "1.0.0",
            PublishedAssignments = new List<PublishedAssignmentIndex>()
        };

        var allAssignments = await assignmentService.GetAssignmentsForTeamAsync(teamFolderName);

        // PUB-2: a team's own content/assignments folder also holds its SOURCE records for
        // assignments it published to OTHER teams (TargetTeam != this team) — those describe
        // content authored FOR someone else, not content for this team's own devices, and must
        // never appear in this team's own index.
        var publishedAssignments = allAssignments
            .Where(a => IndexInclusion.Includes(a, teamFolderName))
            .OrderByDescending(a => a.PublishedDate ?? a.DateCreated)
            .ToList();

        System.Diagnostics.Debug.WriteLine(
            $"Found {publishedAssignments.Count} published assignments");

        foreach (var assignment in publishedAssignments)
        {
            try
            {
                var entry = await BuildIndexEntryAsync(teamFolderName, assignment);
                if (entry != null)
                    index.PublishedAssignments.Add(entry);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Error building index entry for {assignment.AssignmentID}: {ex.Message}");
            }
        }

        index.Statistics = CalculateStatistics(index.PublishedAssignments);
        index.IndexHash  = CalculateIndexHash(index);

        // Normalize through the same options used to persist the file so that
        // DateTimes are truncated to second precision before signing.  Without
        // this, EcdsaSignatureService canonicalizes sub-second timestamps that
        // SmartDateTimeConverter then drops on write, causing every verifier to
        // return Invalid.  The normalized instance is also the one returned to
        // the caller and written to disk, keeping sign payload and file in sync.
        var indexJsonOptions = JsonConfiguration.GetIndexJsonOptions();
        index = JsonSerializer.Deserialize<TeamIndexFile>(
            JsonSerializer.Serialize(index, indexJsonOptions), indexJsonOptions)!;

        // NotConfigured keeps today's behaviour (unsigned). Unreadable never does: a key that exists but cannot
        // be read must not silently turn into an unsigned index, so generation fails before anything is written.
        var decision = SigningKeyGate.Decide(await LoadTeamSigningKeysAsync(teamFolderName));
        if (decision.Action == SigningAction.Block)
            throw new SigningKeyUnreadableException(teamFolderName, decision.BlockReason!);

        var signingKeys = decision.Keys;
        if (decision.Action == SigningAction.Sign && signingKeys != null)
        {
            index.Signature = _ecdsa.Sign(index, signingKeys.PrivateKey!);
            System.Diagnostics.Debug.WriteLine($"✓ index.json signed (ECDSA) for {teamFolderName}");

            // Embed the team's CURRENT public key so it travels with the content
            // (key-with-content model). Emitted for ALL teams: a static-consuming machine
            // ignores it via SignatureGate precedence (registry key wins), while a
            // dynamic-consuming machine has no registry key and verifies against this one.
            // SigningPublicKey is excluded from the canonical signed payload, so setting it
            // after signing does not affect the signature; and because the key travels with
            // the content and matches the signature, dynamic-team readers need no dual-key
            // or rotation handling.
            index.SigningPublicKey = signingKeys.PublicKey;

            // Simulate client deserialization before any distribution to catch
            // canonical/persisted-form drift (e.g., SmartDateTimeConverter changes).
            if (!string.IsNullOrEmpty(index.Signature))
            {
                var written = JsonSerializer.Serialize(index, JsonConfiguration.GetIndexJsonOptions());
                var clientReadOpts = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter() }
                };
                var reloaded = JsonSerializer.Deserialize<TeamIndexFile>(written, clientReadOpts)!;
                if (_ecdsa.Verify(reloaded, signingKeys.PublicKey) != VerifyResult.Valid)
                    throw new InvalidOperationException(
                        $"Self-verification failed for {teamFolderName}: the published index would be " +
                        $"rejected by clients (canonical/persisted-form mismatch). Publish aborted.");
            }
        }
        else
        {
            index.Signature = null;
            System.Diagnostics.Debug.WriteLine(
                $"⚠ WARNING: No signing key for {teamFolderName}; index.json published unsigned");
        }

        System.Diagnostics.Debug.WriteLine(
            $"Index generated with {index.PublishedAssignments.Count} entries");

        return index;
    }

    private async Task<PublishedAssignmentIndex?> BuildIndexEntryAsync(
        string teamFolderName,
        Assignment assignment)
    {
        var presentationService = _serviceProvider.GetRequiredService<PresentationService>();
        var scheduleService     = _serviceProvider.GetRequiredService<ScheduleService>();
        var teamService         = _serviceProvider.GetRequiredService<TeamService>();

        var presentation = await presentationService.GetPresentationAsync(
            assignment.SourceTeam, assignment.PresentationID);

        if (presentation == null)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Presentation not found: {assignment.PresentationID}");
            return null;
        }

        var schedule = await scheduleService.GetScheduleAsync(
            assignment.SourceTeam, assignment.ScheduleID);

        if (schedule == null)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Schedule not found: {assignment.ScheduleID}");
            return null;
        }

        var sourceTeam   = await teamService.GetTeamByFolderNameAsync(assignment.SourceTeam);
        var contentInfo  = await BuildContentInfoAsync(presentation);

        return new PublishedAssignmentIndex
        {
            AssignmentId  = assignment.AssignmentID,
            PresentationId = presentation.PresentationID,
            ScheduleId    = schedule.ScheduleID,

            PresentationName         = presentation.Name,
            PresentationDescription  = presentation.Description,
            PresentationVersion      = presentation.Version,
            PresentationLastModified = presentation.LastModified,

            ScheduleStart = DateTime.SpecifyKind(schedule.ScheduleStart, DateTimeKind.Unspecified),
            ScheduleEnd   = DateTime.SpecifyKind(schedule.ScheduleEnd,   DateTimeKind.Unspecified),
            DaysOfWeek    = schedule.DaysOfWeek,

            PublishedDate = assignment.PublishedDate ?? assignment.DateCreated,
            PublishedBy   = assignment.PublishedBy   ?? assignment.CreatedBy,

            DisplayTypes = new DisplayTypeInfo
            {
                IsNewsOfWeek  = presentation.IsNewsOfWeek,
                IsWallpaper   = presentation.IsWallpaper,
                IsLogonScreen = presentation.IsLogonScreen
            },

            Content = contentInfo,

            SourceTeamFolderName   = assignment.SourceTeam,
            SourceTeamName         = sourceTeam?.Name ?? assignment.SourceTeam,

            PosterText             = presentation.PosterText,
            DisplayDurationSeconds = presentation.DisplayDurationSeconds,
            Priority               = assignment.Priority,
            UseVirtualDesktop      = presentation.UseVirtualDesktop,
            VirtualDesktopBackgroundColor = presentation.VirtualDesktopBackgroundColor
        };
    }

    private async Task<ContentInfo> BuildContentInfoAsync(Presentation presentation)
    {
        // imagePath is already a relative path stored in the Presentation record,
        // e.g. "team-alpha/images/generated/poster_abc_v1.jpg"
        var imagePath = !string.IsNullOrEmpty(presentation.GeneratedImagePath)
            ? presentation.GeneratedImagePath
            : presentation.OriginalImagePath;

        var contentInfo = new ContentInfo
        {
            ImagePath         = imagePath,
            MoreInfoUrl       = presentation.MoreUrl,
            ImageLastModified = presentation.LastModified
        };

        if (!string.IsNullOrEmpty(imagePath))
        {
            if (await _storage.FileExistsAsync(imagePath))
            {
                try
                {
                    contentInfo.ImageSizeBytes = await _storage.GetFileSizeAsync(imagePath);

                    var imageHash = await CalculateFileHashAsync(imagePath);
                    contentInfo.ImageHash = $"sha256:{imageHash}";

                    System.Diagnostics.Debug.WriteLine(
                        $"Image: {Path.GetFileName(imagePath)} — " +
                        $"Size: {contentInfo.ImageSizeBytes} bytes, " +
                        $"Hash: {imageHash[..16]}...");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Error processing image {imagePath}: {ex.Message}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Warning: image not found in authoring tier: {imagePath}");
            }
        }

        return contentInfo;
    }

    private IndexStatistics CalculateStatistics(List<PublishedAssignmentIndex> assignments)
    {
        var now = DateTime.Now;

        var stats = new IndexStatistics
        {
            TotalPublishedAssignments = assignments.Count,
            ActiveAssignments         = 0,
            UpcomingAssignments       = 0,
            ExpiredAssignments        = 0
        };

        foreach (var a in assignments)
        {
            if      (now >= a.ScheduleStart && now <= a.ScheduleEnd) stats.ActiveAssignments++;
            else if (now <  a.ScheduleStart)                         stats.UpcomingAssignments++;
            else if (now >  a.ScheduleEnd)                           stats.ExpiredAssignments++;
        }

        return stats;
    }

    // ── Save / Delete ────────────────────────────────────────────────────────

    /// <summary>
    /// Writes index.json to the authoring tier AND pushes it to blob distribution
    /// in a single operation. This is the only place index.json is written —
    /// both tiers are always kept in sync.
    /// </summary>
    public async Task<IndexSaveResult> SaveIndexFileAsync(string teamFolderName, TeamIndexFile index)
    {
        var relativePath = GetIndexFilePath(teamFolderName);
        var jsonOptions  = JsonConfiguration.GetIndexJsonOptions();
        var jsonContent  = JsonSerializer.Serialize(index, jsonOptions);

        // ── Authoring tier (Azure Files / local disk) ────────────────────────
        await _storage.WriteTextAsync(relativePath, jsonContent);

        // ── Distribution tier (Azure Blob / local dist folder / null) ────────
        // Blob push is non-critical: a failed push leaves authoring correct.
        // The index can be manually re-pushed via RegenerateAllIndexesAsync. M5a: the failure is
        // now captured in the returned result instead of only logged, so callers can surface it.
        var result = new IndexSaveResult { DistributionSucceeded = true };
        try
        {
            await _blobDistribution.UploadTextAsync(relativePath, jsonContent);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"⚠ WARNING: index.json blob push failed for {teamFolderName}: {ex.Message}");
            // Do not rethrow — authoring tier write succeeded; blob is recoverable.
            result.DistributionSucceeded = false;
            result.DistributionError = ex.Message;
        }

        System.Diagnostics.Debug.WriteLine($"✓ Index saved: {relativePath}");
        System.Diagnostics.Debug.WriteLine($"  Size:    {jsonContent.Length} bytes");
        System.Diagnostics.Debug.WriteLine($"  Entries: {index.PublishedAssignments.Count}");

        return result;
    }

    public async Task<IndexSaveResult> GenerateAndSaveIndexAsync(string teamFolderName)
    {
        System.Diagnostics.Debug.WriteLine($"=== GenerateAndSaveIndex: {teamFolderName} ===");
        var index = await GenerateIndexForTeamAsync(teamFolderName);
        var result = await SaveIndexFileAsync(teamFolderName, index);
        _background.IndexGenerated(teamFolderName);   // the key was readable: withdraw any "key unreadable" banner
        System.Diagnostics.Debug.WriteLine($"✓ Index generation complete for {teamFolderName}");
        return result;
    }

    /// <summary>
    /// Deletes index.json from both the authoring tier and the blob distribution tier.
    /// </summary>
    public async Task DeleteIndexFileAsync(string teamFolderName)
    {
        var relativePath = GetIndexFilePath(teamFolderName);

        // Authoring tier — no-op if file does not exist
        await _storage.DeleteFileAsync(relativePath);

        // Distribution tier — remove from blob so agents stop seeing stale data
        try
        {
            await _blobDistribution.DeleteAsync(relativePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"⚠ WARNING: index.json blob delete failed for {teamFolderName}: {ex.Message}");
        }

        System.Diagnostics.Debug.WriteLine($"✓ Index deleted: {relativePath}");
    }

    // ── Query helpers ────────────────────────────────────────────────────────

    public async Task<bool> IndexFileExistsAsync(string teamFolderName) =>
        await _storage.FileExistsAsync(GetIndexFilePath(teamFolderName));

    /// <summary>
    /// Synchronous wrapper kept for call sites that cannot be made async easily.
    /// Prefer IndexFileExistsAsync where possible.
    /// </summary>
    public bool IndexFileExists(string teamFolderName) =>
        _storage.FileExistsAsync(GetIndexFilePath(teamFolderName)).GetAwaiter().GetResult();

    public async Task<string?> GetCurrentIndexHashAsync(string teamFolderName)
    {
        var relativePath = GetIndexFilePath(teamFolderName);
        var jsonContent  = await _storage.ReadTextAsync(relativePath);

        if (jsonContent == null) return null;

        try
        {
            var jsonOptions = JsonConfiguration.GetIndexJsonOptions();
            var index = JsonSerializer.Deserialize<TeamIndexFile>(jsonContent, jsonOptions);
            return index?.IndexHash;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error reading index hash: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads and deserializes the team's index file from the authoring tier.
    /// Returns null if the file does not exist or cannot be parsed.
    /// Use this instead of GetIndexFilePath + File.ReadAllTextAsync in UI code.
    /// </summary>
    public async Task<TeamIndexFile?> ReadIndexAsync(string teamFolderName)
    {
        var jsonContent = await _storage.ReadTextAsync(GetIndexFilePath(teamFolderName));
        if (jsonContent == null) return null;

        try
        {
            return JsonSerializer.Deserialize<TeamIndexFile>(
                jsonContent,
                JsonConfiguration.GetIndexJsonOptions());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Error deserializing index for {teamFolderName}: {ex.Message}");
            return null;
        }
    }

    public async Task<RegenerateAllResult> RegenerateAllIndexesAsync()
    {
        System.Diagnostics.Debug.WriteLine("=== RegenerateAllIndexes ===");

        var teamService  = _serviceProvider.GetRequiredService<TeamService>();
        var allTeams     = await teamService.GetAllTeamsAsync();
        var result       = new RegenerateAllResult();

        foreach (var team in allTeams)
        {
            try
            {
                var saveResult = await GenerateAndSaveIndexAsync(team.FolderName);
                result.Regenerated++;

                if (!saveResult.DistributionSucceeded)
                    result.DistributionFailures.Add($"{team.FolderName}: {saveResult.DistributionError}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Error generating index for {team.FolderName}: {ex.Message}");
                result.Failures.Add($"{team.FolderName}: {ex.Message}");
            }
        }

        System.Diagnostics.Debug.WriteLine(
            $"✓ Regenerated {result.Regenerated} of {allTeams.Count} team indexes " +
            $"({result.DistributionFailures.Count} distribution failures)");

        return result;
    }

    // ── Signing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the team's signing keys from the secure store (SecurePath), migrating them from the old DataPath
    /// location on first load. The result distinguishes NotConfigured (no file) from Unreadable (access denied,
    /// IO or parse error, with a reason) - callers must never treat the second as the first.
    /// The keys are intentionally never passed to IBlobDistributionService.
    /// </summary>
    public Task<TeamKeyLoadResult> LoadTeamSigningKeysAsync(string teamFolderName) =>
        _keys.LoadAsync(teamFolderName);

    /// <summary>
    /// Writes the team's keys to the secure store only. Must never be called with IBlobDistributionService.
    /// Refuses to replace a key that exists but cannot be read.
    /// </summary>
    public Task SaveTeamSigningKeysAsync(string teamFolderName, TeamSigningKeys keys) =>
        _keys.SaveAsync(teamFolderName, keys);

    /// <summary>
    /// Pre-flight for publish flows: throws <see cref="SigningKeyUnreadableException"/> when the team's key exists but
    /// cannot be read, so the flow can stop before it changes anything.
    /// </summary>
    public async Task EnsureSigningKeyUsableAsync(string teamFolderName)
    {
        var load = await LoadTeamSigningKeysAsync(teamFolderName);
        if (load.Outcome == KeyLoadOutcome.Unreadable)
            throw new SigningKeyUnreadableException(teamFolderName, load.Reason ?? "unknown reason");
    }

    // ── Hashing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Streams a file through SHA-256 without loading it fully into memory.
    /// relativePath is passed directly to IStorageService.OpenReadAsync —
    /// the stream need not be seekable.
    /// </summary>
    private async Task<string> CalculateFileHashAsync(string relativePath)
    {
        using var sha256 = SHA256.Create();

        var stream = await _storage.OpenReadAsync(relativePath);
        if (stream == null)
            throw new FileNotFoundException($"File not found for hashing: {relativePath}");

        using (stream)
        {
            var hashBytes = await sha256.ComputeHashAsync(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
    }

    private string CalculateIndexHash(TeamIndexFile index)
    {
        var indexForHashing = new
        {
            index.TeamFolderName,
            index.TeamName,
            index.GeneratedAt,
            index.Version,
            index.PublishedAssignments,
            index.Statistics
        };

        var jsonOptions = JsonConfiguration.GetIndexJsonOptions();
        var jsonContent = JsonSerializer.Serialize(indexForHashing, jsonOptions);

        using var sha256   = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(jsonContent));
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }
}

/// <summary>Result of <see cref="IndexGenerationService.SaveIndexFileAsync"/> / GenerateAndSaveIndexAsync (M5a).</summary>
public sealed class IndexSaveResult
{
    /// <summary>True when the authoring-tier write succeeded AND the blob push succeeded (or
    /// distribution is disabled/null — see <see cref="NullBlobDistributionService"/>). The
    /// authoring-tier write itself still throws out of these methods on failure, unchanged from
    /// before M5a; this flag covers only the non-critical distribution push.</summary>
    public bool DistributionSucceeded { get; set; }

    /// <summary>Set only when <see cref="DistributionSucceeded"/> is false.</summary>
    public string? DistributionError { get; set; }
}

/// <summary>Result of <see cref="IndexGenerationService.RegenerateAllIndexesAsync"/> (M5a).</summary>
public sealed class RegenerateAllResult
{
    /// <summary>Number of teams whose index was successfully generated and written (authoring
    /// tier) — independent of whether its distribution push succeeded.</summary>
    public int Regenerated { get; set; }

    /// <summary>One "{teamFolderName}: {error}" entry per team whose distribution push failed.</summary>
    public List<string> DistributionFailures { get; set; } = new();

    /// <summary>One "{teamFolderName}: {error}" entry per team whose index could not be generated at all
    /// (for example an unreadable signing key). Nothing was written for these teams.</summary>
    public List<string> Failures { get; set; } = new();
}
