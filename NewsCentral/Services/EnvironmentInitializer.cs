using NewsCentral.Audit;
using System.Text.Json;
using System.Text.Json.Serialization;
using NewsCentral.Configuration;
using NewsCentral.Models;

namespace NewsCentral.Services;

/// <summary>Outcome of <see cref="EnvironmentInitializer.InitializeAsync"/>.</summary>
public enum InitializeOutcome
{
    /// <summary>The claim succeeded — <c>config/users.json</c> now exists with the new admin.</summary>
    Success,

    /// <summary>
    /// The claim itself (<c>config/users.json</c> via <see cref="FileMode.CreateNew"/>) lost a race
    /// to another process — nothing else was written.
    /// </summary>
    ClaimedByOther,

    /// <summary>A failure before or at the claim step — nothing was written.</summary>
    Failed
}

/// <summary>
/// Result of <see cref="EnvironmentInitializer.InitializeAsync"/>. <see cref="AdminUser"/> is set
/// only on <see cref="InitializeOutcome.Success"/>. <see cref="Warning"/> is set only on
/// <see cref="InitializeOutcome.Success"/> too — a non-fatal failure in one of the steps AFTER the
/// claim succeeded (environment.json, the directory add, or environments.json): the environment IS
/// genuinely initialized (its claim succeeded), but one of the niceties around it did not land.
/// </summary>
public sealed record InitializeResult(
    InitializeOutcome Outcome, string? ErrorMessage, User? AdminUser, string? Warning = null);

/// <summary>
/// M5b: creates a brand-new environment's first administrator and claims it, replacing the removed
/// admin/admin first-run seed. Driven by <c>EnvironmentSetupWizard.razor</c> from either the login
/// page (a Policy or Configured environment with no <c>config/users.json</c> yet) or Environment
/// Management's "Initialize an environment" section (any environment, while signed in to another
/// one already).
///
/// Deliberately uses plain <see cref="File"/>/<see cref="Directory"/> APIs on the EXPLICIT target
/// path throughout, never <see cref="IStorageService"/> — that abstraction resolves every relative
/// path against the CURRENT <see cref="EnvironmentContext.DataPath"/>, which is wrong here: the
/// environment being initialized is frequently a different one than the one this process is
/// currently pointed at (every Environment Management call, and the login-page call whenever the
/// current environment's own root folder does not exist yet).
/// </summary>
public sealed class EnvironmentInitializer
{
    private readonly EnvironmentDirectoryService _environmentDirectory;
    private readonly IAuditLog _audit;

    // Matches JsonFileRepository&lt;UsersCollection&gt;'s own options exactly (PascalCase, indented,
    // enum-as-string) — AuthenticationService/UserService read config/users.json through that
    // repository unchanged, so the file this writes must be byte-shape-compatible with it.
    private static readonly JsonSerializerOptions UsersJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public EnvironmentInitializer(EnvironmentDirectoryService environmentDirectory, IAuditLog? audit = null)
    {
        _environmentDirectory = environmentDirectory;
        _audit = audit ?? NullAuditLog.Instance;
    }

