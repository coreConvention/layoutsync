using System.Text.Json.Nodes;

namespace LayoutSync.Models;

/// <summary>
/// The outcome of a manifest mutation operation (single route or batch). Designed to be
/// the stable contract that the CLI's <c>--json</c> output and the MCP server's tool
/// responses both serialize, so callers (CI, scripts, MCP clients) can rely on one shape.
/// </summary>
/// <param name="Success">
/// <c>true</c> if no errors were recorded AND every requested change either applied or
/// was intentionally skipped under <see cref="BatchErrorMode.Skip"/>. <c>false</c> if any
/// patch was aborted or any file/DB write failed.
/// </param>
/// <param name="Changes">
/// Per-route status snapshots. Always populated (even when nothing was written under
/// <c>--dry-run</c> or <see cref="BatchErrorMode.Abort"/>) so the caller can see exactly
/// what would-have-happened.
/// </param>
/// <param name="Errors">
/// Top-level errors that aren't tied to a specific route — e.g. "manifest file not found",
/// "patches.json schema invalid". Per-route errors live on the corresponding
/// <see cref="RouteChange"/>.
/// </param>
/// <param name="Warnings">
/// Non-fatal advisories — e.g. "structural section name already matches current value;
/// no-op." Empty by default.
/// </param>
public sealed record MutationResult(
    bool Success,
    IReadOnlyList<RouteChange> Changes,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Per-route record of what changed (or would have changed) during a manifest mutation.
/// Carries enough state to render a human-readable diff and a machine-consumable
/// RFC 6902 JSON Patch.
/// </summary>
/// <param name="Route">The route key, e.g. <c>/events/my-rsvps</c>.</param>
/// <param name="Before">
/// The route's <c>routeConfigs</c> entry before mutation, or <c>null</c> if the route
/// did not exist (i.e. the mutation would create it).
/// </param>
/// <param name="After">
/// The route's <c>routeConfigs</c> entry after mutation, or <c>null</c> if the patch was
/// aborted/skipped before composing the after-state.
/// </param>
/// <param name="Patch">
/// RFC 6902 JSON Patch array describing the diff between <see cref="Before"/> and
/// <see cref="After"/>. <c>null</c> when the patch was aborted/skipped.
/// </param>
/// <param name="Status">
/// Final disposition of this patch — applied, skipped (under
/// <see cref="BatchErrorMode.Skip"/>), or aborted (under <see cref="BatchErrorMode.Abort"/>
/// when any sibling patch failed).
/// </param>
/// <param name="Error">
/// Human-readable error message if the patch was rejected. <c>null</c> for applied or
/// aborted-due-to-sibling patches.
/// </param>
public sealed record RouteChange(
    string Route,
    JsonObject? Before,
    JsonObject? After,
    JsonArray? Patch,
    RouteChangeStatus Status,
    string? Error);

/// <summary>
/// Final disposition of a single patch in a mutation operation.
/// </summary>
public enum RouteChangeStatus
{
    /// <summary>The patch validated and was written to the manifest (or would be, under <c>--dry-run</c>).</summary>
    Applied,

    /// <summary>
    /// The patch failed validation but was non-fatally skipped under
    /// <see cref="BatchErrorMode.Skip"/>. Other patches in the batch may still apply.
    /// </summary>
    Skipped,

    /// <summary>
    /// The patch — or a sibling patch in its batch — failed validation under
    /// <see cref="BatchErrorMode.Abort"/>, so nothing was written.
    /// </summary>
    Aborted,
}
