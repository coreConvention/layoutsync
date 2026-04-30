namespace LayoutSync.Models;

/// <summary>
/// One unit of work for a manifest mutation: describes what to change about a single
/// route entry under <c>routeConfigs</c> in <c>layout-manifest.json</c>.
///
/// Field semantics intentionally distinguish between "leave unchanged" (the field is
/// <c>null</c>) and "set to empty / remove" (an explicit empty list or the matching
/// <c>RemoveMain</c> / <c>RemoveSidebar</c> flag). This lets a caller express a partial
/// patch — for example, swapping the <c>structuralSection</c> while leaving the existing
/// patches untouched — without having to read the manifest first.
/// </summary>
/// <param name="Route">
/// The route key, e.g. <c>/events/my-rsvps</c>. Looked up in <c>routeConfigs[Route]</c>.
/// If the key is absent, the mutation creates it.
/// </param>
/// <param name="StructuralSection">
/// New value for <c>routeConfigs[Route].structuralSection</c>. <c>null</c> = leave unchanged.
/// </param>
/// <param name="MainSections">
/// New <c>sectionIdentifiers</c> for the patch with <c>targetElementId == "main"</c>.
/// <c>null</c> = leave unchanged. Empty list = explicit empty (will produce a patch entry
/// with no sections, which is a degenerate but valid manifest shape — prefer
/// <c>RemoveMain = true</c> to drop the slot entirely).
/// </param>
/// <param name="SidebarSections">
/// Same semantics as <see cref="MainSections"/> but for <c>targetElementId == "sidebar"</c>.
/// </param>
/// <param name="RemoveMain">
/// If true, drop the patch entry where <c>targetElementId == "main"</c>. Mutually
/// exclusive with a non-null <see cref="MainSections"/> — the mutation service rejects
/// requests that specify both.
/// </param>
/// <param name="RemoveSidebar">
/// Same semantics as <see cref="RemoveMain"/> but for the sidebar slot.
/// </param>
public sealed record RoutePatchInput(
    string Route,
    string? StructuralSection = null,
    IReadOnlyList<string>? MainSections = null,
    IReadOnlyList<string>? SidebarSections = null,
    bool RemoveMain = false,
    bool RemoveSidebar = false);

/// <summary>
/// How <c>ManifestMutationService.ApplyBatchAsync</c> should react when one patch in a
/// batch fails validation.
/// </summary>
public enum BatchErrorMode
{
    /// <summary>
    /// The whole batch is rejected on the first validation failure. No file or DB writes
    /// occur. The <see cref="MutationResult"/> reports every invalid patch's error AND
    /// marks all valid patches as <see cref="RouteChangeStatus.Aborted"/> so the caller
    /// can see what would have applied. Default mode — atomic.
    /// </summary>
    Abort,

    /// <summary>
    /// Invalid patches are skipped (not applied, status =
    /// <see cref="RouteChangeStatus.Skipped"/>) and valid patches are applied normally.
    /// Useful for iterative cookbook work where a typo in one of N patches shouldn't
    /// block the other N-1.
    /// </summary>
    Skip,
}
