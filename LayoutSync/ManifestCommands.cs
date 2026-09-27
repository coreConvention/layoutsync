using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
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
/// CLI surface for <c>layoutsync manifest set-route</c> / <c>from-json</c> (route
/// configurations) and <c>add-section</c> / <c>rename-section</c> / <c>remove-section</c>
/// (the <c>entities.sections</c> registry). Lives in the exe project (not
/// <c>LayoutSync.Core</c>) because System.CommandLine is an exe-only concern.
///
/// Each handler builds a minimal DI host containing only the services needed for
/// file-only manifest mutation (<see cref="LocalFileService"/>,
/// <see cref="ManifestSectionValidator"/>, <see cref="ManifestMutationService"/>,
/// <see cref="ManifestSectionRegistryService"/>) — no
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
        Command parent = new("manifest", "Mutate layout-manifest.json: route configurations and the entities.sections registry.");
        parent.AddCommand(BuildSetRouteCommand());
        parent.AddCommand(BuildFromJsonCommand());
        parent.AddCommand(BuildAddSectionCommand());
        parent.AddCommand(BuildRenameSectionCommand());
        parent.AddCommand(BuildRemoveSectionCommand());
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
            description: "Path to layouts/ directory. When omitted, LayoutSync walks up from CWD looking for a `layouts/` ancestor (auto-resolution).");

        Option<bool> allowCrossWorktreeSyncOpt = new(
            aliases: ["--allow-cross-worktree-sync"],
            description: "Explicit opt-in to mutate a layouts directory OUTSIDE the current worktree. Without this flag, LayoutSync refuses cross-worktree mutations and exits with code 4. See issue #526.");

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
        cmd.AddOption(allowCrossWorktreeSyncOpt);
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
                AllowCrossWorktreeSync: context.ParseResult.GetValueForOption(allowCrossWorktreeSyncOpt),
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
            description: "Path to layouts/ directory. When omitted, LayoutSync walks up from CWD looking for a `layouts/` ancestor (auto-resolution).");

        Option<bool> allowCrossWorktreeSyncOpt = new(
            aliases: ["--allow-cross-worktree-sync"],
            description: "Explicit opt-in to mutate a layouts directory OUTSIDE the current worktree. Without this flag, LayoutSync refuses cross-worktree mutations and exits with code 4. See issue #526.");

        Option<bool> dryRunOpt = new(["--dry-run"], "Compute the diff but do not write the manifest.");
        Option<bool> jsonOpt = new(["--json"], "Emit a stable JSON envelope on stdout (logs go to stderr).");
        Option<bool> strictOpt = new(["--strict"], "Exit with code 2 if any validator offense was detected.");
        Option<bool> verboseOpt = new(["--verbose", "-v"], "Enable Debug-level logging.");

        Command cmd = new("from-json", "Apply a batch of route patches from a JSON file.");
        cmd.AddArgument(patchesFileArg);
        cmd.AddOption(onErrorOpt);
        cmd.AddOption(layoutsPathOpt);
        cmd.AddOption(allowCrossWorktreeSyncOpt);
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
                AllowCrossWorktreeSync: context.ParseResult.GetValueForOption(allowCrossWorktreeSyncOpt),
                DryRun: context.ParseResult.GetValueForOption(dryRunOpt),
                Json: context.ParseResult.GetValueForOption(jsonOpt),
                Strict: context.ParseResult.GetValueForOption(strictOpt),
                Verbose: context.ParseResult.GetValueForOption(verboseOpt));
            context.ExitCode = await RunFromJsonAsync(args);
        });

        return cmd;
    }

    // ───── add-section / rename-section / remove-section ─────

    private static Command BuildAddSectionCommand()
    {
        Argument<string> identifierArg = new(
            name: "identifier",
            description: "Section identifier to declare in entities.sections (e.g. events-page-header).");

        Option<string?> typeOpt = new(
            aliases: ["--type"],
            description: $"Section type. Default: {ManifestSectionRegistryService.DefaultSectionType}.");

        Option<string?> fileOpt = new(
            aliases: ["--file"],
            description: "Section file, relative to the layout directory. Default: sections/<identifier>.json.");

        Option<string?> descriptionOpt = new(
            aliases: ["--description"],
            description: "Description stored on the registry entry. Omitted when not given.");

        SectionCommandOptions common = new();
        Command cmd = new("add-section", "Declare a new section in entities.sections so routes can reference it.");
        cmd.AddArgument(identifierArg);
        cmd.AddOption(typeOpt);
        cmd.AddOption(fileOpt);
        cmd.AddOption(descriptionOpt);
        common.AddTo(cmd);

        cmd.SetHandler(async (InvocationContext context) =>
        {
            ParseResult parse = context.ParseResult;
            SectionRunArgs args = common.Read(parse);
            SectionEntryInput entry = new(
                Identifier: parse.GetValueForArgument(identifierArg),
                Type: parse.GetValueForOption(typeOpt),
                File: parse.GetValueForOption(fileOpt),
                Description: parse.GetValueForOption(descriptionOpt));

            context.ExitCode = await RunSectionCommandAsync(
                "manifest add-section",
                args,
                (service, layoutsPath) => service.AddSectionAsync(layoutsPath, args.LayoutId, entry, args.DryRun));
        });

        return cmd;
    }

    private static Command BuildRenameSectionCommand()
    {
        Argument<string> identifierArg = new(
            name: "identifier",
            description: "The section's current identifier.");

        Argument<string> newIdentifierArg = new(
            name: "new-identifier",
            description: "The identifier to rename it to. Must not already be declared.");

        Option<bool> manifestOnlyOpt = new(
            aliases: ["--manifest-only"],
            description: "Rename only inside layout-manifest.json. The section file keeps its old identifier, which is reported as a warning. Use it when the section file can't be updated.");

        SectionCommandOptions common = new();
        Command cmd = new(
            "rename-section",
            "Rename a section in one write: its entities.sections entry and every manifest field that references it. Also rewrites the section file's own identifier unless --manifest-only, and refuses (writing nothing) if that file can't be updated.");
        cmd.AddArgument(identifierArg);
        cmd.AddArgument(newIdentifierArg);
        cmd.AddOption(manifestOnlyOpt);
        common.AddTo(cmd);

        cmd.SetHandler(async (InvocationContext context) =>
        {
            ParseResult parse = context.ParseResult;
            SectionRunArgs args = common.Read(parse);
            string identifier = parse.GetValueForArgument(identifierArg);
            string newIdentifier = parse.GetValueForArgument(newIdentifierArg);
            bool updateSectionFile = !parse.GetValueForOption(manifestOnlyOpt);

            context.ExitCode = await RunSectionCommandAsync(
                "manifest rename-section",
                args,
                (service, layoutsPath) => service.RenameSectionAsync(
                    layoutsPath, args.LayoutId, identifier, newIdentifier, updateSectionFile, args.DryRun));
        });

        return cmd;
    }

    private static Command BuildRemoveSectionCommand()
    {
        Argument<string> identifierArg = new(
            name: "identifier",
            description: "The section to remove from entities.sections.");

        Option<bool> forceOpt = new(
            aliases: ["--force"],
            description: "Remove the entry even while manifest fields still reference it. Those references are left dangling and reported as warnings.");

        SectionCommandOptions common = new();
        Command cmd = new(
            "remove-section",
            "Remove a section from entities.sections. Refuses while any manifest field still references it, unless --force. The section file is not deleted.");
        cmd.AddArgument(identifierArg);
        cmd.AddOption(forceOpt);
        common.AddTo(cmd);

        cmd.SetHandler(async (InvocationContext context) =>
        {
            ParseResult parse = context.ParseResult;
            SectionRunArgs args = common.Read(parse);
            string identifier = parse.GetValueForArgument(identifierArg);
            bool force = parse.GetValueForOption(forceOpt);

            context.ExitCode = await RunSectionCommandAsync(
                "manifest remove-section",
                args,
                (service, layoutsPath) => service.RemoveSectionAsync(
                    layoutsPath, args.LayoutId, identifier, force, args.DryRun));
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

            // Worktree-mismatch guard (issue #526) — runs BEFORE the manifest is touched
            // so a misdirected mutation refuses with exit 4 instead of silently writing to
            // the wrong working tree. Mirrors the sync flow's wiring (Program.cs, #520).
            WorktreePathGuard worktreeGuard = host.Services.GetRequiredService<WorktreePathGuard>();
            if (!worktreeGuard.Authorize(
                    currentDirectory: Directory.GetCurrentDirectory(),
                    layoutsPath: layoutsPath,
                    allowCrossWorktreeSync: args.AllowCrossWorktreeSync))
            {
                return 4;
            }

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

            // Worktree-mismatch guard (issue #526) — runs BEFORE the batch is applied so a
            // misdirected mutation refuses with exit 4 instead of silently writing to the
            // wrong working tree. Mirrors the sync flow's wiring (Program.cs, #520).
            WorktreePathGuard worktreeGuard = host.Services.GetRequiredService<WorktreePathGuard>();
            if (!worktreeGuard.Authorize(
                    currentDirectory: Directory.GetCurrentDirectory(),
                    layoutsPath: layoutsPath,
                    allowCrossWorktreeSync: args.AllowCrossWorktreeSync))
            {
                return 4;
            }

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

    /// <summary>
    /// Runs one section-registry subcommand with set-route's guard rails: layouts path
    /// resolution, the worktree guard (exit 4), JSON or human-readable output, and
    /// <c>--strict</c>. <paramref name="mutate"/> receives the resolved layouts path.
    /// </summary>
    private static async Task<int> RunSectionCommandAsync(
        string command,
        SectionRunArgs args,
        Func<ManifestSectionRegistryService, string, Task<SectionMutationResult>> mutate)
    {
        ConfigureLogging(args.Verbose, args.Json);
        try
        {
            string layoutsPath = ResolveLayoutsPath(args.LayoutsPath);

            using IHost host = BuildManifestHost();

            // Worktree-mismatch guard (issue #526) — same wiring as set-route: refuse with
            // exit 4 before any file is read or written.
            WorktreePathGuard worktreeGuard = host.Services.GetRequiredService<WorktreePathGuard>();
            if (!worktreeGuard.Authorize(
                    currentDirectory: Directory.GetCurrentDirectory(),
                    layoutsPath: layoutsPath,
                    allowCrossWorktreeSync: args.AllowCrossWorktreeSync))
            {
                return 4;
            }

            ManifestSectionRegistryService service = host.Services.GetRequiredService<ManifestSectionRegistryService>();
            SectionMutationResult result = await mutate(service, layoutsPath);

            EmitSectionOutput(command, args.LayoutId, args.DryRun, args.Json, result);
            return ResolveSectionExitCode(args.Strict, result);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "{Command} failed.", command);
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
                services.AddSingleton<ManifestSectionRegistryService>();
                // Worktree-mismatch guard (issue #526) — refuses cross-worktree mutations
                // unless the operator opts in via --allow-cross-worktree-sync. Mirrors the
                // sync flow's wiring (Program.cs, post-#520).
                services.AddSingleton<WorktreePathGuard>();
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
    /// Resolves the layouts/ directory. Explicit <c>--layouts-path</c> wins; otherwise
    /// delegates to <see cref="LayoutsPathResolver.Resolve"/> to walk up from CWD looking
    /// for a <c>layouts/</c> ancestor (post-#526 — mirrors the sync flow's auto-resolve
    /// shipped in #520, eliminating the prior literal <c>{cwd}/layouts</c> default that
    /// only worked from the worktree root, not from subdirectories).
    /// Throws when neither resolves to an existing directory.
    /// </summary>
    private static string ResolveLayoutsPath(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            // GetFullPath normalizes absolute input too (separators, "..", a trailing slash),
            // so every manifest command echoes paths in one form — the form the MCP server
            // already uses (#29) — whatever spelling of --layouts-path was typed.
            string explicitCandidate = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(explicitPath, Directory.GetCurrentDirectory()));

            if (!Directory.Exists(explicitCandidate))
                throw new DirectoryNotFoundException($"Layouts directory not found: {explicitCandidate}");

            return explicitCandidate;
        }

        string? autoResolved = LayoutsPathResolver.Resolve(Directory.GetCurrentDirectory());
        if (autoResolved is null)
        {
            throw new DirectoryNotFoundException(
                "Layouts directory not found by walking up from CWD. Pass --layouts-path or run from a directory whose ancestors contain a `layouts/` folder.");
        }

        return autoResolved;
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

    /// <summary>
    /// Section-registry counterpart of <see cref="EmitOutput"/>: the JSON envelope under
    /// <c>--json</c>; otherwise one line per patch operation and per file changed, the
    /// blocking references when the change was refused, then warnings.
    /// </summary>
    private static void EmitSectionOutput(
        string command,
        string layoutId,
        bool dryRun,
        bool json,
        SectionMutationResult result)
    {
        if (json)
        {
            // JSON envelope to stdout. Logs are already routed to stderr by ConfigureLogging.
            Console.WriteLine(JsonOutputFormatter.FormatAsString(command, layoutId, dryRun, result));
            return;
        }

        foreach (string error in result.Errors) Log.Error("{Error}", error);

        if (result.Success)
        {
            string target = result.NewIdentifier is null
                ? result.Identifier
                : $"{result.Identifier} -> {result.NewIdentifier}";
            Log.Information(
                "[{Command}] {Target}{DryRunNote}",
                command,
                target,
                dryRun ? " (dry-run, not written)" : string.Empty);
            foreach (JsonNode? op in result.Patch ?? [])
            {
                Log.Information("  {Op} {Path}", op?["op"]?.GetValue<string>(), op?["path"]?.GetValue<string>());
            }
            foreach (string file in result.FilesChanged) Log.Information("  file: {File}", file);
        }
        else
        {
            // On a refusal the references are the to-do list: each one must change first.
            foreach (string reference in result.References) Log.Error("  referenced by {Pointer}", reference);
            // Empty for a clean refusal; set only when a failed write could not be rolled back.
            foreach (string file in result.FilesChanged) Log.Error("  left changed: {File}", file);
        }

        foreach (string warning in result.Warnings) Log.Warning("{Warning}", warning);
    }

    /// <summary>
    /// Section-registry counterpart of <see cref="ResolveExitCode"/>: 1 when the change was
    /// refused, 2 on <c>--strict</c> when it left any warning, 0 otherwise.
    /// </summary>
    private static int ResolveSectionExitCode(bool strict, SectionMutationResult result)
    {
        if (!result.Success) return 1;
        if (strict && result.Warnings.Count > 0)
        {
            Log.Error("--strict: the change left {Count} warning(s).", result.Warnings.Count);
            return 2;
        }
        return 0;
    }

    /// <summary>
    /// The options every section subcommand shares with set-route. One instance per command,
    /// because a System.CommandLine symbol belongs to the command it is added to.
    /// </summary>
    private sealed class SectionCommandOptions
    {
        private readonly Option<string> _layout = new(
            aliases: ["--layout", "-l"],
            description: "Layout id whose layout-manifest.json should be mutated (e.g. dirt-life).")
        { IsRequired = true };

        private readonly Option<string?> _layoutsPath = new(
            aliases: ["--layouts-path", "-p"],
            description: "Path to layouts/ directory. When omitted, LayoutSync walks up from CWD looking for a `layouts/` ancestor (auto-resolution).");

        private readonly Option<bool> _allowCrossWorktreeSync = new(
            aliases: ["--allow-cross-worktree-sync"],
            description: "Explicit opt-in to mutate a layouts directory OUTSIDE the current worktree. Without this flag, LayoutSync refuses cross-worktree mutations and exits with code 4. See issue #526.");

        private readonly Option<bool> _dryRun = new(["--dry-run"], "Compute the change but do not write any file.");
        private readonly Option<bool> _json = new(["--json"], "Emit a stable JSON envelope on stdout (logs go to stderr).");
        private readonly Option<bool> _strict = new(["--strict"], "Exit with code 2 if the change left any warning (e.g. references left dangling by remove-section --force).");
        private readonly Option<bool> _verbose = new(["--verbose", "-v"], "Enable Debug-level logging.");

        public void AddTo(Command command)
        {
            command.AddOption(_layout);
            command.AddOption(_layoutsPath);
            command.AddOption(_allowCrossWorktreeSync);
            command.AddOption(_dryRun);
            command.AddOption(_json);
            command.AddOption(_strict);
            command.AddOption(_verbose);
        }

        public SectionRunArgs Read(ParseResult parse) => new(
            LayoutId: parse.GetValueForOption(_layout)!,
            LayoutsPath: parse.GetValueForOption(_layoutsPath),
            AllowCrossWorktreeSync: parse.GetValueForOption(_allowCrossWorktreeSync),
            DryRun: parse.GetValueForOption(_dryRun),
            Json: parse.GetValueForOption(_json),
            Strict: parse.GetValueForOption(_strict),
            Verbose: parse.GetValueForOption(_verbose));
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
        bool AllowCrossWorktreeSync,
        bool DryRun,
        bool Json,
        bool Strict,
        bool Verbose);

    private sealed record FromJsonArgs(
        string PatchesFile,
        string OnError,
        string? LayoutsPath,
        bool AllowCrossWorktreeSync,
        bool DryRun,
        bool Json,
        bool Strict,
        bool Verbose);

    private sealed record SectionRunArgs(
        string LayoutId,
        string? LayoutsPath,
        bool AllowCrossWorktreeSync,
        bool DryRun,
        bool Json,
        bool Strict,
        bool Verbose);
}
