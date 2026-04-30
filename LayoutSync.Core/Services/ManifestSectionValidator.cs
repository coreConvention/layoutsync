using System.Text.Json.Nodes;
using LayoutSync.Models;
using Microsoft.Extensions.Logging;

namespace LayoutSync.Services;

/// <summary>
/// Validates that every section identifier referenced by a proposed manifest mutation
/// resolves to an entry under <c>entities.sections[*].identifier</c>. Catches typos like
/// <c>event-fitlers-panel</c> before they make it into the manifest, where they would
/// otherwise render as silently-blank routes at runtime.
///
/// Mirrors the lifecycle pattern used by <see cref="SeedAuthorshipValidator"/> and
/// <see cref="SeedCrossReferenceValidator"/>: instantiated as a DI singleton, accumulates
/// an offense counter across calls, and is consulted by <c>--strict</c> mode to fail the
/// process with exit code 2 when any offenses were detected.
///
/// Detection-only — never mutates the manifest or auto-corrects identifiers.
/// </summary>
public class ManifestSectionValidator(ILogger<ManifestSectionValidator> logger)
{
    private readonly ILogger<ManifestSectionValidator> _logger = logger;

    /// <summary>
    /// Number of routes that emitted at least one section-resolution offense during
    /// this validator's lifetime. Consumed by <c>--strict</c> mode in <c>Program.cs</c>
    /// to fail the sync when any unresolved section reference is present.
    ///
    /// One offense per route (not per identifier) so a route with three typo'd section
    /// IDs counts as one route-level offense — analogous to <see cref="SeedAuthorshipValidator"/>'s
    /// "one offense per file" semantic.
    /// </summary>
    public int OffenseCount { get; private set; }

    /// <summary>
    /// Validates that every section / structural-section identifier referenced by
    /// <paramref name="proposed"/> resolves to a declared entry in the manifest's
    /// <c>entities.sections</c>. Pure — neither <paramref name="manifestContent"/> nor
    /// <paramref name="proposed"/> is mutated.
    /// </summary>
    /// <param name="manifestContent">
    /// The full <c>layout-manifest.json</c> document. Validator reads
    /// <c>entities.sections[*].identifier</c> to build the allowed set.
    /// </param>
    /// <param name="proposed">
    /// One or more <see cref="RoutePatchInput"/> records describing intended changes.
    /// </param>
    /// <returns>
    /// Per-route validation outcome. <see cref="SectionValidationResult.AllValid"/> is
    /// true if every patch's references resolve. Per-route error strings are keyed by
    /// <see cref="RoutePatchInput.Route"/>.
    /// </returns>
    public SectionValidationResult Validate(
        JsonObject manifestContent,
        IReadOnlyList<RoutePatchInput> proposed)
    {
        HashSet<string> declaredSections = CollectDeclaredSectionIdentifiers(manifestContent);
        Dictionary<string, IReadOnlyList<string>> errorsByRoute = [];

        foreach (RoutePatchInput patch in proposed)
        {
            List<string> routeErrors = [];

            if (!string.IsNullOrEmpty(patch.StructuralSection)
                && !declaredSections.Contains(patch.StructuralSection))
            {
                routeErrors.Add(FormatUnresolvedError(
                    "structural section",
                    patch.StructuralSection,
                    declaredSections));
            }

            if (patch.MainSections is { } mainSections)
            {
                foreach (string id in mainSections)
                {
                    if (!declaredSections.Contains(id))
                        routeErrors.Add(FormatUnresolvedError("main section", id, declaredSections));
                }
            }

            if (patch.SidebarSections is { } sidebarSections)
            {
                foreach (string id in sidebarSections)
                {
                    if (!declaredSections.Contains(id))
                        routeErrors.Add(FormatUnresolvedError("sidebar section", id, declaredSections));
                }
            }

            if (routeErrors.Count > 0)
            {
                errorsByRoute[patch.Route] = routeErrors;
                OffenseCount++;
                _logger.LogWarning(
                    "Manifest mutation offense for route {Route}: {ErrorCount} unresolved section reference(s).",
                    patch.Route,
                    routeErrors.Count);
            }
        }

        return new SectionValidationResult(
            AllValid: errorsByRoute.Count == 0,
            ErrorsByRoute: errorsByRoute);
    }

