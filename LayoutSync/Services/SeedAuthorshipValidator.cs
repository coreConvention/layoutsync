using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LayoutSync.Models;
using Microsoft.Extensions.Logging;

namespace LayoutSync.Services;

/// <summary>
/// Inspects seed entity files for raw-NanoID values in identity-bearing fields and emits a
/// non-blocking warning when any are found.
///
/// Motivation: identity-bearing fields (`adminIds`, `memberIds`, `ownerId`, etc.) accept a tagged
/// union of two formats — a runtime-minted NanoID (env-specific, opaque) or a stable tagged ref
/// `ext:{provider}:{externalId}` (portable across envs, human-reviewable, authored from known
/// data like an Entra OID). Both are valid at runtime, but raw NanoIDs in seed JSON reintroduce
/// the bootstrap cliff every time a seed is authored from scratch and silently couple the seed to
/// whichever environment minted the NanoID. This validator nudges seed authors toward `ext:*`
/// without enforcing it — not every identity has a provider subject claim yet.
///
/// Scope: only <see cref="DocumentType.Entity"/>. Runtime-created docs and non-entity system
/// collections are skipped. Inspection is pure — <paramref name="contentToSync"/> is never
/// mutated.
///
/// Output: at most one <c>LogWarning</c> per offending seed file, with all flagged JSON paths
/// aggregated into a single message.
/// </summary>
public partial class SeedAuthorshipValidator(ILogger<SeedAuthorshipValidator> logger)
{
    private readonly ILogger<SeedAuthorshipValidator> _logger = logger;

    /// <summary>
    /// Number of seed files that emitted at least one raw-NanoID authorship warning during
    /// this service's lifetime. Consumed by <c>--strict</c> mode to fail the sync when any
    /// authorship drift is present.
    ///
    /// One offense per file (not per field) so a file with six offending fields counts as
    /// a single authorship offense — matches the operator-facing WARN-per-file log shape.
    /// </summary>
    public int AuthorshipWarningCount { get; private set; }

    /// <summary>
    /// NanoID-looking value detector. Generous length bounds (18-28) accommodate historical
    /// NanoIDs that pre-date the current 21-char standard. Pure `[A-Za-z0-9_-]` with no `:`
    /// separator — anything containing `:` is assumed to be a tagged ref (`ext:provider:id`)
    /// and is not flagged here.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9_-]{18,28}$")]
    private static partial Regex NanoIdPattern();

    /// <summary>
    /// Index-level identity fields. These live under <c>indexes.*</c> on the wrapped document.
    /// Scalar values (<c>ownerId</c>, <c>userId</c>, ...) and array values (<c>adminIds</c>,
    /// <c>memberIds</c>, <c>attendeeIds</c>) are both handled.
    /// </summary>
    private static readonly string[] IndexIdentityFields =
    [
        "adminIds",
        "memberIds",
        "ownerId",
        "userId",
        "organizerId",
        "hostId",
        "identityId",
        "attendeeIds",
    ];

    /// <summary>
    /// Scalar identity fields that live directly on <c>data.*</c>.
    /// </summary>
    private static readonly string[] DataScalarIdentityFields =
    [
        "organizerId",
        "ownerId",
    ];

    /// <summary>
    /// Collection-of-objects fields under <c>data.*</c> where each element has an
    /// <c>identityId</c> property (e.g. <c>data.members[i].identityId</c>).
    /// </summary>
    private static readonly string[] DataIdentityCollectionFields =
    [
        "members",
        "attendees",
    ];

    /// <summary>
    /// Inspects a wrapped seed document for raw-NanoID identity references and emits a single
    /// <c>LogWarning</c> per file when any are found. No-ops for non-entity documents.
    /// </summary>
    /// <param name="documentType">Document type as classified by LayoutSync.</param>
    /// <param name="relativePath">Relative path used in the warning message.</param>
    /// <param name="content">Wrapped content (identifier/type/active/tags/indexes/data). Not mutated.</param>
    public void Validate(DocumentType documentType, string relativePath, JsonObject content)
    {
        if (documentType != DocumentType.Entity)
            return;

        List<string> offendingPaths = [];

        if (content["indexes"] is JsonObject indexes)
        {
            foreach (string field in IndexIdentityFields)
                CollectOffendingPaths(indexes[field], $"indexes.{field}", offendingPaths);
        }

        if (content["data"] is JsonObject data)
        {
            foreach (string field in DataScalarIdentityFields)
                CollectOffendingPaths(data[field], $"data.{field}", offendingPaths);

            foreach (string field in DataIdentityCollectionFields)
            {
                if (data[field] is JsonArray collection)
                {
                    for (int i = 0; i < collection.Count; i++)
                    {
                        if (collection[i] is JsonObject element)
                            CollectOffendingPaths(
                                element["identityId"],
                                $"data.{field}[{i}].identityId",
                                offendingPaths);
                    }
                }
            }
        }

        if (offendingPaths.Count == 0)
            return;

        AuthorshipWarningCount++;

        _logger.LogWarning(
            "Seed file uses raw NanoID for identity references — consider migrating to `ext:{{provider}}:{{externalId}}`.\n"
            + "        File: {File}\n"
            + "        Fields: {Fields}\n"
            + "        See .claude/references/architecture-patterns.md (\"Stable Identity References\").",
            relativePath,
            string.Join(", ", offendingPaths));
    }

    /// <summary>
    /// Adds <paramref name="jsonPath"/> (or indexed variants for arrays) to
    /// <paramref name="offendingPaths"/> if <paramref name="node"/> contains a raw-NanoID value.
    /// Arrays are walked element-by-element so the warning enumerates only the offending indices.
    /// </summary>
    private static void CollectOffendingPaths(JsonNode? node, string jsonPath, List<string> offendingPaths)
    {
        if (node is JsonValue value)
        {
            if (IsRawNanoId(value))
                offendingPaths.Add(jsonPath);
            return;
        }

        if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue element && IsRawNanoId(element))
                    offendingPaths.Add($"{jsonPath}[{i}]");
            }
        }
    }

    /// <summary>
    /// Returns true if the JSON value is a non-empty string matching the NanoID shape and is
    /// NOT a tagged reference (<c>ext:*</c>). Empty strings and non-string values are ignored.
    /// </summary>
    private static bool IsRawNanoId(JsonValue value)
    {
        string? str = value.GetValue<object>() is string s ? s : null;
        if (string.IsNullOrEmpty(str))
            return false;
        if (str.StartsWith("ext:", StringComparison.Ordinal))
            return false;
        return NanoIdPattern().IsMatch(str);
    }
}
