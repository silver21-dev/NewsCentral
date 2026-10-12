using System.Security.Cryptography;
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

    public PresentationSelector(
        string cacheRootPath,
        IConfiguration configuration,
        bool bypassImageIntegrityCheck = false)
    {
        _cacheRootPath             = cacheRootPath;
        _configuration             = configuration;
        _bypassImageIntegrityCheck = bypassImageIntegrityCheck;
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
    /// team set, pick the newest active one matching <paramref name="predicate"/>, then resolve and
    /// integrity-check its image. Image verification failure returns (best, null) so the caller can
    /// decline to act on unverified content.
    /// </summary>
    private (PublishedAssignmentIndex? Assignment, string? ImagePath) SelectActiveMatching(
        string[] teams, Func<PublishedAssignmentIndex, bool> predicate)
    {
        var now  = DateTime.Now;
        var best = ActiveAssignmentSelector.PickNewestToTeam(
            EnumerateVerifiedAssignments(teams), now, predicate);

        if (best is null) return (null, null);

        var imagePath = Path.Combine(
            _cacheRootPath,
            best.Content.ImagePath.Replace('/', Path.DirectorySeparatorChar));

        if (!_bypassImageIntegrityCheck && !VerifyImageHash(imagePath, best.Content.ImageHash))
            return (best, null);

        return (best, imagePath);
    }

    /// <summary>
    /// Yields every assignment from each effective team's index.json that passes signature
    /// verification (SignatureGate.VerifyWithPrecedence → ShouldReject). Rejected/unreadable
    /// indexes are skipped. No active-window or display-type filtering is applied here.
    /// </summary>
    private IEnumerable<PublishedAssignmentIndex> EnumerateVerifiedAssignments(string[] teams)
    {
        // Effective teams = static (registry/appsettings) ∪ dynamic (Entra-resolved, read-only here;
        // NewsViewer never writes resolved-teams.json). Per-team verification routes through
        // SignatureGate.VerifyWithPrecedence so registry-wins / delivered-key / anti-downgrade are
        // handled centrally — no precedence branching at this call site.
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

            var keys          = SigningKeyConfigurationReader.GetPublicKeys(_configuration, team);
            var result        = SignatureGate.VerifyWithPrecedence(index, keys, dynamicTeams.Contains(team));
            var requireSigned = _configuration.GetValue<bool>("Signing:RequireSignedIndex");
            if (SignatureGate.ShouldReject(result, requireSigned, out var reason))
            {
                System.Diagnostics.Debug.WriteLine($"[Signing] {team}: index rejected — {reason}");
                continue;
            }
            System.Diagnostics.Debug.WriteLine($"[Signing] {team}: index accepted — {reason}");

            foreach (var a in index.PublishedAssignments)
                yield return a;
        }
    }

    private static bool VerifyImageHash(string imagePath, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ImageHash] {Path.GetFileName(imagePath)}: no hash in index — skipping integrity check");
            return true;
        }

        if (!File.Exists(imagePath))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ImageHash] {Path.GetFileName(imagePath)}: file not found");
            return false;
        }

        try
        {
            string prefix   = "sha256:";
            string expected = storedHash.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? storedHash[prefix.Length..]
                : storedHash;

            using var sha256    = SHA256.Create();
            using var stream    = File.OpenRead(imagePath);
            byte[] hashBytes    = sha256.ComputeHash(stream);
            string actual       = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ImageHash] {Path.GetFileName(imagePath)}: hash mismatch — image rejected");
                return false;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[ImageHash] {Path.GetFileName(imagePath)}: hash OK");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ImageHash] {Path.GetFileName(imagePath)}: error during verification — {ex.Message}");
            return false;
        }
    }
}
