using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;
using NewsCentral.Launch;
using NewsViewer.Configuration;
using NewsViewer.Forms;
using NewsViewer.Services;

namespace NewsViewer;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
#if DEBUG
        // --reset-launch-state (Debug builds only): clears this user's daily-gate state, content marker
        // and launch log, then exits. For manual testing of the launch decision; runs before everything
        // else so it works regardless of configuration and of another running instance.
        if (args.Any(a => string.Equals(a, "--reset-launch-state", StringComparison.OrdinalIgnoreCase)))
        {
            var stateDir = UserStatePath();
            new ViewerStateService(stateDir, 0).Clear();
            new LaunchLog(stateDir).Delete();
            System.Diagnostics.Debug.WriteLine("[Launch] launch state reset (--reset-launch-state)");
            return;
        }
#endif

        // Single-instance guard, session-scoped. HKLM Run (logon) and the Workstation Unlock task can
        // double-launch on a fast lock/unlock cycle; two concurrent runs race on the HKCU wallpaper
        // write and on viewerstate.json (whose Write swallows exceptions), so a lost write silently
        // re-fires the poster next unlock. Local\ (not Global\) so fast user switching still gives each
        // session its own poster. createdNew — not WaitOne — avoids AbandonedMutexException if a prior
        // instance crashed holding it.
        using var mutex = new Mutex(initiallyOwned: true, "Local\\NewsCentral.NewsViewer", out bool createdNew);
        // createdNew is acted on below, after the Active guard, so the launch log can record it.

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        var configuration = BuildConfiguration();
        var config        = configuration.Get<ViewerConfiguration>() ?? new ViewerConfiguration();

        // Per-machine master switch (registry Active, DWORD 0/1). Evaluated first, ahead of every
        // other guard: the guards below all mean "there is work but conditions block it", whereas
        // Active = false means "there is no work". Exits silently — no poster, no wallpaper style
        // re-assert, no viewerstate write, no telemetry. The last-applied wallpaper style is
        // deliberately left in place; deactivation is not a revert. (The wallpaper image is
        // NewsService's concern and is unaffected either way.)
        if (!config.Active) return;

        // --manual (case-insensitive; unknown args ignored): an on-demand launch, e.g. from the Start menu
        // shortcut. It bypasses the daily gate and does not stamp it. Evaluated AFTER Active, so a
        // deactivated machine still exits silently.
        bool manual = args.Any(a => string.Equals(a, "--manual", StringComparison.OrdinalIgnoreCase));
        if (manual)
            System.Diagnostics.Debug.WriteLine("[Manual] manual launch (--manual): daily gate bypassed, gate marker not written");

#if DEBUG
        // --culture <name> (Debug builds only): force the UI culture for localization testing, in any
        // argument order relative to --manual. Set on the default AND the current thread (the Virtual
        // Desktop UI thread below inherits the default) before any Form is constructed. An invalid name
        // is ignored. CurrentCulture (dates/numbers) is deliberately untouched.
        int cultureIdx = Array.FindIndex(args, a => string.Equals(a, "--culture", StringComparison.OrdinalIgnoreCase));
        if (cultureIdx >= 0 && cultureIdx + 1 < args.Length)
        {
            try
            {
                var ci = System.Globalization.CultureInfo.GetCultureInfo(args[cultureIdx + 1]);
                System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = ci;
                System.Globalization.CultureInfo.CurrentUICulture = ci;
                System.Diagnostics.Debug.WriteLine($"[Culture] UI culture forced to {ci.Name} (--culture)");
            }
            catch (System.Globalization.CultureNotFoundException)
            {
                System.Diagnostics.Debug.WriteLine($"[Culture] invalid --culture value '{args[cultureIdx + 1]}' ignored");
            }
        }
        else if (cultureIdx >= 0)
            System.Diagnostics.Debug.WriteLine("[Culture] --culture given without a value; ignored");
