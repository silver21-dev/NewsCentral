using System.Security.AccessControl;
using System.Security.Principal;

namespace NewsCentral.Security;

/// <summary>One access rule, reduced to what the exposure check needs.</summary>
public readonly record struct AclRuleView(SecurityIdentifier Sid, int Rights, bool Allow);

/// <summary>A broad (non-administrative) identity that holds one of the watched rights, and which.</summary>
public sealed record AclExposure(string Identity, string Sid, string Rights)
{
    public string Signature => $"{Identity}|{Rights}";
}

/// <summary>
/// Decides whether a folder or file grants a watched set of rights to a broad identity. Pure over
/// <see cref="AclRuleView"/> so it is testable without touching a file system. The rights mask is a
/// parameter: each caller states which rights count as exposure for its asset.
///
/// Identities: Users, Everyone, Authenticated Users and INTERACTIVE. A Deny for the same identity cancels the
/// rights it covers: an Allow covered by a matching Deny is not an exposure, and a Deny alone never is.
/// Inheritance flags are deliberately ignored - an inherit-only ACE applies to the files created later, which
/// are exactly what must be protected. The corollary is a caveat: on a folder whose ACEs are scoped by
/// inheritance flags (for example a provisioned append-only folder) a check can over-report.
/// The raw GENERIC_WRITE and GENERIC_ALL bits an inherit-only ACE can carry count as write rights; the
/// generic bits are matched only when the mask asks for them (see <see cref="WithGenericWrite"/>).
/// </summary>
public static class AclExposureEvaluator
{
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    private static readonly (SecurityIdentifier Sid, string Name)[] Watched =
    [
        (new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), "Users"),
        (new SecurityIdentifier(WellKnownSidType.WorldSid, null), "Everyone"),
        (new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), "Authenticated Users"),
        (new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), "INTERACTIVE"),
    ];

    /// <summary>
    /// Widens a rights mask with GENERIC_ALL and GENERIC_WRITE, which an inherit-only ACE can carry in place
    /// of the specific write rights. Use for masks that mean "can modify".
    /// </summary>
    public static int WithGenericWrite(FileSystemRights rights) => (int)rights | GenericAll | GenericWrite;

    /// <summary>
    /// Widens a rights mask with GENERIC_ALL and GENERIC_READ, for masks that mean "can read".
    /// </summary>
    public static int WithGenericRead(FileSystemRights rights) => (int)rights | GenericAll | unchecked((int)0x80000000);

    /// <summary>The first watched identity left with an effective right in <paramref name="mask"/>, or null.</summary>
    public static AclExposure? FindExposure(IEnumerable<AclRuleView> rules, int mask, FileSystemRights namedRights)
    {
        var all = rules.ToList();
        foreach (var (sid, name) in Watched)
        {
            var allowed = 0;
            var denied = 0;
            foreach (var rule in all.Where(r => r.Sid.Equals(sid)))
            {
                if (rule.Allow) allowed |= rule.Rights & mask;
                else denied |= rule.Rights & mask;
            }
            var effective = allowed & ~denied;
            if (effective != 0) return new AclExposure(name, sid.Value, Describe(effective, namedRights));
        }
        return null;
    }

    /// <summary>The ACL of a directory or file as <see cref="AclRuleView"/>s (explicit and inherited).</summary>
    public static IReadOnlyList<AclRuleView> Read(string fullPath)
    {
        FileSystemSecurity security = Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath).GetAccessControl()
            : new FileInfo(fullPath).GetAccessControl();
        var list = new List<AclRuleView>();
        foreach (FileSystemAccessRule rule in
                 security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference is SecurityIdentifier sid)
                list.Add(new AclRuleView(sid, (int)rule.FileSystemRights, rule.AccessControlType == AccessControlType.Allow));
        }
        return list;
    }

    private static string Describe(int rights, FileSystemRights namedRights)
    {
        var parts = new List<string>();
        var named = (FileSystemRights)(rights & (int)namedRights);
        if (named != 0) parts.Add(named.ToString());
        if ((rights & GenericAll) != 0) parts.Add("GenericAll");
        if ((rights & GenericWrite) != 0) parts.Add("GenericWrite");
        if ((rights & unchecked((int)0x80000000)) != 0) parts.Add("GenericRead");
        return string.Join(", ", parts);
    }
}
