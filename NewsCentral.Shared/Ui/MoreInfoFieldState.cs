namespace NewsCentral.Ui;

/// <summary>
/// The decision logic behind the authoring app's "More information" field, kept out of the Razor component so it is
/// tested. Uses <see cref="MoreInfoUrlPolicy"/> (the same rule NewsViewer enforces) against the app's own
/// <c>Ui:AllowedDomains</c>. A value is committed when the author leaves the field or presses Enter; an allowed value is
/// stored in its normalised form, a rejected one never reaches the model and the previous value stays.
/// </summary>
public sealed class MoreInfoFieldState
{
    // Resource keys (SharedResource.resx) for the message under the field.
    public const string KeyNoAllowedDomains = "MoreInfoNoAllowedDomains";
    public const string KeyNotHttps = "MoreInfoSchemeNotHttps";
    public const string KeyHostNotAllowed = "MoreInfoHostNotAllowed";
    public const string KeyNotAbsolute = "MoreInfoNotAbsolute";
    public const string KeyUserInfo = "MoreInfoUserInfo";

    private readonly IReadOnlyList<string> _allowed;
    private MoreInfoDecision _current;

    public MoreInfoFieldState(IReadOnlyList<string>? allowedDomains, string? initialValue)
    {
        _allowed = allowedDomains ?? Array.Empty<string>();
        Value = initialValue ?? string.Empty;
        _current = MoreInfoUrlPolicy.Evaluate(Value, _allowed);
        // A stored link that the current settings no longer allow is kept (the author may leave it) but flagged.
        ExistingValueNotAllowed = Value.Trim().Length > 0 && !_current.IsAllowed;
    }

    /// <summary>The accepted value: what goes into the model and what the field shows.</summary>
    public string Value { get; private set; }

    /// <summary>Resource key of the reason the last commit was rejected; null when it was accepted (or none yet).</summary>
    public string? MessageKey { get; private set; }

    /// <summary>Format argument for <see cref="MessageKey"/>: the allowed domains, comma separated.</summary>
    public string AllowedDomainsText => string.Join(", ", _allowed);

    /// <summary>The value loaded into the field is not allowed by the current settings (viewers will see the button grayed out).</summary>
    public bool ExistingValueNotAllowed { get; private set; }

    /// <summary>The open-link button is enabled only for an allowed value.</summary>
    public bool CanOpen => _current.IsAllowed;

    /// <summary>The exact URL the open-link button opens, and its tooltip; null while the button is disabled.</summary>
    public string? OpenUrl => _current.IsAllowed ? _current.LaunchUrl : null;

    /// <summary>
    /// Commits what the author typed. Returns true when it was accepted (empty always is). Saving is never blocked by
    /// this: a rejected value simply leaves <see cref="Value"/> as it was.
    /// </summary>
    public bool Commit(string? input)
    {
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            Accept(string.Empty, MoreInfoUrlPolicy.Evaluate(null, _allowed));
            return true;
        }

        var decision = MoreInfoUrlPolicy.Evaluate(text, _allowed);
        if (decision.IsAllowed)
        {
            Accept(decision.LaunchUrl!, decision);
            return true;
        }

        MessageKey = decision.Reason switch
        {
            MoreInfoRejection.NoAllowedDomains => KeyNoAllowedDomains,
            MoreInfoRejection.SchemeNotHttps => KeyNotHttps,
            MoreInfoRejection.HostNotAllowed => KeyHostNotAllowed,
            MoreInfoRejection.UserInfoPresent => KeyUserInfo,
            _ => KeyNotAbsolute
        };
        return false;
    }

    private void Accept(string value, MoreInfoDecision decision)
    {
        Value = value;
        _current = decision;
        MessageKey = null;
        ExistingValueNotAllowed = false;   // the author chose a value; the old warning no longer applies
    }
}
