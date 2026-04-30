using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using System.Text.Json.Nodes;
using LayoutSync.Configuration;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace LayoutSync;

/// <summary>
/// CLI surface for <c>layoutsync manifest set-route</c> and
/// <c>layoutsync manifest from-json</c>. Lives in the exe project (not
/// <c>LayoutSync.Core</c>) because System.CommandLine is an exe-only concern.
///
/// Each handler builds a minimal DI host containing only the services needed for
/// file-only manifest mutation (<see cref="LocalFileService"/>,
/// <see cref="ManifestSectionValidator"/>, <see cref="ManifestMutationService"/>) — no
/// RavenDB connection is initialized, since the mutation operates on
/// <c>layout-manifest.json</c> and the existing sync flow is responsible for
/// persisting to the database.
///
/// Output policy:
/// <list type="bullet">
///   <item>Default: human-readable Serilog output to stdout.</item>
///   <item><c>--json</c>: a stable JSON envelope (see <see cref="JsonOutputFormatter"/>)
///         on stdout, with all logging redirected to stderr so consumers (CI scripts,
///         the future MCP server) get clean machine-readable output.</item>
/// </list>
/// </summary>
public static class ManifestCommands
{
    /// <summary>
    /// Builds the <c>manifest</c> command tree to be added to the root command via
    /// <c>rootCommand.AddCommand(...)</c>.
    /// </summary>
    public static Command Build()
    {
        Command parent = new("manifest", "Mutate layout-manifest.json route configurations.");
        parent.AddCommand(BuildSetRouteCommand());
        parent.AddCommand(BuildFromJsonCommand());
        return parent;
    }

    // ───── set-route ─────

    private static Command BuildSetRouteCommand()
    {
        Argument<string> routeArg = new(
            name: "route",
            description: "The route key to mutate (e.g. /events/my-rsvps). Created if absent.");

        Option<string> layoutOpt = new(
            aliases: ["--layout", "-l"],
            description: "Layout id whose layout-manifest.json should be mutated (e.g. dirt-life).")
        { IsRequired = true };

        Option<string?> structuralOpt = new(
            aliases: ["--structural-section"],
            description: "New structuralSection identifier. Omit to leave unchanged.");

        Option<string?> patchMainOpt = new(
            aliases: ["--patch-main"],
            description: "Comma-separated section identifiers for the 'main' slot. Replaces sectionIdentifiers wholesale.");

        Option<string?> patchSidebarOpt = new(
            aliases: ["--patch-sidebar"],
            description: "Comma-separated section identifiers for the 'sidebar' slot.");

        Option<List<string>> removePatchOpt = new(
            aliases: ["--remove-patch"],
            description: "Drop the named slot ('main' or 'sidebar'). Repeatable.")
        { Arity = ArgumentArity.ZeroOrMore };

        Option<string?> layoutsPathOpt = new(
            aliases: ["--layouts-path", "-p"],
            description: "Path to layouts/ directory. Defaults to current working directory./layouts.");

        Option<bool> dryRunOpt = new(
            aliases: ["--dry-run"],
            description: "Compute the diff but do not write the manifest.");

        Option<bool> jsonOpt = new(
            aliases: ["--json"],
            description: "Emit a stable JSON envelope on stdout (logs go to stderr).");

        Option<bool> strictOpt = new(
            aliases: ["--strict"],
            description: "Exit with code 2 if any validator offense was detected.");

        Option<bool> verboseOpt = new(
            aliases: ["--verbose", "-v"],
            description: "Enable Debug-level logging.");

        Command cmd = new("set-route", "Mutate a single route entry in routeConfigs.");
        cmd.AddArgument(routeArg);
        cmd.AddOption(layoutOpt);
        cmd.AddOption(structuralOpt);
        cmd.AddOption(patchMainOpt);
        cmd.AddOption(patchSidebarOpt);
        cmd.AddOption(removePatchOpt);
        cmd.AddOption(layoutsPathOpt);
        cmd.AddOption(dryRunOpt);
        cmd.AddOption(jsonOpt);
        cmd.AddOption(strictOpt);
        cmd.AddOption(verboseOpt);

        cmd.SetHandler(async (InvocationContext context) =>
        {
            SetRouteArgs args = new(
                Route: context.ParseResult.GetValueForArgument(routeArg),
                LayoutId: context.ParseResult.GetValueForOption(layoutOpt)!,
                StructuralSection: context.ParseResult.GetValueForOption(structuralOpt),
                PatchMainCsv: context.ParseResult.GetValueForOption(patchMainOpt),
                PatchSidebarCsv: context.ParseResult.GetValueForOption(patchSidebarOpt),
                RemovePatch: context.ParseResult.GetValueForOption(removePatchOpt) ?? [],
                LayoutsPath: context.ParseResult.GetValueForOption(layoutsPathOpt),
                DryRun: context.ParseResult.GetValueForOption(dryRunOpt),
                Json: context.ParseResult.GetValueForOption(jsonOpt),
                Strict: context.ParseResult.GetValueForOption(strictOpt),
                Verbose: context.ParseResult.GetValueForOption(verboseOpt));
            context.ExitCode = await RunSetRouteAsync(args);
        });

        return cmd;
    }