#endif

        // Event-triggered launch (scheduled task "NewsViewer Content Arrived"): --trigger content. Unknown
        // --trigger values are ignored (treated as absent).
        int triggerIdx = Array.FindIndex(args, a => string.Equals(a, "--trigger", StringComparison.OrdinalIgnoreCase));
        bool contentTrigger = triggerIdx >= 0 && triggerIdx + 1 < args.Length &&
            string.Equals(args[triggerIdx + 1], "content", StringComparison.OrdinalIgnoreCase);
        string triggerName = contentTrigger ? "content" : "none";

        var userStatePath = UserStatePath();
        var launchLog = new LaunchLog(userStatePath);

        // Another instance is live in THIS session (the mutex is Local\, so each user session on a
        // multi-session host runs its own) — exit silently apart from the launch-log line.
        if (!createdNew)
        {
            launchLog.Append(triggerName, "Exit", "AlreadyRunning", null);
            return;
        }

        // UI theme — resolved ONCE here and consumed via Theme.Current (the same
        // resolve-once-then-pass pattern as duration resolution). Registry Ui\Theme
        // (REG_SZ "Dark" | "Light", surfaced as Ui:Theme) overrides the code default:
        // "light" (case-insensitive) selects Light; anything else — absent, "Dark",
        // or garbage — is Dark. Must be set before any Form is constructed.
        Theme.Current = string.Equals(configuration["Ui:Theme"], "Light", StringComparison.OrdinalIgnoreCase)
            ? Theme.Light
            : Theme.Dark;

        // Company is a build-time constant (SolutionConstants.Company), enforced at build time — it is
        // not read from config and cannot be empty here. Only CacheRootPath still needs validating.
        if (string.IsNullOrWhiteSpace(config.CacheRootPath))
        {
            MessageBox.Show(
                "NewsViewer configuration is incomplete.\n" +
                "CacheRootPath must be set in appsettings.json.",
                "NewsViewer — Configuration Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        // The effective team set is static (registry/appsettings) union dynamic (Entra-resolved,
        // read from resolved-teams.json inside PresentationSelector) — an empty static list here is
        // NOT an empty effective set, so there is deliberately no early return on it. A machine
        // subscribed only to dynamic teams would otherwise never show a poster and never reach the
        // wallpaper style re-assert below. Mirrors the equivalent fix on the service side
        // (NewsService/Worker.cs, commit f8cd54b).
        var teams = TeamConfigurationReader.GetTeams(configuration);

        // Remote/virtual sessions get neither the poster nor the wallpaper style re-assert. The
        // style step otherwise runs on any local interactive session — it is NOT gated behind a
        // qualifying monitor.
        if (IsRemoteOrVirtualSession()) return;

        var selector = new PresentationSelector(
            config.CacheRootPath, configuration, config.BypassImageIntegrityCheck);

        // Terminal step — re-asserted every run, unconditionally, regardless of whether any
        // wallpaper content is active. NewsService owns the wallpaper IMAGE machine-wide via
        // PersonalizationCSP; this only re-applies the per-user HKCU STYLE (Fill/Fit/Stretch/
        // Center/Tile + letterbox background), which CSP does not cover and NewsService's
        // session-0 context cannot reach. See WallpaperService for the verified CSP-enforces-
        // image / HKCU-controls-fit finding.
        void ApplyWallpaperStyle()
        {
            new WallpaperService(config.Delivery.WallpaperStyle, config.Delivery.WallpaperBackgroundColor)
                .ApplyWallpaperStyle();
            System.Diagnostics.Debug.WriteLine("[Wallpaper] style re-asserted");
        }

        // Resolve the active display (poster) assignment — may be absent.
        var (assignment, imagePath) = selector.SelectActive(teams);

        // A poster requires an active assignment, a verified image AND a Full-HD-or-better monitor.
        bool contentAvailable = assignment is not null && imagePath is not null && HasQualifyingMonitor();

        Directory.CreateDirectory(userStatePath);

        var hmac        = new HmacService(config.Hmac);        // telemetry only — unchanged
        var viewerState = new ViewerStateService(userStatePath, config.Display.LogicalDayStartHour);
        var telemetry   = new TelemetryWriter(config.CacheRootPath, hmac);

        // ── Launch decision (pure, ordered — see LaunchDecider / docs/newsviewer-spec.md) ──────────
        // Active and the mutex were handled above; they are re-passed so the decision is self-contained.
        var contentKey = assignment is null ? null : ContentKeys.For(assignment);
        var title      = assignment is null ? null
            : !string.IsNullOrWhiteSpace(assignment.PosterText) ? assignment.PosterText : assignment.PresentationName;
        bool activationRefused = false;   // set when SetForegroundWindow refused the expanded poster (logged)

#if DEBUG
        // --test-notify (Debug builds only): shows the "News arrived!" form for the currently selected item,
        // bypassing the launch decision. Writes NO marker and NO telemetry (an Expanded poster included);
        // logs decision=MiniForm reason=Test. Combinable with --culture.
        if (args.Any(a => string.Equals(a, "--test-notify", StringComparison.OrdinalIgnoreCase)))
        {
            if (assignment is null)
            {
                launchLog.Append(triggerName, "Exit", "NoContent", null);
                ApplyWallpaperStyle();
                return;
            }

            launchLog.Append(triggerName, "MiniForm", "Test", contentKey, title);
            var test = ShowNotification(null, ExpandedPosterFactory(assignment, imagePath ?? string.Empty, writeTelemetry: false, source: null), null);
            if (test?.Outcome == NotificationOutcome.Expanded)
                ShowExpandedFallback(test, assignment, imagePath ?? string.Empty, writeTelemetry: false, source: null);

            ApplyWallpaperStyle();
            GC.KeepAlive(mutex);
            return;
        }
#endif

        var decision = LaunchDecider.Decide(new LaunchInputs
        {
            Active           = config.Active,
            MutexAcquired    = createdNew,
            Manual           = manual,
            ContentTrigger   = contentTrigger,
            ContentAvailable = contentAvailable,
            ContentKey       = contentKey,
            Title            = title,
            GateHit          = viewerState.AlreadyShownToday(),
            BypassDailyGate  = config.BypassDailyGate,
            NotifyNewContent = config.Ui.NotifyNewContent,
            Marker           = viewerState.ReadContentMarker()
        }, new LaunchProbes());

        if (decision.WriteMarker && decision.ContentKey is not null)
            viewerState.WriteContentMarker(decision.ContentKey);

        if (decision.Action == LaunchAction.Exit)
        {
            launchLog.Append(triggerName, "Exit", decision.Reason, decision.ContentKey);
            // The wallpaper style is still the terminal step before exit.
            ApplyWallpaperStyle();
            return;
        }

        // Builds the standard poster form for `item`. Shared by the normal full-poster launch and the
        // notification's Expanded outcome, so both open exactly the same form.
        ViewerForm BuildViewer(PublishedAssignmentIndex item, string itemImage, bool recordShown, bool countdownStopped,
            bool writeTelemetry, string? source)
        {
            var isOnline = ReadOnlineStatus(config.CacheRootPath);

            // Optional delivered wallpaper / lock screen buttons (Ui:ShowDeliveredImages, default off). Null = the
            // form is built without them. The resolver is read-only and never throws; failures are only logged.
            var delivered = config.Ui.ShowDeliveredImages
                ? DeliveredImageResolver.CreateDefault(m => System.Diagnostics.Debug.WriteLine($"[DeliveredImages] {m}"))
                : null;

            // Resolve the raw duration to an effective value here (not in the view): -1 = never auto-close,
            // 0 / any other negative = unset -> 30s default, >0 = as-is. Single source of truth is
            // PresentationDefaults.
            int effectiveDuration = PresentationDefaults.ResolveDuration(item.DisplayDurationSeconds);

            return new ViewerForm(item, itemImage, telemetry, viewerState, isOnline, effectiveDuration,
                recordShown: recordShown, deliveredImages: delivered,
                startCountdownStopped: countdownStopped, writeTelemetry: writeTelemetry, source: source);
        }

        // Shows the standard poster form for `item` (on a Virtual Desktop when the assignment asks for one).
        // Blocks until the form is closed.
        void ShowPoster(PublishedAssignmentIndex item, string itemImage, bool recordShown, bool countdownStopped,
            bool writeTelemetry, string? source = null)
        {
            if (item.UseVirtualDesktop)
            {
                // Application.EnableVisualStyles() and other WinForms startup calls on the main
                // thread create hidden internal windows (the WinForms parking window, etc.).
                // SetThreadDesktop silently returns false once a thread owns any window handle,
                // so the forms end up on the original desktop instead of the new one.
                // Using a fresh STA thread guarantees no prior window handles exist when
                // SetThreadDesktop is called. (After a notification the main thread has already owned
                // windows, which is exactly why this path never reuses it.)
                var uiThread = new Thread(() =>
                {
                    using var desktop = new VirtualDesktopManager();
                    desktop.SwitchToNew();

                    var bgForm     = new BackgroundForm(item.VirtualDesktopBackgroundColor);
                    bgForm.Show();

                    var viewerForm = BuildViewer(item, itemImage, recordShown, countdownStopped, writeTelemetry, source);
                    viewerForm.FormClosed += (_, _) => { bgForm.Close(); desktop.SwitchToOriginal(); };
                    Application.Run(viewerForm);
                });
                uiThread.SetApartmentState(ApartmentState.STA);
                uiThread.Start();
                uiThread.Join();
            }
            else
            {
                Application.Run(BuildViewer(item, itemImage, recordShown, countdownStopped, writeTelemetry, source));
            }
        }

        // Expanded outcome, same-desktop poster: the form is created, shown and ACTIVATED from inside the
        // click handler (the click is input to this process, so the foreground rules allow it) while the
        // notification is still open - closing the notification first could let Windows hand activation to
        // another window. The caller then runs the message loop on the already-shown form. A poster that
        // wants a Virtual Desktop cannot be built on this thread, so it gets no factory and is opened by
        // ShowPoster after the notification closes (the desktop switch brings it to the front).
        Func<Form?>? ExpandedPosterFactory(PublishedAssignmentIndex item, string itemImage, bool writeTelemetry, string? source) =>
            item.UseVirtualDesktop
                ? null
                : () =>
                {
                    var poster = BuildViewer(item, itemImage, recordShown: false, countdownStopped: true, writeTelemetry, source);
                    poster.Show();
                    activationRefused = !ForegroundWindow.Activate(poster);
                    return poster;
                };

        // Falls back to the regular poster path when the Expanded poster was NOT already shown and run by the
        // notification itself (a Virtual Desktop poster, or a factory failure). The daily gate was consumed
        // earlier today, so it is not stamped again.
        void ShowExpandedFallback(NotificationForm notification, PublishedAssignmentIndex item, string itemImage,
            bool writeTelemetry, string? source)
        {
            if (notification.ExpandedForm is null)
                ShowPoster(item, itemImage, recordShown: false, countdownStopped: true, writeTelemetry, source);
        }

        // Shows the "News arrived!" form and blocks until it - and, after Expanded, the poster it opened - is
        // closed. One message loop serves both: ending the notification's own loop would dispose every window
        // on this thread, the freshly opened poster included, so the loop only ends when the last of the two
        // closes. `onShown` runs from the form's Shown handler (the form is on screen) - the only place the
        // content marker is written. When the notification closes (before the poster is run to completion) the
        // outcome line is appended and `onClosed` runs; a failure before the form is shown is logged and
        // writes no marker.
        NotificationForm? ShowNotification(Action? onShown, Func<Form?>? expandedPoster, Action<NotificationForm>? onClosed)
        {
            NotificationForm? form = null;
            try
            {
                var context = new ApplicationContext();   // no MainForm: the loop ends only via ExitThread
                form = new NotificationForm(title, onShown) { ExpandedFormFactory = expandedPoster };
                var shown = form;
                shown.FormClosed += (_, _) =>
                {
                    if (shown.ShownUtc is not null)
                    {
                        var outcome = shown.Outcome?.ToString() ?? "Closed";
                        if (shown.Outcome == NotificationOutcome.Expanded && activationRefused) outcome += " (activation refused)";
                        launchLog.Append(triggerName, "MiniForm", "Outcome", contentKey, outcome: outcome);
                        onClosed?.Invoke(shown);
                    }

                    if (shown.ExpandedForm is { } poster) poster.FormClosed += (_, _) => context.ExitThread();
                    else context.ExitThread();
                };
                shown.Show();
                Application.Run(context);
            }
            catch (Exception ex)
            {
                launchLog.Append(triggerName, "MiniForm", $"Error({ex.GetType().Name})", contentKey);
                form?.Dispose();
                return null;
            }

            return form;
        }

        if (decision.Action == LaunchAction.MiniForm)
        {
            launchLog.Append(triggerName, "MiniForm", decision.Reason, decision.ContentKey, decision.Title);

            // The marker is written once the form is actually on screen (Shown), not on the decision. One
            // record per shown notification (shown time, outcome, time to outcome) goes through the existing
            // session-file mechanism as soon as the notification closes, before a long-open poster runs.
            var notification = ShowNotification(
                () => viewerState.WriteContentMarker(decision.ContentKey!),
                ExpandedPosterFactory(assignment!, imagePath!, writeTelemetry: true, SessionTelemetry.NotificationSource),
                n =>
                {
                    if (n is { ShownUtc: { } shownUtc, OutcomeUtc: { } outcomeUtc, Outcome: { } outcome })
                        telemetry.WriteNotification(assignment!.PresentationId, assignment.SourceTeamFolderName,
                            decision.ContentKey!, shownUtc, outcomeUtc, outcome.ToString());
                });

            // Expanded: the standard poster for the SAME item the decision chose (no re-selection), countdown
            // already stopped, its display record marked source "notification".
            if (notification?.Outcome == NotificationOutcome.Expanded)
                ShowExpandedFallback(notification, assignment!, imagePath!, writeTelemetry: true, SessionTelemetry.NotificationSource);

            ApplyWallpaperStyle();
            GC.KeepAlive(mutex);
            return;
        }

        launchLog.Append(triggerName, "FullPoster", decision.Reason, decision.ContentKey);
        ShowPoster(assignment!, imagePath!, recordShown: !manual, countdownStopped: false, writeTelemetry: true);
        ApplyWallpaperStyle();

        // Keep the mutex rooted until the very end. `using var` guarantees disposal at method exit but
        // does NOT keep the object reachable — once the last read is behind us the GC may finalize it
        // and release the mutex while the VD thread and the wallpaper style write are still in flight,
        // which is exactly the window this guard exists to close.
        GC.KeepAlive(mutex);
    }

    // Per-user state folder: viewerstate.json (gate + content marker) and launch.log.
    private static string UserStatePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NewsCentral");

    private static IConfiguration BuildConfiguration()
    {
        var jsonPath    = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var devJsonPath = Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json");

        // Company is the build-time constant SolutionConstants.Company (defines the hive path, not
        // registry-overridable), so there is nothing to pre-read. Layering, registry always wins:
        //   appsettings.json (required) -> appsettings.Development.json (optional) -> registry.
        // The Development overlay is deliberately NOT gated on ASPNETCORE_ENVIRONMENT/DOTNET_ENVIRONMENT
        // (unset for a WinForms process launched from HKLM Run or a scheduled task) — its mere
        // presence activates it. It is gitignored and never published (see NewsViewer.csproj).
        return new ConfigurationBuilder()
            .AddJsonFile(jsonPath, optional: false)
            .AddJsonFile(devJsonPath, optional: true)
            .AddRegistryOverrides(SolutionConstants.Company, SolutionConstants.SolutionName, "NewsViewer")
            .Build();
    }

    private static bool HasQualifyingMonitor() =>
        Screen.AllScreens.Any(s => s.Bounds.Width >= 1920 && s.Bounds.Height >= 1080);

    // RDP and Citrix ICA both set SM_REMOTESESSION (TerminalServerSession).
    // VMware Horizon sets the ViewClient_Machine_Name environment variable.
    private static bool IsRemoteOrVirtualSession() =>
        SystemInformation.TerminalServerSession ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ViewClient_Machine_Name"));

    private static bool ReadOnlineStatus(string cacheRootPath)
    {
        try
        {
            var path = Path.Combine(cacheRootPath, "status.json");
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("isOnline", out var prop) && prop.GetBoolean();
        }
        catch { return false; }
    }
}
