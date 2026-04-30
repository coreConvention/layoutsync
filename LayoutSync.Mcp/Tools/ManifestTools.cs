using System.ComponentModel;
using LayoutSync.Configuration;
using LayoutSync.Models;
using LayoutSync.Services;
using ModelContextProtocol.Server;

namespace LayoutSync.Mcp.Tools;

/// <summary>
/// MCP tools that mutate <c>layout-manifest.json</c>. Each tool delegates to
/// <see cref="ManifestMutationService"/> in <c>LayoutSync.Core</c> — this class is just
/// the MCP-facing surface. Tool responses are formatted via <see cref="JsonOutputFormatter"/>
/// so the on-the-wire shape matches the CLI's <c>--json</c> output exactly.
///
/// Why not static methods (the EchoTool sample pattern)? These tools need DI access to
/// <see cref="ManifestMutationService"/> and the resolved layouts path. The MCP C# SDK
/// supports instance tool classes with constructor injection, which is the cleaner fit.
/// </summary>
[McpServerToolType]
public sealed class ManifestTools(
    ManifestMutationService mutationService,
    LayoutsPathProvider pathProvider)
{
    private readonly ManifestMutationService _service = mutationService;
    private readonly LayoutsPathProvider _pathProvider = pathProvider;

    [McpServerTool(Name = "manifest_set_route")]
    [Description(
        "Mutate a single route entry in a layout's layout-manifest.json. Creates the route if absent. "
        + "Returns a JSON envelope with the before/after snapshots, an RFC 6902 patch, and a per-route "
        + "status. Validates that all referenced section identifiers resolve to entries declared in "
        + "entities.sections; typos produce a structured error with 'did you mean' suggestions.")]
    public async Task<string> ManifestSetRoute(
        [Description("Layout id (e.g. 'dirt-life').")]
        string layoutId,

        [Description("Route key to mutate, e.g. '/events/my-rsvps'. Created if absent in routeConfigs.")]
        string route,

        [Description("Optional new structuralSection identifier. Omit to leave unchanged.")]
        string? structuralSection = null,

        [Description("Optional list of section identifiers for the 'main' slot. Replaces existing wholesale.")]
        string[]? mainSections = null,

        [Description("Optional list of section identifiers for the 'sidebar' slot. Replaces existing wholesale.")]
        string[]? sidebarSections = null,

        [Description("Optional list of slot names ('main' and/or 'sidebar') to remove from patches. "
                   + "Mutually exclusive with the corresponding *Sections list for the same slot.")]
        string[]? removePatch = null,

        [Description("If true, compute the diff but do not write the manifest file. Default: false.")]
        bool dryRun = false)
    {
        bool removeMain = removePatch?.Contains("main") ?? false;
        bool removeSidebar = removePatch?.Contains("sidebar") ?? false;

        RoutePatchInput patch = new(
            Route: route,
            StructuralSection: structuralSection,
            MainSections: mainSections,
            SidebarSections: sidebarSections,
            RemoveMain: removeMain,
            RemoveSidebar: removeSidebar);

        MutationResult result = await _service.SetRouteAsync(
            _pathProvider.Path, layoutId, patch, dryRun);

        return JsonOutputFormatter.FormatAsString(
            command: "manifest set-route",
            layoutId: layoutId,
            dryRun: dryRun,
            result);
    }

    [McpServerTool(Name = "manifest_apply_batch")]
    [Description(
        "Apply a batch of route patches against a layout's manifest atomically. "
        + "On any validation failure, behavior is governed by `onError`: 'abort' (default) rejects "
        + "the entire batch; 'skip' applies the valid patches and reports the invalid ones. "
        + "Returns the same JSON envelope shape as manifest_set_route, with one entry per route.")]
    public async Task<string> ManifestApplyBatch(
        [Description("Layout id (e.g. 'dirt-life').")]
        string layoutId,

        [Description("Array of route patches. Each item: { route, structuralSection?, mainSections?, "
                   + "sidebarSections?, removeMain?, removeSidebar? }. mainSections/sidebarSections: "
                   + "non-null array sets the slot, null removes it (use removeMain/removeSidebar for "
                   + "explicit removal in this tool), absent leaves unchanged.")]
        BatchPatch[] patches,

        [Description("Error mode: 'abort' (atomic — any failure rejects the batch) or 'skip' "
                   + "(apply valid patches, mark invalid as skipped). Default: 'abort'.")]
        string onError = "abort",

        [Description("If true, compute the diff but do not write the manifest file. Default: false.")]
        bool dryRun = false)
    {
        BatchErrorMode mode = onError == "skip" ? BatchErrorMode.Skip : BatchErrorMode.Abort;

        List<RoutePatchInput> inputs = [];
        foreach (BatchPatch p in patches)
        {
            inputs.Add(new RoutePatchInput(
                Route: p.Route,
                StructuralSection: p.StructuralSection,
                MainSections: p.MainSections,
                SidebarSections: p.SidebarSections,
                RemoveMain: p.RemoveMain ?? false,
                RemoveSidebar: p.RemoveSidebar ?? false));
        }

        MutationResult result = await _service.ApplyBatchAsync(
            _pathProvider.Path, layoutId, inputs, mode, dryRun);

        return JsonOutputFormatter.FormatAsString(
            command: "manifest apply-batch",
            layoutId: layoutId,
            dryRun: dryRun,
            result);
    }

    /// <summary>
    /// Wire shape for batch entries received via the MCP tool call. The MCP SDK's JSON
    /// schema generation produces a clean schema from this record.
    /// </summary>
    public sealed record BatchPatch(
        [property: Description("Route key, e.g. '/events/my-rsvps'.")] string Route,
        [property: Description("Optional new structuralSection identifier.")] string? StructuralSection = null,
        [property: Description("Optional sections for the main slot.")] string[]? MainSections = null,
        [property: Description("Optional sections for the sidebar slot.")] string[]? SidebarSections = null,
        [property: Description("If true, remove the main slot.")] bool? RemoveMain = null,
        [property: Description("If true, remove the sidebar slot.")] bool? RemoveSidebar = null);
}