    // ───── from-json ─────

    private static Command BuildFromJsonCommand()
    {
        Argument<string> patchesFileArg = new(
            name: "patches-file",
            description: "Path to a JSON file describing the batch of route patches.");

        Option<string> onErrorOpt = new(
            aliases: ["--on-error"],
            description: "How to react when a patch fails validation.",
            getDefaultValue: () => "abort");
        onErrorOpt.AddCompletions("abort", "skip");
        onErrorOpt.AddValidator(result =>
        {
            string? value = result.GetValueOrDefault<string>();
            if (value is not null and not "abort" and not "skip")
            {
                result.ErrorMessage = $"--on-error must be 'abort' or 'skip' (got '{value}').";
            }
        });

        Option<string?> layoutsPathOpt = new(
            aliases: ["--layouts-path", "-p"],
            description: "Path to layouts/ directory. Defaults to current working directory./layouts.");

        Option<bool> dryRunOpt = new(["--dry-run"], "Compute the diff but do not write the manifest.");
        Option<bool> jsonOpt = new(["--json"], "Emit a stable JSON envelope on stdout (logs go to stderr).");
        Option<bool> strictOpt = new(["--strict"], "Exit with code 2 if any validator offense was detected.");
        Option<bool> verboseOpt = new(["--verbose", "-v"], "Enable Debug-level logging.");

        Command cmd = new("from-json", "Apply a batch of route patches from a JSON file.");
        cmd.AddArgument(patchesFileArg);
        cmd.AddOption(onErrorOpt);
        cmd.AddOption(layoutsPathOpt);
        cmd.AddOption(dryRunOpt);
        cmd.AddOption(jsonOpt);
        cmd.AddOption(strictOpt);
        cmd.AddOption(verboseOpt);

        cmd.SetHandler(async (InvocationContext context) =>
        {
            FromJsonArgs args = new(
                PatchesFile: context.ParseResult.GetValueForArgument(patchesFileArg),
                OnError: context.ParseResult.GetValueForOption(onErrorOpt) ?? "abort",
                LayoutsPath: context.ParseResult.GetValueForOption(layoutsPathOpt),
                DryRun: context.ParseResult.GetValueForOption(dryRunOpt),
                Json: context.ParseResult.GetValueForOption(jsonOpt),
                Strict: context.ParseResult.GetValueForOption(strictOpt),
                Verbose: context.ParseResult.GetValueForOption(verboseOpt));
            context.ExitCode = await RunFromJsonAsync(args);
        });

        return cmd;
    }

    // ───── handlers ─────

