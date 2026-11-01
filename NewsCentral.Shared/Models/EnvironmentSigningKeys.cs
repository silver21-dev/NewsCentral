namespace NewsCentral.Models;

/// <summary>One ECDSA P-256 key pair. Authoring side only.</summary>
public sealed class SigningKeyPair
{
    /// <summary>Base64 PKCS#8 private key - authoring side only.</summary>
    public string? PrivateKey { get; set; }

    /// <summary>Base64 SubjectPublicKeyInfo - distributed to verifying components.</summary>
    public string? PublicKey { get; set; }

    /// <summary>When the pair was generated (UTC); the Key Management page shows its age.</summary>
    public DateTime? CreatedAt { get; set; }
}

/// <summary>
/// The environment's signing keys: the key every index is signed with (<see cref="Current"/>), an optional
/// prepared successor (<see cref="Next"/>, not yet used for signing) and the public half of the key that was
/// replaced (<see cref="PreviousPublicKey"/>, kept so clients can still verify content signed before the rotation).
/// <para>
/// The full object (including every private key) lives ONLY on the authoring tier at
/// <c>{SecurePath}\keys\environment-signing.json</c> and must NEVER be written through
/// <c>IBlobDistributionService</c> or synced to client machines.
/// </para>
/// <para>
/// Trust assumption: only system administrators change these keys; authors may read the file (their app signs
/// indexes with it) and are trusted with it by design.
/// </para>
/// </summary>
public sealed class EnvironmentSigningKeys
{
    public SigningKeyPair? Current { get; set; }
    public SigningKeyPair? Next { get; set; }
    public string? PreviousPublicKey { get; set; }
    public DateTime? PreviousCreatedAt { get; set; }
}