    /// <summary>
    /// Resets the offense counter. Useful for tests that share a single validator
    /// instance across cases. Production callers (singleton DI) should not reset.
    /// </summary>
    public void Reset() => OffenseCount = 0;

    /// <summary>
    /// Returns the set of section identifiers declared under
    /// <c>entities.sections[*].identifier</c>. An empty set is returned (rather than an
    /// exception) when the manifest is missing those keys, so a degenerate manifest
    /// produces clean validation errors rather than crashes.
    /// </summary>
    private static HashSet<string> CollectDeclaredSectionIdentifiers(JsonObject manifestContent)
    {
        HashSet<string> declared = new(StringComparer.Ordinal);
        if (manifestContent["entities"] is JsonObject entities
            && entities["sections"] is JsonArray sections)
        {
            foreach (JsonNode? node in sections)
            {
                // TryGetValue<string> is the reliable form: it works for both
                // JsonNode.Parse-backed JsonValues (which wrap a JsonElement) and
                // C#-constructed JsonValues. The looser `GetValue<object>() is string`
                // pattern silently fails on parsed JSON.
                if (node is JsonObject section
                    && section["identifier"] is JsonValue identifier
                    && identifier.TryGetValue(out string? id)
                    && !string.IsNullOrEmpty(id))
                {
                    declared.Add(id);
                }
            }
        }
        return declared;
    }

    /// <summary>
    /// Builds an error string of the form
    /// <c>"main section 'event-fitlers-panel' not found. Did you mean: event-filters-panel, event-list-filters?"</c>
    /// using Levenshtein-ranked nearest matches as suggestions.
    /// </summary>
    private static string FormatUnresolvedError(
        string fieldKind,
        string offending,
        HashSet<string> declared)
    {
        IReadOnlyList<string> suggestions = NearestMatches(offending, declared, max: 3);
        if (suggestions.Count == 0)
        {
            return $"{fieldKind} '{offending}' not found in entities.sections.";
        }
        return $"{fieldKind} '{offending}' not found in entities.sections. "
             + $"Did you mean: {string.Join(", ", suggestions)}?";
    }

    /// <summary>
    /// Returns up to <paramref name="max"/> declared identifiers ordered by ascending
    /// Levenshtein distance from <paramref name="needle"/>. Stable sort: ties broken
    /// alphabetically. Identifiers with edit distance &gt; needle.Length are filtered out
    /// — they're too distant to be a useful "did you mean" guess.
    /// </summary>
    internal static IReadOnlyList<string> NearestMatches(
        string needle,
        IEnumerable<string> haystack,
        int max)
    {
        return haystack
            .Select(candidate => (Identifier: candidate, Distance: Levenshtein(needle, candidate)))
            .Where(pair => pair.Distance <= needle.Length)
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => pair.Identifier, StringComparer.Ordinal)
            .Take(max)
            .Select(pair => pair.Identifier)
            .ToList();
    }

    /// <summary>
    /// Standard O(m*n) Levenshtein edit distance. Implemented inline because .NET has
    /// no built-in and the function is small enough that pulling a NuGet dep would be
    /// disproportionate.
    /// </summary>
    internal static int Levenshtein(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        int[,] dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) dp[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }
        return dp[a.Length, b.Length];
    }
}

/// <summary>
/// Outcome of <see cref="ManifestSectionValidator.Validate"/>. Designed as the structured
/// contract that <c>ManifestMutationService</c> consumes when deciding whether to apply,
/// skip, or abort a patch under <see cref="BatchErrorMode"/>.
/// </summary>
/// <param name="AllValid">True iff <see cref="ErrorsByRoute"/> is empty.</param>
/// <param name="ErrorsByRoute">
/// Per-route error messages. Keyed by <see cref="RoutePatchInput.Route"/>; value is a
/// list of human-readable error strings — one per unresolved identifier — including
/// "did you mean" suggestions.
/// </param>
public sealed record SectionValidationResult(
    bool AllValid,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ErrorsByRoute);
