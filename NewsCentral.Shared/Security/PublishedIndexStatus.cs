namespace NewsCentral.Security;

/// <summary>Which environment key a team's published authoring-tier index.json was signed with.</summary>
public enum PublishedIndexStatus
{
    Current,
    Next,
    Previous,
    UnknownKey,
    Unsigned,
    NoIndex,
    Unreadable
}

/// <summary>
/// What was found when reading a team's authoring-tier index.json: only whether it exists, whether it carries a
/// signature, its <c>keyId</c>, or the reason it could not be read. Nothing else is read from the index.
/// </summary>
public sealed record PublishedIndexObservation(bool Exists, bool Signed, string? KeyId, string? UnreadableReason = null)
{
    public static PublishedIndexObservation None() => new(false, false, null);
    public static PublishedIndexObservation Failed(string reason) => new(false, false, null, reason);
}

/// <summary>
/// Pure status rule for Key Management's "Published indexes" section: compares a team's KeyId with the fingerprints
/// of the environment's current, next and previous keys. A KeyId matching <c>Next</c> should be impossible (Next
/// never signs) but is reported as such rather than hidden.
/// </summary>
public static class PublishedIndexStatusEvaluator
{
    public static PublishedIndexStatus Evaluate(
        PublishedIndexObservation observation, string? currentKeyId, string? nextKeyId, string? previousKeyId)
    {
        if (observation.UnreadableReason is not null) return PublishedIndexStatus.Unreadable;
        if (!observation.Exists) return PublishedIndexStatus.NoIndex;
        if (!observation.Signed) return PublishedIndexStatus.Unsigned;
        if (string.IsNullOrEmpty(observation.KeyId)) return PublishedIndexStatus.UnknownKey;

        if (Same(observation.KeyId, currentKeyId)) return PublishedIndexStatus.Current;
        if (Same(observation.KeyId, nextKeyId)) return PublishedIndexStatus.Next;
        if (Same(observation.KeyId, previousKeyId)) return PublishedIndexStatus.Previous;
        return PublishedIndexStatus.UnknownKey;
    }

    /// <summary>
    /// True for the statuses that mean "an index is out there that is not signed with the current key". A team without
    /// an index carries no old key, so it does not count.
    /// </summary>
    public static bool IsBehindCurrent(PublishedIndexStatus status) =>
        status is not (PublishedIndexStatus.Current or PublishedIndexStatus.NoIndex);

    private static bool Same(string keyId, string? other) =>
        !string.IsNullOrEmpty(other) && string.Equals(keyId, other, StringComparison.Ordinal);
}
