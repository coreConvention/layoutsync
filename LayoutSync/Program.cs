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
            preserveIdsOption
        };

        rootCommand.SetHandler(
            async (context) =>
            {
                await RunAsync(new CommandLineArgs
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
                    PreserveIds = context.ParseResult.GetValueForOption(preserveIdsOption)
                });
            });

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task RunAsync(CommandLineArgs args)
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
                    services.AddSingleton<RavenDbService>();
                    services.AddSingleton<LocalFileService>();
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
                return;
            }

            // Resolve layouts path (supports relative or absolute)
            string layoutsPath = Path.IsPathRooted(options.LayoutsPath)
                ? options.LayoutsPath
                : Path.GetFullPath(options.LayoutsPath, Directory.GetCurrentDirectory());

            if (!Directory.Exists(layoutsPath))
            {
                Log.Error("Layouts directory not found: {Path}", layoutsPath);
                return;
            }

            Log.Information("Layouts: {Path}", layoutsPath);
            Log.Information("RavenDB: {Url}/{Database}", ravenOpts.Url, ravenOpts.Database);

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
                await syncService.SyncAllAsync(layoutsPath, args.Layout, args.DryRun, args.Clean);

                Log.Information("Watching for changes... (Ctrl+C to stop)");

                FileWatcherService watcher = host.Services.GetRequiredService<FileWatcherService>();
                using CancellationTokenSource cts = new();
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                await watcher.WatchAsync(layoutsPath, args.Layout, args.DryRun, cts.Token);
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
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
}