    /// <summary>
    /// Creates <paramref name="targetPath"/>'s first administrator and claims the environment.
    /// </summary>
    /// <param name="targetPath">The environment root to initialize — absolute, local or UNC.</param>
    /// <param name="createRootIfMissing">
    /// Login-page entry only: create <paramref name="targetPath"/> itself if it does not exist,
    /// provided its parent does. The Environment Management entry always passes false — "Initialize…"
    /// already required the folder to exist before this is ever called.
    /// </param>
    /// <param name="distributionSettings">
    /// The settings to write to <c>config/environment.json</c> — built by the wizard from its own
    /// Distribution section fields; this method stamps <see cref="EnvironmentSettings.ModifiedBy"/>/
    /// <see cref="EnvironmentSettings.ModifiedUtc"/> onto it before writing.
    /// </param>
    /// <param name="adminUsername">Already validated by <see cref="EnvironmentInitializationValidator"/>.</param>
    /// <param name="adminDisplayName">Already validated.</param>
    /// <param name="adminUpn">Already validated; null/empty means no UPN.</param>
    /// <param name="adminPassword">Already validated (length + confirmation match).</param>
    /// <param name="modifiedBy">
    /// What to stamp as <c>environment.json</c>'s <c>ModifiedBy</c> — the new admin's UPN or
    /// username for the login entry, or the CURRENT (already signed-in) administrator's identity
    /// for the Environment Management entry. The wizard computes which.
    /// </param>
    /// <param name="addToDirectory">
    /// Environment Management entry only: after the claim succeeds, add <paramref name="targetPath"/>
    /// to this machine's environment list (via <see cref="EnvironmentDirectoryService.AddAsync"/>,
    /// UNC → Shared / otherwise → User, per the M4 Add rules) if it is not already listed. The
    /// login entry always passes false — the current environment is, by definition, already how
    /// this process got here.
    /// </param>
    /// <param name="addDisplayName">
    /// Display name to pass to <see cref="EnvironmentDirectoryService.AddAsync"/> when
    /// <paramref name="addToDirectory"/> is true — the wizard's own Display name field.
    /// </param>
    public async Task<InitializeResult> InitializeAsync(
        string targetPath,
        bool createRootIfMissing,
        EnvironmentSettings distributionSettings,
        string adminUsername,
        string adminDisplayName,
        string? adminUpn,
        string adminPassword,
        string modifiedBy,
        bool addToDirectory,
        string? addDisplayName)
    {
        var result = await InitializeCoreAsync(targetPath, createRootIfMissing, distributionSettings, adminUsername,
            adminDisplayName, adminUpn, adminPassword, modifiedBy, addToDirectory, addDisplayName);

        // Only the new administrator's username is recorded - never the password.
        await _audit.RecordAsync(new AuditRecord(
            AuditEvents.EnvironmentInitialized, targetPath,
            result.Outcome == InitializeOutcome.Success ? AuditOutcome.Success : AuditOutcome.Failure,
            result.Outcome == InitializeOutcome.Success ? $"admin={adminUsername}" : result.Outcome.ToString()));
        return result;
    }

