using NewsCentral.Ui;

namespace NewsCentral.Shared.Tests;

public class MoreInfoFieldStateTests
{
    private static IReadOnlyList<string> Allow(string raw) => MoreInfoUrlPolicy.ParseAllowedDomains(raw).Domains;

    private static MoreInfoFieldState Field(string allowed = "contoso.com", string? initial = null) => new(Allow(allowed), initial);

    [Fact]
    public void AnAllowedUrl_IsStoredInItsNormalisedForm()
    {
        var f = Field();

        Assert.True(f.Commit("  https://Intranet.Contoso.com/News Today "));

        Assert.Equal("https://intranet.contoso.com/News%20Today", f.Value);
        Assert.Null(f.MessageKey);
    }

    [Theory]
    [InlineData("http://intranet.contoso.com/news", MoreInfoFieldState.KeyNotHttps)]
    [InlineData("file://contoso.com/x", MoreInfoFieldState.KeyNotHttps)]
    [InlineData("ms-settings:display", MoreInfoFieldState.KeyNotHttps)]
    [InlineData("https://evilcontoso.com/news", MoreInfoFieldState.KeyHostNotAllowed)]
    [InlineData("https://contoso.com.evil.com/news", MoreInfoFieldState.KeyHostNotAllowed)]
    [InlineData("intranet.contoso.com/news", MoreInfoFieldState.KeyNotAbsolute)]
    [InlineData("/news", MoreInfoFieldState.KeyNotAbsolute)]
    [InlineData("https://user@contoso.com/news", MoreInfoFieldState.KeyUserInfo)]
    public void ARejectedUrl_LeavesThePreviousValue_AndNamesTheReason(string typed, string expectedKey)
    {
        var f = Field();
        Assert.True(f.Commit("https://contoso.com/first"));

        Assert.False(f.Commit(typed));

        Assert.Equal("https://contoso.com/first", f.Value);
        Assert.Equal(expectedKey, f.MessageKey);
        Assert.True(f.CanOpen);   // still the last accepted value
    }

    [Fact]
    public void ARejectedUrlOnANewPresentation_LeavesTheFieldEmpty()
    {
        var f = Field();

        Assert.False(f.Commit("http://contoso.com"));

        Assert.Equal(string.Empty, f.Value);
        Assert.False(f.CanOpen);
    }

    [Fact]
    public void TheMessageStaysUntilTheNextAcceptedValue()
    {
        var f = Field();
        f.Commit("http://contoso.com");
        f.Commit("https://evilcontoso.com");
        Assert.Equal(MoreInfoFieldState.KeyHostNotAllowed, f.MessageKey);

        f.Commit("https://contoso.com/ok");

        Assert.Null(f.MessageKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_IsAlwaysAccepted_AndClearsTheValue(string? typed)
    {
        var f = Field();
        f.Commit("https://contoso.com/first");
        f.Commit("http://contoso.com");   // leaves a message behind

        Assert.True(f.Commit(typed));

        Assert.Equal(string.Empty, f.Value);
        Assert.Null(f.MessageKey);
        Assert.False(f.CanOpen);
    }

    [Fact]
    public void NotConfigured_RejectsEverythingNonEmpty_ButAcceptsEmpty()
    {
        var f = Field(allowed: "");

        Assert.False(f.Commit("https://contoso.com/news"));
        Assert.Equal(MoreInfoFieldState.KeyNoAllowedDomains, f.MessageKey);
        Assert.Equal(string.Empty, f.Value);
        Assert.True(f.Commit(""));
    }

    [Fact]
    public void AnExistingDisallowedValue_IsKeptAndWarnedAbout_WithTheOpenLinkDisabled()
    {
        var f = Field(allowed: "fabrikam.com", initial: "https://intranet.contoso.com/news");

        Assert.Equal("https://intranet.contoso.com/news", f.Value);   // shown, not removed
        Assert.True(f.ExistingValueNotAllowed);
        Assert.False(f.CanOpen);
        Assert.Null(f.OpenUrl);
        Assert.Null(f.MessageKey);
    }

    [Fact]
    public void AnExistingDisallowedValue_WarnsWhenNothingIsConfigured_Too()
    {
        var f = Field(allowed: "", initial: "https://intranet.contoso.com/news");

        Assert.True(f.ExistingValueNotAllowed);
        Assert.False(f.CanOpen);
    }

    [Fact]
    public void TheWarning_GoesWhenTheAuthorCommitsAnotherValue_AndAnEmptyExistingValueNeverWarns()
    {
        var f = Field(allowed: "fabrikam.com", initial: "https://intranet.contoso.com/news");
        f.Commit("https://fabrikam.com/news");

        Assert.False(f.ExistingValueNotAllowed);
        Assert.True(f.CanOpen);

        Assert.False(Field(initial: null).ExistingValueNotAllowed);
        Assert.False(Field(initial: "").ExistingValueNotAllowed);
    }

    [Fact]
    public void AnAllowedExistingValue_IsNotWarnedAbout_AndOpens()
    {
        var f = Field(initial: "https://intranet.contoso.com/news");

        Assert.False(f.ExistingValueNotAllowed);
        Assert.True(f.CanOpen);
        Assert.Equal("https://intranet.contoso.com/news", f.OpenUrl);
    }

    [Fact]
    public void OpenLink_IsEnabledOnlyForAllowed_AndItsTooltipIsTheExactUrl()
    {
        var f = Field();
        Assert.False(f.CanOpen);
        Assert.Null(f.OpenUrl);

        f.Commit("https://INTRANET.contoso.com/news");
        Assert.True(f.CanOpen);
        Assert.Equal("https://intranet.contoso.com/news", f.OpenUrl);
        Assert.Equal(f.Value, f.OpenUrl);   // what is shown in the field is what opens

        f.Commit("");
        Assert.False(f.CanOpen);
        Assert.Null(f.OpenUrl);
    }

    [Fact]
    public void TheHostNotAllowedMessage_CanListTheAllowedDomains()
    {
        var f = Field(allowed: "contoso.com; fabrikam.com");

        Assert.Equal("contoso.com, fabrikam.com", f.AllowedDomainsText);
    }
}
