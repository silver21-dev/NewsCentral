using NewsCentral.Audit;
using NewsCentral.Models;
using NewsCentral.Repositories;

namespace NewsCentral.Services;

public class AssignmentService
{
    private readonly IStorageService _storage;
    private readonly AuthenticationService _authService;
    private readonly TeamService _teamService;
    private readonly IndexGenerationService _indexGenerationService;
    private readonly IAuditLog _audit;

    public AssignmentService(
        IStorageService storage,
        AuthenticationService authService,
        TeamService teamService,
        IndexGenerationService indexGenerationService,
        IAuditLog? audit = null)
    {
        _storage                 = storage;
        _authService             = authService;
        _teamService             = teamService;
        _indexGenerationService  = indexGenerationService;
        _audit                   = audit ?? NullAuditLog.Instance;
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    public async Task<List<Assignment>> GetAssignmentsForTeamAsync(string teamFolderName)
    {
        var repo = new TeamAwareRepository<Assignment>(_storage, teamFolderName, "assignments");
        return await repo.GetAllAsync();
    }

    public async Task<List<Assignment>> GetAllAssignmentsAsync()
    {
        var allTeams       = await _teamService.GetAllTeamsAsync();
        var allAssignments = new List<Assignment>();

        foreach (var team in allTeams)
        {
            var teamAssignments = await GetAssignmentsForTeamAsync(team.FolderName);
            allAssignments.AddRange(teamAssignments);
        }

        return allAssignments;
    }

    public async Task<List<Assignment>> GetPendingApprovalsForUserAsync()
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            Console.WriteLine("DEBUG: User not authenticated");
            throw new UnauthorizedAccessException("Not authenticated");
        }

        Console.WriteLine(
            $"DEBUG: Current user: {currentUser.Username}, " +
            $"IsSystemAdmin: {currentUser.IsSystemAdmin}");

        var allAssignments = await GetAllAssignmentsAsync();
        Console.WriteLine($"DEBUG: Total assignments loaded: {allAssignments.Count}");

        var pendingAssignments = allAssignments
            .Where(a => a.Status == AssignmentStatus.PendingApproval)
            .ToList();

        Console.WriteLine($"DEBUG: Pending assignments found: {pendingAssignments.Count}");

        foreach (var a in pendingAssignments)
        {
            Console.WriteLine(
                $"DEBUG: Assignment {a.AssignmentID} — " +
                $"Status: {a.Status}, TargetTeam: {a.TargetTeam}");
        }

        if (currentUser.IsSystemAdmin)
        {
            Console.WriteLine("DEBUG: User is SystemAdmin — returning all pending");
            return pendingAssignments;
        }

        var userApproverTeams = currentUser.TeamRoles
            .Where(tr => tr.Roles.Contains("ContentApprover") ||
                         tr.Roles.Contains("TeamAdmin"))
            .Select(tr => tr.TeamFolderName)
            .ToList();

        Console.WriteLine(
            $"DEBUG: User approver teams: {string.Join(", ", userApproverTeams)}");

