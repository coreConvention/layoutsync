using System.Text.Json.Nodes;
using LayoutSync.Configuration;
using LayoutSync.Models;

namespace LayoutSync.Services;

/// <summary>
/// The decisions behind <c>--clean-records</c> (issue #62): which stored entities and identities a
/// scoped clean may delete, and which flag combinations may ask for it. Pure, so every rule is
/// unit-tested without RavenDB; <see cref="DocumentSyncService"/> does the querying and deleting.
/// </summary>
/// <remarks>
/// <c>--clean</c> never deletes records, because they are user data (issue #282). That stays the
/// default. <c>--clean-records</c> exists for one job: resetting a TEST database to exactly what
/// one layout's tree defines, as the w31rd e2e gate does before each Playwright run (w31rd #3963).
/// Every guard below makes the flag hard to point at real data: it needs a layout scope, runs only
/// once (never in watch mode, where it would delete records an app creates between file saves),
/// runs only against a local database even when <c>--allow-remote-sync</c> is passed, and deletes
/// only records that belong to the scoped layout and to no other.
/// </remarks>
public static class RecordCleanup
{
    /// <summary>The record collections <c>--clean-records</c> extends a scoped clean to.</summary>
    public static readonly IReadOnlyList<DocumentType> RecordTypes = [DocumentType.Entity, DocumentType.Identity];

    /// <summary>
    /// Returns why the parsed arguments may not use <c>--clean-records</c>, or null when they may
    /// (or do not ask for it). The remote-target refusal is separate (<see cref="RefusesTarget"/>)
    /// because it needs the resolved RavenDB URL.
    /// </summary>
    public static string? ValidateArgs(CommandLineArgs args)
    {
        if (!args.CleanRecords)
            return null;
        if (!args.Clean)
            return "--clean-records extends --clean, so it needs --clean as well.";
        if (string.IsNullOrEmpty(args.Layout))
            return "--clean-records needs --layout: it deletes records of one layout, never of every layout.";
        if (!args.SyncOnce || args.ValidateOnly || args.FixIds)
            return "--clean-records needs --sync-once: in watch mode it would delete records the app creates between file saves.";
        return null;
    }

    /// <summary>
    /// True when <c>--clean-records</c> must refuse this target: anything but a local database.
    /// Unlike the other write modes, <c>--allow-remote-sync</c> does not lift this, because what it
    /// deletes is user data that no file can restore.
    /// </summary>
    public static bool RefusesTarget(string? ravenUrl) =>
        ProductionTargetGuard.Classify(ravenUrl) != ProductionTargetClassification.Local;

    /// <summary>
    /// Every layout a stored record names: its <c>layoutId</c>, its <c>primaryLayoutId</c> and each
    /// <c>layoutIds</c> entry, without empty values. Identities minted by the w31rd platform carry
    /// no <c>layoutId</c>, only the other two, so reading one field would miss them.
    /// </summary>
    /// <returns>
    /// Null when any of the three fields has an unexpected shape (a non-string <c>layoutId</c> or
    /// <c>primaryLayoutId</c>, a <c>layoutIds</c> that is not an array, or an entry that is not a
    /// string). Such a record may name a layout this method cannot read, so it is never deleted.
    /// An absent field, a JSON null and an empty string all mean "names no layout".
    /// </returns>
    public static IReadOnlySet<string>? ReadLayoutRefs(JsonObject? content)
    {
        HashSet<string> refs = new(StringComparer.Ordinal);
        if (!AddString(content?["layoutId"]) || !AddString(content?["primaryLayoutId"]))
            return null;

        switch (content?["layoutIds"])
        {
            case null:
                break;
            case JsonArray layoutIds:
                foreach (JsonNode? entry in layoutIds)
                {
                    if (entry is null || !AddString(entry))
                        return null;
                }
                break;
            default:
                return null;
        }

        return refs;

        // False when the node is present but not a string.
        bool AddString(JsonNode? node)
        {
            if (node is null)
                return true;
            if (node is not JsonValue value || !value.TryGetValue(out string? text))
                return false;
            if (!string.IsNullOrEmpty(text))
                refs.Add(text);
            return true;
        }
    }

    /// <summary>
    /// True when the record belongs to <paramref name="layoutId"/> and to no other layout: it names
    /// at least one layout, and every layout it names is this one. A record shared with another
    /// layout (an identity that is a member of two tenants), or whose layout fields could not be
    /// read (<paramref name="layoutRefs"/> null), is never deleted.
    /// </summary>
    public static bool BelongsOnlyTo(IReadOnlySet<string>? layoutRefs, string layoutId) =>
        layoutRefs is { Count: > 0 } && layoutRefs.All(layout => string.Equals(layout, layoutId, StringComparison.Ordinal));

    /// <summary>
    /// The candidates to delete: records that belong only to <paramref name="layoutId"/> and that
    /// no file of this run declares. A record counts as declared when its document id is one this
    /// run's files resolved to or pinned, or when its identifier is one of theirs. Matching by id
    /// catches records created at runtime, which often carry no identifier; matching by identifier
    /// as well keeps a record whose file failed to sync or was only compared in a dry run. When in
    /// doubt a record is kept.
    /// </summary>
    public static IReadOnlyList<RavenDbService.RecordCandidate> SelectOrphans(
        IEnumerable<RavenDbService.RecordCandidate> candidates,
        string layoutId,
        IReadOnlySet<string> declaredIds,
        IReadOnlySet<string> declaredIdentifiers) =>
        [
            .. candidates.Where(candidate =>
                BelongsOnlyTo(candidate.LayoutRefs, layoutId)
                && !declaredIds.Contains(candidate.DocumentId)
                && (string.IsNullOrEmpty(candidate.Identifier) || !declaredIdentifiers.Contains(candidate.Identifier))),
        ];
}
