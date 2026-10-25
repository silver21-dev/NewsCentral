using NewsCentral.Ui;

namespace NewsCentral.Shared.Tests;

public class MoreInfoUrlPolicyTests
{
    private static IReadOnlyList<string> List(string raw) => MoreInfoUrlPolicy.ParseAllowedDomains(raw).Domains;

    // ── Allowed ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://contoso.com/x")]
    [InlineData("https://intranet.contoso.com/x")]
    [InlineData("https://INTRANET.Contoso.com./x")]
    [InlineData("https://a.b.contoso.com:8443/x?q=1#f")]
    public void Allowed_WhenHttpsAndTheHostIsTheDomainOrASubdomain(string url)
    {
        var d = MoreInfoUrlPolicy.Evaluate(url, List("contoso.com"));

        Assert.True(d.IsAllowed);
        Assert.Null(d.Reason);
    }

    [Fact]
    public void Allowed_LaunchUrlIsTheParsedNormalisedForm_NotTheRawString()
    {
        var d = MoreInfoUrlPolicy.Evaluate("https://INTRANET.Contoso.com/News Today", List("contoso.com"));

        Assert.True(d.IsAllowed);
        Assert.Equal("https://intranet.contoso.com/News%20Today", d.LaunchUrl);
        Assert.Equal("intranet.contoso.com", d.Host);
    }

    [Fact]
    public void ListWithWhitespaceAndWildcard_AllowsTheSubdomainAndTheApex()
    {
        var list = List(" contoso.com ; *.fabrikam.com ");

        Assert.True(MoreInfoUrlPolicy.Evaluate("https://a.fabrikam.com", list).IsAllowed);
        Assert.True(MoreInfoUrlPolicy.Evaluate("https://fabrikam.com", list).IsAllowed);
        Assert.True(MoreInfoUrlPolicy.Evaluate("https://contoso.com", list).IsAllowed);
    }

