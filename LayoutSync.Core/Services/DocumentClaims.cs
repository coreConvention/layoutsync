using LayoutSync.Models;

namespace LayoutSync.Services;

/// <summary>
/// One sync run's record of which local file owns which stored document (issue #28). Built from
/// every file the run read, BEFORE its first write, so ownership never depends on discovery order
/// (which differs between fresh CI checkouts):
/// <list type="bullet">
///   <item><description><b>Declared collisions.</b> Files that declare the same document identity
///   (<see cref="IdentityKey"/>) all resolve to one stored document, so whichever is written last
///   would silently win. When their contents differ, every one of them is refused and the stored
///   document keeps exactly the content it had; byte-identical copies carry no conflict, so the first
///   is written and the rest are refused (<see cref="RefusalReason"/>). Both count as a collision.</description></item>
///   <item><description><b>Resolved collisions.</b> The backstop for any other path to one document:
///   each existing document a file resolves to is claimed (<see cref="TryClaim"/>), and a second
///   file resolving to it is refused, so no document is replaced twice in one run.</description></item>
///   <item><description><b>Adoption guard</b> for the migration fallback — see
///   <see cref="AdoptionScopeFor"/>.</description></item>
/// </list>
/// <see cref="CollisionCount"/> feeds <c>--strict</c> (exit code 2).
/// </summary>
internal sealed class DocumentClaims
{
    // The maps are case-insensitive, like RavenDB's own identifier and document-id matching: two
    // files whose identifiers differ only in case resolve to the same stored document.
    private readonly Dictionary<string, List<SyncDocument>> _declarants = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _identicalCopies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unattributedIdentifiers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SyncDocument> _claimedBy = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _claimedDocumentIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<SyncDocument> _refusedAtClaim = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _collisions = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="docs">Every file this run syncs.</param>
    /// <param name="outsideRun">Files an unscoped sync would also read but this run does not — the
    /// other layouts, under <c>--layout</c>. They count for the adoption guard only: an unattributed
    /// document's rightful owner may live outside a scoped run.</param>
    public DocumentClaims(IEnumerable<SyncDocument> docs, IEnumerable<SyncDocument>? outsideRun = null)
    {
        foreach (SyncDocument doc in docs)
        {
            NoteUnattributedDeclaration(doc);
            if (IdentityKey(doc) is not { } key)
                continue;

            if (!_declarants.TryGetValue(key, out List<SyncDocument>? group))
                _declarants[key] = group = [];
            group.Add(doc);
        }

        if (outsideRun != null)
        {
            foreach (SyncDocument doc in outsideRun)
                NoteUnattributedDeclaration(doc);
        }

        foreach ((string key, List<SyncDocument> group) in _declarants)
        {
            if (group.Count < 2)
                continue;

            _collisions.Add(key);

            // Compared as read, before sync mutates Content (layoutId stamping, relative dates).
            string first = group[0].Content?.ToJsonString() ?? string.Empty;
            if (group.All(doc => (doc.Content?.ToJsonString() ?? string.Empty) == first))
                _identicalCopies.Add(key);
        }
    }

    /// <summary>
    /// Stored documents two or more files collided on this run: declared collisions plus any
    /// document a second file resolved to after another file had claimed it.
    /// </summary>
    public int CollisionCount => _collisions.Count;

    /// <summary>
    /// Groups of files that declare the same document identity, in discovery order, and whether
    /// their contents are byte-identical (then the first is written).
    /// </summary>
    public IEnumerable<(IReadOnlyList<SyncDocument> Files, bool IdenticalCopies)> DeclaredCollisions =>
        _declarants
            .Where(kvp => kvp.Value.Count > 1)
            .Select(kvp => ((IReadOnlyList<SyncDocument>)kvp.Value, _identicalCopies.Contains(kvp.Key)));

    /// <summary>
    /// The OTHER files in this run that declare <paramref name="doc"/>'s identity; empty when the
    /// file is the only one.
    /// </summary>
    public IReadOnlyList<SyncDocument> CoDeclarants(SyncDocument doc) =>
        IdentityKey(doc) is { } key && _declarants.TryGetValue(key, out List<SyncDocument>? group) && group.Count > 1
            ? [.. group.Where(other => !ReferenceEquals(other, doc))]
            : [];