    private static async Task<int> RunSetRouteAsync(SetRouteArgs args)
    {
        ConfigureLogging(args.Verbose, args.Json);
        try
        {
            string layoutsPath = ResolveLayoutsPath(args.LayoutsPath);

            // Translate CLI flags into a typed RoutePatchInput.
            (bool removeMain, bool removeSidebar) = ParseRemovePatch(args.RemovePatch);
            RoutePatchInput patch = new(
                Route: args.Route,
                StructuralSection: args.StructuralSection,
                MainSections: ParseSectionsCsv(args.PatchMainCsv),
                SidebarSections: ParseSectionsCsv(args.PatchSidebarCsv),
                RemoveMain: removeMain,
                RemoveSidebar: removeSidebar);

            using IHost host = BuildManifestHost();
            ManifestMutationService service = host.Services.GetRequiredService<ManifestMutationService>();
            ManifestSectionValidator validator = host.Services.GetRequiredService<ManifestSectionValidator>();

            MutationResult result = await service.SetRouteAsync(
                layoutsPath, args.LayoutId, patch, args.DryRun);

            EmitOutput("manifest set-route", args.LayoutId, args.DryRun, args.Json, result);
            return ResolveExitCode(args.Strict, validator, result);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "manifest set-route failed.");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static async Task<int> RunFromJsonAsync(FromJsonArgs args)
    {
        ConfigureLogging(args.Verbose, args.Json);
        try
        {
            string layoutsPath = ResolveLayoutsPath(args.LayoutsPath);

            if (!File.Exists(args.PatchesFile))
            {
                Log.Error("Patches file not found: {Path}", args.PatchesFile);
                return 1;
            }

            string patchesJson = await File.ReadAllTextAsync(args.PatchesFile);
            (string layoutId, IReadOnlyList<RoutePatchInput> patches) = ParsePatchesFile(patchesJson);

            BatchErrorMode mode = args.OnError == "skip" ? BatchErrorMode.Skip : BatchErrorMode.Abort;

            using IHost host = BuildManifestHost();
            ManifestMutationService service = host.Services.GetRequiredService<ManifestMutationService>();
            ManifestSectionValidator validator = host.Services.GetRequiredService<ManifestSectionValidator>();

            MutationResult result = await service.ApplyBatchAsync(
                layoutsPath, layoutId, patches, mode, args.DryRun);

            EmitOutput("manifest from-json", layoutId, args.DryRun, args.Json, result);
            return ResolveExitCode(args.Strict, validator, result);
        }
        catch (JsonException jsonEx)
        {
            Log.Error("Invalid patches file JSON: {Message}", jsonEx.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "manifest from-json failed.");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    // ───── helpers ─────

    /// <summary>
    /// Builds a minimal host that contains only the services needed for file-only
    /// manifest mutation. Intentionally omits RavenDB-related services so this command
    /// can run without a database connection.
    /// </summary>
    private static IHost BuildManifestHost()
    {
        return Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton<LocalFileService>();
                services.AddSingleton<ManifestSectionValidator>();
                services.AddSingleton<ManifestMutationService>();
            })
            .Build();
    }

    /// <summary>
    /// Configures Serilog. In <c>--json</c> mode all log events are routed to stderr
    /// so stdout stays clean for the JSON envelope (consumed by CI / MCP).
    /// </summary>
    private static void ConfigureLogging(bool verbose, bool jsonMode)
    {
        LogEventLevel minLevel = verbose ? LogEventLevel.Debug : LogEventLevel.Information;
        LoggerConfiguration cfg = new LoggerConfiguration()
            .MinimumLevel.Is(minLevel);

        if (jsonMode)
        {
            cfg = cfg.WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                standardErrorFromLevel: LogEventLevel.Verbose);
        }
        else
        {
            cfg = cfg.WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
        }

        Log.Logger = cfg.CreateLogger();
    }

    /// <summary>
    /// Resolves the layouts/ directory: explicit flag wins; otherwise defaults to
    /// <c>{cwd}/layouts</c>. Throws when neither resolves to an existing directory.
    /// </summary>
    private static string ResolveLayoutsPath(string? explicitPath)
    {
        string candidate = !string.IsNullOrEmpty(explicitPath)
            ? Path.IsPathRooted(explicitPath)
                ? explicitPath
                : Path.GetFullPath(explicitPath, Directory.GetCurrentDirectory())
            : Path.Combine(Directory.GetCurrentDirectory(), "layouts");

        if (!Directory.Exists(candidate))
            throw new DirectoryNotFoundException($"Layouts directory not found: {candidate}");

        return candidate;
    }

