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

        Option<bool> syncOnceOption = new(
            aliases: ["--sync-once"],
            description: "Sync once and exit (no watch mode)")
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
            description: "Preserve IDs from local files when creating documents (uses @metadata.@id for identities)")
        { IsRequired = false };

        Option<bool> strictOption = new(
            aliases: ["--strict"],
            description: "Exit non-zero (code 2) if any validator emits an offense during sync (duplicate entity identifiers, raw-NanoID authorship warnings). Detection only — nothing is auto-deleted. Intended for CI.")
        { IsRequired = false };

        Option<bool> allowRemoteSyncOption = new(
            aliases: ["--allow-remote-sync"],
            description: "Explicit opt-in to sync against a non-localhost RavenDB target. Without this flag, LayoutSync refuses to write to Remote or Unknown targets and exits with code 3.")
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
            syncOnceOption,
            validateOnlyOption,
            fixIdsOption,
            dryRunOption,
            cleanOption,
            verboseOption,
            preserveIdsOption,
            strictOption,
            allowRemoteSyncOption
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
                    SyncOnce = context.ParseResult.GetValueForOption(syncOnceOption),
                    ValidateOnly = context.ParseResult.GetValueForOption(validateOnlyOption),
                    FixIds = context.ParseResult.GetValueForOption(fixIdsOption),
                    DryRun = context.ParseResult.GetValueForOption(dryRunOption),
                    Clean = context.ParseResult.GetValueForOption(cleanOption),
                    Verbose = context.ParseResult.GetValueForOption(verboseOption),
                    PreserveIds = context.ParseResult.GetValueForOption(preserveIdsOption),
                    Strict = context.ParseResult.GetValueForOption(strictOption),
                    AllowRemoteSync = context.ParseResult.GetValueForOption(allowRemoteSyncOption)
                });
            });

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

            // Guard: --clean cannot be combined with --layout.
            // Orphan detection queries each static collection globally without a layoutId
            // filter, so combining a scoped sync with cleanup flags all non-scoped docs
            // as orphans and deletes them. Refuse the combination to prevent destructive
            // cross-layout deletions. See issue #235.
            if (args.Clean && !string.IsNullOrEmpty(args.Layout))
            {
                Log.Error("--clean cannot be combined with --layout. Clean only runs unscoped.");
                Log.Error("Either sync all layouts with --clean, or sync a single layout without --clean.");
                return 1;
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

                    services.AddSingleton(syncOptions);
                    services.AddSingleton(ravenOptions);
                    services.AddSingleton(args);

                    // Register services
                    services.AddSingleton<ProductionTargetGuard>();
                    services.AddSingleton<RavenDbService>();
                    services.AddSingleton<LocalFileService>();
                    services.AddSingleton<RelativeDateResolver>();
                    services.AddSingleton<SeedAuthorshipValidator>();
                    services.AddSingleton<SeedCrossReferenceValidator>();
                    services.AddSingleton<DocumentSyncService>();
                    services.AddSingleton<FileWatcherService>();
                })
                .Build();

            // Resolve and run the sync service
            SyncOptions options = host.Services.GetRequiredService<SyncOptions>();
            RavenDbOptions ravenOpts = host.Services.GetRequiredService<RavenDbOptions>();

            // Validate layouts path is provided
            if (string.IsNullOrEmpty(options.LayoutsPath))
            {
                Log.Error("Layouts path is required. Use --layouts-path or set SyncOptions:LayoutsPath in config.");
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
                await syncService.SyncAllAsync(layoutsPath, args.Layout, args.DryRun, args.Clean);
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

            // --strict: fail the run with exit code 2 if any of the detection-only validators
            // flagged something during sync. All of the following contribute to the same gate:
            //   • duplicate entity identifiers (RavenDbService)
            //   • raw-NanoID authorship warnings (SeedAuthorshipValidator, #308)
            //   • dangling / unpinned-target cross-references (SeedCrossReferenceValidator, #300)
            // Detection-only: none of these mutate state. Strict mode is the CI escalation hook.
            RavenDbService ravenService = host.Services.GetRequiredService<RavenDbService>();
            SeedAuthorshipValidator authorshipValidator =
                host.Services.GetRequiredService<SeedAuthorshipValidator>();
            SeedCrossReferenceValidator crossRefValidator =
                host.Services.GetRequiredService<SeedCrossReferenceValidator>();

            if (args.Strict)
            {
                bool hasDuplicates = ravenService.DuplicateEntityIdentifierCount > 0;
                bool hasAuthorshipWarnings = authorshipValidator.AuthorshipWarningCount > 0;
                bool hasCrossRefViolations = crossRefValidator.WarningCount > 0;

                if (hasDuplicates)
                {
                    Log.Error(
                        "--strict: {Count} duplicate entity identifier(s) detected during sync. See WARN lines above for document IDs. LayoutSync does not auto-delete entity duplicates; purge manually via RavenDB.",
                        ravenService.DuplicateEntityIdentifierCount
                    );
                }

                if (hasAuthorshipWarnings)
                {
                    Log.Error(
                        "--strict: {Count} seed file(s) with raw-NanoID identity references. Migrate those fields to `ext:{{provider}}:{{externalId}}` (see .claude/references/architecture-patterns.md, \"Stable Identity References\").",
                        authorshipValidator.AuthorshipWarningCount
                    );
                }

                if (hasCrossRefViolations)
                {
                    Log.Error(
                        "--strict: {Count} seed file(s) contain cross-references whose target is either unpinned or missing. See WARN lines above for specific JSON paths.",
                        crossRefValidator.WarningCount
                    );
                }

                if (hasDuplicates || hasAuthorshipWarnings || hasCrossRefViolations)
                    return 2;
            }

            return 0;
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

/// <summary>
/// Command-line arguments parsed from the command line.
/// </summary>
public class CommandLineArgs
{
    public string? LayoutsPath { get; init; }
    public string? RavenUrl { get; init; }
    public string? Database { get; init; }
    public string? CertificatePath { get; init; }
    public string? CertificatePassword { get; init; }
    public string? Layout { get; init; }
    public bool SyncOnce { get; init; }
    public bool ValidateOnly { get; init; }
    public bool FixIds { get; init; }
    public bool DryRun { get; init; }
    public bool Clean { get; init; }
    public bool Verbose { get; init; }
    public bool PreserveIds { get; init; }
    public bool Strict { get; init; }
    public bool AllowRemoteSync { get; init; }
}