    /// <summary>
    /// Why <paramref name="doc"/> must not be written this run, or null when it may be. Refused:
    /// every file of a group whose contents differ (no file is the source of truth), and every copy
    /// after the first in a group of byte-identical files (the first is written instead).
    /// </summary>
    public string? RefusalReason(SyncDocument doc)
    {
        if (IdentityKey(doc) is not { } key
            || !_declarants.TryGetValue(key, out List<SyncDocument>? group)
            || group.Count < 2)
        {
            return null;
        }

        if (_identicalCopies.Contains(key))
        {
            return ReferenceEquals(group[0], doc)
                ? null
                : $"Collision: identical copy of {group[0].RelativePath}, which is synced instead";
        }

        return $"Collision: declares the same {doc.DocumentType.GetCollection()} document as {string.Join(", ", CoDeclarants(doc).Select(other => other.RelativePath))}";
    }

    /// <summary>True when <paramref name="doc"/> was refused this run, up front or by <see cref="TryClaim"/>.</summary>
    public bool IsRefused(SyncDocument doc) => RefusalReason(doc) != null || _refusedAtClaim.Contains(doc);

    /// <summary>
    /// Grants <paramref name="doc"/> the migration fallback to an unattributed document
    /// (<see cref="RavenDbService.AdoptionScope"/>), or returns null. Granted only when all of these
    /// hold:
    /// <list type="bullet">
    ///   <item><description>its type newly scopes its lookup by an authored layoutId
    ///   (<see cref="DocumentTypeExtensions.AdoptsUnattributedDocuments"/>). Stamped types have been
    ///   strictly scoped since #16, so they never adopt;</description></item>
    ///   <item><description>the file declares a layoutId, so there is an attribution to move the
    ///   document to;</description></item>
    ///   <item><description>no file declares the same identifier WITHOUT a layoutId — in this run or,
    ///   under <c>--layout</c>, in the layouts outside it. Such a file is the unattributed document's
    ///   rightful owner, and letting a tenant file adopt it first would steal it.</description></item>
    /// </list>
    /// Documents other files have already claimed this run are excluded through the live
    /// <see cref="RavenDbService.AdoptionScope.ClaimedDocumentIds"/> set.
    /// </summary>
    public RavenDbService.AdoptionScope? AdoptionScopeFor(SyncDocument doc) =>
        doc.DocumentType.AdoptsUnattributedDocuments()
        && doc.StoredLayoutId.Length > 0
        && !_unattributedIdentifiers.Contains(IdentifierKey(doc))
            ? new RavenDbService.AdoptionScope(_claimedDocumentIds)
            : null;

    /// <summary>
    /// Claims the existing stored document <paramref name="documentId"/> for <paramref name="doc"/>.
    /// Returns false, with the file that claimed it first in <paramref name="owner"/>, when another
    /// file already resolved to it this run. That counts as a collision.
    /// </summary>
    public bool TryClaim(string documentId, SyncDocument doc, out SyncDocument? owner)
    {
        if (_claimedBy.TryGetValue(documentId, out owner) && !ReferenceEquals(owner, doc))
        {
            _collisions.Add($"id\u001f{documentId}");
            _refusedAtClaim.Add(doc);
            return false;
        }

        _claimedBy[documentId] = doc;
        _claimedDocumentIds.Add(documentId);
        owner = null;
        return true;
    }

    /// <summary>
    /// A document's identity within its collection: the id for identities, otherwise the identifier,
    /// qualified by the stored layoutId when the type is layout-scoped
    /// (<see cref="DocumentTypeExtensions.IsLayoutScoped"/>). Files with equal keys resolve to the same
    /// stored document. Null when the file has no lookup key: it cannot be looked up, so it always
    /// creates a fresh document and can never collide. U+001F delimits the parts, as in
    /// <see cref="DocumentSyncService.OrphanTrackingKey"/>.
    /// </summary>
    internal static string? IdentityKey(SyncDocument doc)
    {
        if (string.IsNullOrEmpty(doc.LookupKey))
            return null;

        string scope = doc.DocumentType.IsLayoutScoped() ? doc.StoredLayoutId : string.Empty;
        return $"{doc.DocumentType.GetCollection()}\u001f{scope}\u001f{doc.LookupKey}";
    }

    private void NoteUnattributedDeclaration(SyncDocument doc)
    {
        if (doc.DocumentType.IsLayoutScoped() && doc.StoredLayoutId.Length == 0 && !string.IsNullOrEmpty(doc.Identifier))
            _unattributedIdentifiers.Add(IdentifierKey(doc));
    }

    private static string IdentifierKey(SyncDocument doc) =>
        $"{doc.DocumentType.GetCollection()}\u001f{doc.Identifier}";
}
