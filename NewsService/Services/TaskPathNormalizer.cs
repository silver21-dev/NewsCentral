namespace NewsService.Services;

/// <summary>
/// Canonical form of a path for hashing and comparing: a tokenised path and its expanded form denote
/// the same file and must be equal. Trim, strip surrounding quotes, expand environment variables,
/// forward slashes to backslashes, drop a trailing separator, lower-case (Windows). The generated
/// script implements exactly the same steps (<c>Get-NormalizedPath</c>); keep them in step.
/// </summary>
internal static class TaskPathNormalizer
{
    public static string Normalize(string path)
    {
        var p = path.Trim().Trim('"').Trim();
        p = Environment.ExpandEnvironmentVariables(p);
        p = p.Replace('/', '\\').TrimEnd('\\');
        return p.ToLowerInvariant();
    }
}
