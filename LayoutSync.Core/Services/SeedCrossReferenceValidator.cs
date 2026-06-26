using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LayoutSync.Models;
using Microsoft.Extensions.Logging;

namespace LayoutSync.Services;

/// <summary>
/// Detects cross-referenced seed entity documents whose target either (a) has no owning seed in
/// the batch (dangling reference) or (b) has an owning seed that did NOT pin
/// <c>@metadata.@id</c> (the owner's id regenerates on every sync, silently breaking the
/// reference). Emits a WARN per offending referencer file; when operated in strict mode, the
/// warnings contribute to the process-level non-zero exit gate.
///
/// Motivation: when a seed's doc id is externally referenced (e.g. <c>data.eventId</c> holds the
/// target's NanoID literally), the target seed MUST pin <c>@metadata.@id</c> — otherwise the
/// next cold sync mints a fresh NanoID for the target and every referencer points at nothing.
/// LayoutSync can occasionally be pointed at production for legitimate reseeding; a dangling
/// cross-reference here isn't a style nit, it's a silent data-integrity failure.
///
/// Scope: only <see cref="DocumentType.Entity"/>. Only scalar string values under
/// <c>indexes.*</c> and <c>data.*</c> are inspected for NanoID-shaped cross-references.
/// Tagged refs (<c>ext:*</c>), empty strings, and non-NanoID-shaped scalars are ignored.
///
/// Lifecycle: <see cref="Inspect(DocumentType, string, JsonObject)"/> is called during
/// <c>SyncFileAsync</c> to accumulate per-file metadata (declared id, pinned-ness, outbound
/// reference list). After the full batch completes, <see cref="FinalizeBatch"/> performs the
/// cross-check and emits one aggregated warning per offending referencer file.
///
/// Tenant-agnostic: this validator reasons about NanoID grammar and cross-reference structure
/// only. It never knows or branches on tenant identifiers.
/// </summary>
public partial class SeedCrossReferenceValidator(ILogger<SeedCrossReferenceValidator> logger) : ISeedValidator
{
    private readonly ILogger<SeedCrossReferenceValidator> _logger = logger;

    /// <inheritdoc />
    public string Name => "cross-reference";

    /// <summary>
    /// Accumulator of every seed file seen in the current sync batch, keyed by the file's
    /// relative path. Order is not significant — the cross-check is a set/map operation.
    /// </summary>
    private readonly Dictionary<string, SeedRecord> _seeds = [];

    /// <summary>
    /// Count of warning lines emitted during the most recent <see cref="FinalizeBatch"/> pass.
    /// Consumed by <c>Program.cs</c> to decide the <c>--strict</c> exit code. Each warning
    /// corresponds to exactly one offending referencer file, so this is also the number of
    /// distinct referencers with violations.
    /// </summary>
    public int WarningCount { get; private set; }

    /// <inheritdoc />
    public string StrictWarningDetail =>
        "seed file(s) contain cross-references whose target is either unpinned or missing. "
        + "See WARN lines above for specific JSON paths.";

    /// <summary>
    /// NanoID-looking value detector. Generous length bounds (18-28) match
    /// <see cref="SeedAuthorshipValidator"/> and accommodate historical NanoIDs that pre-date
    /// the current 21-char standard. Pure <c>[A-Za-z0-9_-]</c> with no <c>:</c> separator —
    /// tagged refs (<c>ext:provider:id</c>) are never flagged here.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9_-]{18,28}$")]
    private static partial Regex NanoIdPattern();

    /// <summary>
    /// Records a seed file for later cross-reference validation. No-ops for non-entity
    /// document types and for files with no parseable content.
    /// </summary>
    /// <param name="documentType">Document type as classified by LayoutSync.</param>
    /// <param name="relativePath">Relative path used as the record key and in warning messages.</param>
    /// <param name="content">Wrapped seed content (identifier/type/indexes/data/@metadata). Not mutated.</param>
    public void Inspect(DocumentType documentType, string relativePath, JsonObject content)
    {
        if (documentType != DocumentType.Entity)
            return;

        string? declaredId = content["@metadata"]?.AsObject()?["@id"]?.GetValue<string>();
        bool pinned = !string.IsNullOrEmpty(declaredId);

        List<OutboundReference> outbound = [];
        if (content["indexes"] is JsonObject indexes)
            CollectOutboundReferences(indexes, "indexes", outbound);
        if (content["data"] is JsonObject data)
            CollectOutboundReferences(data, "data", outbound);

        // Drop self-references: a seed referencing its OWN declared id inside its own data/
        // indexes tree is not an outbound cross-reference. The common case here is a seed
        // that embeds its canonical id for runtime denormalization.
        if (pinned)
            outbound.RemoveAll(r => string.Equals(r.Value, declaredId, StringComparison.Ordinal));

        _seeds[relativePath] = new SeedRecord(
            RelativePath: relativePath,
            DeclaredId: declaredId,
            Pinned: pinned,
            OutboundReferences: outbound);
    }

