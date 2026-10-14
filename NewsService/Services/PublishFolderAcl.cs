using System.Security.AccessControl;
using System.Security.Principal;

namespace NewsService.Services;

/// <summary>One access rule, reduced to what the write-exposure check needs.</summary>
internal readonly record struct AclRuleView(SecurityIdentifier Sid, int Rights, bool Allow);

/// <summary>A non-administrative identity that can modify the publish folder, and with which rights.</summary>
internal sealed record WriteExposure(string Identity, string Sid, string Rights)
{
    public string Signature => $"{Identity}|{Rights}";
}

/// <summary>
/// Decides whether the publish folder (<c>Delivery:PublishedImagePath</c>) is writable by a
/// non-administrative principal. The images in it are handed to the wallpaper and lock-screen APIs in
/// every session, so a non-administrator who can replace one bypasses the whole index-signing chain.
/// Pure over <see cref="AclRuleView"/> so it is testable without touching a file system.
///
/// Rights: only write-specific rights count (WriteData/CreateFiles, AppendData/CreateDirectories,
/// WriteExtendedAttributes, WriteAttributes, DeleteSubdirectoriesAndFiles, Delete) plus ChangePermissions and
/// TakeOwnership, which let a holder grant themselves write by a longer route, plus the raw GENERIC_WRITE and
/// GENERIC_ALL bits an inherit-only ACE can carry. The previous test used the composite
/// <c>Write | Modify | FullControl</c>; Modify contains ReadAndExecute and Synchronize, so an ordinary read-only
/// Users ACE overlapped it and the check fired on every correctly protected folder.
///
/// Identities: Users, Everyone, Authenticated Users and INTERACTIVE. A Deny for the same identity cancels the
/// rights it covers: an Allow-write covered by a matching Deny is not an exposure, and a Deny alone never is.
/// Inheritance flags are deliberately ignored - an inherit-only ACE applies to the files created later, which
/// are exactly what must be protected.
/// </summary>
internal static class PublishFolderAcl
{
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    internal const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private const int WriteMask = (int)WriteRights | GenericAll | GenericWrite;

    private static readonly (SecurityIdentifier Sid, string Name)[] Watched =
    [
        (new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), "Users"),
        (new SecurityIdentifier(WellKnownSidType.WorldSid, null), "Everyone"),
        (new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), "Authenticated Users"),
        (new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), "INTERACTIVE"),
    ];

    /// <summary>The first watched identity left with an effective write right, or null.</summary>
    public static WriteExposure? FindWriteExposure(IEnumerable<AclRuleView> rules)
    {
        var all = rules.ToList();
        foreach (var (sid, name) in Watched)
        {
            var allowed = 0;
            var denied = 0;
            foreach (var rule in all.Where(r => r.Sid.Equals(sid)))
            {
                if (rule.Allow) allowed |= rule.Rights & WriteMask;
                else denied |= rule.Rights & WriteMask;
            }
            var effective = allowed & ~denied;
            if (effective != 0) return new WriteExposure(name, sid.Value, Describe(effective));
        }
        return null;
    }

    public static IReadOnlyList<AclRuleView> Read(string fullPath)
    {
        var security = new DirectoryInfo(fullPath).GetAccessControl();
        var list = new List<AclRuleView>();
        foreach (FileSystemAccessRule rule in
                 security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference is SecurityIdentifier sid)
                list.Add(new AclRuleView(sid, (int)rule.FileSystemRights, rule.AccessControlType == AccessControlType.Allow));
        }
        return list;
    }

    /// <summary>Remediation tailored to where the folder lives.</summary>
    public static string Remediation(string fullPath)
    {
        var web = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web") + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(web, StringComparison.OrdinalIgnoreCase)
            ? "The folder is under %SystemRoot%\\Web, whose inherited ACL grants non-administrators only read access, so " +
              "that ACL has been altered: restore it (remove the write grant, or reset the folder's inheritance)."
            : "The folder is outside %SystemRoot%\\Web and inherits user-write from its parent (C:\\ProgramData\\... is the " +
              "usual case): the installer must set an explicit ACL on it (administrators and SYSTEM only write; " +
              "everyone else read), or Delivery:PublishedImagePath must move back under %SystemRoot%\\Web\\NewsCentral.";
    }

    private static string Describe(int rights)
    {
        var parts = new List<string>();
        var named = (FileSystemRights)(rights & (int)WriteRights);
        if (named != 0) parts.Add(named.ToString());
        if ((rights & GenericAll) != 0) parts.Add("GenericAll");
        if ((rights & GenericWrite) != 0) parts.Add("GenericWrite");
        return string.Join(", ", parts);
    }
}
