namespace NewsCentral.Services;

/// <summary>
/// The environment signing key exists but could not be read. Index generation refuses rather than publishing
/// an unsigned index; the message names the reason and is safe to show to the user.
/// </summary>
public sealed class SigningKeyUnreadableException : InvalidOperationException
{
    public string Reason { get; }

    public SigningKeyUnreadableException(string reason)
        : base($"The environment signing key could not be read: {reason}. " +
               "Nothing was published and the existing index is unchanged. Ask a system administrator to restore access to the key.")
    {
        Reason = reason;
    }
}
