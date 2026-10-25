using NewsCentral.Audit;
using NewsCentral.Models;
using NewsCentral.Publishing;
using NewsCentral.Repositories;

namespace NewsCentral.Services;

public class PresentationService
{
    private readonly IStorageService _storage;
    private readonly IBlobDistributionService _blobDistribution;
    private readonly AuthenticationService _authService;
    private readonly PosterGenerationService _posterService;
    private readonly IndexGenerationService _indexGenerationService;
    private readonly IAuditLog _audit;

    public PresentationService(
        IStorageService storage,
        IBlobDistributionService blobDistribution,
        AuthenticationService authService,
        PosterGenerationService posterService,
        IndexGenerationService indexGenerationService,
        IAuditLog? audit = null)
    {
        _storage                = storage;
        _blobDistribution       = blobDistribution;
        _authService            = authService;
        _posterService          = posterService;
        _indexGenerationService = indexGenerationService;
        _audit                  = audit ?? NullAuditLog.Instance;
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    public async Task<List<Presentation>> GetPresentationsForTeamAsync(string teamFolderName)
    {
        var repo = new TeamAwareRepository<Presentation>(_storage, teamFolderName, "presentations");
        return await repo.GetAllAsync();
    }

    public async Task<Presentation?> GetPresentationAsync(string teamFolderName, string presentationId)
    {
        var repo = new TeamAwareRepository<Presentation>(_storage, teamFolderName, "presentations");
        return await repo.GetByIdAsync(presentationId);
    }

    public async Task<byte[]> GetImageDataAsync(string teamFolderName, string imagePath)
    {
        var data = await _storage.ReadBytesAsync(imagePath);
        if (data == null)
            throw new FileNotFoundException($"Image not found: {imagePath}");
        return data;
    }

    // ── Create ───────────────────────────────────────────────────────────────

    public Task<Presentation> CreatePresentationAsync(
        string teamId,
        string teamFolderName,
        string name,
        string description,
        string moreUrl,
        byte[] imageData,
        string originalImageName) =>
        _audit.RunAsync(AuditEvents.PresentationCreated, null,
            () => CreatePresentationCoreAsync(teamId, teamFolderName, name, description, moreUrl, imageData, originalImageName),
            targetFrom: p => p.PresentationID, detail: $"team={teamFolderName}");

    private async Task<Presentation> CreatePresentationCoreAsync(
        string teamId,
        string teamFolderName,
        string name,
        string description,
        string moreUrl,
        byte[] imageData,
        string originalImageName)
    {
        var currentUser = _authService.GetCurrentUser()
            ?? throw new UnauthorizedAccessException("Not authenticated");

        if (!_authService.HasRole(teamId, "ContentAuthor") && !currentUser.IsSystemAdmin)
            throw new UnauthorizedAccessException(
                "You don't have permission to create content for this team");

        // ── Original image → original/ ────────────────────────────────────────
        var imageId        = Guid.NewGuid().ToString();
        var imageExtension = Path.GetExtension(originalImageName);
        var savedImageName = $"img_{imageId}{imageExtension}";
        var imageRelPath   = $"{teamFolderName}/images/original/{savedImageName}";

        await _storage.WriteBytesAsync(imageRelPath, imageData);

        // ── Copy to generated/ so GeneratedImagePath is always populated ──────
        // Satellite components can read from generated/ regardless of whether
        // posterization was run. The original/ copy is preserved as the untouched
        // source for future poster regeneration.
        var presentationId    = Guid.NewGuid().ToString();
        var generatedFileName = $"poster_{presentationId}_v1.jpg";
        var generatedRelPath  = $"{teamFolderName}/images/generated/{generatedFileName}";

        await _storage.WriteBytesAsync(generatedRelPath, imageData);

        var presentation = new Presentation
        {
            PresentationID     = presentationId,
            Version            = 1,
            TeamID             = teamId,
            TeamFolderName     = teamFolderName,
            Name               = name,
            Description        = description,
            MoreUrl            = moreUrl,
            DateCreated        = DateTime.UtcNow,
            OriginalImagePath  = imageRelPath,
            GeneratedImagePath = generatedRelPath,   // always set at creation
            ImageOriginalName  = originalImageName,
            ImageName          = savedImageName,
            CreatedBy          = currentUser.UserID,
            LastModified       = DateTime.UtcNow,
            ModifiedBy         = currentUser.UserID,
            ContentImageBase64 = Convert.ToBase64String(imageData)
        };

        var repo = new TeamAwareRepository<Presentation>(_storage, teamFolderName, "presentations");
        return await repo.CreateAsync(presentation);
    }

    // ── Duplicate ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a NEW presentation in <paramref name="targetTeamFolder"/> seeded from the source with
    /// ONLY its name, MoreUrl, and ORIGINAL picture — no description, poster, or assignment/schedule.
    /// The copy is "draft" by construction (a presentation with no assignments). The source's ORIGINAL
    /// image bytes are copied (falling back to the embedded content image only if the original is
    /// missing/unreadable); reusing <see cref="CreatePresentationAsync"/> performs the cross-team image
    /// copy into the target team's storage and enforces ContentAuthor/SystemAdmin on the target team.
    /// </summary>
    public Task<Presentation> DuplicatePresentationAsync(
        string sourceTeamFolder,
        string presentationId,
        string targetTeamId,
        string targetTeamFolder) =>
        _audit.RunAsync(AuditEvents.PresentationDuplicated, presentationId,
            () => DuplicatePresentationCoreAsync(sourceTeamFolder, presentationId, targetTeamId, targetTeamFolder),
            detail: $"from={sourceTeamFolder}; to={targetTeamFolder}",
            detailFrom: p => $"from={sourceTeamFolder}; to={targetTeamFolder}; copy={p.PresentationID}");

    private async Task<Presentation> DuplicatePresentationCoreAsync(
        string sourceTeamFolder,
        string presentationId,
        string targetTeamId,
        string targetTeamFolder)
    {
        var sourceRepo = new TeamAwareRepository<Presentation>(_storage, sourceTeamFolder, "presentations");
        var source     = await sourceRepo.GetByIdAsync(presentationId)
            ?? throw new InvalidOperationException($"Presentation {presentationId} not found");

        // Copy the source's ORIGINAL image (pre-posterization), not the poster/ContentImageBase64.
        byte[]? imageData = null;
        if (!string.IsNullOrEmpty(source.OriginalImagePath))
            imageData = await _storage.ReadBytesAsync(source.OriginalImagePath);

        // Fallback only when the original file is missing/unreadable.
        if ((imageData is null || imageData.Length == 0) && !string.IsNullOrEmpty(source.ContentImageBase64))
            imageData = Convert.FromBase64String(source.ContentImageBase64);

        if (imageData is null || imageData.Length == 0)
            throw new InvalidOperationException(
                $"Cannot duplicate presentation {presentationId}: source image is unavailable");

        var originalImageName = string.IsNullOrEmpty(source.ImageOriginalName)
            ? "image.jpg"
            : source.ImageOriginalName;

        // Seed ONLY name + moreUrl + original picture; description omitted; no poster/assignment.
        return await CreatePresentationCoreAsync(
            targetTeamId,
            targetTeamFolder,
            name: source.Name,
            description: string.Empty,
            moreUrl: source.MoreUrl,
            imageData: imageData,
            originalImageName: originalImageName);
    }

    // ── Update ───────────────────────────────────────────────────────────────

    public Task<Presentation> UpdatePresentationAsync(
        string teamFolderName,
        string presentationId,
        string name,
        string description,
        string moreUrl) =>
        _audit.RunAsync(AuditEvents.PresentationUpdated, presentationId,
            () => UpdatePresentationCoreAsync(teamFolderName, presentationId, name, description, moreUrl),
            detail: $"team={teamFolderName}");

    private async Task<Presentation> UpdatePresentationCoreAsync(
        string teamFolderName,
        string presentationId,
        string name,
        string description,
        string moreUrl)
    {
        var currentUser = _authService.GetCurrentUser()
            ?? throw new UnauthorizedAccessException("Not authenticated");

        var repo         = new TeamAwareRepository<Presentation>(_storage, teamFolderName, "presentations");
        var presentation = await repo.GetByIdAsync(presentationId)
            ?? throw new InvalidOperationException($"Presentation {presentationId} not found");

        if (presentation.CreatedBy != currentUser.UserID && !currentUser.IsSystemAdmin)
            throw new UnauthorizedAccessException("You can only edit your own presentations");

        presentation.Name         = name;
        presentation.Description  = description;
        presentation.MoreUrl      = moreUrl;
        presentation.LastModified = DateTime.UtcNow;
        presentation.ModifiedBy   = currentUser.UserID;

        var result = await repo.UpdateAsync(presentation);
        await RegenerateIndexesForPresentationAsync(teamFolderName, presentationId);
        return result;
    }

    public Task<Presentation> UpdatePresentationWithPosterAsync(
        string teamFolderName,
        string presentationId,
        bool isNewsOfWeek,
        bool isWallpaper,
        bool isLogonScreen) =>
        _audit.RunAsync(AuditEvents.PresentationUpdated, presentationId,
            () => UpdatePresentationWithPosterCoreAsync(teamFolderName, presentationId, isNewsOfWeek, isWallpaper, isLogonScreen),
            detail: $"team={teamFolderName}; poster/display types");

    private async Task<Presentation> UpdatePresentationWithPosterCoreAsync(
        string teamFolderName,
        string presentationId,
        bool isNewsOfWeek,
        bool isWallpaper,
        bool isLogonScreen)
    {
        var currentUser = _authService.GetCurrentUser()
            ?? throw new UnauthorizedAccessException("Not authenticated");

        var repo         = new TeamAwareRepository<Presentation>(_storage, teamFolderName, "presentations");
        var presentation = await repo.GetByIdAsync(presentationId)
            ?? throw new InvalidOperationException($"Presentation {presentationId} not found");

        if (presentation.CreatedBy != currentUser.UserID && !currentUser.IsSystemAdmin)
            throw new UnauthorizedAccessException("You can only edit your own presentations");

        var originalImageData = await _storage.ReadBytesAsync(presentation.OriginalImagePath)
            ?? throw new FileNotFoundException(
                $"Original image not found: {presentation.OriginalImagePath}");

        var posterPath = await _posterService.GeneratePosterAsync(
            teamFolderName,
            presentationId,
            presentation.Version.ToString(),
            originalImageData);

        var posterImageData = await _posterService.GetPosterImageDataAsync(teamFolderName, posterPath);

        presentation.GeneratedImagePath = posterPath;
        presentation.ContentImageBase64 = Convert.ToBase64String(posterImageData);
        presentation.LastModified       = DateTime.UtcNow;
        presentation.ModifiedBy         = currentUser.UserID;
        presentation.IsNewsOfWeek       = isNewsOfWeek;
        presentation.IsWallpaper        = isWallpaper;
        presentation.IsLogonScreen      = isLogonScreen;

        var result = await repo.UpdateAsync(presentation);
        await RegenerateIndexesForPresentationAsync(teamFolderName, presentationId);
        return result;
    }

    /// <summary>
    /// Update all metadata fields without regenerating the poster.
    /// Promotes original to generated/ for any presentation that still has
    /// GeneratedImagePath empty (created before the always-populate fix).
    /// </summary>
    public Task<Presentation> UpdatePresentationFullAsync(
        string teamFolderName,
        string presentationId,
        string name,
        string description,
        string moreUrl,
        bool isNewsOfWeek,
        bool isWallpaper,
        bool isLogonScreen,
        bool useVirtualDesktop = false,
        string virtualDesktopBackgroundColor = "#000000") =>
        _audit.RunAsync(AuditEvents.PresentationUpdated, presentationId,
            () => UpdatePresentationFullCoreAsync(teamFolderName, presentationId, name, description, moreUrl,
                isNewsOfWeek, isWallpaper, isLogonScreen, useVirtualDesktop, virtualDesktopBackgroundColor),
            detail: $"team={teamFolderName}");

    private async Task<Presentation> UpdatePresentationFullCoreAsync(
        string teamFolderName,
        string presentationId,
        string name,
        string description,
        string moreUrl,
        bool isNewsOfWeek,
        bool isWallpaper,
        bool isLogonScreen,
        bool useVirtualDesktop,
        string virtualDesktopBackgroundColor)
    {
        var currentUser = _authService.GetCurrentUser()
            ?? throw new UnauthorizedAccessException("Not authenticated");

        var repo         = new TeamAwareRepository<Presentation>(_storage, teamFolderName, "presentations");
        var presentation = await repo.GetByIdAsync(presentationId)
            ?? throw new InvalidOperationException($"Presentation {presentationId} not found");

        if (presentation.CreatedBy != currentUser.UserID && !currentUser.IsSystemAdmin)
            throw new UnauthorizedAccessException("You can only edit your own presentations");

        presentation.Name                         = name;
        presentation.Description                  = description;
        presentation.MoreUrl                      = moreUrl;
        presentation.IsNewsOfWeek                 = isNewsOfWeek;
        presentation.IsWallpaper                  = isWallpaper;
        presentation.IsLogonScreen                = isLogonScreen;
        presentation.UseVirtualDesktop            = useVirtualDesktop;
        presentation.VirtualDesktopBackgroundColor = virtualDesktopBackgroundColor;
        presentation.LastModified                 = DateTime.UtcNow;
        presentation.ModifiedBy                   = currentUser.UserID;

        // Safety net for presentations created before the always-populate fix.
        // Condition checks GeneratedImagePath (not ContentImageBase64, which is
        // set at create time and would never trigger this block).
        if (string.IsNullOrEmpty(presentation.GeneratedImagePath) &&
            !string.IsNullOrEmpty(presentation.OriginalImagePath))
        {
            var imageData = await _storage.ReadBytesAsync(presentation.OriginalImagePath);

            if (imageData != null)
            {
                presentation.ContentImageBase64 = Convert.ToBase64String(imageData);

                var contentImageFileName = $"content_{presentation.PresentationID}.jpg";
                var contentImageRelPath  = $"{teamFolderName}/images/generated/{contentImageFileName}";

                await _storage.WriteBytesAsync(contentImageRelPath, imageData);
                presentation.GeneratedImagePath = contentImageRelPath;
            }
        }

        var result = await repo.UpdateAsync(presentation);
        await RegenerateIndexesForPresentationAsync(teamFolderName, presentationId);
        return result;
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    public Task<bool> DeletePresentationAsync(string teamFolderName, string presentationId) =>
        _audit.RunAsync(AuditEvents.PresentationDeleted, presentationId,
            () => DeletePresentationCoreAsync(teamFolderName, presentationId),
            isSuccess: ok => ok, detail: $"team={teamFolderName}", detailFrom: ok => ok ? $"team={teamFolderName}" : "not found");

    private async Task<bool> DeletePresentationCoreAsync(string teamFolderName, string presentationId)
    {
        var currentUser = _authService.GetCurrentUser()
            ?? throw new UnauthorizedAccessException("Not authenticated");

        var repo         = new TeamAwareRepository<Presentation>(_storage, teamFolderName, "presentations");
        var presentation = await repo.GetByIdAsync(presentationId)
            ?? throw new InvalidOperationException($"Presentation {presentationId} not found");

        if (!_authService.IsSystemAdmin() && presentation.CreatedBy != currentUser.UserID)
        {
            var hasContentAuthorRole = currentUser.TeamRoles.Any(tr =>
                tr.TeamFolderName == teamFolderName &&
                tr.Roles.Contains("ContentAuthor"));

            if (!hasContentAuthorRole)
                throw new UnauthorizedAccessException(
                    "Only the presentation creator, a ContentAuthor of this team, " +
                    "or a SystemAdmin can delete this presentation");
        }

        System.Diagnostics.Debug.WriteLine($"=== DeletePresentation: {presentationId} ===");

        var targetTeamsWithPublished =
            await GetTargetTeamsForPresentationAsync(teamFolderName, presentationId);

        // Related source assignments, read once before anything moves: used by step 2 and by the
        // target-copy plan below (PUB-1).
        var assignmentRepo     = new TeamAwareRepository<Assignment>(_storage, teamFolderName, "assignments");
        var allAssignments     = await assignmentRepo.GetAllAsync();
        var relatedAssignments = allAssignments
            .Where(a => a.PresentationID == presentationId)
            .ToList();

        // ── 1. Soft-delete presentation JSON ─────────────────────────────────
        await _storage.MoveFileAsync(
            $"{teamFolderName}/content/presentations/pres_{presentationId}.json",
            $"{teamFolderName}/deleted/pres_{presentationId}.json");

        System.Diagnostics.Debug.WriteLine($"✓ Moved pres_{presentationId} to deleted/");

        // ── 2. Soft-delete all related assignments ────────────────────────────
        foreach (var assignment in relatedAssignments)
        {
            var src = $"{teamFolderName}/content/assignments/assign_{assignment.AssignmentID}.json";
            var dst = $"{teamFolderName}/deleted/assign_{assignment.AssignmentID}.json";

            if (await _storage.FileExistsAsync(src))
            {
                await _storage.MoveFileAsync(src, dst);
                System.Diagnostics.Debug.WriteLine(
                    $"✓ Moved assign_{assignment.AssignmentID} to deleted/");
            }
        }

        // ── 3. Soft-delete all related schedules ──────────────────────────────
        var scheduleRepo     = new TeamAwareRepository<Schedule>(_storage, teamFolderName, "schedules");
        var allSchedules     = await scheduleRepo.GetAllAsync();
        var relatedSchedules = allSchedules
            .Where(s => s.PresentationID == presentationId)
            .ToList();

        foreach (var schedule in relatedSchedules)
        {
            var src = $"{teamFolderName}/content/schedules/sched_{schedule.ScheduleID}.json";
            var dst = $"{teamFolderName}/deleted/sched_{schedule.ScheduleID}.json";

            if (await _storage.FileExistsAsync(src))
            {
                await _storage.MoveFileAsync(src, dst);
                System.Diagnostics.Debug.WriteLine(
                    $"✓ Moved sched_{schedule.ScheduleID} to deleted/");
            }
        }

        System.Diagnostics.Debug.WriteLine(
            $"✓ Moved {relatedAssignments.Count} assignments, " +
            $"{relatedSchedules.Count} schedules to deleted/");

        // ── 3b. Soft-delete the TARGET teams' own authoring-tier copies (PUB-1) ──
        // Publish wrote presentation/schedule/assignment copies into each target team's own
        // folder; without this they stay visible there and keep feeding the target's index.
        // Failures are non-fatal — the source is already soft-deleted and the Index Management
        // repair finishes the job. Images are left alone (shared-file rule).
        var deletePlan = PresentationDeletePlanner.Plan(teamFolderName, presentationId, relatedAssignments);
        foreach (var move in deletePlan.Moves)
        {
            try
            {
                if (!await _storage.FileExistsAsync(move.From))
                {
                    System.Diagnostics.Debug.WriteLine($"  already absent: {move.From}");
                    continue;
                }

                await _storage.MoveFileAsync(move.From, move.To);
                System.Diagnostics.Debug.WriteLine($"✓ Moved {move.From} to {move.To}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"⚠ WARNING: Could not move target copy {move.From}: {ex.Message}");
            }
        }

        // ── 4. Blob distribution cleanup ──────────────────────────────────────
        var publishedAssignments = relatedAssignments
            .Where(a => a.Status == AssignmentStatus.Published)
            .ToList();

        foreach (var assignment in publishedAssignments)
        {
            try
            {
                await _blobDistribution.DeleteAsync(
                    $"{assignment.TargetTeam}/content/presentations/pres_{presentationId}.json");

                await _blobDistribution.DeleteAsync(
                    $"{assignment.TargetTeam}/content/schedules/sched_{assignment.ScheduleID}.json");

                if (!string.IsNullOrEmpty(presentation.GeneratedImagePath))
                {
                    var genFile = Path.GetFileName(presentation.GeneratedImagePath);
                    await _blobDistribution.DeleteAsync(
                        $"{assignment.TargetTeam}/images/generated/{genFile}");
                }

                if (!string.IsNullOrEmpty(presentation.OriginalImagePath))
                {
                    var origFile = Path.GetFileName(presentation.OriginalImagePath);
                    await _blobDistribution.DeleteAsync(
                        $"{assignment.TargetTeam}/images/original/{origFile}");
                }

                System.Diagnostics.Debug.WriteLine(
                    $"✓ Blob cleanup done for assignment {assignment.AssignmentID} " +
                    $"→ target {assignment.TargetTeam}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"⚠ WARNING: Blob cleanup failed for assignment " +
                    $"{assignment.AssignmentID}: {ex.Message}");
            }
        }

        // ── 5. Regenerate indexes for affected target teams (after the copy moves) ──
        foreach (var targetTeam in targetTeamsWithPublished)
        {
            if (await _indexGenerationService.RegenerateInBackgroundAsync(targetTeam))
                System.Diagnostics.Debug.WriteLine($"✓ Index regenerated for {targetTeam}");
        }

        System.Diagnostics.Debug.WriteLine($"=== DeletePresentation complete ===");
        return true;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task RegenerateIndexesForPresentationAsync(
        string sourceTeamFolderName,
        string presentationId)
    {
        try
        {
            var targetTeams = await GetTargetTeamsForPresentationAsync(
                sourceTeamFolderName, presentationId);

            if (targetTeams.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"  No published assignments for {presentationId} — skipping index regen");
                return;
            }

            System.Diagnostics.Debug.WriteLine(
                $"→ Regenerating indexes for {targetTeams.Count} target team(s)");

            foreach (var targetTeam in targetTeams)
            {
                if (await _indexGenerationService.RegenerateInBackgroundAsync(targetTeam))
                    System.Diagnostics.Debug.WriteLine(
                        $"  ✓ Index regenerated for {targetTeam}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"  ⚠ WARNING: Index regeneration check failed: {ex.Message}");
        }
    }

    private async Task<List<string>> GetTargetTeamsForPresentationAsync(
        string sourceTeamFolderName,
        string presentationId)
    {
        try
        {
            var repo = new TeamAwareRepository<Assignment>(
                _storage, sourceTeamFolderName, "assignments");

            var all = await repo.GetAllAsync();

            return all
                .Where(a => a.PresentationID == presentationId &&
                            a.Status == AssignmentStatus.Published)
                .Select(a => a.TargetTeam)
                .Distinct()
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Error getting target teams for {presentationId}: {ex.Message}");
            return new List<string>();
        }
    }
}
