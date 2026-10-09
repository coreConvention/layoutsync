using System.Globalization;
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
/// Output format: UTC with millisecond precision, "yyyy-MM-ddTHH:mm:ss.fffZ" — the exact text a
/// browser's Date.prototype.toISOString() produces, which the w31rd platform standardises on
/// (coreConvention/w31rd#3238). Until issue #48 this wrote .NET's round-trip "o" format (seven
/// fractional digits), so seeded values differed in shape from SPA-written ones in the same
/// fields and string comparisons between them misordered.
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
    /// toISOString()'s shape. "fff" truncates the ticks below a millisecond rather than rounding
    /// (as DateTimeOffset.ToUnixTimeMilliseconds does), so "now" never names a later millisecond
    /// than the real one, and 23:59:59.9999999 never rolls into the next day. Always
    /// formatted with the invariant culture: in a custom format ':' is the culture's time separator
    /// and "yyyy" follows the culture's calendar (th-TH would write 2026 as 2569).
    /// </summary>
    private const string IsoMillisecondFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

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
    /// Resolves a single relative-date expression to an ISO 8601 UTC string with millisecond
    /// precision, e.g. "2026-10-16T19:10:51.123Z" (see <see cref="IsoMillisecondFormat"/>).
    /// The resolution is performed relative to <paramref name="referenceUtc"/> (defaults to UtcNow).
    /// Returns null if the expression does not match the pattern.
    /// </summary>
    /// <param name="expression">The relative-date expression, e.g. "+3d" or "now".</param>
    /// <param name="referenceUtc">
    /// The point in time to calculate the offset from. Defaults to <see cref="DateTime.UtcNow"/>
    /// when null. Injecting a fixed reference is primarily for deterministic unit testing.
    /// A <see cref="DateTimeKind.Local"/> value is converted to UTC before the offset is added;
    /// an <see cref="DateTimeKind.Unspecified"/> one is taken as UTC, as the name says.
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

        DateTime reference = ToUtc(referenceUtc ?? DateTime.UtcNow);

        // "now" special case
        if (trimmed.Equals("now", StringComparison.OrdinalIgnoreCase))
            return ToIsoMilliseconds(reference);

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

        string iso = ToIsoMilliseconds(resolved);
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

    /// <summary>
    /// Normalizes the reference to UTC before any arithmetic. Adding days to a Local value and
    /// converting afterwards would shift the result by any daylight-saving change in between,
    /// making the stored instant depend on the machine's time zone. Unspecified is not passed to
    /// <see cref="DateTime.ToUniversalTime"/>, which would treat it as local.
    /// </summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static string ToIsoMilliseconds(DateTime utc)
        => utc.ToString(IsoMillisecondFormat, CultureInfo.InvariantCulture);

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
