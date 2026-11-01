using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NewsCentral.Configuration;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;

namespace NewsViewer.Services;

public sealed class PresentationSelector
{
    private readonly string _cacheRootPath;
    private readonly IConfiguration _configuration;
    private readonly bool _bypassImageIntegrityCheck;
    private readonly Action<string, string>? _onRejected;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    /// <param name="onRejected">
    /// Called with (presentation id, reason) the first time a candidate is rejected in this run; NewsViewer
    /// writes it to launch.log as a Warning line. NewsService owns the Error-level signal.
    /// </param>
    public PresentationSelector(
        string cacheRootPath,
        IConfiguration configuration,
        bool bypassImageIntegrityCheck = false,
        Action<string, string>? onRejected = null)
    {
        _cacheRootPath             = cacheRootPath;
        _configuration             = configuration;
        _bypassImageIntegrityCheck = bypassImageIntegrityCheck;
        _onRejected                = onRejected;
    }

    /// <summary>
    /// Reads index.json for each configured team, filters to News-of-the-Week assignments that are
    /// active right now, and returns the one newest to the team (later of PresentationLastModified and PublishedDate). Only
    /// <see cref="DisplayTypeInfo.IsNewsOfWeek"/> content is shown as a full-screen poster — a
    /// wallpaper-only / lock-screen-only assignment is never displayed here (the wallpaper and lock
    /// screen are applied by their own independent selections). Returns null if no qualifying
    /// assignment is found or all indexes fail verification.
    /// </summary>
    public (PublishedAssignmentIndex? Assignment, string? ImagePath) SelectActive(string[] teams) =>
        SelectActiveMatching(teams, a => a.DisplayTypes.IsNewsOfWeek);

    /// <summary>
    /// Shared selection core: enumerate every signature-verified assignment across the effective
    /// team set and pick the newest active one matching <paramref name="predicate"/> whose image is
    /// eligible: <c>ImagePath</c> passes <see cref="ImagePathValidator"/> and stays inside the cache team
    /// root, <c>ImageHash</c> is present, and the file's SHA-256 matches it (<see cref="CachedImageCheck"/>;
    /// no empty-hash skip). A candidate failing any check is ineligible and the selection falls through
    /// to the next newest; (null, null) when none is eligible — exactly as with no content.
    /// </summary>
    private (PublishedAssignmentIndex? Assignment, string? ImagePath) SelectActiveMatching(
        string[] teams, Func<PublishedAssignmentIndex, bool> predicate)
    {
        var now        = DateTime.Now;
        var candidates = EnumerateVerifiedAssignments(teams).ToList();
        var teamOf     = new Dictionary<PublishedAssignmentIndex, string>(ReferenceEqualityComparer.Instance);
        foreach (var (team, assignment) in candidates) teamOf[assignment] = team;

        var remaining = candidates.Select(c => c.Assignment).ToList();
        while (true)
        {
            var best = ActiveAssignmentSelector.PickNewestToTeam(remaining, now, predicate);
            if (best is null) return (null, null);

            if (CachedImageCheck.TryResolveVerified(_cacheRootPath, teamOf[best], best.Content.ImagePath,
                    best.Content.ImageHash, _bypassImageIntegrityCheck, out var imagePath, out var reason))
                return (best, imagePath);

            remaining.Remove(best);
            Reject(best, reason);
        }
    }

    private void Reject(PublishedAssignmentIndex item, string reason)
    {
        System.Diagnostics.Debug.WriteLine($"[Image] presentation {item.PresentationId}: ineligible — {reason}");
        if (_warned.Add(item.AssignmentId + "|" + item.PresentationId))
            _onRejected?.Invoke(item.PresentationId, reason);
    }

    /// <summary>
    /// Yields every assignment from each effective team's index.json that passes signature
    /// verification (SignatureGate.Verify → ShouldReject). Rejected/unreadable
    /// indexes are skipped. No active-window or display-type filtering is applied here.
    /// </summary>
    private IEnumerable<(string Team, PublishedAssignmentIndex Assignment)> EnumerateVerifiedAssignments(string[] teams)
    {
        // Effective teams = static (registry/appsettings) ∪ dynamic (Entra-resolved, read-only here;
        // NewsViewer never writes resolved-teams.json). Every team verifies the same way through
        // SignatureGate.Verify (KeyId selects the configured key, team binding).
        var dynamicTeams   = ResolvedTeamsReader.ReadDynamicTeamFolders(_cacheRootPath);
        var effectiveTeams = EffectiveTeams.Union(teams, dynamicTeams);

        foreach (var team in effectiveTeams)
        {
            var indexPath = Path.Combine(_cacheRootPath, team, "index.json");
            if (!File.Exists(indexPath)) continue;

            TeamIndexFile? index;
            try
            {
                var json = File.ReadAllText(indexPath);
                index = JsonSerializer.Deserialize<TeamIndexFile>(json, JsonDefaults.Options);
            }
            catch { continue; }

            if (index?.PublishedAssignments is null) continue;

            var keys          = SigningKeyConfigurationReader.GetPublicKeys(_configuration);
            var result        = SignatureGate.Verify(index, team, keys);
            var requireSigned = _configuration.GetValue<bool>("Signing:RequireSignedIndex");
            if (SignatureGate.ShouldReject(result, requireSigned, out var reason, index))
            {
                System.Diagnostics.Debug.WriteLine($"[Signing] {team}: index rejected — {reason}");
                continue;
            }
            System.Diagnostics.Debug.WriteLine($"[Signing] {team}: index accepted — {reason}");

            foreach (var a in index.PublishedAssignments)
                yield return (team, a);
        }
    }
}
