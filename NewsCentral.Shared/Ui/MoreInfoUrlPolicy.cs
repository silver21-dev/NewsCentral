using System.Globalization;

namespace NewsCentral.Ui;

/// <summary>Why the poster's "More information" link is not offered. The names are written to launch.log.</summary>
public static class MoreInfoRejection
{
    /// <summary>The presentation has no link. The normal case: not logged.</summary>
    public const string NoUrl = "NoUrl";

    /// <summary>Ui\AllowedDomains is empty or absent, so no link is trusted.</summary>
    public const string NoAllowedDomains = "NoAllowedDomains";

    /// <summary>Not an absolute URI (relative path, plain text, UNC path form that does not parse).</summary>
    public const string NotAbsolute = "NotAbsolute";

    /// <summary>Any scheme other than https: http, file, ms-settings, javascript, custom protocol handlers.</summary>
    public const string SchemeNotHttps = "SchemeNotHttps";

    /// <summary>The URL carries user information (https://user@host), the classic look-alike trick.</summary>
    public const string UserInfoPresent = "UserInfoPresent";

    /// <summary>The host is neither an allowed domain nor a subdomain of one.</summary>
    public const string HostNotAllowed = "HostNotAllowed";
}

/// <summary>
/// The outcome of <see cref="MoreInfoUrlPolicy.Evaluate"/>. When allowed, <see cref="LaunchUrl"/> is the parsed,
/// normalised URL: exactly what is launched, and exactly what the button's tooltip shows.
/// </summary>
public sealed record MoreInfoDecision(bool IsAllowed, string? LaunchUrl, string? Reason, string? Host)
{
    public static MoreInfoDecision Allowed(string launchUrl, string host) => new(true, launchUrl, null, host);

    public static MoreInfoDecision Rejected(string reason, string? host = null) => new(false, null, reason, host);

    /// <summary>True when the rejection deserves a launch.log line (everything but a missing link).</summary>
    public bool ShouldLog => !IsAllowed && Reason != MoreInfoRejection.NoUrl;
}

/// <summary>Result of parsing the Ui\AllowedDomains setting: the usable entries and the ones that were ignored.</summary>
public sealed record AllowedDomainParseResult(IReadOnlyList<string> Domains, IReadOnlyList<string> InvalidEntries);

/// <summary>
/// Decides whether the poster's "More information" link may be launched. Only https URLs without user information
/// whose host equals an allowed domain or is a subdomain of one qualify; an empty allow-list allows nothing. Pure
/// and AOT-safe (no reflection); shared so the rule is tested without the viewer.
/// </summary>
public static class MoreInfoUrlPolicy
{
    private static readonly char[] Separators = { ';', ',' };
    private static readonly char[] ForbiddenInEntry = { '/', ':', '@', '?', '#', '*' };

    /// <summary>
    /// Parses a <c>;</c> (or <c>,</c>) separated list. Per entry: whitespace trimmed, a leading <c>*.</c> or <c>.</c>
    /// and a trailing <c>.</c> removed, converted to its punycode form, lower-cased. Empty entries are skipped; entries
    /// containing <c>/ : @ ? #</c>, whitespace or a stray <c>*</c> (or that cannot be converted) are reported in
    /// <see cref="AllowedDomainParseResult.InvalidEntries"/> and ignored. Duplicates are dropped.
    /// </summary>
    public static AllowedDomainParseResult ParseAllowedDomains(string? raw)
    {
        var domains = new List<string>();
        var invalid = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return new(domains, invalid);

        foreach (var part in raw.Split(Separators))
        {
            var entry = part.Trim();
            if (entry.Length == 0) continue;

            var domain = entry;
            if (domain.StartsWith("*.", StringComparison.Ordinal)) domain = domain.Substring(2);
            else if (domain.StartsWith('.')) domain = domain.Substring(1);
            domain = domain.TrimEnd('.');

            if (domain.Length == 0 || domain.IndexOfAny(ForbiddenInEntry) >= 0 || ContainsWhitespace(domain))
            {
                invalid.Add(entry);
                continue;
            }

            string ascii;
            try { ascii = new IdnMapping().GetAscii(domain).ToLowerInvariant(); }
            catch (ArgumentException) { invalid.Add(entry); continue; }

            if (!domains.Contains(ascii)) domains.Add(ascii);
        }

        return new(domains, invalid);
    }

    public static MoreInfoDecision Evaluate(string? url, IReadOnlyList<string>? allowedDomains)
    {
        if (string.IsNullOrWhiteSpace(url)) return MoreInfoDecision.Rejected(MoreInfoRejection.NoUrl);
        if (allowedDomains is null || allowedDomains.Count == 0)
            return MoreInfoDecision.Rejected(MoreInfoRejection.NoAllowedDomains);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return MoreInfoDecision.Rejected(MoreInfoRejection.NotAbsolute);

        string host;
        try { host = uri.IdnHost.ToLowerInvariant().TrimEnd('.'); }
        catch (InvalidOperationException) { return MoreInfoDecision.Rejected(MoreInfoRejection.NotAbsolute); }

        if (uri.Scheme != Uri.UriSchemeHttps)
            return MoreInfoDecision.Rejected(MoreInfoRejection.SchemeNotHttps, host);

        if (uri.UserInfo.Length != 0)
            return MoreInfoDecision.Rejected(MoreInfoRejection.UserInfoPresent, host);

        foreach (var domain in allowedDomains)
        {
            // Equal, or a real subdomain: the dot is part of the comparison, so evilcontoso.com never matches contoso.com.
            if (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal))
                return MoreInfoDecision.Allowed(uri.AbsoluteUri, host);
        }

        return MoreInfoDecision.Rejected(MoreInfoRejection.HostNotAllowed, host);
    }

    private static bool ContainsWhitespace(string s)
    {
        foreach (var c in s)
            if (char.IsWhiteSpace(c)) return true;
        return false;
    }
}
