namespace NewsCentral.Ui;

/// <summary>Outcome of one item in a bulk run. <see cref="Warning"/> is set only when <see cref="Succeeded"/>.</summary>
public sealed record BulkItemOutcome(bool Succeeded, string? Warning = null, string? Error = null)
{
    public static BulkItemOutcome Success(string? warning = null) => new(true, warning, null);

    public static BulkItemOutcome Failure(string error) => new(false, null, error);
}

/// <summary>Result of <see cref="BulkOperationRunner.RunAsync{TItem}"/>.</summary>
public sealed class BulkRunResult<TItem>
{
    public IReadOnlyList<(TItem Item, BulkItemOutcome Outcome)> Outcomes { get; }

    /// <summary>True when Stop was requested — the run ended after its current item, not all items ran.</summary>
    public bool Stopped { get; }

    public int SucceededCount => Outcomes.Count(o => o.Outcome.Succeeded);

    public int FailedCount => Outcomes.Count(o => !o.Outcome.Succeeded);

    public int WarningCount => Outcomes.Count(o => o.Outcome.Succeeded && o.Outcome.Warning != null);

    public BulkRunResult(IReadOnlyList<(TItem Item, BulkItemOutcome Outcome)> outcomes, bool stopped)
    {
        Outcomes = outcomes;
        Stopped = stopped;
    }
}

/// <summary>
/// UI-2: runs a bulk action over a list of items strictly one after another — never in parallel.
/// Pure orchestration, no knowledge of what the action does. A thrown exception from
/// <paramref name="action"/> is caught per item and turned into a failed outcome rather than
/// aborting the run; a run never aborts on a single item's failure. <paramref name="stop"/> is
/// checked only BETWEEN items, never passed into an in-flight item — an item already started
/// always finishes.
/// </summary>
public static class BulkOperationRunner
{
    public static async Task<BulkRunResult<TItem>> RunAsync<TItem>(
        IReadOnlyList<TItem> items,
        Func<TItem, Task<BulkItemOutcome>> action,
        IProgress<(int done, int total)>? progress,
        CancellationToken stop)
    {
        var outcomes = new List<(TItem Item, BulkItemOutcome Outcome)>();
        var stopped = false;

        for (var i = 0; i < items.Count; i++)
        {
            if (stop.IsCancellationRequested)
            {
                stopped = true;
                break;
            }

            var item = items[i];
            BulkItemOutcome outcome;
            try
            {
                outcome = await action(item);
            }
            catch (Exception ex)
            {
                outcome = BulkItemOutcome.Failure(ex.Message);
            }

            outcomes.Add((item, outcome));
            progress?.Report((i + 1, items.Count));
        }

        return new BulkRunResult<TItem>(outcomes, stopped);
    }
}

/// <summary>
/// Which items of a bulk run to try again: the ones that failed plus the ones never processed because the
/// run was stopped.
/// </summary>
public static class BulkRetrySelection
{
    /// <summary>
    /// The items of <paramref name="attempted"/> whose outcome failed, plus those with no outcome at all, in
    /// <paramref name="attempted"/> order. Outcome i belongs to attempted[i] (the runner processes in order and
    /// appends one outcome per processed item), so items are matched by position, never by equality.
    /// </summary>
    /// <exception cref="ArgumentException">There are more outcomes than attempted items.</exception>
    public static List<TItem> Remaining<TItem>(IReadOnlyList<TItem> attempted, BulkRunResult<TItem> result)
    {
        if (result.Outcomes.Count > attempted.Count)
            throw new ArgumentException("The result has more outcomes than attempted items.", nameof(result));

        var remaining = new List<TItem>();
        for (var i = 0; i < attempted.Count; i++)
        {
            if (i >= result.Outcomes.Count || !result.Outcomes[i].Outcome.Succeeded)
                remaining.Add(attempted[i]);
        }

        return remaining;
    }
}
