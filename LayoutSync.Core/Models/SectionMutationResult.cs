using System.Text.Json.Nodes;

namespace LayoutSync.Models;

/// <summary>
/// The outcome of one change to a manifest's <c>entities.sections</c> registry (add, rename or
/// remove). The CLI's <c>--json</c> output and the MCP section tools both serialize this record,
/// so the two surfaces report the same shape.
/// </summary>
/// <param name="Success">
/// <c>true</c> when the change was valid and was written (or, under <c>--dry-run</c>, would
/// be). <c>false</c> when it was refused, in which case nothing was written, or in the rare
/// case that a write failed and could not be rolled back, in which case
/// <see cref="FilesChanged"/> lists what is left changed and <see cref="Errors"/> says how
/// to recover.
/// </param>
/// <param name="ManifestPath">
/// Absolute path of the <c>layout-manifest.json</c> the operation targeted, on every result
/// including refusals. With several git worktrees on disk the same layout id maps to a
/// different file in each, so this is what tells a caller which checkout was touched
/// (issue #29; the route commands' <see cref="MutationResult.ManifestPath"/> is the same
/// field). Positional rather than init-only because this type is new: every construction
/// site has to supply it.
/// </param>
/// <param name="Identifier">The section identifier the operation targeted.</param>
/// <param name="NewIdentifier">The replacement identifier for a rename; <c>null</c> otherwise.</param>
/// <param name="Before">
/// The registry entry before the change, or <c>null</c> when it did not exist (add).
/// </param>
/// <param name="After">
/// The registry entry after the change, or <c>null</c> when it no longer exists (remove) or the
/// change was refused.
/// </param>
/// <param name="Patch">
/// RFC 6902 operations that turn the original manifest into the new one. Paths are JSON
/// Pointers from the manifest root, so the patch can be applied as-is. <c>null</c> when the
/// change was refused (nothing was written).
/// </param>
/// <param name="References">
/// JSON Pointers of every section-reference field that named <see cref="Identifier"/> before
/// the change: rewritten by a rename, blocking (or left dangling) for a remove, resolved by an add.
/// </param>
/// <param name="FilesChanged">
/// Absolute paths of every file written (or, under <c>--dry-run</c>, every file that would be).
/// </param>
/// <param name="Errors">Why the change was refused. Empty when <see cref="Success"/> is true.</param>
/// <param name="Warnings">
/// Non-fatal problems the change leaves behind, such as references left dangling by a forced
/// remove, or a section file whose own identifier does not match the registry. <c>--strict</c>
/// turns any warning into exit code 2.
/// </param>
public sealed record SectionMutationResult(
    bool Success,
    string ManifestPath,
    string Identifier,
    string? NewIdentifier,
    JsonObject? Before,
    JsonObject? After,
    JsonArray? Patch,
    IReadOnlyList<string> References,
    IReadOnlyList<string> FilesChanged,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// A new <c>entities.sections</c> entry to declare. Only <see cref="Identifier"/> is required;
/// the defaults follow the convention every existing registry entry uses. A blank optional
/// value is treated the same as <c>null</c>.
/// </summary>
/// <param name="Identifier">The section identifier, e.g. <c>events-page-header</c>.</param>
/// <param name="Type">Section type. <c>null</c> = <c>ui-schema-section</c>.</param>
/// <param name="File">
/// Section file path relative to the layout directory. <c>null</c> = <c>sections/{Identifier}.json</c>.
/// </param>
/// <param name="Description">Optional description. <c>null</c> = no <c>description</c> key.</param>
public sealed record SectionEntryInput(
    string Identifier,
    string? Type = null,
    string? File = null,
    string? Description = null);
