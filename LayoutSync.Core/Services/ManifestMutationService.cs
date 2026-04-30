using System.Text.Json.Nodes;
using LayoutSync.Models;
using Microsoft.Extensions.Logging;

namespace LayoutSync.Services;

/// <summary>
/// Programmatic mutator for <c>layouts/{layoutId}/manifests/layout-manifest.json</c>.
/// The single canonical entry point for every change to <c>routeConfigs</c> — replaces
/// the ad-hoc <c>node -e "..."</c> one-liners that previously edited the file off-pipeline.
///
/// Architecture:
/// <list type="bullet">
///   <item>Reads the manifest as a <see cref="JsonObject"/> via <see cref="LocalFileService"/>.</item>
///   <item>Runs <see cref="ManifestSectionValidator"/> against the proposed patches before
///         touching any state.</item>
///   <item>Applies validated patches to the in-memory <see cref="JsonObject"/>; subsequent
///         patches in the same batch see the cumulative state.</item>
///   <item>Writes the file back via <see cref="LocalFileService"/> unless <c>dryRun</c>
///         is set or no patch in the batch was applied.</item>
///   <item>Computes a shallow RFC 6902 diff per route so callers (CLI <c>--json</c>, MCP
///         server) get a structured before/after.</item>
/// </list>
///
/// Persistence to RavenDB is the existing <c>DocumentSyncService</c>'s job — this service
/// only mutates the source file. Callers that want the manifest synced to the database
/// after mutation should run LayoutSync's normal sync flow (or, in the MCP case, call the
/// dedicated <c>layout_sync</c> tool).
/// </summary>
public class ManifestMutationService(
    LocalFileService fileService,
    ManifestSectionValidator validator,
    ILogger<ManifestMutationService> logger)
{
    private readonly LocalFileService _fileService = fileService;
    private readonly ManifestSectionValidator _validator = validator;
    private readonly ILogger<ManifestMutationService> _logger = logger;

    /// <summary>
    /// Convenience wrapper for single-route mutations. Equivalent to
    /// <see cref="ApplyBatchAsync"/> with a one-element list and <see cref="BatchErrorMode.Abort"/>.
    /// </summary>
    public Task<MutationResult> SetRouteAsync(
        string layoutsPath,
        string layoutId,
        RoutePatchInput patch,
        bool dryRun,
        CancellationToken ct = default)
        => ApplyBatchAsync(layoutsPath, layoutId, [patch], BatchErrorMode.Abort, dryRun, ct);

    /// <summary>
    /// Applies one or more <see cref="RoutePatchInput"/> records to the named layout's
    /// <c>layout-manifest.json</c>. Behavior on validation failure is governed by
    /// <paramref name="onError"/>.
    /// </summary>
    /// <param name="layoutsPath">Absolute path to the <c>layouts/</c> directory.</param>
    /// <param name="layoutId">The tenant layout, e.g. <c>dirt-life</c>.</param>
    /// <param name="patches">Mutations to apply. Order is preserved; later patches see earlier patches' state.</param>
    /// <param name="onError">How to react to validation failures.</param>
    /// <param name="dryRun">If true, skip the file write — but compute and return the would-be diff.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<MutationResult> ApplyBatchAsync(
        string layoutsPath,
        string layoutId,
        IReadOnlyList<RoutePatchInput> patches,
        BatchErrorMode onError,
        bool dryRun,
        CancellationToken ct = default)
    {
        // 1. Pre-flight checks for self-conflicting patches (e.g. --patch-main and
        //    --remove-patch main on the same route). These aren't validation errors
        //    against manifest state — they're contradictions in the input itself, so
        //    they're surfaced as top-level Errors, not per-route errors.
        List<string> topLevelErrors = [];
        foreach (RoutePatchInput patch in patches)
        {
            if (patch.MainSections is not null && patch.RemoveMain)
                topLevelErrors.Add(
                    $"Route {patch.Route}: --patch-main and --remove-patch main are mutually exclusive.");
            if (patch.SidebarSections is not null && patch.RemoveSidebar)
                topLevelErrors.Add(
                    $"Route {patch.Route}: --patch-sidebar and --remove-patch sidebar are mutually exclusive.");
        }
        if (topLevelErrors.Count > 0)
        {
            return new MutationResult(
                Success: false,
                Changes: [],
                Errors: topLevelErrors,
                Warnings: []);
        }

        // 2. Load the manifest. The path convention matches LayoutSync's existing
        //    discovery in LocalFileService.DiscoverFiles (manifests live under
        //    layouts/{layoutId}/manifests/).
        string manifestPath = Path.Combine(layoutsPath, layoutId, "manifests", "layout-manifest.json");
        SyncDocument? manifestDoc = await _fileService.ReadDocumentAsync(manifestPath, layoutsPath);
        if (manifestDoc?.Content is not JsonObject manifest)
        {
            return new MutationResult(
                Success: false,
                Changes: [],
                Errors: [$"Manifest not found or unparseable at {manifestPath}."],
                Warnings: []);
        }

        // 3. Validate. The validator only inspects state — it doesn't mutate.
        SectionValidationResult validation = _validator.Validate(manifest, patches);

        // 4. Decide which routes to apply vs reject based on error mode.
        HashSet<string> routesToReject = onError switch
        {
            BatchErrorMode.Abort when !validation.AllValid
                => new HashSet<string>(patches.Select(p => p.Route), StringComparer.Ordinal),
            BatchErrorMode.Skip
                => new HashSet<string>(validation.ErrorsByRoute.Keys, StringComparer.Ordinal),
            _ => [],
        };

        // 5. Apply patches in order. The mutation is performed against the live manifest
        //    JsonObject so later patches in the batch see earlier patches' state.
        JsonObject routeConfigs = GetOrCreateRouteConfigs(manifest);
        List<RouteChange> changes = [];
        bool anyApplied = false;

        foreach (RoutePatchInput patch in patches)
        {
            JsonObject? before = CloneRouteIfPresent(routeConfigs, patch.Route);

            if (routesToReject.Contains(patch.Route))
            {
                RouteChangeStatus status = onError == BatchErrorMode.Abort
                    ? RouteChangeStatus.Aborted
                    : RouteChangeStatus.Skipped;

                string? error = validation.ErrorsByRoute.TryGetValue(patch.Route, out IReadOnlyList<string>? errs)
                    ? string.Join(" ", errs)
                    : null;

                changes.Add(new RouteChange(
                    Route: patch.Route,
                    Before: before,
                    After: null,
                    Patch: null,
                    Status: status,
                    Error: error));
                continue;
            }

            JsonObject after = ApplyPatchToRoute(routeConfigs, patch);
            JsonArray patchOps = ComputeRfc6902Diff(before, after);
            changes.Add(new RouteChange(
                Route: patch.Route,
                Before: before,
                After: after,
                Patch: patchOps,
                Status: RouteChangeStatus.Applied,
                Error: null));
            anyApplied = true;
        }

        // 6. Persist (unless dry-run or nothing actually changed).
        if (anyApplied && !dryRun)
        {
            await _fileService.WriteDocumentAsync(manifestPath, manifest);
            _logger.LogInformation(
                "Manifest mutation applied: {AppliedCount} route(s), {SkippedCount} skipped.",
                changes.Count(c => c.Status == RouteChangeStatus.Applied),
                changes.Count(c => c.Status == RouteChangeStatus.Skipped));
        }

        bool anyAborted = changes.Any(c => c.Status == RouteChangeStatus.Aborted);
        return new MutationResult(
            Success: !anyAborted,
            Changes: changes,
            Errors: [],
            Warnings: []);
    }

    /// <summary>
    /// Returns the manifest's <c>routeConfigs</c> object, creating an empty one in place
    /// if it's missing. The created entry mutates <paramref name="manifest"/>; this is
    /// intentional — the caller is mid-mutation.
    /// </summary>
    private static JsonObject GetOrCreateRouteConfigs(JsonObject manifest)
    {
        if (manifest["routeConfigs"] is not JsonObject existing)
        {
            JsonObject created = [];
            manifest["routeConfigs"] = created;
            return created;
        }
        return existing;
    }

    /// <summary>
    /// Returns a deep clone of <c>routeConfigs[route]</c>, or <c>null</c> if the route
    /// key is absent. Cloning matters because callers store the result as the "before"
    /// snapshot — without a clone, in-place mutation would silently rewrite the snapshot.
    /// </summary>
    private static JsonObject? CloneRouteIfPresent(JsonObject routeConfigs, string route)
    {
        if (routeConfigs[route] is JsonObject existing)
        {
            return existing.DeepClone().AsObject();
        }
        return null;
    }

    /// <summary>
    /// Applies <paramref name="patch"/> to <c>routeConfigs[patch.Route]</c>, creating
    /// the route entry if absent. Returns a deep clone of the post-mutation state, which
    /// the caller pairs with the pre-mutation clone to compute the diff.
    /// </summary>
    private static JsonObject ApplyPatchToRoute(JsonObject routeConfigs, RoutePatchInput patch)
    {
        // Locate or create the route's config entry.
        JsonObject route;
        if (routeConfigs[patch.Route] is JsonObject existing)
        {
            route = existing;
        }
        else
        {
            route = new JsonObject
            {
                ["structuralSection"] = null,
                ["patches"] = new JsonArray(),
            };
            routeConfigs[patch.Route] = route;
        }

        // Apply structuralSection if requested.
        if (patch.StructuralSection is not null)
        {
            route["structuralSection"] = patch.StructuralSection;
        }

        // Apply main / sidebar slot mutations. Each is independent — null = no change.
        ApplySlotChange(route, slot: "main", sections: patch.MainSections, remove: patch.RemoveMain);
        ApplySlotChange(route, slot: "sidebar", sections: patch.SidebarSections, remove: patch.RemoveSidebar);

        return route.DeepClone().AsObject();
    }

    /// <summary>
    /// Applies a single slot mutation against <c>route.patches[]</c>. The patches array
    /// is a list of objects keyed by <c>targetElementId</c> — this method finds the entry
    /// for <paramref name="slot"/> and either replaces its <c>sectionIdentifiers</c>
    /// (when <paramref name="sections"/> is non-null) or removes it (when <paramref name="remove"/>
    /// is true).
    ///
    /// Pre-flight already rejects the case where both flags are set on the same slot, so
    /// only one branch executes here.
    /// </summary>
    private static void ApplySlotChange(
        JsonObject route,
        string slot,
        IReadOnlyList<string>? sections,
        bool remove)
    {
        if (sections is null && !remove)
            return;

        if (route["patches"] is not JsonArray patchesArray)
        {
            patchesArray = [];
            route["patches"] = patchesArray;
        }

        int existingIndex = FindPatchIndex(patchesArray, slot);

        if (remove)
        {
            if (existingIndex >= 0) patchesArray.RemoveAt(existingIndex);
            return;
        }

        // sections is non-null here (either branch is guarded above).
        JsonArray newSectionIdentifiers = [];
        foreach (string id in sections!) newSectionIdentifiers.Add(id);

        if (existingIndex >= 0)
        {
            // Replace the sectionIdentifiers on the existing entry — leave any other
            // properties on the entry untouched (forward-compat with future fields).
            if (patchesArray[existingIndex] is JsonObject existingEntry)
            {
                existingEntry["sectionIdentifiers"] = newSectionIdentifiers;
            }
            return;
        }

        // Insert new entry. Order doesn't matter to the renderer, but appending at the
        // end keeps existing diffs minimal.
        patchesArray.Add(new JsonObject
        {
            ["targetElementId"] = slot,
            ["sectionIdentifiers"] = newSectionIdentifiers,
        });
    }

    /// <summary>
    /// Returns the index of the <c>patches[]</c> entry whose <c>targetElementId</c> equals
    /// <paramref name="slot"/>, or <c>-1</c> if no such entry exists.
    /// </summary>
    private static int FindPatchIndex(JsonArray patches, string slot)
    {
        for (int i = 0; i < patches.Count; i++)
        {
            // TryGetValue<string> handles both JsonNode.Parse-backed and C#-constructed
            // JsonValues. The looser `GetValue<object>() is string` pattern silently
            // fails on parsed JSON because GetValue<object>() returns the underlying
            // JsonElement rather than a boxed string.
            if (patches[i] is JsonObject entry
                && entry["targetElementId"] is JsonValue v
                && v.TryGetValue(out string? id)
                && id == slot)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Computes a shallow RFC 6902 JSON Patch describing the diff between
    /// <paramref name="before"/> and <paramref name="after"/>. The diff is intentionally
    /// non-recursive: any difference at a top-level key produces a single
    /// <c>add</c> / <c>remove</c> / <c>replace</c> op carrying the new (sub-)value
    /// verbatim. For the route-config schema (only <c>structuralSection</c> and
    /// <c>patches</c>), this gives a readable patch without the complexity of a full
    /// recursive diff library.
    /// </summary>
    internal static JsonArray ComputeRfc6902Diff(JsonObject? before, JsonObject? after)
    {
        JsonArray ops = [];
        if (before is null && after is null) return ops;

        if (before is null)
        {
            ops.Add(new JsonObject
            {
                ["op"] = "add",
                ["path"] = "",
                ["value"] = after!.DeepClone(),
            });
            return ops;
        }

        if (after is null)
        {
            ops.Add(new JsonObject
            {
                ["op"] = "remove",
                ["path"] = "",
            });
            return ops;
        }

        HashSet<string> allKeys = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> entry in before) allKeys.Add(entry.Key);
        foreach (KeyValuePair<string, JsonNode?> entry in after) allKeys.Add(entry.Key);

        foreach (string key in allKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            JsonNode? leftNode = before[key];
            JsonNode? rightNode = after[key];

            bool inLeft = before.ContainsKey(key);
            bool inRight = after.ContainsKey(key);

            if (inLeft && !inRight)
            {
                ops.Add(new JsonObject
                {
                    ["op"] = "remove",
                    ["path"] = $"/{EscapeJsonPointer(key)}",
                });
                continue;
            }

            if (!inLeft && inRight)
            {
                ops.Add(new JsonObject
                {
                    ["op"] = "add",
                    ["path"] = $"/{EscapeJsonPointer(key)}",
                    ["value"] = rightNode is null ? null : rightNode.DeepClone(),
                });
                continue;
            }

            // Both sides have the key — emit replace if the JSON-serialized form differs.
            string leftJson = leftNode?.ToJsonString() ?? "null";
            string rightJson = rightNode?.ToJsonString() ?? "null";
            if (leftJson != rightJson)
            {
                ops.Add(new JsonObject
                {
                    ["op"] = "replace",
                    ["path"] = $"/{EscapeJsonPointer(key)}",
                    ["value"] = rightNode is null ? null : rightNode.DeepClone(),
                });
            }
        }

        return ops;
    }

    /// <summary>
    /// RFC 6901 JSON Pointer escaping: <c>~</c> becomes <c>~0</c> and <c>/</c> becomes
    /// <c>~1</c>. Necessary because route keys typically contain <c>/</c> (e.g.
    /// <c>/events/my-rsvps</c>) — without escaping, the pointer would be ambiguous.
    /// </summary>
    private static string EscapeJsonPointer(string token)
        => token.Replace("~", "~0").Replace("/", "~1");
}
