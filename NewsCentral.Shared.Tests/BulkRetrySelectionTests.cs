using NewsCentral.Ui;
using Xunit;

namespace NewsCentral.Shared.Tests;

/// <summary>Covers BulkRetrySelection — the failed and unprocessed items of a bulk run, matched by position.</summary>
public sealed class BulkRetrySelectionTests
{
    private static readonly string[] Items = { "a", "b", "c", "d" };

    private static BulkRunResult<string> Result(bool stopped, params bool[] succeeded) =>
        new(succeeded.Select((ok, i) => (Items[i], ok ? BulkItemOutcome.Success() : BulkItemOutcome.Failure("x"))).ToList(),
            stopped);

    [Fact]
    public void AllSucceeded_GivesEmpty() =>
        Assert.Empty(BulkRetrySelection.Remaining(Items, Result(false, true, true, true, true)));

    [Fact]
    public void Failures_AreKeptInAttemptedOrder() =>
        Assert.Equal(new[] { "b", "d" }, BulkRetrySelection.Remaining(Items, Result(false, true, false, true, false)));

    [Fact]
    public void StoppedEarly_KeepsUnprocessedTailPlusEarlierFailures() =>
        Assert.Equal(new[] { "a", "c", "d" }, BulkRetrySelection.Remaining(Items, Result(true, false, true)));

    [Fact]
    public void StoppedBeforeFirstItem_KeepsEverything() =>
        Assert.Equal(Items, BulkRetrySelection.Remaining(Items, Result(true)));

    [Fact]
    public void EmptyAttempted_GivesEmpty() =>
        Assert.Empty(BulkRetrySelection.Remaining(Array.Empty<string>(),
            new BulkRunResult<string>(new List<(string, BulkItemOutcome)>(), false)));

    [Fact]
    public void MoreOutcomesThanAttempted_Throws() =>
        Assert.Throws<ArgumentException>(
            () => BulkRetrySelection.Remaining(new[] { "a" }, Result(false, true, true)));
}
