namespace NewsCentral.Services;

/// <summary>
/// The team's signing key exists but could not be read. Index generation refuses rather than publishing
/// an unsigned index; the message names the team and the reason and is safe to show to the user.
/// </summary>
public sealed class SigningKeyUnreadableException : InvalidOperationException
{
    public string TeamFolderName { get; }
    public string Reason { get; }

    public SigningKeyUnreadableException(string teamFolderName, string reason)
        : base($"Signing key for {teamFolderName} could not be read: {reason}. " +
               "Nothing was published and the existing index is unchanged. Ask an administrator to restore access to the key.")
    {
        TeamFolderName = teamFolderName;
        Reason = reason;
    }
}
