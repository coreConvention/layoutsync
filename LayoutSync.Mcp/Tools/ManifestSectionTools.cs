using System.ComponentModel;
using LayoutSync.Configuration;
using LayoutSync.Models;
using LayoutSync.Services;
using ModelContextProtocol.Server;

namespace LayoutSync.Mcp.Tools;

/// <summary>
/// MCP tools that change a layout manifest's <c>entities.sections</c> registry. Each tool
/// delegates to <see cref="ManifestSectionRegistryService"/> (the same service behind the
/// CLI's <c>manifest add-section</c> / <c>rename-section</c> / <c>remove-section</c>), so
/// validation is identical on both surfaces, and responses use the CLI's <c>--json</c>
/// envelope via <see cref="JsonOutputFormatter"/>.
///
/// A separate class from <see cref="ManifestTools"/> (routes) for the same reason
/// <see cref="ManifestReadTools"/> is: one concern per tool type, one service dependency each.
/// Target checkout: the same per-call <c>layoutsPath</c> contract as <see cref="ManifestTools"/>
/// (issue #29), including its warning when a call falls back to the server's default.
/// </summary>
[McpServerToolType]
public sealed class ManifestSectionTools(
    ManifestSectionRegistryService registryService,
    LayoutsPathProvider pathProvider)
{
    private readonly ManifestSectionRegistryService _service = registryService;
    private readonly LayoutsPathProvider _pathProvider = pathProvider;

    [McpServerTool(Name = "manifest_add_section")]
    [Description(
        "Declare a new section in a layout's entities.sections registry, so manifest_set_route accepts "
        + "its identifier. Refuses an identifier that is already declared. Warns when the section file "
        + "is missing or declares a different identifier. Returns a JSON envelope with the new registry "
        + "entry ('after'), an RFC 6902 patch against the manifest, the files changed, warnings and errors."
        + LayoutsPathProvider.WriteToolNote)]
    public async Task<string> ManifestAddSection(
        [Description("Layout id (e.g. 'dirt-life').")]
        string layoutId,

        [Description("Section identifier to declare, e.g. 'events-page-header'.")]
        string identifier,

        [Description("Optional section type. Default: 'ui-schema-section'.")]
        string? type = null,

        [Description("Optional section file, relative to the layout directory. Default: 'sections/<identifier>.json'.")]
        string? file = null,

        [Description("Optional description stored on the registry entry.")]
        string? description = null,

        [Description("If true, compute the change but do not write any file. Default: false.")]
        bool dryRun = false,

        [Description(LayoutsPathProvider.ParameterDescription)]
        string? layoutsPath = null)
    {
        ResolvedLayoutsPath target = _pathProvider.Resolve(layoutsPath);

        SectionMutationResult result = await _service.AddSectionAsync(
            target.LayoutsPath,
            layoutId,
            new SectionEntryInput(identifier, type, file, description),
            dryRun);

        return FormatEnvelope("manifest add-section", layoutId, dryRun, target, result);
    }

    [McpServerTool(Name = "manifest_rename_section")]
    [Description(
        "Rename a section identifier everywhere in a layout's manifest in one write: its entities.sections "
        + "entry and every field that references it (routeConfigs structuralSection and patch "
        + "sectionIdentifiers, routing.notFoundSection, onboarding and authoring section fields). By default "
        + "also rewrites the section file's own identifier, leaving the rest of that file untouched; the file "
        + "keeps its name. Both files change or neither: if the section file cannot be updated (unreadable, "
        + "invalid JSON, or declaring another identifier) the rename is refused, and manifestOnly=true renames "
        + "just the manifest. Also refuses when the new identifier is already declared. Dry-run first. Returns "
        + "a JSON envelope listing every rewritten reference ('references'), the RFC 6902 patch and every file changed."
        + LayoutsPathProvider.WriteToolNote)]
    public async Task<string> ManifestRenameSection(
        [Description("Layout id (e.g. 'dirt-life').")]
        string layoutId,

        [Description("The section's current identifier.")]
        string identifier,

        [Description("The identifier to rename it to. Must not already be declared.")]
        string newIdentifier,

        [Description("If true, rename only inside layout-manifest.json and leave the section file's own "
                   + "identifier as it is (reported as a warning). Default: false.")]
        bool manifestOnly = false,

        [Description("If true, compute the change but do not write any file. Default: false.")]
        bool dryRun = false,

        [Description(LayoutsPathProvider.ParameterDescription)]
        string? layoutsPath = null)
    {
        ResolvedLayoutsPath target = _pathProvider.Resolve(layoutsPath);

        SectionMutationResult result = await _service.RenameSectionAsync(
            target.LayoutsPath,
            layoutId,
            identifier,
            newIdentifier,
            updateSectionFile: !manifestOnly,
            dryRun);

        return FormatEnvelope("manifest rename-section", layoutId, dryRun, target, result);
    }

    [McpServerTool(Name = "manifest_remove_section")]
    [Description(
        "Remove a section from a layout's entities.sections registry. Refuses while any manifest field still "
        + "references it, and lists those fields as JSON Pointers in 'references'. With force=true it removes "
        + "the entry anyway and reports each reference it leaves dangling as a warning. The section file itself "
        + "is not deleted."
        + LayoutsPathProvider.WriteToolNote)]
    public async Task<string> ManifestRemoveSection(
        [Description("Layout id (e.g. 'dirt-life').")]
        string layoutId,

        [Description("The section identifier to remove.")]
        string identifier,

        [Description("If true, remove the entry even while fields still reference it. Default: false.")]
        bool force = false,

        [Description("If true, compute the change but do not write any file. Default: false.")]
        bool dryRun = false,

        [Description(LayoutsPathProvider.ParameterDescription)]
        string? layoutsPath = null)
    {
        ResolvedLayoutsPath target = _pathProvider.Resolve(layoutsPath);

        SectionMutationResult result = await _service.RemoveSectionAsync(
            target.LayoutsPath, layoutId, identifier, force, dryRun);

        return FormatEnvelope("manifest remove-section", layoutId, dryRun, target, result);
    }

    /// <summary>
    /// Serializes the section envelope. A call that fell back to the server default gets the
    /// same warning <see cref="ManifestTools"/> adds, naming the file it targeted. A write
    /// happened exactly when files were changed outside a dry run; that also holds for a
    /// failed rename that could not be rolled back.
    /// </summary>
    private static string FormatEnvelope(
        string command,
        string layoutId,
        bool dryRun,
        ResolvedLayoutsPath target,
        SectionMutationResult result)
    {
        if (!target.IsExplicit)
        {
            bool wroteFile = !dryRun && result.FilesChanged.Count > 0;
            result = result with
            {
                Warnings = [.. result.Warnings, ManifestTools.DefaultTargetWarning(result.ManifestPath, wroteFile)],
            };
        }

        return JsonOutputFormatter.FormatAsString(command, layoutId, dryRun, result);
    }
}
