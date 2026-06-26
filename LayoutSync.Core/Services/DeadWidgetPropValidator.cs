using System.Text.Json.Nodes;
using LayoutSync.Models;
using Microsoft.Extensions.Logging;

namespace LayoutSync.Services;

/// <summary>
/// Scans UI-schema section documents for widget props that the renderer never reads — "dead" /
/// no-op props that look meaningful in JSON but have zero runtime effect. Emits a single
/// non-blocking <c>LogWarning</c> per offending section file.
///
/// First (and currently only) rule: <c>defaultExpanded</c> on a <c>floating-panel</c> element.
/// The floating-panel widget controls collapse state per <c>panel-section</c> via
/// <c>initialExpanded</c> (default <c>false</c>); <c>defaultExpanded</c> on the panel element
/// itself is never read. It is set on the panel in dirt-life's <c>trail-report-detail.json</c>
/// and has no effect. See w31rd.com issue #984 and <c>docs/systems/floating-panel-system.md</c>.
///
/// IMPORTANT — the rule is TYPE-SCOPED. <c>defaultExpanded</c> is a LEGITIMATE prop on the
/// unrelated <c>accordion</c> widget (array form, e.g. <c>["about"]</c>), so a blanket key match
/// would produce false positives. <see cref="DeadPropsByType"/> pairs each prop with the exact
/// widget <c>type</c> on which it is dead, so props that are valid elsewhere are never flagged.
///
/// Scope: only <see cref="DocumentType.Section"/> documents (the widget tree lives under
/// <c>data</c>). Inspection is pure — <paramref name="content"/> is never mutated. Mirrors the
/// lifecycle of <see cref="SeedAuthorshipValidator"/>: instantiated as a DI singleton, accumulates
/// an offense counter, and is consulted by <c>--strict</c> mode to fail the process with exit
/// code 2 when any offenses were detected. Detection-only — never auto-corrects.
/// </summary>
public class DeadWidgetPropValidator(ILogger<DeadWidgetPropValidator> logger)
{
    private readonly ILogger<DeadWidgetPropValidator> _logger = logger;

    /// <summary>
    /// Known dead/no-op props, keyed by the widget <c>type</c> on which they have no effect. A
    /// prop is only flagged when it appears on an element of the paired type, so props that are
    /// legitimate on other widgets (e.g. <c>defaultExpanded</c> on <c>accordion</c>) are never
    /// false-flagged. Add future (type, prop) pairs here as new dead props are discovered.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, HashSet<string>> DeadPropsByType =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["floating-panel"] = new(StringComparer.Ordinal) { "defaultExpanded" },
        };

    /// <summary>
    /// Number of section files that emitted at least one dead-prop warning during this service's
    /// lifetime. Consumed by <c>--strict</c> mode in <c>Program.cs</c> to fail the sync.
    ///
    /// One offense per file (not per prop) so a section with three dead props counts as a single
    /// offense — matches the operator-facing WARN-per-file log shape and
    /// <see cref="SeedAuthorshipValidator.AuthorshipWarningCount"/>'s semantics.
    /// </summary>
    public int DeadPropWarningCount { get; private set; }

    /// <summary>
    /// Inspects a wrapped section document's <c>data</c> widget tree for dead props and emits a
    /// single <c>LogWarning</c> per file when any are found. No-ops for non-section documents.
    /// </summary>
    /// <param name="documentType">Document type as classified by LayoutSync.</param>
    /// <param name="relativePath">Relative path used in the warning message.</param>
    /// <param name="content">Wrapped content (identifier/type/active/tags/indexes/data). Not mutated.</param>
    public void Validate(DocumentType documentType, string relativePath, JsonObject content)
    {
        if (documentType != DocumentType.Section)
            return;

        if (content["data"] is not JsonNode data)
            return;

        List<string> offendingPaths = [];
        Walk(data, "data", offendingPaths);

        if (offendingPaths.Count == 0)
            return;

        DeadPropWarningCount++;

        _logger.LogWarning(
            "Section uses dead/no-op widget prop(s) the renderer never reads — remove them.\n"
            + "        File: {File}\n"
            + "        Props: {Props}\n"
            + "        See docs/systems/floating-panel-system.md (\"Hidden semantics\").",
            relativePath,
            string.Join(", ", offendingPaths));
    }

    /// <summary>
    /// Resets the offense counter. Useful for tests that share a single validator instance across
    /// cases. Production callers (singleton DI) should not reset.
    /// </summary>
    public void Reset() => DeadPropWarningCount = 0;

    /// <summary>
    /// Depth-first walk of the widget subtree. For every object whose <c>type</c> has a dead-prop
    /// rule, flags each dead prop present under its <c>props</c>. Recurses through every array and
    /// nested object so panels nested at any depth (e.g. inside a detail page's <c>children</c>)
    /// are seen. Pure — appends offending JSON paths to <paramref name="offendingPaths"/> only.
    /// </summary>
    private static void Walk(JsonNode? node, string path, List<string> offendingPaths)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["type"] is JsonValue typeValue
                    && typeValue.TryGetValue(out string? type)
                    && !string.IsNullOrEmpty(type)
                    && DeadPropsByType.TryGetValue(type, out HashSet<string>? deadProps)
                    && obj["props"] is JsonObject props)
                {
                    foreach (string deadProp in deadProps)
                    {
                        if (props.ContainsKey(deadProp))
                            offendingPaths.Add($"{path}.props.{deadProp} (dead on '{type}')");
                    }
                }

                foreach (KeyValuePair<string, JsonNode?> child in obj)
                    Walk(child.Value, $"{path}.{child.Key}", offendingPaths);
                break;

            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                    Walk(array[i], $"{path}[{i}]", offendingPaths);
                break;
        }
    }
}
