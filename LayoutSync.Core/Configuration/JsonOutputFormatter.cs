using System.Text.Json;
using System.Text.Json.Nodes;
using LayoutSync.Models;

namespace LayoutSync.Configuration;

/// <summary>
/// Serializes a <see cref="MutationResult"/> into the stable JSON envelope that the
/// CLI's <c>--json</c> mode emits on stdout. This is the contract that consumers — CI
/// scripts, the MCP server, and external automation — bind to, so the schema is
/// intentionally documented and stable.
///
/// Envelope shape:
/// <code>
/// {
///   "command":  "manifest set-route" | "manifest from-json",
///   "dryRun":   true | false,
///   "success":  true | false,
///   "layoutId": "dirt-life",
///   "changes": [
///     {
///       "route":  "/events/my-rsvps",
///       "status": "applied" | "skipped" | "aborted",
///       "before": { ... } | null,
///       "after":  { ... } | null,
///       "patch":  [ ...rfc6902... ] | null,
///       "error":  "string" | null
///     }
///   ],
///   "warnings": [ "string", ... ],
///   "errors":   [ "string", ... ]
/// }
/// </code>
/// </summary>
public static class JsonOutputFormatter
{
    /// <summary>
    /// Default JsonSerializerOptions for stdout emission. Pretty-printed for human
    /// readability; nulls preserved so consumers don't have to second-guess "field
    /// missing" vs "field is null."
    /// </summary>
    public static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Builds the JSON envelope. Returns a <see cref="JsonObject"/> so callers can
    /// further mutate or merge it before serializing — e.g. the MCP server may want to
    /// embed the envelope as a tool-result payload.
    /// </summary>
    public static JsonObject Format(
        string command,
        string layoutId,
        bool dryRun,
        MutationResult result)
    {
        JsonArray changes = [];
        foreach (RouteChange change in result.Changes)
        {
            changes.Add(SerializeChange(change));
        }

        JsonArray errors = [];
        foreach (string error in result.Errors) errors.Add(error);

        JsonArray warnings = [];
        foreach (string warning in result.Warnings) warnings.Add(warning);

        return new JsonObject
        {
            ["command"] = command,
            ["layoutId"] = layoutId,
            ["dryRun"] = dryRun,
            ["success"] = result.Success,
            ["changes"] = changes,
            ["warnings"] = warnings,
            ["errors"] = errors,
        };
    }

    /// <summary>
    /// Convenience: produce the envelope and serialize it to a JSON string with
    /// <see cref="PrettyOptions"/>. Used by the CLI when <c>--json</c> is set.
    /// </summary>
    public static string FormatAsString(
        string command,
        string layoutId,
        bool dryRun,
        MutationResult result)
        => Format(command, layoutId, dryRun, result).ToJsonString(PrettyOptions);

    private static JsonObject SerializeChange(RouteChange change)
    {
        return new JsonObject
        {
            ["route"] = change.Route,
            ["status"] = ToWireStatus(change.Status),
            ["before"] = change.Before is null ? null : change.Before.DeepClone(),
            ["after"] = change.After is null ? null : change.After.DeepClone(),
            ["patch"] = change.Patch is null ? null : change.Patch.DeepClone(),
            ["error"] = change.Error,
        };
    }

    /// <summary>
    /// Maps the C# enum to the lowercase wire string. Kept centralized so a future
    /// rename of the enum value (e.g. "Aborted" → "Rejected") doesn't accidentally
    /// break the JSON contract.
    /// </summary>
    internal static string ToWireStatus(RouteChangeStatus status) => status switch
    {
        RouteChangeStatus.Applied => "applied",
        RouteChangeStatus.Skipped => "skipped",
        RouteChangeStatus.Aborted => "aborted",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
