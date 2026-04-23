using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LayoutSync.Services;

/// <summary>
/// Resolves relative date expressions in JSON seed documents before they are written to RavenDB.
///
/// Motivation: Seed files that hardcode absolute ISO dates (e.g. "2025-01-26T09:00:00Z") silently
/// go stale as time passes. By using a relative-date convention (e.g. "+3d", "+2w", "-1m") in seed
/// files, schema authors express intent ("3 days from now") rather than a point in time that will
/// eventually be in the past. LayoutSync resolves the expression to an ISO string at sync time, so
/// the database always holds real timestamps.
///
/// Supported syntax (in recognized date field names only — see <see cref="IsDateFieldName"/>):
///   +Nd   — N days in the future
///   +Nw   — N weeks in the future
///   +Nm   — N months in the future
///   -Nd   — N days in the past
///   -Nw   — N weeks in the past
///   -Nm   — N months in the past
///   now   — current UTC instant
///
/// Idempotency: Only values that match the relative-date pattern are resolved. If a field already
/// holds an ISO timestamp (from a previous sync), it is left unchanged. Re-syncing a seed that still
/// says "+3d" will produce a new resolved date each run — which is intentional for "upcoming events"
/// seed data. Seeds that have been migrated away from relative syntax keep their fixed ISO dates.
/// </summary>
public partial class RelativeDateResolver(ILogger<RelativeDateResolver> logger)
{
    private readonly ILogger<RelativeDateResolver> _logger = logger;

    /// <summary>
    /// Regex that matches the relative-date syntax: optional sign, integer, unit (d/w/m) or the
    /// literal "now". The sign defaults to "+" if omitted. Case-insensitive.
    /// Examples: "+3d", "-2w", "+1m", "now", "7d" (treated as +7d).
    /// </summary>
    [GeneratedRegex(@"^(?<sign>[+-]?)(?<amount>\d+)(?<unit>[dwm])$|^now$", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeDatePattern();

    /// <summary>
    /// Set of JSON field names that are recognized as date/time carriers. Only fields whose name
    /// is in this set will be processed by the resolver. All comparisons are case-insensitive.
    /// </summary>
    private static readonly HashSet<string> DateFieldNames =
    [
        "date",
        "startdate",
        "enddate",
        "starttime",
        "endtime",
        "eventdate",
        "scheduleddate",
        "publisheddate",
        "createddatetime",
        "lastupdateddatetime",
        "timestamp",
        "duedate",
        "expiresdate",
        "expirydate",
        "resolveddate",
        "closeddate",
        "opendate",
        "registrationdeadline",
    ];

    /// <summary>
    /// Returns true if the given JSON field name is a recognized date field.
    /// The check is case-insensitive to handle camelCase, PascalCase, etc.
    /// </summary>
    public static bool IsDateFieldName(string fieldName)
        => DateFieldNames.Contains(fieldName.ToLowerInvariant());

    /// <summary>
    /// Returns true if the given string value matches the relative-date syntax.
    /// </summary>
    public static bool IsRelativeDate(string value)
        => RelativeDatePattern().IsMatch(value.Trim());

    /// <summary>
    /// Resolves a single relative-date expression to an ISO 8601 UTC string.
    /// The resolution is performed relative to <paramref name="referenceUtc"/> (defaults to UtcNow).
    /// Returns null if the expression does not match the pattern.
    /// </summary>
    /// <param name="expression">The relative-date expression, e.g. "+3d" or "now".</param>
    /// <param name="referenceUtc">
    /// The point in time to calculate the offset from. Defaults to <see cref="DateTime.UtcNow"/>
    /// when null. Injecting a fixed reference is primarily for deterministic unit testing.
    /// </param>
    public string? Resolve(string expression, DateTime? referenceUtc = null)
    {
        string trimmed = expression.Trim();
        Match match = RelativeDatePattern().Match(trimmed);

        if (!match.Success)
        {
            _logger.LogDebug("RelativeDateResolver: not a relative-date expression: {Value}", trimmed);
            return null;
        }

        DateTime reference = referenceUtc ?? DateTime.UtcNow;

        // "now" special case
        if (trimmed.Equals("now", StringComparison.OrdinalIgnoreCase))
            return reference.ToString("o");

        int amount = int.Parse(match.Groups["amount"].Value);
        char unit = char.ToLowerInvariant(match.Groups["unit"].Value[0]);
        bool negative = match.Groups["sign"].Value == "-";

        if (negative)
            amount = -amount;

        DateTime resolved = unit switch
        {
            'd' => reference.AddDays(amount),
            'w' => reference.AddDays(amount * 7),
            'm' => reference.AddMonths(amount),
            // The regex only allows d/w/m, so this branch is unreachable in practice.
            _ => throw new InvalidOperationException($"Unknown relative-date unit '{unit}'")
        };

        string iso = resolved.ToString("o");
        _logger.LogDebug(
            "RelativeDateResolver: resolved '{Expression}' -> '{Iso}'",
            expression, iso);
        return iso;
    }

    /// <summary>
    /// Walks a <see cref="JsonObject"/> recursively and resolves any relative-date values found
    /// in recognized date fields. The object is mutated in-place; its reference is also returned
    /// for convenience.
    ///
    /// Only string values in fields whose name passes <see cref="IsDateFieldName"/> and whose
    /// value passes <see cref="IsRelativeDate"/> are touched — all other content is left verbatim.
    /// </summary>
    /// <param name="obj">The JSON object to transform.</param>
    /// <param name="referenceUtc">
    /// Reference time for resolution. All relative dates in a single document are resolved against
    /// the same reference so that, for example, "+0d" and "+3d" in the same document are
    /// consistently anchored.
    /// </param>
    public JsonObject ResolveInDocument(JsonObject obj, DateTime? referenceUtc = null)
    {
        DateTime reference = referenceUtc ?? DateTime.UtcNow;
        ResolveObject(obj, reference);
        return obj;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void ResolveObject(JsonObject obj, DateTime reference)
    {
        foreach (string key in obj.Select(kvp => kvp.Key).ToList())
        {
            JsonNode? node = obj[key];

            if (node is JsonValue value && IsDateFieldName(key))
            {
                string? strValue = value.GetValue<string?>();
                if (!string.IsNullOrWhiteSpace(strValue) && IsRelativeDate(strValue))
                {
                    string? resolved = Resolve(strValue, reference);
                    if (resolved != null)
                    {
                        obj[key] = resolved;
                        _logger.LogDebug(
                            "Resolved date field '{Field}': '{Original}' -> '{Resolved}'",
                            key, strValue, resolved);
                    }
                }
            }
            else if (node is JsonObject nested)
            {
                ResolveObject(nested, reference);
            }
            else if (node is JsonArray array)
            {
                ResolveArray(array, reference);
            }
        }
    }

    private void ResolveArray(JsonArray array, DateTime reference)
    {
        foreach (JsonNode? item in array)
        {
            if (item is JsonObject nested)
                ResolveObject(nested, reference);
            else if (item is JsonArray nestedArray)
                ResolveArray(nestedArray, reference);
            // Primitive array elements (string/number) are not processed —
            // relative dates in arrays are not a use-case we support.
        }
    }
}
