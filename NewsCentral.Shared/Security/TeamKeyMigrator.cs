namespace NewsCentral.Security;

public enum KeyMigrationStatus
{
    /// <summary>There is no key file at the old DataPath location.</summary>
    NothingToMigrate,

    /// <summary>The key is at the new location, verified byte-for-byte, and the old file is deleted.</summary>
    Migrated,

    /// <summary>Something failed; the old file is untouched and remains in use for this session.</summary>
    Failed,

    /// <summary>Both locations hold a key file and they differ. Nothing was overwritten or deleted.</summary>
    Conflict
}

public sealed record KeyMigrationResult(string Team, KeyMigrationStatus Status, string? Detail, string? Fingerprint);

/// <summary>
/// Moves <c>{DataPath}\{team}\team-signing.json</c> into the secure store. The old file is deleted only after the
/// new copy has been read back, parsed and compared byte-for-byte with the source - never before. Every failure
/// leaves the old file in place. The old file is exposed (anyone with DataPath access could copy it), which is why
/// the docs recommend rotating every team key after migrating.
/// </summary>
public static class TeamKeyMigrator
{
    public static string LegacyPath(string dataPath, string team) =>
        Path.Combine(dataPath, team, TeamKeyStore.FileName);

    public static async Task<KeyMigrationResult> MigrateAsync(string dataPath, string team, ISecureStorage storage)
    {
        if (!TeamKeyStore.IsSafeTeamFolderName(team))
            return new(team, KeyMigrationStatus.Failed, "invalid team folder name", null);

        var oldPath = LegacyPath(dataPath, team);
        if (!File.Exists(oldPath))
            return new(team, KeyMigrationStatus.NothingToMigrate, null, null);

        byte[] source;
        try
        {
            source = await File.ReadAllBytesAsync(oldPath);
        }
        catch (Exception ex)
        {
            return new(team, KeyMigrationStatus.Failed, $"old key file could not be read ({TeamKeyStore.Describe(ex)})", null);
        }

        var parsedSource = TeamKeyStore.Parse(source);
        var fingerprint = TeamKeyStore.Fingerprint(parsedSource.Keys);
        var relative = TeamKeyStore.RelativePath(team);

        byte[]? existing;
        try
        {
            existing = await storage.ReadBytesAsync(relative);
        }
        catch (Exception ex)
        {
            return new(team, KeyMigrationStatus.Failed, $"secure store not readable ({TeamKeyStore.Describe(ex)})", fingerprint);
        }

        if (existing is not null)
        {
            // Identical copy already in place (an earlier run died before deleting the old file): finish the job.
            if (existing.AsSpan().SequenceEqual(source))
                return TryDeleteOld(team, oldPath, fingerprint);

            return new(team, KeyMigrationStatus.Conflict,
                "a different key file exists in both the old and the secure location; the secure one is used and the old one was left in place",
                fingerprint);
        }

        // The source must itself be a valid key file; copying garbage into the secure store would only hide it.
        if (parsedSource.Outcome != KeyLoadOutcome.Loaded)
            return new(team, KeyMigrationStatus.Failed, $"old key file is not valid ({parsedSource.Reason})", fingerprint);

        try
        {
            await storage.WriteBytesAsync(relative, source);
        }
        catch (Exception ex)
        {
            return new(team, KeyMigrationStatus.Failed, $"could not write to the secure store ({TeamKeyStore.Describe(ex)})", fingerprint);
        }

        string? mismatch = null;
        try
        {
            var copy = await storage.ReadBytesAsync(relative);
            if (copy is null)
                mismatch = "the copy could not be found after writing";
            else if (TeamKeyStore.Parse(copy).Outcome != KeyLoadOutcome.Loaded)
                mismatch = "the copy could not be parsed";
            else if (!copy.AsSpan().SequenceEqual(source))
                mismatch = "the copy differs from the source";
        }
        catch (Exception ex)
        {
            mismatch = $"the copy could not be read back ({TeamKeyStore.Describe(ex)})";
        }

        if (mismatch is not null)
        {
            // Do not leave a bad copy where the next load would pick it up.
            try { await storage.DeleteAsync(relative); } catch { /* best effort */ }
            return new(team, KeyMigrationStatus.Failed, $"verification failed: {mismatch}; old file kept", fingerprint);
        }

        return TryDeleteOld(team, oldPath, fingerprint);
    }

    private static KeyMigrationResult TryDeleteOld(string team, string oldPath, string? fingerprint)
    {
        try
        {
            File.Delete(oldPath);
            return new(team, KeyMigrationStatus.Migrated, null, fingerprint);
        }
        catch (Exception ex)
        {
            return new(team, KeyMigrationStatus.Failed,
                $"copied and verified, but the old file could not be deleted ({TeamKeyStore.Describe(ex)})", fingerprint);
        }
    }
}
