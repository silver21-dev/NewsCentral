using System.Security.AccessControl;
using NewsCentral.Security;

namespace NewsService.Services;

/// <summary>
/// Decides whether the publish folder (<c>Delivery:PublishedImagePath</c>) is writable by a
/// non-administrative principal. The images in it are handed to the wallpaper and lock-screen APIs in
/// every session, so a non-administrator who can replace one bypasses the whole index-signing chain.
/// The evaluation itself lives in <see cref="AclExposureEvaluator"/> (NewsCentral.Shared); this class
/// supplies the write mask for this asset.
///
/// Rights: only write-specific rights count (WriteData/CreateFiles, AppendData/CreateDirectories,
/// WriteExtendedAttributes, WriteAttributes, DeleteSubdirectoriesAndFiles, Delete) plus ChangePermissions and
/// TakeOwnership, which let a holder grant themselves write by a longer route, plus the raw GENERIC_WRITE and
/// GENERIC_ALL bits an inherit-only ACE can carry. The previous test used the composite
/// <c>Write | Modify | FullControl</c>; Modify contains ReadAndExecute and Synchronize, so an ordinary read-only
/// Users ACE overlapped it and the check fired on every correctly protected folder.
/// </summary>
internal static class PublishFolderAcl
{
    internal const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static readonly int WriteMask = AclExposureEvaluator.WithGenericWrite(WriteRights);

    /// <summary>The first watched identity left with an effective write right, or null.</summary>
    public static AclExposure? FindWriteExposure(IEnumerable<AclRuleView> rules) =>
        AclExposureEvaluator.FindExposure(rules, WriteMask, WriteRights);

    public static IReadOnlyList<AclRuleView> Read(string fullPath) => AclExposureEvaluator.Read(fullPath);

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
}