    /// <summary>
    /// Runs the cross-check over every recorded seed. For each outbound NanoID-shaped
    /// reference in each referencer, emits a WARN when the referenced id has either
    /// (a) no owning seed in the batch or (b) an owning seed that did NOT pin its
    /// <c>@metadata.@id</c>. One aggregated WARN per offending referencer file.
    /// </summary>
    /// <returns>The number of warnings emitted (also exposed via <see cref="WarningCount"/>).</returns>
    public int FinalizeBatch()
    {
        // Index owning seeds by their pinned declared id. Unpinned seeds are not indexed here
        // because their declared id is volatile — any match against them is a defect regardless.
        Dictionary<string, SeedRecord> pinnedOwnersById = [];
        // Track every (file-bound) declared id, pinned or not, so we can distinguish
        // "dangling" from "unpinned owner exists".
        List<SeedRecord> allSeeds = [.. _seeds.Values];
        foreach (SeedRecord seed in allSeeds)
        {
            if (seed.Pinned && !string.IsNullOrEmpty(seed.DeclaredId))
                pinnedOwnersById[seed.DeclaredId!] = seed;
        }

        WarningCount = 0;

        foreach (SeedRecord referencer in allSeeds)
        {
            if (referencer.OutboundReferences.Count == 0)
                continue;

            List<string> violationLines = [];

            foreach (OutboundReference reference in referencer.OutboundReferences)
            {
                // Pinned owner exists → reference is safe. Skip.
                if (pinnedOwnersById.ContainsKey(reference.Value))
                    continue;

                // No pinned owner. Does an unpinned owner claim this id? Search all seeds
                // whose current declared id (if any) matches — these are seeds where the
                // owning file either lacked @metadata.@id entirely, or the id was minted
                // fresh during this sync and will mint differently next time.
                SeedRecord? unpinnedOwner = allSeeds.FirstOrDefault(
                    s => !s.Pinned
                         && !string.IsNullOrEmpty(s.DeclaredId)
                         && string.Equals(s.DeclaredId, reference.Value, StringComparison.Ordinal));

                if (unpinnedOwner != null)
                {
                    violationLines.Add(
                        $"{reference.JsonPath}={reference.Value} (unpinned owner: {unpinnedOwner.RelativePath})");
                }
                else
                {
                    violationLines.Add($"{reference.JsonPath}={reference.Value} (<no matching seed>)");
                }
            }

            if (violationLines.Count == 0)
                continue;

            WarningCount++;
            _logger.LogWarning(
                "Seed file contains cross-references whose target is either unpinned or missing — references will break on next cold sync. "
                + "Pin `@metadata.@id` on the target seed(s), or remove the dangling reference.\n"
                + "        File: {File}\n"
                + "        Violations: {Violations}\n"
                + "        See .claude/references/architecture-patterns.md (\"Seed cross-reference pinning\").",
                referencer.RelativePath,
                string.Join("; ", violationLines));
        }

        return WarningCount;
    }

    /// <summary>
    /// Clears all accumulated seed state. Called between separate sync batches (e.g. watch-mode
    /// re-syncs) so a prior batch's state does not leak into the current validation pass.
    /// </summary>
    public void Reset()
    {
        _seeds.Clear();
        WarningCount = 0;
    }

    /// <summary>
    /// Walks <paramref name="node"/> recursively, appending every NanoID-shaped scalar string
    /// found within to <paramref name="outbound"/>, annotated with its JSON path. Arrays are
    /// walked element-by-element; nested objects contribute dotted path segments. Values that
    /// start with <c>ext:</c> are skipped (tagged refs, not NanoIDs).
    /// </summary>
    private static void CollectOutboundReferences(JsonNode node, string jsonPath, List<OutboundReference> outbound)
    {
        switch (node)
        {
            case JsonValue value:
                if (IsCrossReferenceCandidate(value, out string? idValue))
                    outbound.Add(new OutboundReference(jsonPath, idValue!));
                break;

            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonNode child)
                        CollectOutboundReferences(child, $"{jsonPath}[{i}]", outbound);
                }
                break;

            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> kvp in obj)
                {
                    if (kvp.Value is JsonNode child)
                        CollectOutboundReferences(child, $"{jsonPath}.{kvp.Key}", outbound);
                }
                break;
        }
    }

    /// <summary>
    /// Returns <c>true</c> and yields the NanoID string if <paramref name="value"/> is a
    /// non-empty string that matches the NanoID shape and is NOT a tagged reference.
    /// </summary>
    private static bool IsCrossReferenceCandidate(JsonValue value, out string? idValue)
    {
        idValue = null;
        string? str = value.GetValue<object>() is string s ? s : null;
        if (string.IsNullOrEmpty(str))
            return false;
        if (str.StartsWith("ext:", StringComparison.Ordinal))
            return false;
        if (!NanoIdPattern().IsMatch(str))
            return false;

        idValue = str;
        return true;
    }

    /// <summary>
    /// Per-seed record accumulated during the scanning phase. Structural only — the cross-check
    /// phase reads these without further JSON parsing.
    /// </summary>
    private sealed record SeedRecord(
        string RelativePath,
        string? DeclaredId,
        bool Pinned,
        List<OutboundReference> OutboundReferences);

    /// <summary>
    /// A single NanoID-shaped value found inside a seed's <c>indexes.*</c> or <c>data.*</c>
    /// tree, annotated with the greppable JSON path that carried it.
    /// </summary>
    private sealed record OutboundReference(string JsonPath, string Value);
}
