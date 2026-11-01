using NewsCentral.Audit;

namespace NewsCentral.Services;

/// <summary>
/// Index regeneration that follows another operation (assignment delete, presentation update or delete). The
/// operation itself has already happened, so regeneration problems never throw; but a signing key that cannot be
/// read is not a transient hiccup, so it is shown to the user and written to the audit log. The old index stays in
/// place: the generator refuses before anything is written, and an unsigned index is never produced.
/// </summary>
public sealed class BackgroundIndexRegeneration
{
    private readonly UserNotices _notices;
    private readonly IAuditLog _audit;

    public BackgroundIndexRegeneration(UserNotices notices, IAuditLog? audit = null)
    {
        _notices = notices;
        _audit = audit ?? NullAuditLog.Instance;
    }

    private const string NoticeKey = "index-key-unreadable";

    /// <summary>
    /// An index was generated and saved (by a publish or by a background regeneration), so the environment signing key was
    /// readable after all: the environment-wide warning no longer applies.
    /// </summary>
    public void IndexGenerated() => _notices.Clear(NoticeKey);

    /// <summary>True when the index was regenerated.</summary>
    public async Task<bool> RunAsync(string teamFolderName, Func<Task> regenerate)
    {
        try
        {
            await regenerate();
            IndexGenerated();
            return true;
        }
        catch (SigningKeyUnreadableException ex)
        {
            _notices.Add(UserNoticeLevel.Warning,
                "Distributed indexes are not being updated because the environment signing key could not be read.",
                NoticeKey);
            await _audit.RecordAsync(new AuditRecord(
                AuditEvents.IndexRegenerationFailed, teamFolderName, AuditOutcome.Failure, ex.Reason));
            return false;
        }
        catch (Exception ex)
        {
            // Unchanged behaviour for every other failure: logged, non-fatal, repairable from Index Management.
            System.Diagnostics.Debug.WriteLine(
                $"  WARNING: Index regeneration failed for {teamFolderName}: {ex.Message}");
            return false;
        }
    }
}