    /// <summary>
    /// Splits a comma-separated string into a list of trimmed identifiers. Returns
    /// <c>null</c> when the input is null (the "leave unchanged" signal); returns an
    /// empty list when the input is the empty string. Identifiers with surrounding
    /// whitespace are trimmed.
    /// </summary>
    private static IReadOnlyList<string>? ParseSectionsCsv(string? csv)
    {
        if (csv is null) return null;
        if (csv.Length == 0) return [];
        return csv
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    /// <summary>
    /// Translates the repeatable <c>--remove-patch</c> flag values into two booleans
    /// (RemoveMain, RemoveSidebar). Unknown slot names are reported as a top-level
    /// error rather than silently ignored.
    /// </summary>
    private static (bool RemoveMain, bool RemoveSidebar) ParseRemovePatch(IReadOnlyList<string> slots)
    {
        bool removeMain = false;
        bool removeSidebar = false;
        foreach (string slot in slots)
        {
            switch (slot)
            {
                case "main": removeMain = true; break;
                case "sidebar": removeSidebar = true; break;
                default:
                    throw new ArgumentException(
                        $"Unknown slot for --remove-patch: '{slot}'. Expected 'main' or 'sidebar'.");
            }
        }
        return (removeMain, removeSidebar);
    }

    /// <summary>
    /// Parses the <c>patches-file</c> JSON into a list of <see cref="RoutePatchInput"/>.
    /// Distinguishes "key absent" (leave unchanged) from "key present with null value"
    /// (remove the slot) by inspecting <see cref="JsonObject.ContainsKey"/> directly,
    /// since System.Text.Json record deserialization can't represent that three-way
    /// distinction cleanly.
    /// </summary>
    internal static (string LayoutId, IReadOnlyList<RoutePatchInput> Patches) ParsePatchesFile(string json)
    {
        JsonObject root = JsonNode.Parse(json) is JsonObject obj
            ? obj
            : throw new JsonException("Patches file root must be a JSON object.");

        string layoutId = root["layoutId"]?.GetValue<string>()
            ?? throw new JsonException("Patches file missing 'layoutId' field.");

        if (root["patches"] is not JsonArray array)
            throw new JsonException("Patches file missing 'patches' array.");

        List<RoutePatchInput> patches = [];
        foreach (JsonNode? element in array)
        {
            if (element is not JsonObject entry)
                throw new JsonException("Each entry in 'patches' must be an object.");

            string route = entry["route"]?.GetValue<string>()
                ?? throw new JsonException("Each patch entry must have a 'route' field.");

            string? structuralSection = entry.ContainsKey("structuralSection")
                ? entry["structuralSection"]?.GetValue<string>()
                : null;

            (IReadOnlyList<string>? mainSections, bool removeMain) =
                ParseSlotField(entry, "mainSections");
            (IReadOnlyList<string>? sidebarSections, bool removeSidebar) =
                ParseSlotField(entry, "sidebarSections");

            patches.Add(new RoutePatchInput(
                Route: route,
                StructuralSection: structuralSection,
                MainSections: mainSections,
                SidebarSections: sidebarSections,
                RemoveMain: removeMain,
                RemoveSidebar: removeSidebar));
        }

        return (layoutId, patches);
    }

    /// <summary>
    /// Three-way semantic for slot fields: key absent → leave unchanged (null, false);
    /// key present with null value → remove (null, true); key present with array value
    /// → set (parsed list, false).
    /// </summary>
    private static (IReadOnlyList<string>? Sections, bool Remove) ParseSlotField(
        JsonObject entry,
        string fieldName)
    {
        if (!entry.ContainsKey(fieldName)) return (null, false);
        JsonNode? value = entry[fieldName];
        if (value is null) return (null, true);
        if (value is JsonArray array)
        {
            List<string> identifiers = [];
            foreach (JsonNode? n in array)
            {
                if (n is null) continue;
                identifiers.Add(n.GetValue<string>());
            }
            return (identifiers, false);
        }
        throw new JsonException($"'{fieldName}' must be either an array or null.");
    }

    /// <summary>
    /// Routes the result to the appropriate output channel: structured JSON envelope
    /// on stdout when <paramref name="json"/> is true, or human-readable Serilog lines
    /// otherwise.
    /// </summary>
    private static void EmitOutput(
        string command,
        string layoutId,
        bool dryRun,
        bool json,
        MutationResult result)
    {
        if (json)
        {
            // JSON envelope to stdout. Logs are already routed to stderr by ConfigureLogging.
            Console.WriteLine(JsonOutputFormatter.FormatAsString(command, layoutId, dryRun, result));
            return;
        }

        // Human-readable summary.
        if (result.Errors.Count > 0)
        {
            foreach (string error in result.Errors) Log.Error("{Error}", error);
        }
        foreach (RouteChange change in result.Changes)
        {
            switch (change.Status)
            {
                case RouteChangeStatus.Applied:
                    Log.Information(
                        "[applied] {Route}: {OpCount} op(s){DryRunNote}",
                        change.Route,
                        change.Patch?.Count ?? 0,
                        dryRun ? " (dry-run, not written)" : string.Empty);
                    break;
                case RouteChangeStatus.Skipped:
                    Log.Warning("[skipped] {Route}: {Error}", change.Route, change.Error);
                    break;
                case RouteChangeStatus.Aborted:
                    Log.Error("[aborted] {Route}: {Error}", change.Route, change.Error);
                    break;
            }
        }
    }

    /// <summary>
    /// Final exit code resolution: 1 on top-level errors, 2 on <c>--strict</c> with any
    /// validator offense, 0 otherwise. Mirrors the existing root-command exit-code
    /// pattern in <see cref="Program.RunAsync"/>.
    /// </summary>
    private static int ResolveExitCode(bool strict, ManifestSectionValidator validator, MutationResult result)
    {
        if (result.Errors.Count > 0) return 1;
        if (!result.Success) return 1;
        if (strict && validator.OffenseCount > 0)
        {
            Log.Error(
                "--strict: {Count} route(s) failed section-identifier validation.",
                validator.OffenseCount);
            return 2;
        }
        return 0;
    }

    // ───── arg records ─────

    private sealed record SetRouteArgs(
        string Route,
        string LayoutId,
        string? StructuralSection,
        string? PatchMainCsv,
        string? PatchSidebarCsv,
        IReadOnlyList<string> RemovePatch,
        string? LayoutsPath,
        bool DryRun,
        bool Json,
        bool Strict,
        bool Verbose);

    private sealed record FromJsonArgs(
        string PatchesFile,
        string OnError,
        string? LayoutsPath,
        bool DryRun,
        bool Json,
        bool Strict,
        bool Verbose);
}
