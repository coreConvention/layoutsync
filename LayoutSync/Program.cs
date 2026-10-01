using System.CommandLine;
using System.CommandLine.Invocation;
using LayoutSync.Configuration;
using LayoutSync.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace LayoutSync;

/// <summary>
/// Layout Sync Tool - Watches layouts/ directory and syncs to RavenDB.
/// Enforces NanoID format for document IDs.
/// </summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Define command-line options
        Option<string> layoutsPathOption = new(
            aliases: ["--layouts-path", "-p"],
            description: "Path to layouts directory")
        { IsRequired = false };

        Option<string> ravenUrlOption = new(
            aliases: ["--raven-url", "-r"],
            description: "RavenDB URL")
        { IsRequired = false };

        Option<string> databaseOption = new(
            aliases: ["--database", "-d"],
            description: "Database name")
        { IsRequired = false };

        Option<string> certPathOption = new(
            aliases: ["--cert", "-c"],
            description: "Path to .pfx certificate for RavenDB Cloud authentication")
        { IsRequired = false };

        Option<string> certPasswordOption = new(
            aliases: ["--cert-password"],
            description: "Password for the .pfx certificate (if password-protected)")
        { IsRequired = false };

        Option<string> layoutOption = new(
            aliases: ["--layout", "-l"],
            description: "Specific layout to watch (default: all)")
        { IsRequired = false };

        Option<string[]> excludeCollectionOption = new(
            aliases: ["--exclude-collection"],
            description: "Collection folder to exclude from discovery, sync, and orphan detection (repeatable: --exclude-collection entities --exclude-collection identities). Validated against the known collection folders; a typo exits with code 1 and a suggestion. Sync filter only — does not protect the files from other tooling. Intended for CI. See issue #9.")
        { IsRequired = false, Arity = ArgumentArity.ZeroOrMore };

        Option<string[]> excludeLayoutOption = new(
            aliases: ["--exclude-layout"],
            description: "Layout directory to exclude from discovery, sync, and orphan deletion (repeatable), e.g. a test-fixture layout that must never reach a shared DB. Validated against the layout directories on disk; a typo exits with code 1 and a suggestion. Intended for CI. See issue #9.")
        { IsRequired = false, Arity = ArgumentArity.ZeroOrMore };

        Option<bool> syncOnceOption = new(
            aliases: ["--sync-once"],
            description: "Sync once and exit (no watch mode). Exits with code 5 if any document failed to sync (a file could not be read or parsed, or writing its document failed), with or without --strict; 5 takes precedence over --strict's code 2. See issue #36.")
        { IsRequired = false };

        Option<bool> validateOnlyOption = new(
            aliases: ["--validate-only"],
            description: "Validate only - report issues without changes")
        { IsRequired = false };

        Option<bool> fixIdsOption = new(
            aliases: ["--fix-ids"],
            description: "Fix human-readable IDs in local files")
        { IsRequired = false };

        Option<bool> dryRunOption = new(
            aliases: ["--dry-run"],
            description: "Show what would happen without making changes")
        { IsRequired = false };

        Option<bool> cleanOption = new(
            aliases: ["--clean"],
            description: "Delete orphaned documents from static collections (Sections, Layouts, Menus, Modals)")
        { IsRequired = false };

        Option<bool> verboseOption = new(
            aliases: ["--verbose", "-v"],
            description: "Enable verbose logging")
        { IsRequired = false };

        Option<bool> preserveIdsOption = new(
            aliases: ["--preserve-ids"],
            description: "Preserve IDs from local files when creating documents (uses @metadata.@id for identities). Applies at creation only: a document that already exists keeps its id, and a file whose @metadata.@id differs from it is reported as a pinned id mismatch (see --strict).")
        { IsRequired = false };

        Option<bool> strictOption = new(
            aliases: ["--strict"],
            description: "Exit non-zero (code 2) if the sync flagged anything: duplicate identifiers, document collisions (files that resolve to the same stored document; those files are never synced, with or without --strict), pinned id mismatches (a file whose @metadata.@id is not the id its document is already stored under; the document keeps its stored id), or seed-validator warnings (e.g. raw-NanoID authorship). Detection only — nothing is auto-deleted. Intended for CI.")
        { IsRequired = false };

        Option<bool> allowRemoteSyncOption = new(
            aliases: ["--allow-remote-sync"],
            description: "Explicit opt-in to sync against a non-localhost RavenDB target. Without this flag, LayoutSync refuses to write to Remote or Unknown targets and exits with code 3.")
        { IsRequired = false };

        Option<bool> allowCrossWorktreeSyncOption = new(
            aliases: ["--allow-cross-worktree-sync"],
            description: "Explicit opt-in to sync against a layouts directory OUTSIDE the current worktree. Without this flag, LayoutSync refuses cross-worktree syncs and exits with code 4. See issue #520.")
        { IsRequired = false };

        Option<int?> debounceMsOption = new(
            aliases: ["--debounce-ms"],
            description: "Override debounce delay (ms) between file events and sync. Default 100. Set to 0 (or use --no-debounce) for fastest cadence; safe because batches are serialized.")
        { IsRequired = false };

        Option<bool> noDebounceOption = new(
            aliases: ["--no-debounce"],
            description: "Equivalent to --debounce-ms 0. Fires sync on the next thread-pool tick after a file event.")
        { IsRequired = false };

        // Create root command
        RootCommand rootCommand = new("Layout Sync Tool - Syncs layouts/ to RavenDB with NanoID enforcement")
        {
            layoutsPathOption,
            ravenUrlOption,
            databaseOption,
            certPathOption,
            certPasswordOption,
            layoutOption,
            excludeCollectionOption,
            excludeLayoutOption,
            syncOnceOption,
            validateOnlyOption,
            fixIdsOption,
            dryRunOption,
            cleanOption,
            verboseOption,
            preserveIdsOption,
            strictOption,
            allowRemoteSyncOption,
            allowCrossWorktreeSyncOption,
            debounceMsOption,
            noDebounceOption
        };

        rootCommand.SetHandler(
            async (context) =>
            {
                context.ExitCode = await RunAsync(new CommandLineArgs
                {
                    LayoutsPath = context.ParseResult.GetValueForOption(layoutsPathOption),
                    RavenUrl = context.ParseResult.GetValueForOption(ravenUrlOption),
                    Database = context.ParseResult.GetValueForOption(databaseOption),
                    CertificatePath = context.ParseResult.GetValueForOption(certPathOption),
                    CertificatePassword = context.ParseResult.GetValueForOption(certPasswordOption),
                    Layout = context.ParseResult.GetValueForOption(layoutOption),
                    // `?? []`: GetValueForOption returns null (not an empty array) when a
                    // ZeroOrMore option is absent — the POCO's `= []` initializer cannot
                    // save us because this explicit assignment overrides it.
                    ExcludeCollections = context.ParseResult.GetValueForOption(excludeCollectionOption) ?? [],
                    ExcludeLayouts = context.ParseResult.GetValueForOption(excludeLayoutOption) ?? [],
                    SyncOnce = context.ParseResult.GetValueForOption(syncOnceOption),
                    ValidateOnly = context.ParseResult.GetValueForOption(validateOnlyOption),
                    FixIds = context.ParseResult.GetValueForOption(fixIdsOption),
                    DryRun = context.ParseResult.GetValueForOption(dryRunOption),
                    Clean = context.ParseResult.GetValueForOption(cleanOption),
                    Verbose = context.ParseResult.GetValueForOption(verboseOption),
                    PreserveIds = context.ParseResult.GetValueForOption(preserveIdsOption),
                    Strict = context.ParseResult.GetValueForOption(strictOption),
                    AllowRemoteSync = context.ParseResult.GetValueForOption(allowRemoteSyncOption),
                    AllowCrossWorktreeSync = context.ParseResult.GetValueForOption(allowCrossWorktreeSyncOption),
                    DebounceMs = context.ParseResult.GetValueForOption(debounceMsOption),
                    NoDebounce = context.ParseResult.GetValueForOption(noDebounceOption)
                });
            });

        // Subcommand: `layoutsync manifest set-route ...` and `layoutsync manifest from-json ...`.
        // These have their own option set (--json, --on-error, etc.) and a minimal DI host
        // that omits RavenDB — manifest mutation is a file-only operation.
        rootCommand.AddCommand(ManifestCommands.Build());

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task<int> RunAsync(CommandLineArgs args)
    {
        // Build configuration
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        // Configure Serilog
        string logLevel = args.Verbose ? "Debug" : "Information";
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(Enum.Parse<Serilog.Events.LogEventLevel>(logLevel))
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            Log.Information("Layout Sync Tool v1.0");

            // Scoped clean: --clean + --layout X is now SAFE because orphan detection filters
            // candidates by the layoutId each document is stored with (stamped, or declared by its
            // file — issue #31). Documents stored WITHOUT a layoutId are conservatively skipped
            // from scoped runs — operators must run unscoped clean if they truly need to prune
            // those. See issue #427.
            //
            // The original rejection (issue #235) was the safe-by-default response to the
            // pre-filter implementation, which would have deleted *every* non-scoped document
            // as an "orphan" relative to the scoped sync set. With the layoutId filter in
            // DocumentSyncService.FilterOrphansForScope, that class of cross-tenant data loss
            // can no longer occur from this combo.
            if (args.Clean && !string.IsNullOrEmpty(args.Layout))
            {
                Log.Information(
                    "Scoped clean active: orphan deletion filtered to layoutId='{Layout}'. Documents stored without a layoutId will be skipped — run unscoped --clean to prune those.",
                    args.Layout);
            }

            // Build host
            IHost host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureServices((context, services) =>
                {
                    // Bind configuration
                    SyncOptions syncOptions = new();
                    RavenDbOptions ravenOptions = new();
                    configuration.GetSection("SyncOptions").Bind(syncOptions);
                    configuration.GetSection("RavenDb").Bind(ravenOptions);

                    // Override with command-line args
                    if (!string.IsNullOrEmpty(args.LayoutsPath))
                        syncOptions.LayoutsPath = args.LayoutsPath;
                    if (!string.IsNullOrEmpty(args.RavenUrl))
                        ravenOptions.Url = args.RavenUrl;
                    if (!string.IsNullOrEmpty(args.Database))
                        ravenOptions.Database = args.Database;
                    if (!string.IsNullOrEmpty(args.CertificatePath))
                        ravenOptions.CertificatePath = args.CertificatePath;
                    if (!string.IsNullOrEmpty(args.CertificatePassword))
                        ravenOptions.CertificatePassword = args.CertificatePassword;

                    // Debounce overrides: --no-debounce wins, then --debounce-ms, else
                    // whatever appsettings/default supplied. 0 is a valid explicit value
                    // (FileWatcherService treats it as "next thread-pool tick").
                    if (args.NoDebounce)
                        syncOptions.DebounceMs = 0;
                    else if (args.DebounceMs.HasValue)
                        syncOptions.DebounceMs = args.DebounceMs.Value;

                    services.AddSingleton(syncOptions);
                    services.AddSingleton(ravenOptions);
                    services.AddSingleton(args);

                    // Register services
                    services.AddSingleton<ProductionTargetGuard>();
                    services.AddSingleton<WorktreePathGuard>();
                    services.AddSingleton<RavenDbService>();
                    services.AddSingleton<LocalFileService>();
                    services.AddSingleton<RelativeDateResolver>();
                    // Seed validators are registered via ISeedValidator and injected into
                    // DocumentSyncService as IEnumerable<ISeedValidator>. Registration order is
                    // iteration order; the current three are order-independent. Adding a validator
                    // is a single line here + one new class — nothing else changes. See issue #7.
                    services.AddSingleton<ISeedValidator, SeedAuthorshipValidator>();
                    services.AddSingleton<ISeedValidator, SeedCrossReferenceValidator>();
                    services.AddSingleton<ISeedValidator, DeadWidgetPropValidator>();
                    services.AddSingleton<DocumentSyncService>();
                    services.AddSingleton<FileWatcherService>();
                })
                .Build();

            // Resolve and run the sync service
            SyncOptions options = host.Services.GetRequiredService<SyncOptions>();
            RavenDbOptions ravenOpts = host.Services.GetRequiredService<RavenDbOptions>();

            // Auto-walk-up: if neither --layouts-path nor SyncOptions:LayoutsPath provided
            // a value, try to resolve a `layouts/` directory by walking up from CWD. This
            // makes the common case (run LayoutSync from inside a worktree) implicitly
            // correct — see issue #520. Failure to find one falls through to the existing
            // "Layouts path is required" error below.
            if (string.IsNullOrEmpty(options.LayoutsPath))
            {
                string? autoResolved = LayoutsPathResolver.Resolve(Directory.GetCurrentDirectory());
                if (autoResolved != null)
                {
                    options.LayoutsPath = autoResolved;
                    Log.Information(
                        "Auto-resolved --layouts-path from CWD walk-up: {Path}",
                        autoResolved);
                }
            }

            // Validate layouts path is provided
            if (string.IsNullOrEmpty(options.LayoutsPath))
            {
                Log.Error("Layouts path is required. Use --layouts-path or set SyncOptions:LayoutsPath in config (or run from a directory whose ancestors contain a `layouts/` folder for auto-resolution).");
                return 1;
            }

            // Resolve layouts path (supports relative or absolute)
            string layoutsPath = Path.IsPathRooted(options.LayoutsPath)
                ? options.LayoutsPath
                : Path.GetFullPath(options.LayoutsPath, Directory.GetCurrentDirectory());

            if (!Directory.Exists(layoutsPath))
            {
                Log.Error("Layouts directory not found: {Path}", layoutsPath);
                return 1;
            }

            Log.Information("Layouts: {Path}", layoutsPath);
            Log.Information("RavenDB: {Url}/{Database}", ravenOpts.Url, ravenOpts.Database);

            // --exclude-collection / --exclude-layout gate — issue #9. Validate BEFORE the
            // guards and before any RavenDB service is resolved: a typo'd exclusion would
            // otherwise silently sync the very data the flag was meant to protect, so it
            // must die here with exit 1 and a "did you mean" hint.
            IReadOnlyList<string> exclusionErrors = ExclusionValidator.Validate(
                layoutsPath, args.ExcludeCollections, args.ExcludeLayouts, args.Layout);
            if (exclusionErrors.Count > 0)
            {
                foreach (string error in exclusionErrors)
                    Log.Error("{ExclusionError}", error);
                return 1;
            }
            if (args.ExcludeCollections.Length > 0)
                Log.Information("Excluding collection(s): {Collections}", string.Join(", ", args.ExcludeCollections));
            if (args.ExcludeLayouts.Length > 0)
            {
                Log.Information("Excluding layout(s): {Layouts}", string.Join(", ", args.ExcludeLayouts));
                if (!string.IsNullOrEmpty(args.Layout))
                    Log.Information(
                        "--layout '{Layout}' already scopes this run to a single layout; --exclude-layout adds nothing beyond orphan-deletion gating here.",
                        args.Layout);
            }

            // Worktree-mismatch guard — issue #520. When CWD is inside a git worktree
            // (found by asking git, wherever it lives) but the resolved layouts-path is
            // OUTSIDE that worktree, refuse the run. This catches the silent-failure
            // pattern where an explicit --layouts-path pointed at the main repo while
            // the operator was editing files in a worktree, causing LayoutSync to sync
            // stale main content as if it were the worktree edits. Runs BEFORE the
            // production-target guard so the more proximate "your local edits aren't
            // what's about to ship" error fires first.
            WorktreePathGuard worktreeGuard =
                host.Services.GetRequiredService<WorktreePathGuard>();
            if (!worktreeGuard.Authorize(
                    currentDirectory: Directory.GetCurrentDirectory(),
                    layoutsPath: layoutsPath,
                    allowCrossWorktreeSync: args.AllowCrossWorktreeSync))
            {
                return 4;
            }

            // Production-target guard — classify the RavenDB URL and refuse Remote/Unknown
            // targets unless the operator explicitly passed --allow-remote-sync. Applies to
            // every mode (sync-once, watch, validate-only, fix-ids, dry-run) because any of
            // them can leak host/credentials into logs against a non-localhost target, and
            // the write-mode paths can overwrite real user data.
            ProductionTargetGuard targetGuard =
                host.Services.GetRequiredService<ProductionTargetGuard>();
            bool allowed = targetGuard.Authorize(
                url: ravenOpts.Url,
                allowRemoteSync: args.AllowRemoteSync,
                dryRun: args.DryRun,
                layout: args.Layout,
                preserveIds: args.PreserveIds,
                strict: args.Strict,
                clean: args.Clean);
            if (!allowed)
            {
                return 3;
            }

            // Get sync service
            DocumentSyncService syncService = host.Services.GetRequiredService<DocumentSyncService>();

            // The batch a --sync-once run produced, kept for the failed-documents check below.
            // Stays null in every other mode: watch mode runs until Ctrl+C and logs each failure
            // as it happens, and nothing reads its exit code. See issue #36.
            LayoutSync.Models.SyncBatchResult? syncOnceResult = null;

            if (args.ValidateOnly)
            {
                Log.Information("Validation mode - checking for issues...");
                await syncService.ValidateAsync(layoutsPath, args.Layout);
            }
            else if (args.FixIds)
            {
                Log.Information("Fix IDs mode - generating NanoIDs for human-readable IDs...");
                await syncService.FixIdsAsync(layoutsPath, args.Layout, args.DryRun);
            }
            else if (args.SyncOnce)
            {
                Log.Information("Sync once mode - syncing all files...");
                if (args.Clean)
                    Log.Information("Clean mode - orphaned documents will be deleted from static collections");
                syncOnceResult = await syncService.SyncAllAsync(layoutsPath, args.Layout, args.DryRun, args.Clean);
            }
            else
            {
                // Watch mode
                Log.Information("Initial sync...");
                if (args.Clean)
                    Log.Information("Clean mode - orphaned documents will be deleted from static collections");

                LayoutSync.Models.SyncBatchResult initialResult = await syncService.SyncAllAsync(
                    layoutsPath, args.Layout, args.DryRun, args.Clean);

                Log.Information("Watching for changes... (Ctrl+C to stop)");
                Log.Information("File deletions will be synced to RavenDB");

                FileWatcherService watcher = host.Services.GetRequiredService<FileWatcherService>();
                using CancellationTokenSource cts = new();
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                await watcher.WatchAsync(layoutsPath, args.Layout, args.DryRun, initialResult, cts.Token);
            }

            // Completion banner for Remote targets — mirrors the startup banner so the end
            // of the log stream also surfaces the non-localhost target. No-op for Local.
            targetGuard.EmitCompletionBanner(ravenOpts.Url, args.DryRun);

            // --strict: fail the run with exit code 2 if anything detection-only was flagged during
            // sync. All of the following contribute to the same gate (StrictModeGate):
            //   • duplicate entity identifiers (RavenDbService)
            //   • document collisions — files resolving to one stored document (DocumentSyncService, #28)
            //   • pinned id mismatches — a file's @metadata.@id is not its stored document's id (DocumentSyncService, #46)
            //   • raw-NanoID authorship warnings (SeedAuthorshipValidator, #308)
            //   • dangling / unpinned-target cross-references (SeedCrossReferenceValidator, #300)
            //   • dead/no-op widget props on sections (DeadWidgetPropValidator, #984)
            // Detection-only: none of these mutate state. Strict mode is the CI escalation hook.
            int exitCode = 0;
            if (args.Strict)
            {
                // Same singleton instances DocumentSyncService was injected with, so their counters
                // reflect the sync that just ran.
                RavenDbService ravenService = host.Services.GetRequiredService<RavenDbService>();
                IReadOnlyList<string> offenses = StrictModeGate.Offenses(
                    ravenService.DuplicateEntityIdentifierCount,
                    syncService.DocumentCollisionCount,
                    syncService.PinnedIdMismatchCount,
                    host.Services.GetServices<ISeedValidator>());

                foreach (string offense in offenses)
                    Log.Error("{StrictOffense}", offense);

                exitCode = StrictModeGate.ExitCodeFor(offenses);
            }

            // Failed documents: a --sync-once run (dry runs included) that could not read a file or
            // write its document exits 5, with or without --strict. Checked after the strict gate so
            // its offenses are still logged; 5 wins over 2 because CI reads 2 as "every write
            // succeeded". See issue #36.
            if (syncOnceResult is not null)
            {
                string? failure = SyncFailureGate.Describe(syncOnceResult);
                if (failure is not null)
                    Log.Error("{SyncFailure}", failure);

                exitCode = SyncFailureGate.ExitCodeFor(syncOnceResult.FailedCount, exitCode);
            }

            return exitCode;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}

