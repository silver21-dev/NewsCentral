using NewsCentral.Security;

namespace NewsCentral.Shared.Tests;

public sealed class PublishedIndexStatusTests
{
    private const string Cur = "f03527a0d496fa09";
    private const string Nxt = "1111111111111111";
    private const string Prv = "2222222222222222";

    private static PublishedIndexStatus Eval(PublishedIndexObservation o, string? cur = Cur, string? nxt = Nxt, string? prv = Prv) =>
        PublishedIndexStatusEvaluator.Evaluate(o, cur, nxt, prv);

    private static PublishedIndexObservation Signed(string? keyId) => new(true, true, keyId);

    [Fact]
    public void KeyIdOfCurrent_IsCurrent() => Assert.Equal(PublishedIndexStatus.Current, Eval(Signed(Cur)));

    [Fact]
    public void KeyIdOfPrevious_IsPrevious() => Assert.Equal(PublishedIndexStatus.Previous, Eval(Signed(Prv)));

    [Fact]
    public void KeyIdOfNext_IsReportedAsNext_NotACrash()
    {
        // Next never signs, so this "cannot happen" - but it must be visible if it does.
        Assert.Equal(PublishedIndexStatus.Next, Eval(Signed(Nxt)));
    }

    [Fact]
    public void AnyOtherKeyId_IsUnknownKey() => Assert.Equal(PublishedIndexStatus.UnknownKey, Eval(Signed("ffffffffffffffff")));

    [Fact]
    public void SignedButWithoutKeyId_IsUnknownKey()
    {
        Assert.Equal(PublishedIndexStatus.UnknownKey, Eval(Signed(null)));
        Assert.Equal(PublishedIndexStatus.UnknownKey, Eval(Signed("")));
    }

    [Fact]
    public void NoSignature_IsUnsigned_EvenWithAKeyId() =>
        Assert.Equal(PublishedIndexStatus.Unsigned, Eval(new PublishedIndexObservation(true, false, Cur)));

    [Fact]
    public void MissingFile_IsNoIndex() => Assert.Equal(PublishedIndexStatus.NoIndex, Eval(PublishedIndexObservation.None()));

    [Fact]
    public void ReadFailure_IsUnreadable_AndWinsOverEverythingElse() =>
        Assert.Equal(PublishedIndexStatus.Unreadable, Eval(PublishedIndexObservation.Failed("access denied")));

    [Fact]
    public void MissingFingerprints_NeverMatch()
    {
        // No previous key: a null/empty previous must not match a signed index with no KeyId or any other id.
        Assert.Equal(PublishedIndexStatus.UnknownKey, Eval(Signed(Prv), prv: null));
        Assert.Equal(PublishedIndexStatus.UnknownKey, Eval(Signed("x"), cur: null, nxt: null, prv: null));
    }

    [Theory]
    [InlineData(PublishedIndexStatus.Current, false)]
    [InlineData(PublishedIndexStatus.NoIndex, false)]
    [InlineData(PublishedIndexStatus.Previous, true)]
    [InlineData(PublishedIndexStatus.Next, true)]
    [InlineData(PublishedIndexStatus.UnknownKey, true)]
    [InlineData(PublishedIndexStatus.Unsigned, true)]
    [InlineData(PublishedIndexStatus.Unreadable, true)]
    public void IsBehindCurrent_DrivesTheRetireWarning(PublishedIndexStatus status, bool behind) =>
        Assert.Equal(behind, PublishedIndexStatusEvaluator.IsBehindCurrent(status));
}