        return pendingAssignments
            .Where(a => userApproverTeams.Contains(a.TargetTeam))
            .ToList();
    }

    public async Task<Assignment?> GetAssignmentAsync(string teamFolderName, string assignmentId)
    {
        var repo = new TeamAwareRepository<Assignment>(_storage, teamFolderName, "assignments");
        return await repo.GetByIdAsync(assignmentId);
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    public async Task<Assignment> CreateAssignmentAsync(
        string sourceTeamFolderName,
        string presentationId,
        string presentationVersion,
        string scheduleId,
        string targetTeamFolderName,
        bool requiresApproval)
    {
        var currentUser = _authService.GetCurrentUser()
            ?? throw new UnauthorizedAccessException("Not authenticated");

        var assignment = new Assignment
        {
            AssignmentID        = Guid.NewGuid().ToString(),
            PresentationID      = presentationId,
            PresentationVersion = presentationVersion,
            ScheduleID          = scheduleId,
            SourceTeam          = sourceTeamFolderName,
            TargetTeam          = targetTeamFolderName,
            Status              = requiresApproval
                                    ? AssignmentStatus.PendingApproval
                                    : AssignmentStatus.Approved,
            RequiresApproval    = requiresApproval,
            CreatedBy           = currentUser.UserID,
            DateCreated         = DateTime.UtcNow
        };

        var repo = new TeamAwareRepository<Assignment>(_storage, sourceTeamFolderName, "assignments");
        return await repo.CreateAsync(assignment);
    }

    public async Task<Assignment> ApproveAssignmentAsync(
        string sourceTeamFolderName,
        string assignmentId,
        string? approvalNotes = null)
    {
        var currentUser = _authService.GetCurrentUser()
            ?? throw new UnauthorizedAccessException("Not authenticated");

        var repo       = new TeamAwareRepository<Assignment>(_storage, sourceTeamFolderName, "assignments");
        var assignment = await repo.GetByIdAsync(assignmentId)
            ?? throw new InvalidOperationException($"Assignment {assignmentId} not found");

        if (assignment.Status != AssignmentStatus.PendingApproval)
            throw new InvalidOperationException(
                $"Assignment is not pending approval (status: {assignment.Status})");

        if (!currentUser.IsSystemAdmin)
        {
            var hasApproverRole = currentUser.TeamRoles.Any(tr =>
                tr.TeamFolderName == assignment.TargetTeam &&
                tr.Roles.Contains("ContentApprover"));

            if (!hasApproverRole)
                throw new UnauthorizedAccessException(
                    "You don't have permission to approve for this team");
        }

        assignment.Status        = AssignmentStatus.Approved;
        assignment.ApprovedBy    = currentUser.UserID;
        assignment.ApprovedDate  = DateTime.UtcNow;
        assignment.ApprovalNotes = approvalNotes;

        return await repo.UpdateAsync(assignment);
    }

    public async Task<Assignment> RejectAssignmentAsync(
        string sourceTeamFolderName,
        string assignmentId,
        string rejectionReason)
    {
        var currentUser = _authService.GetCurrentUser()
            ?? throw new UnauthorizedAccessException("Not authenticated");

        var repo       = new TeamAwareRepository<Assignment>(_storage, sourceTeamFolderName, "assignments");
        var assignment = await repo.GetByIdAsync(assignmentId)
            ?? throw new InvalidOperationException($"Assignment {assignmentId} not found");

        if (assignment.Status != AssignmentStatus.PendingApproval)
            throw new InvalidOperationException(
                $"Assignment is not pending approval (status: {assignment.Status})");

        if (!currentUser.IsSystemAdmin)
        {
            var hasApproverRole = currentUser.TeamRoles.Any(tr =>
                tr.TeamFolderName == assignment.TargetTeam &&
                tr.Roles.Contains("ContentApprover"));

            if (!hasApproverRole)
                throw new UnauthorizedAccessException(
                    "You don't have permission to reject for this team");
        }

        assignment.Status           = AssignmentStatus.Rejected;
        assignment.RejectedBy       = currentUser.UserID;
        assignment.RejectedDate     = DateTime.UtcNow;
        assignment.RejectionReason  = rejectionReason;

        return await repo.UpdateAsync(assignment);
    }

    public async Task<Assignment> CancelAssignmentAsync(
        string sourceTeamFolderName,
        string assignmentId,
        string? cancellationReason = null)
    {
        var currentUser = _authService.GetCurrentUser()
            ?? throw new UnauthorizedAccessException("Not authenticated");

        var repo       = new TeamAwareRepository<Assignment>(_storage, sourceTeamFolderName, "assignments");
        var assignment = await repo.GetByIdAsync(assignmentId)
            ?? throw new InvalidOperationException($"Assignment {assignmentId} not found");

        if (assignment.Status != AssignmentStatus.Draft &&
            assignment.Status != AssignmentStatus.PendingApproval)
            throw new InvalidOperationException(
                $"Cannot cancel assignment with status: {assignment.Status}");

        if (assignment.CreatedBy != currentUser.UserID && !currentUser.IsSystemAdmin)
            throw new UnauthorizedAccessException("Only the creator can cancel this assignment");

        assignment.Status              = AssignmentStatus.Cancelled;
        assignment.CancelledBy         = currentUser.UserID;
        assignment.CancelledDate       = DateTime.UtcNow;
        assignment.CancellationReason  = cancellationReason;

        return await repo.UpdateAsync(assignment);
    }

    /// <summary>
    /// Soft-deletes an assignment by moving it to the team's flat deleted/ folder.
    /// If the assignment was published, regenerates the target team's index
    /// (which also pushes the updated index.json to blob distribution).
    /// </summary>
    public Task DeleteAssignmentAsync(string sourceTeamFolderName, string assignmentId) =>
        _audit.RunAsync(AuditEvents.AssignmentDeleted, assignmentId,
            () => DeleteAssignmentCoreAsync(sourceTeamFolderName, assignmentId), detail: $"team={sourceTeamFolderName}");

    private async Task DeleteAssignmentCoreAsync(string sourceTeamFolderName, string assignmentId)
    {
        var currentUser = _authService.GetCurrentUser()
            ?? throw new UnauthorizedAccessException("Not authenticated");

        var repo       = new TeamAwareRepository<Assignment>(_storage, sourceTeamFolderName, "assignments");
        var assignment = await repo.GetByIdAsync(assignmentId)
            ?? throw new InvalidOperationException($"Assignment {assignmentId} not found");

        if (!currentUser.IsSystemAdmin && assignment.CreatedBy != currentUser.UserID)
        {
            // ContentAuthor of the source team can also delete
            // (mirrors CanDeletePublished permission in Assignments.razor)
            var hasContentAuthorRole = currentUser.TeamRoles.Any(tr =>
                tr.TeamFolderName == sourceTeamFolderName &&
                tr.Roles.Contains("ContentAuthor"));

            if (!hasContentAuthorRole)
                throw new UnauthorizedAccessException(
                    "Only the assignment creator, a ContentAuthor of this team, " +
                    "or a SystemAdmin can delete this assignment");
        }

        System.Diagnostics.Debug.WriteLine($"=== DeleteAssignment: {assignmentId} ===");
        System.Diagnostics.Debug.WriteLine($"Status: {assignment.Status}");
        System.Diagnostics.Debug.WriteLine(
            $"Source: {assignment.SourceTeam}, Target: {assignment.TargetTeam}");

        var targetTeam    = assignment.TargetTeam;
        var wasPublished  = assignment.Status == AssignmentStatus.Published;
        var isCrossTeam   = !string.Equals(sourceTeamFolderName, targetTeam, StringComparison.OrdinalIgnoreCase);

        // Soft-delete: move to flat {team}/deleted/ folder.
        // MoveFileAsync creates the deleted/ folder if it does not exist.
        var sourcePath = $"{sourceTeamFolderName}/content/assignments/assign_{assignmentId}.json";
        var destPath   = $"{sourceTeamFolderName}/deleted/assign_{assignmentId}.json";

        await _storage.MoveFileAsync(sourcePath, destPath);

        System.Diagnostics.Debug.WriteLine(
            $"✓ Moved assign_{assignmentId} to deleted folder by {currentUser.Username}");

        // PUB-1: a published cross-team assignment also has a copy under the TARGET team's own
        // content/assignments/ (written by PublishingService at publish time) — delete that too,
        // or the target team's index/page would keep showing content whose source was removed.
        // Not an error if the copy is already gone (never existed, or a previous delete already
        // moved it) — only the images/presentation/schedule it may share with other assignments
        // are deliberately left alone, since those can be referenced by more than one assignment.
        if (wasPublished && isCrossTeam)
        {
            var targetCopyPath = $"{targetTeam}/content/assignments/assign_{assignmentId}.json";
            var targetDeletedPath = $"{targetTeam}/deleted/assign_{assignmentId}.json";

            if (await _storage.FileExistsAsync(targetCopyPath))
            {
                try
                {
                    await _storage.MoveFileAsync(targetCopyPath, targetDeletedPath);
                    System.Diagnostics.Debug.WriteLine(
                        $"✓ Moved target team's copy ({targetCopyPath}) to deleted folder");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"  ⚠ WARNING: Could not move target team's copy to deleted: {ex.Message}");
                    // Non-fatal — the source record is already soft-deleted; the orphaned copy is
                    // also within reach of the Index Management repair tool.
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    $"  Target team's copy was already absent ({targetCopyPath}) — nothing to move");
            }
        }

        // Regenerate index for the target team so agents/viewers see the removal.
        // IndexGenerationService.GenerateAndSaveIndexAsync pushes to blob automatically.
        if (wasPublished && !string.IsNullOrEmpty(targetTeam))
        {
            try
            {
                System.Diagnostics.Debug.WriteLine(
                    $"→ Regenerating index for target team: {targetTeam}");
                await _indexGenerationService.GenerateAndSaveIndexAsync(targetTeam);
                System.Diagnostics.Debug.WriteLine($"  ✓ Index regenerated for {targetTeam}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"  ⚠ WARNING: Index regeneration failed: {ex.Message}");
                // Non-fatal — the assignment is already soft-deleted;
                // index can be manually regenerated from the admin UI.
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine(
                "  Assignment was not published — no index regeneration needed");
        }

        System.Diagnostics.Debug.WriteLine("=== DeleteAssignment Complete ===");
    }
}
