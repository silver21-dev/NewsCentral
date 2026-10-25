using System.Security.AccessControl;

namespace NewsCentral.Security;

/// <summary>One drift finding: which asset, where, and what to tell the administrator.</summary>
public sealed record AclDriftWarning(string Area, string Path, string Message);

/// <summary>
/// Detects when the permissions scripts/Set-AuthoringAcl.ps1 should have applied are missing: the key store readable,
/// users.json writable, or the audit log tamperable by a broad identity (Users, Everyone, Authenticated Users,
/// INTERACTIVE - see <see cref="AclExposureEvaluator"/>). The app only WARNS; it never blocks and never sets an ACL.
///
/// Caveat inherited from the evaluator: inheritance flags are ignored, so an ACE scoped to "files only" or "this folder
/// only" is still counted. The audit check can therefore over-report on a folder that is not provisioned by the
/// script (an ordinary inherited folder grants Users create rights). That is acceptable for a warning.
/// </summary>
public static class AuthoringAclChecks
{
    public const string AreaKeys = "keys";
    public const string AreaUsers = "users.json";
    public const string AreaAudit = "audit";

    /// <summary>Reading a key file (ReadData is also ListDirectory on a folder).</summary>
    public const FileSystemRights KeysReadRights = FileSystemRights.ReadData;

    /// <summary>Anything that lets a holder change users.json (or give themselves the right to).</summary>
    public const FileSystemRights UsersFileWriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>What lets a holder rewrite or remove audit lines, or take the folder over.</summary>
    public const FileSystemRights AuditTamperRights =
        FileSystemRights.WriteData | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static readonly int KeysMask = AclExposureEvaluator.WithGenericRead(KeysReadRights);
    private static readonly int UsersMask = AclExposureEvaluator.WithGenericWrite(UsersFileWriteRights);
    private static readonly int AuditMask = AclExposureEvaluator.WithGenericWrite(AuditTamperRights);

    // ── Pure evaluation (over already-read rules) ───────────────────────────────

    public static AclExposure? KeysReadableByBroadIdentity(IEnumerable<AclRuleView> rules) =>
        AclExposureEvaluator.FindExposure(rules, KeysMask, KeysReadRights);

    public static AclExposure? UsersFileWritableByBroadIdentity(IEnumerable<AclRuleView> rules) =>
        AclExposureEvaluator.FindExposure(rules, UsersMask, UsersFileWriteRights);

    public static AclExposure? AuditTamperableByBroadIdentity(IEnumerable<AclRuleView> rules) =>
        AclExposureEvaluator.FindExposure(rules, AuditMask, AuditTamperRights);

    // ── File-system check ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads the three ACLs and returns a warning for each exposure found. Never throws: an asset that does not exist
    /// yet, or whose ACL cannot be read (a network share the account cannot query), produces no warning.
    /// </summary>
    public static IReadOnlyList<AclDriftWarning> Check(string dataPath, string securePath, string auditPath)
    {
        var warnings = new List<AclDriftWarning>();

        // Keys: the keys folder, and each team folder in it (a broad ACE on one team folder exposes that team's key).
        var keysRoot = Path.Combine(securePath, "keys");
        foreach (var folder in KeyFolders(securePath, keysRoot))
        {
            var exposure = Safe(() => KeysReadableByBroadIdentity(AclExposureEvaluator.Read(folder)));
            if (exposure != null)
            {
                warnings.Add(new AclDriftWarning(AreaKeys, folder,
                    $"The signing key folder {folder} can be read by {exposure.Identity}. Any author could copy a team's private key. " +
                    "Run scripts/Set-AuthoringAcl.ps1, then rotate the team keys."));
                break;   // one warning for the keys area is enough
            }
        }

        var usersFile = Path.Combine(dataPath, "config", "users.json");
        if (File.Exists(usersFile))
        {
            var exposure = Safe(() => UsersFileWritableByBroadIdentity(AclExposureEvaluator.Read(usersFile)));
            if (exposure != null)
                warnings.Add(new AclDriftWarning(AreaUsers, usersFile,
                    $"users.json ({usersFile}) can be changed by {exposure.Identity} ({exposure.Rights}). An author could make themselves administrator. " +
                    "Run scripts/Set-AuthoringAcl.ps1."));
        }

        if (Directory.Exists(auditPath))
        {
            var exposure = Safe(() => AuditTamperableByBroadIdentity(AclExposureEvaluator.Read(auditPath)));
            if (exposure != null)
                warnings.Add(new AclDriftWarning(AreaAudit, auditPath,
                    $"The audit folder {auditPath} lets {exposure.Identity} {exposure.Rights}, so audit lines could be rewritten or deleted. " +
                    "Run scripts/Set-AuthoringAcl.ps1. (This check ignores inheritance scoping and can over-report on a folder the script has not provisioned.)"));
        }

        return warnings;
    }

    private static IEnumerable<string> KeyFolders(string securePath, string keysRoot)
    {
        if (Directory.Exists(keysRoot))
        {
            yield return keysRoot;
            string[] teams;
            try { teams = Directory.GetDirectories(keysRoot); }
            catch { yield break; }
            foreach (var team in teams) yield return team;
        }
        else if (Directory.Exists(securePath))
        {
            yield return securePath;
        }
    }

    private static T? Safe<T>(Func<T?> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            return default;
        }
    }
}
