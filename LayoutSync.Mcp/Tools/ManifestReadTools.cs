using System.ComponentModel;
using System.Text.Json.Nodes;
using LayoutSync.Configuration;
using LayoutSync.Models;
using LayoutSync.Services;
using ModelContextProtocol.Server;

namespace LayoutSync.Mcp.Tools;

/// <summary>
/// Read-only inspection tools. The real ergonomic win of an MCP wrapper: callers
/// can ask "what routes exist on dirt-life?" without opening a 1421-line manifest by
/// hand and eyeballing it. Each tool reads the manifest fresh on each call —
/// no caching — because the file may have changed between calls (e.g. a sibling
/// mutation tool just ran).
/// </summary>
[McpServerToolType]
public sealed class ManifestReadTools(
    LocalFileService fileService,
    LayoutsPathProvider pathProvider)
{
    private readonly LocalFileService _fileService = fileService;
    private readonly LayoutsPathProvider _pathProvider = pathProvider;

    [McpServerTool(Name = "manifest_list_routes")]
    [Description(
        "List every route declared in a layout's manifest, with its structural section and "
        + "the slots that have content. Returns a JSON object: "
        + "{ layoutId, manifestPath, routes: [{ route, structuralSection, slots: [...] }] }.")]
    public async Task<string> ManifestListRoutes(
        [Description("Layout id (e.g. 'dirt-life').")]
        string layoutId)
    {
        JsonObject manifest = await LoadManifestAsync(layoutId);

        JsonArray routes = [];
        if (manifest["routeConfigs"] is JsonObject routeConfigs)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in routeConfigs.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (entry.Value is not JsonObject routeConfig) continue;

                JsonArray slots = [];
                if (routeConfig["patches"] is JsonArray patches)
                {
                    foreach (JsonNode? patch in patches)
                    {
                        if (patch is JsonObject patchObj
                            && patchObj["targetElementId"] is JsonValue v
                            && v.TryGetValue(out string? slot))
                        {
                            slots.Add(slot);
                        }
                    }
                }

                routes.Add(new JsonObject
                {
                    ["route"] = entry.Key,
                    ["structuralSection"] = routeConfig["structuralSection"]?.DeepClone(),
                    ["slots"] = slots,
                });
            }
        }

        return new JsonObject
        {
            ["layoutId"] = layoutId,
            ["manifestPath"] = ManifestPath(layoutId),
            ["routes"] = routes,
        }.ToJsonString(JsonOutputFormatter.PrettyOptions);
    }

    [McpServerTool(Name = "manifest_get_route")]
    [Description(
        "Get the full route configuration for a single route key. Returns the verbatim "
        + "routeConfigs[route] object, or { found: false } if the route is not declared.")]
    public async Task<string> ManifestGetRoute(
        [Description("Layout id (e.g. 'dirt-life').")]
        string layoutId,

        [Description("Route key to look up, e.g. '/events/my-rsvps'.")]
        string route)
    {
        JsonObject manifest = await LoadManifestAsync(layoutId);

        if (manifest["routeConfigs"] is not JsonObject routeConfigs
            || routeConfigs[route] is not JsonObject routeConfig)
        {
            return new JsonObject
            {
                ["layoutId"] = layoutId,
                ["route"] = route,
                ["found"] = false,
            }.ToJsonString(JsonOutputFormatter.PrettyOptions);
        }

        return new JsonObject
        {
            ["layoutId"] = layoutId,
            ["route"] = route,
            ["found"] = true,
            ["config"] = routeConfig.DeepClone(),
        }.ToJsonString(JsonOutputFormatter.PrettyOptions);
    }

    [McpServerTool(Name = "manifest_list_sections")]
    [Description(
        "List every section declared under entities.sections in a layout's manifest. "
        + "Use this to discover the valid section identifiers for manifest_set_route / "
        + "manifest_apply_batch — picking from this list guarantees no validation typo.")]
    public async Task<string> ManifestListSections(
        [Description("Layout id (e.g. 'dirt-life').")]
        string layoutId)
    {
        JsonObject manifest = await LoadManifestAsync(layoutId);

        JsonArray sections = [];
        if (manifest["entities"] is JsonObject entities
            && entities["sections"] is JsonArray entitiesSections)
        {
            foreach (JsonNode? section in entitiesSections)
            {
                if (section is not JsonObject sectionObj) continue;
                sections.Add(new JsonObject
                {
                    ["identifier"] = sectionObj["identifier"]?.DeepClone(),
                    ["type"] = sectionObj["type"]?.DeepClone(),
                    ["file"] = sectionObj["file"]?.DeepClone(),
                    ["description"] = sectionObj["description"]?.DeepClone(),
                });
            }
        }

        return new JsonObject
        {
            ["layoutId"] = layoutId,
            ["manifestPath"] = ManifestPath(layoutId),
            ["sections"] = sections,
        }.ToJsonString(JsonOutputFormatter.PrettyOptions);
    }

    private async Task<JsonObject> LoadManifestAsync(string layoutId)
    {
        string manifestPath = ManifestPath(layoutId);
        SyncDocument? doc = await _fileService.ReadDocumentAsync(manifestPath, _pathProvider.Path);
        if (doc?.Content is not JsonObject manifest)
        {
            throw new FileNotFoundException(
                $"Manifest not found or unparseable at {manifestPath}.", manifestPath);
        }
        return manifest;
    }

    private string ManifestPath(string layoutId)
        => Path.Combine(_pathProvider.Path, layoutId, "manifests", "layout-manifest.json");
}