    private async Task<InitializeResult> InitializeCoreAsync(
        string targetPath,
        bool createRootIfMissing,
        EnvironmentSettings distributionSettings,
        string adminUsername,
        string adminDisplayName,
        string? adminUpn,
        string adminPassword,
        string modifiedBy,
        bool addToDirectory,
        string? addDisplayName)
    {
        try
        {
            if (createRootIfMissing && !Directory.Exists(targetPath))
            {
                var parent = Path.GetDirectoryName(
                    targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                    return new InitializeResult(InitializeOutcome.Failed,
                        "The parent folder does not exist.", AdminUser: null);

                Directory.CreateDirectory(targetPath);
            }

            if (!Directory.Exists(targetPath))
                return new InitializeResult(InitializeOutcome.Failed,
                    "That folder does not exist or is not accessible.", AdminUser: null);

            var configDir = Path.Combine(targetPath, "config");
            Directory.CreateDirectory(configDir);

            // ── Step 2 — claim: config/users.json via FileMode.CreateNew ──────────────────────
            // No code path may overwrite an existing users.json — CreateNew throws IOException if
            // the file already exists, which is exactly the signal a losing race needs.
            var adminUser = new User
            {
                UserID = Guid.NewGuid().ToString(),
                Username = adminUsername,
                DisplayName = adminDisplayName,
                Email = adminUpn ?? string.Empty,
                UPN = string.IsNullOrWhiteSpace(adminUpn) ? null : adminUpn,
                PasswordHash = await Task.Run(() => BCrypt.Net.BCrypt.HashPassword(adminPassword)),
                IsSystemAdmin = true,
                IsActive = true,
                DateCreated = DateTime.UtcNow,
                TeamRoles = new List<TeamRole>()
            };

            var usersCollection = new UsersCollection
            {
                Users = new List<User> { adminUser },
                Version = "1",
                LastModified = DateTime.UtcNow,
                ModifiedBy = "system"
            };

            var usersJson = JsonSerializer.Serialize(usersCollection, UsersJsonOptions);
            var usersPath = Path.Combine(configDir, "users.json");

            // M5c: distinguish "the file already exists" (a lost race — expected, not an error)
            // from every other I/O failure (network drop, disk full — a real failure that must
            // surface as such, not be misreported as someone else having claimed the environment).
            const int ErrorFileExists = unchecked((int)0x80070050);
            const int ErrorAlreadyExists = unchecked((int)0x800700B7);

            var createdByUs = false;
            try
            {
                using (var fs = new FileStream(usersPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // Reaching here means CreateNew itself succeeded — this call, and no other,
                    // owns usersPath from this point on, so the catch-all below may delete it.
                    createdByUs = true;
                    using var writer = new StreamWriter(fs, System.Text.Encoding.UTF8);
                    await writer.WriteAsync(usersJson);
                }
            }
            catch (IOException ex) when (ex.HResult == ErrorFileExists || ex.HResult == ErrorAlreadyExists)
            {
                // The expected case: the file already exists — someone else claimed this
                // environment between the caller's own checks and this call. Nothing was written.
                System.Diagnostics.Debug.WriteLine(
                    $"[EnvironmentInitializer] Claim lost — '{usersPath}' already exists (HResult 0x{ex.HResult:X8}).");
                return new InitializeResult(InitializeOutcome.ClaimedByOther,
                    "This environment was initialized by someone else in the meantime.", AdminUser: null);
            }
            catch (IOException ex) when (!createdByUs && File.Exists(usersPath))
            {
                // Safety net: some OTHER IOException (an unmapped HResult) was thrown before
                // CreateNew itself succeeded, yet the file is now there anyway — the only plausible
                // explanation is a competing CreateNew that won the race in between. Treated the
                // same as the expected case above; createdByUs is false here, so nothing of ours
                // is ever deleted.
                System.Diagnostics.Debug.WriteLine(
                    $"[EnvironmentInitializer] Claim lost (unmapped HResult 0x{ex.HResult:X8}) — '{usersPath}' exists.");
                return new InitializeResult(InitializeOutcome.ClaimedByOther,
                    "This environment was initialized by someone else in the meantime.", AdminUser: null);
            }
            catch (Exception ex)
            {
                // A genuine failure (network drop, disk full, …) — not a lost race. If THIS call
                // created the file (createdByUs), it may now hold a truncated write; delete it so
                // the environment is not left looking permanently (and silently) initialized with
                // no way to log in. Never deletes a users.json this call did not create.
                if (createdByUs)
                {
                    try
                    {
                        File.Delete(usersPath);
                    }
                    catch (Exception deleteEx)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[EnvironmentInitializer] Failed to delete the partial '{usersPath}' after a claim failure: {deleteEx.Message}");
                    }
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[EnvironmentInitializer] Claim failed for '{usersPath}': {ex.Message}");
                return new InitializeResult(InitializeOutcome.Failed,
                    $"Could not create config/users.json: {ex.Message}", AdminUser: null);
            }

            // ── Steps 3-5 — everything from here on is best-effort. The claim already succeeded,
            // so the environment IS genuinely initialized regardless of what happens next; a
            // failure here is reported as a warning, never as ClaimedByOther or Failed. A missing
            // environment.json simply falls back to the existing MachineDefaults ("no stored
            // settings") behavior on the next read.
            string? warning = null;
            try
            {
                distributionSettings.ModifiedBy = modifiedBy;
                distributionSettings.ModifiedUtc = DateTime.UtcNow;

                var envJson = JsonSerializer.Serialize(distributionSettings, EnvironmentSettingsJson.Options);
                var envPath = Path.Combine(configDir, "environment.json");
                var envTempPath = envPath + ".tmp";
                await File.WriteAllTextAsync(envTempPath, envJson);
                File.Move(envTempPath, envPath, overwrite: true);

                if (addToDirectory)
                {
                    var fingerprint = EnvironmentSettingsResolver.Fingerprint(distributionSettings, targetPath);
                    await _environmentDirectory.AddAsync(targetPath, addDisplayName, fingerprint);
                }

                // Step 5 — so the new environment immediately knows the shared directory (including
                // its own entry, just added above when addToDirectory) instead of starting from an
                // empty environments.json until its first SyncSharedDirectoryAsync.
                var sharedFile = new SharedDirectoryFile
                {
                    SchemaVersion = 1,
                    Entries = _environmentDirectory.SharedEntries.ToList()
                };
                var sharedJson = JsonSerializer.Serialize(sharedFile, SharedDirectoryJson.Options);
                await File.WriteAllTextAsync(Path.Combine(configDir, "environments.json"), sharedJson);
            }
            catch (Exception ex)
            {
                warning = ex.Message;
            }

            return new InitializeResult(InitializeOutcome.Success, ErrorMessage: null, adminUser, warning);
        }
        catch (Exception ex)
        {
            return new InitializeResult(InitializeOutcome.Failed, ex.Message, AdminUser: null);
        }
    }
}