    // ── Rejected ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://contoso.com", MoreInfoRejection.SchemeNotHttps)]
    [InlineData("https://evilcontoso.com", MoreInfoRejection.HostNotAllowed)]
    [InlineData("https://contoso.com.evil.com", MoreInfoRejection.HostNotAllowed)]
    [InlineData("https://contoso.com@evil.com", MoreInfoRejection.UserInfoPresent)]
    [InlineData("https://user@contoso.com", MoreInfoRejection.UserInfoPresent)]
    [InlineData("file://contoso.com/x", MoreInfoRejection.SchemeNotHttps)]
    [InlineData("\\\\contoso.com\\share", MoreInfoRejection.SchemeNotHttps)]
    [InlineData("ms-settings:display", MoreInfoRejection.SchemeNotHttps)]
    [InlineData("javascript:alert(1)", MoreInfoRejection.SchemeNotHttps)]
    [InlineData("/x", MoreInfoRejection.NotAbsolute)]
    [InlineData("contoso.com/x", MoreInfoRejection.NotAbsolute)]
    public void Rejected_WithTheMatchingReason(string url, string reason)
    {
        var d = MoreInfoUrlPolicy.Evaluate(url, List("contoso.com"));

        Assert.False(d.IsAllowed);
        Assert.Null(d.LaunchUrl);
        Assert.Equal(reason, d.Reason);
        Assert.True(d.ShouldLog);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyUrl_IsRejectedAsNoUrl_AndIsNotLogged(string? url)
    {
        var d = MoreInfoUrlPolicy.Evaluate(url, List("contoso.com"));

        Assert.False(d.IsAllowed);
        Assert.Equal(MoreInfoRejection.NoUrl, d.Reason);
        Assert.False(d.ShouldLog);
    }

    [Fact]
    public void EmptyOrNullAllowList_RejectsEverything_AndIsLogged()
    {
        foreach (var list in new IReadOnlyList<string>?[] { null, Array.Empty<string>(), List(""), List(" ; , ") })
        {
            var d = MoreInfoUrlPolicy.Evaluate("https://contoso.com/x", list);
            Assert.False(d.IsAllowed);
            Assert.Equal(MoreInfoRejection.NoAllowedDomains, d.Reason);
            Assert.True(d.ShouldLog);
        }
    }

    [Fact]
    public void NoUrlWinsOverNoAllowedDomains_SoAnUnconfiguredFleetDoesNotLogEveryLinklessPoster()
    {
        var d = MoreInfoUrlPolicy.Evaluate("", null);

        Assert.Equal(MoreInfoRejection.NoUrl, d.Reason);
        Assert.False(d.ShouldLog);
    }

    // ── IDN ───────────────────────────────────────────────────────────────────

    [Fact]
    public void UnicodeEntryAndUrl_MatchTheirPunycodeEquivalents()
    {
        var unicodeList = List("bücher.example");   // bücher.example
        var punycodeList = List("xn--bcher-kva.example");

        Assert.Equal(punycodeList, unicodeList);
        Assert.True(MoreInfoUrlPolicy.Evaluate("https://bücher.example/x", unicodeList).IsAllowed);
        Assert.True(MoreInfoUrlPolicy.Evaluate("https://xn--bcher-kva.example/x", unicodeList).IsAllowed);
        Assert.True(MoreInfoUrlPolicy.Evaluate("https://shop.bücher.example/x", punycodeList).IsAllowed);
    }

    [Fact]
    public void HomoglyphHost_IsRejected()
    {
        // Cyrillic small o (U+043E) in place of the Latin o in contoso.
        var d = MoreInfoUrlPolicy.Evaluate("https://cоntoso.com/x", List("contoso.com"));

        Assert.False(d.IsAllowed);
        Assert.Equal(MoreInfoRejection.HostNotAllowed, d.Reason);
        Assert.StartsWith("xn--", d.Host);
    }

    // ── Entry parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void BothSeparatorsWork_EntriesAreNormalised_AndDuplicatesDropped()
    {
        var r = MoreInfoUrlPolicy.ParseAllowedDomains("Contoso.COM; .fabrikam.com, *.Intranet.Net. ;;contoso.com");

        Assert.Equal(new[] { "contoso.com", "fabrikam.com", "intranet.net" }, r.Domains);
        Assert.Empty(r.InvalidEntries);
    }

    [Theory]
    [InlineData("https://contoso.com")]
    [InlineData("contoso.com/path")]
    [InlineData("contoso.com:443")]
    [InlineData("user@contoso.com")]
    [InlineData("contoso.com?x")]
    [InlineData("contoso.com#x")]
    [InlineData("con toso.com")]
    [InlineData("con*toso.com")]
    [InlineData("*")]
    public void InvalidEntries_AreIgnoredAndReported(string entry)
    {
        var r = MoreInfoUrlPolicy.ParseAllowedDomains($"fabrikam.com;{entry}");

        Assert.Equal(new[] { "fabrikam.com" }, r.Domains);
        Assert.Equal(new[] { entry }, r.InvalidEntries);
    }

    [Fact]
    public void NullOrBlankSetting_IsEmpty()
    {
        Assert.Empty(MoreInfoUrlPolicy.ParseAllowedDomains(null).Domains);
        Assert.Empty(MoreInfoUrlPolicy.ParseAllowedDomains("  ").Domains);
    }

    // ── What the form shows and launches ─────────────────────────────────────

    [Fact]
    public void TheTooltipTextAndTheLaunchedUrl_AreTheSameString_WhenAllowed()
    {
        var d = MoreInfoUrlPolicy.Evaluate("https://intranet.contoso.com/news", List("contoso.com"));

        Assert.True(d.IsAllowed);
        Assert.Equal("https://intranet.contoso.com/news", d.LaunchUrl);   // ViewerForm uses LaunchUrl for both
    }

    [Fact]
    public void WhenRejected_ThereIsNothingToShowOrLaunch()
    {
        var d = MoreInfoUrlPolicy.Evaluate("http://intranet.contoso.com/news", List("contoso.com"));

        Assert.False(d.IsAllowed);
        Assert.Null(d.LaunchUrl);
    }
}
