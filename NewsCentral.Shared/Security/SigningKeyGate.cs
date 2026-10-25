using NewsCentral.Models;

namespace NewsCentral.Security;

public enum SigningAction
{
    /// <summary>A private key is available: sign the index.</summary>
    Sign,

    /// <summary>No key is configured for the team: today's behaviour, the index is published unsigned.</summary>
    PublishUnsigned,

    /// <summary>The key exists or may exist but could not be read: nothing may be published.</summary>
    Block
}

public readonly record struct SigningDecision(SigningAction Action, TeamSigningKeys? Keys, string? BlockReason);

/// <summary>
/// The one rule that connects key loading to index signing: an unreadable key blocks, it is never the same as no key.
/// Pure so the rule is tested without the authoring app.
/// </summary>
public static class SigningKeyGate
{
    public static SigningDecision Decide(TeamKeyLoadResult load) => load.Outcome switch
    {
        KeyLoadOutcome.Unreadable => new(SigningAction.Block, null, load.Reason ?? "unknown reason"),
        KeyLoadOutcome.Loaded when !string.IsNullOrEmpty(load.Keys?.PrivateKey) => new(SigningAction.Sign, load.Keys, null),
        _ => new(SigningAction.PublishUnsigned, null, null)
    };
}
