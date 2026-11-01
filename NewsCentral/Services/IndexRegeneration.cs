namespace NewsCentral.Services;

/// <summary>
/// The "regenerate every team" loop, kept apart from IndexGenerationService so it is tested without the authoring app.
/// The environment key is shared, so an unreadable key stops the run at the first team: it is reported once in
/// <see cref="RegenerateAllResult.KeyUnreadableReason"/> and the teams not tried are listed in
/// <see cref="RegenerateAllResult.NotProcessed"/>.
/// </summary>
public static class IndexRegeneration
{
    public static async Task<RegenerateAllResult> RegenerateAllAsync(
        IReadOnlyList<string> teamFolderNames, Func<string, Task<IndexSaveResult>> generate)
    {
        var result = new RegenerateAllResult();

        for (var i = 0; i < teamFolderNames.Count; i++)
        {
            var team = teamFolderNames[i];
            try
            {
                var saveResult = await generate(team);
                result.Regenerated++;

                if (!saveResult.DistributionSucceeded)
                    result.DistributionFailures.Add($"{team}: {saveResult.DistributionError}");
            }
            catch (SigningKeyUnreadableException ex)
            {
                result.KeyUnreadableReason = ex.Reason;
                result.NotProcessed.AddRange(teamFolderNames.Skip(i));   // this team and every one after it
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error generating index for {team}: {ex.Message}");
                result.Failures.Add($"{team}: {ex.Message}");
            }
        }

        return result;
    }
}

/// <summary>Result of <see cref="IndexGenerationService.SaveIndexFileAsync"/> / GenerateAndSaveIndexAsync (M5a).</summary>
public sealed class IndexSaveResult
{
    /// <summary>True when the authoring-tier write succeeded AND the blob push succeeded (or
    /// distribution is disabled/null — see NullBlobDistributionService). The
    /// authoring-tier write itself still throws out of these methods on failure, unchanged from
    /// before M5a; this flag covers only the non-critical distribution push.</summary>
    public bool DistributionSucceeded { get; set; }

    /// <summary>Set only when <see cref="DistributionSucceeded"/> is false.</summary>
    public string? DistributionError { get; set; }
}

/// <summary>Result of <see cref="IndexGenerationService.RegenerateAllIndexesAsync"/> (M5a).</summary>
public sealed class RegenerateAllResult
{
    /// <summary>Number of teams whose index was successfully generated and written (authoring
    /// tier) — independent of whether its distribution push succeeded.</summary>
    public int Regenerated { get; set; }

    /// <summary>One "{teamFolderName}: {error}" entry per team whose distribution push failed.</summary>
    public List<string> DistributionFailures { get; set; } = new();

    /// <summary>One "{teamFolderName}: {error}" entry per team whose index could not be generated at all
    /// (for example a storage error). Nothing was written for these teams.</summary>
    public List<string> Failures { get; set; } = new();

    /// <summary>Set when the environment signing key could not be read; the run stopped at that point.</summary>
    public string? KeyUnreadableReason { get; set; }

    /// <summary>Teams not tried because the run stopped on an unreadable signing key (the team that hit it included).</summary>
    public List<string> NotProcessed { get; set; } = new();
}
