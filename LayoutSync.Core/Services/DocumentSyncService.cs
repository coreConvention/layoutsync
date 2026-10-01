using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using coreConvention.Core.Validation;
using LayoutSync.Configuration;
using LayoutSync.Models;
using Microsoft.Extensions.Logging;

namespace LayoutSync.Services;

/// <summary>
/// Core service for syncing documents between local files and RavenDB.
/// Handles document wrapping, comparison, and ID enforcement.
/// </summary>
public class DocumentSyncService(
    ILogger<DocumentSyncService> logger,
    LocalFileService fileService,
    RavenDbService ravenService,
    RelativeDateResolver relativeDateResolver,
    IEnumerable<ISeedValidator> validators,
    SyncOptions options,
    CommandLineArgs cliArgs)
{
    private readonly ILogger<DocumentSyncService> _logger = logger;
    private readonly LocalFileService _fileService = fileService;
    private readonly RavenDbService _ravenService = ravenService;
    private readonly RelativeDateResolver _relativeDateResolver = relativeDateResolver;
    // Strategy collection: every detection-only seed validator is driven through the same
    // Reset → Inspect (per file) → FinalizeBatch lifecycle. Adding a validator is one new class +
    // one DI line — no edits here. See issue #7.
    private readonly IEnumerable<ISeedValidator> _validators = validators;
    private readonly SyncOptions _options = options;
    private readonly CommandLineArgs _cliArgs = cliArgs;

    /// <summary>
    /// Stored documents that two or more files collided on in the most recent
    /// <see cref="SyncAllAsync"/> batch. Colliding files are refused (only the first of several
    /// byte-identical copies is written), so no document is replaced twice in one run;
    /// <c>--strict</c> turns a non-zero count into exit code 2. See issue #28.
    /// </summary>
    public int DocumentCollisionCount { get; private set; }

    /// <summary>
    /// Files, since the most recent <see cref="SyncAllAsync"/> batch started, whose
    /// <c>@metadata.@id</c> pin is not the id of the stored document they resolved to. A pin is
    /// applied only when the document is created, so that document keeps its id and nothing exists
    /// under the pinned one; <c>--strict</c> turns a non-zero count into exit code 2. See issue #46.
    /// </summary>
    public int PinnedIdMismatchCount { get; private set; }

    /// <summary>
    /// Syncs all files in the layouts directory.
    /// </summary>
    /// <param name="layoutsPath">Path to layouts directory.</param>
    /// <param name="layout">Optional specific layout to sync.</param>
    /// <param name="dryRun">If true, don't make changes.</param>
    /// <param name="cleanOrphans">If true, delete orphaned documents from static collections.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SyncBatchResult> SyncAllAsync(
        string layoutsPath,
        string? layout = null,
        bool dryRun = false,
        bool cleanOrphans = false,
        CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        SyncBatchResult batch = new();

        // Reset every validator's accumulator/counter state at the start of every batch so
        // prior-batch state (e.g. earlier watch-mode re-syncs) doesn't leak into this pass.
        foreach (ISeedValidator validator in _validators)
            validator.Reset();
        PinnedIdMismatchCount = 0;

        // Track synced documents per collection for orphan detection, one bucket per collection.
        // Each bucket holds (layoutId, identifier) COMPOSITE keys (see OrphanTrackingKey), not bare
        // identifiers, so cross-layout identifier reuse can't mask an orphan (issue #17).
        // Registry-driven; an excluded collection gets no bucket, which is what "skip orphan
        // detection" means mechanically — DetectOrphansAsync iterates these KEYS. See issue #9.
        Dictionary<string, HashSet<string>> syncedIdentifiers =
            BuildOrphanTracking(_cliArgs.ExcludeCollections);

        // Read every file BEFORE the first write, so which file owns which stored document is
        // settled for the whole run up front: files that declare the same document are refused
        // whatever order they are discovered in — and discovery order differs between fresh CI
        // checkouts, which is what made #28 flip from one sync to the next.
        List<SyncDocument> docs = [];
        int fileCount = 0;
        foreach (string filePath in _fileService.DiscoverFiles(
            layoutsPath, layout, _cliArgs.ExcludeCollections, _cliArgs.ExcludeLayouts))
        {
            if (ct.IsCancellationRequested)
                break;

            fileCount++;
            SyncDocument? doc = await _fileService.ReadDocumentAsync(filePath, layoutsPath);
            if (doc == null)
            {
                batch.Results.Add(ReadFailure(filePath));
                continue;
            }

            docs.Add(doc);
        }

        DocumentClaims claims = new(docs, await ReadOutsideScopeAsync(layoutsPath, layout, ct));
        LogDeclaredCollisions(claims);

        foreach (SyncDocument doc in docs)
        {
            if (ct.IsCancellationRequested)
                break;

            SyncResult result = await SyncDocumentAsync(doc, dryRun, claims, ct);
            batch.Results.Add(result);

            // Track synced document for orphan detection (static collections only). Key by
            // (stored layoutId, identifier) — NOT identifier alone — so two layouts that
            // legitimately ship the same identifier in one collection are tracked independently
            // (issue #17). The layoutId must be the one the document is STORED with, because that
            // is what GetAllOrphanCandidatesAsync reads back: keying non-stamped documents by ""
            // made every section/manifest/... that declares a layoutId an orphan, which --clean
            // then deleted (issue #31). Refused collision files count as local files too, so the
            // document they declare is protected.
            if (CountsAsLocalFile(result))
            {
                SyncDocument syncedDoc = result.Document;
                string collection = syncedDoc.DocumentType.GetCollection();
                string? identifier = syncedDoc.Identifier;
                if (!string.IsNullOrEmpty(identifier) && syncedIdentifiers.ContainsKey(collection))
                {
                    syncedIdentifiers[collection].Add(OrphanTrackingKey(syncedDoc.StoredLayoutId, identifier));

                    // A refused file never learned which stored document is its own. Where it could
                    // have adopted an unattributed one, protect that too until the collision is fixed.
                    if (claims.IsRefused(syncedDoc) && syncedDoc.DocumentType.AdoptsUnattributedDocuments())
                        syncedIdentifiers[collection].Add(OrphanTrackingKey(string.Empty, identifier));
                }
            }
        }

        DocumentCollisionCount = claims.CollisionCount;

        // Always detect orphans in static collections (deletion is conditional on cleanOrphans flag)
        await DetectOrphansAsync(batch, syncedIdentifiers, cleanOrphans, dryRun, ct);

        // Batch-end phase: stateful, multi-phase validators run their deferred cross-check now
        // that every file has been inspected (e.g. cross-reference emits one WARN per offending
        // referencer whose outbound NanoID refs point at a missing/unpinned owner — issue #300).
        // Single-phase validators inherit a no-op FinalizeBatch.
        foreach (ISeedValidator validator in _validators)
            validator.FinalizeBatch();

        sw.Stop();
        batch.TotalDuration = sw.Elapsed;

        LogBatchSummary(batch, fileCount);
        return batch;
    }

    /// <summary>
    /// Reports each group of files that declare the same document identity, once, before any of
    /// them is refused in the sync loop.
    /// </summary>
    private void LogDeclaredCollisions(DocumentClaims claims)
    {
        foreach ((IReadOnlyList<SyncDocument> group, bool identicalCopies) in claims.DeclaredCollisions)
        {
            SyncDocument first = group[0];
            string identity = first.DocumentType == DocumentType.Identity
                ? $"id '{first.Id}'"
                : first.DocumentType.IsLayoutScoped()
                    ? $"identifier '{first.Identifier}', layoutId {(first.StoredLayoutId.Length > 0 ? $"'{first.StoredLayoutId}'" : "(none)")}"
                    : $"identifier '{first.Identifier}'";
            string files = string.Join(", ", group.Select(doc => doc.RelativePath));

            if (identicalCopies)
            {
                _logger.LogWarning(
                    "Document collision: {Count} files declare the same {Collection} document ({Identity}) with identical content: {Files}. Only {SyncedFile} is synced; delete the copies. --strict fails the run (exit code 2). See issue #28.",
                    group.Count, first.DocumentType.GetCollection(), identity, files, first.RelativePath);
            }
            else
            {
                _logger.LogWarning(
                    "Document collision: {Count} files declare the same {Collection} document ({Identity}) with different content: {Files}. None of them is synced this run, so the stored document keeps its current content. Give each file its own identifier. --strict fails the run (exit code 2). See issue #28.",
                    group.Count, first.DocumentType.GetCollection(), identity, files);
            }
        }
    }

    /// <summary>
    /// Under <c>--layout</c>, reads the files an unscoped sync would also see (same exclusions), for
    /// the adoption guard only: an unattributed document's rightful owner may live in another layout
    /// (<see cref="DocumentClaims.AdoptionScopeFor"/>). Empty for an unscoped run, which already
    /// read everything.
    /// </summary>
    private async Task<List<SyncDocument>> ReadOutsideScopeAsync(string layoutsPath, string? layout, CancellationToken ct)
    {
        List<SyncDocument> outside = [];
        if (string.IsNullOrEmpty(layout))
            return outside;

        string scopedDirectory = Path.Combine(layoutsPath, layout) + Path.DirectorySeparatorChar;
        foreach (string filePath in _fileService.DiscoverFiles(
            layoutsPath, specificLayout: null, _cliArgs.ExcludeCollections, _cliArgs.ExcludeLayouts))
        {
            if (ct.IsCancellationRequested)
                break;
            if (filePath.StartsWith(scopedDirectory, StringComparison.Ordinal))
                continue;

            if (await _fileService.ReadDocumentAsync(filePath, layoutsPath) is { } doc)
                outside.Add(doc);
        }

        return outside;
    }

    /// <summary>
    /// Syncs a single file to RavenDB (watch mode). A lone file has no run to check ownership
    /// against, so it gets neither collision detection nor the migration fallback to an unattributed
    /// document — both need to know every other file in the run (<see cref="DocumentClaims"/>).
    /// </summary>
    public async Task<SyncResult> SyncFileAsync(string filePath, string layoutsPath, bool dryRun = false, CancellationToken ct = default)
    {
        SyncDocument? doc = await _fileService.ReadDocumentAsync(filePath, layoutsPath);
        return doc == null
            ? ReadFailure(filePath)
            : await SyncDocumentAsync(doc, dryRun, claims: null, ct);
    }

    private static SyncResult ReadFailure(string filePath) =>
        SyncResult.Failed(
            new SyncDocument { FilePath = filePath },
            SyncAction.Skipped,
            "Failed to read file"
        );

    /// <summary>
    /// Syncs one already-read document. <paramref name="claims"/> is the batch's ownership record
    /// (<see cref="SyncAllAsync"/>); null for a single-file sync.
    /// </summary>
    private async Task<SyncResult> SyncDocumentAsync(SyncDocument doc, bool dryRun, DocumentClaims? claims, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();

        // Log human-readable ID warning
        if (doc.HasHumanReadableId)
        {
            _logger.LogWarning("File has human-readable id '{Id}': {Path}", doc.Id, doc.RelativePath);
        }

        // All JSON files should already have wrapper structure
        JsonObject contentToSync = doc.Content ?? new JsonObject();

        // Detection-only nudge pass: every seed validator inspects this file. Each is non-blocking
        // and pure (contentToSync is never mutated) — authorship flags raw NanoIDs in identity
        // fields (#308), cross-reference accumulates seed metadata for the batch-end check (#300),
        // dead-widget-prop flags no-op section props (w31rd #984). See issue #7 for the strategy.
        foreach (ISeedValidator validator in _validators)
            validator.Inspect(doc.DocumentType, doc.RelativePath, contentToSync);

        // A file that shares its document identity with another file in this run is not written:
        // whichever landed last would silently replace the other (issue #28); only the first of
        // several byte-identical copies goes through. Refused AFTER the validators, which must
        // still see every file (e.g. cross-reference accumulates declared ids from all of them).
        // The group was reported once in LogDeclaredCollisions.
        if (claims?.RefusalReason(doc) is { } refusal)
        {
            return SyncResult.Skipped(doc, refusal);
        }

        // Inject layoutId for entity documents — entities must be scoped to a layout.
        // The layoutId is derived from the layout directory name (e.g., "layouts/dirt-life/" → "dirt-life").
        // We always overwrite layoutId in the content to ensure consistency with the directory name.
        // System collections (sections, layouts, menus, modals, manifests, tags, workflows) are EXEMPT:
        // they are written as authored, so they carry a layoutId exactly when their file declares one.
        // Themes have two flavors: layout-scoped overrides (LayoutId set, the resolver matches
        // request tenant context) and the platform catalogue (LayoutId null/empty, available to
        // every tenant via /api/init). IsLayoutIdStamped is false for the platform-scoped flavor so
        // those documents stay layoutId-less.
        if (doc.IsLayoutIdStamped)
        {
            contentToSync["layoutId"] = doc.LayoutId;
            _logger.LogDebug(
                "Injecting layoutId='{LayoutId}' for entity: {Identifier}",
                doc.LayoutId,
                doc.Identifier
            );
        }

        // Resolve relative-date expressions (e.g. "+3d", "+2w") in recognized date fields.
        // This anchors "upcoming event" seeds to real future timestamps at sync time, ensuring
        // seed data doesn't silently go stale as calendar time advances. Only fields that match
        // the relative-date syntax are modified; ISO strings already present are left unchanged.
        // All dates in a document are resolved against the same reference instant for consistency.
        DateTime syncInstant = DateTime.UtcNow;
        _relativeDateResolver.ResolveInDocument(contentToSync, syncInstant);

        doc.WrappedContent = contentToSync;

        // Look up in database
        (string? existingDocId, JsonObject? existingDoc, string? existingChangeVector) =
            await _ravenService.FindDocumentAsync(doc, claims?.AdoptionScopeFor(doc), ct);

        // Backstop for any other route by which two files could reach one stored document: the
        // first file to resolve to it owns it for the rest of the run (issue #28).
        if (existingDocId != null && claims != null && !claims.TryClaim(existingDocId, doc, out SyncDocument? owner))
        {
            _logger.LogWarning(
                "Document collision: {Path} resolves to {Collection} document {DocumentId}, which {OwnerPath} already resolved to in this run. The later file is not synced, so the document is not replaced twice. --strict fails the run (exit code 2). See issue #28.",
                doc.RelativePath, doc.DocumentType.GetCollection(), existingDocId, owner!.RelativePath);
            return SyncResult.Skipped(doc, $"Collision: resolves to document {existingDocId}, already claimed by {owner.RelativePath}");
        }

        // A pin (@metadata.@id) is applied only when the document is created. This file's document
        // already exists under another id and every branch below keeps that id, so nothing is ever
        // stored under the pinned one and whatever references it finds nothing — with no other
        // sign of it (issue #46). Reported here, ahead of the unchanged and dry-run returns,
        // because an up-to-date document is the steady state of a pin added after the fact.
        // Identities are looked up BY their id, so theirs cannot differ; ids compare
        // case-insensitively, as RavenDB's do.
        if (existingDocId != null
            && !string.IsNullOrEmpty(doc.PinnedId)
            && !string.Equals(existingDocId, doc.PinnedId, StringComparison.OrdinalIgnoreCase))
        {
            PinnedIdMismatchCount++;
            _logger.LogWarning(
                "Pinned id mismatch: {Path} pins @metadata.@id '{PinnedId}', but its {Collection} document is already stored as '{StoredId}'. A pin only applies when the document is created (with --preserve-ids); an existing document is updated in place and keeps its id, so no document has the pinned id and anything that references it finds nothing. Change the pin and its references to the stored id, or delete the stored document so the next --preserve-ids sync creates it under the pinned id. --strict fails the run (exit code 2). See issue #46.",
                doc.RelativePath, doc.PinnedId, doc.DocumentType.GetCollection(), existingDocId);
        }

        // Compare BEFORE the dry-run branch so --dry-run is a true diff: "Would UPDATE" only when
        // the stored document actually differs, not for every existing document (issue #11).
        // Documents with relative-date seeds ("+3d") re-resolve against the clock every run, so
        // they legitimately differ — and are rewritten — on every sync.
        if (existingDocId != null && ContentEquals(existingDoc, contentToSync))
        {
            if (dryRun)
                _logger.LogInformation("No changes: {Path}", doc.RelativePath);
            else
                _logger.LogInformation("No changes: {Identifier}", doc.Identifier);
            return SyncResult.Unchanged(doc, existingDocId);
        }

        if (dryRun)
        {
            string action = existingDocId == null ? "Would CREATE" : "Would UPDATE";
            _logger.LogInformation("{Action}: {Path}", action, doc.RelativePath);
            return SyncResult.Skipped(doc, $"Dry run: {action}");
        }

        try
        {
            if (existingDocId == null)
            {
                // Create new document - preserve ID from file if --preserve-ids flag is set
                string? preservedId = _cliArgs.PreserveIds ? doc.Id : null;
                string? newId = await _ravenService.CreateDocumentAsync(doc, contentToSync, existingDocId: preservedId, ct: ct);
                sw.Stop();

                if (_cliArgs.PreserveIds && !string.IsNullOrEmpty(doc.Id))
                {
                    _logger.LogInformation("Created with preserved ID: {Identifier} -> {Id}", doc.Identifier, doc.Id);
                }
                else
                {
                    _logger.LogInformation("Created: {Identifier}", doc.Identifier);
                }
                return SyncResult.Succeeded(doc, SyncAction.Created, newId, duration: sw.Elapsed);
            }
            else
            {
                // Content differs: overwrite the whole document in place, guarded by the change
                // vector we just compared against. A full-document PUT drops stale properties
                // ($type included) exactly as the old delete + create did, but the document is
                // never absent and a concurrent edit fails the write instead of being clobbered.
                string? newId = await _ravenService.ReplaceDocumentAsync(
                    existingDocId, existingChangeVector, doc, contentToSync, ct);
                sw.Stop();
                _logger.LogInformation("Replaced: {Identifier}", doc.Identifier);
                return SyncResult.Succeeded(doc, SyncAction.Replaced, newId, duration: sw.Elapsed);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Sync failed: {Path}", doc.RelativePath);
            return SyncResult.Failed(doc, existingDocId == null ? SyncAction.Created : SyncAction.Replaced, ex.Message, ex, sw.Elapsed);
        }
    }

    /// <summary>
    /// Deletes a document from RavenDB by its tracked identifier and type.
    /// If ravenDocumentId is provided, deletes directly. Otherwise, queries by identifier.
    /// </summary>
    public async Task<SyncResult> DeleteTrackedDocumentAsync(
        string identifier,
        DocumentType documentType,
        string? ravenDocumentId = null,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        SyncDocument doc = new()
        {
            Identifier = identifier,
            DocumentType = documentType
        };

        if (dryRun)
        {
            _logger.LogInformation("Would DELETE: {Identifier} ({Type})", identifier, documentType);
            return SyncResult.Skipped(doc, "Dry run: Would DELETE");
        }

        try
        {
            // If we don't have the RavenDB document ID, look it up. The deleted file's content is
            // gone, so its stored layoutId is unknown: layout-scoped types can only resolve an
            // unattributed document here, never another layout's same-identifier document.
            if (string.IsNullOrEmpty(ravenDocumentId))
            {
                (string? foundDocId, _, _) = await _ravenService.FindDocumentAsync(doc, ct);
                ravenDocumentId = foundDocId;
            }

            if (string.IsNullOrEmpty(ravenDocumentId))
            {
                _logger.LogDebug("Document not found in RavenDB for deletion: {Identifier}", identifier);
                return SyncResult.Skipped(doc, "Not found in database");
            }

            bool deleted = await _ravenService.DeleteDocumentAsync(ravenDocumentId, ct);
            if (deleted)
            {
                _logger.LogInformation("Deleted: {Identifier} ({DocId})", identifier, ravenDocumentId);
                return SyncResult.Succeeded(doc, SyncAction.Deleted, ravenDocumentId);
            }

            return SyncResult.Failed(doc, SyncAction.Deleted, "Delete operation returned false");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting document: {Identifier}", identifier);
            return SyncResult.Failed(doc, SyncAction.Deleted, ex.Message, ex);
        }
    }

    /// <summary>
    /// Detects orphaned documents in static collections (documents in DB but not in local files).
    /// When <c>--layout X</c> is active (<see cref="CommandLineArgs.Layout"/> set), the candidate
    /// set is filtered through <see cref="FilterOrphansForScope"/> so cross-tenant orphans
    /// are excluded and globally-shared documents (no <c>layoutId</c>) are skipped — preventing
    /// the data-loss class of bug from issue #235 while still allowing safe scoped cleanup
    /// per issue #427.
    /// </summary>
    private async Task DetectOrphansAsync(
        SyncBatchResult batch,
        Dictionary<string, HashSet<string>> syncedIdentifiers,
        bool deleteOrphans,
        bool dryRun,
        CancellationToken ct)
    {
        string? scopedLayoutId = string.IsNullOrEmpty(_cliArgs.Layout) ? null : _cliArgs.Layout;

        foreach ((string collection, HashSet<string> synced) in syncedIdentifiers)
        {
            if (ct.IsCancellationRequested)
                break;

            // Get every document currently in the collection, keyed by unique document id
            // (NOT identifier — that would collapse cross-layout duplicates; issue #17).
            Dictionary<string, RavenDbService.OrphanCandidate> existingDocs =
                await _ravenService.GetAllOrphanCandidatesAsync(collection, ct);

            // Find orphans (in DB but not synced from local files), matched by the
            // (layoutId, identifier) composite so a document counts as "synced" only when a
            // local file with the SAME layoutId AND identifier was seen this run (issue #17).
            Dictionary<string, RavenDbService.OrphanCandidate> rawOrphans =
                ComputeRawOrphans(existingDocs, synced);

            // Apply layout-scope filter when --layout is active. Without a scope all candidates
            // pass through (legacy unscoped clean). With a scope, only documents whose layoutId
            // matches the scope are eligible; documents without a layoutId are skipped (they
            // are globally-shared and cannot be safely attributed to a single tenant).
            Dictionary<string, RavenDbService.OrphanCandidate> orphans =
                FilterOrphansForScope(rawOrphans, scopedLayoutId);

            int filteredOut = rawOrphans.Count - orphans.Count;
            if (filteredOut > 0 && scopedLayoutId is not null)
            {
                _logger.LogDebug(
                    "Scoped clean: skipped {FilteredOut} cross-tenant or unscoped candidate(s) in {Collection} (kept only layoutId={Layout})",
                    filteredOut, collection, scopedLayoutId);
            }

            // Apply --exclude-layout filter. Drops candidates attributed to an excluded layout
            // AND (conservatively) candidates with no layoutId at all — see
            // FilterOrphansForExcludedLayouts for why null must not survive this filter.
            int beforeExclusion = orphans.Count;
            orphans = FilterOrphansForExcludedLayouts(orphans, _cliArgs.ExcludeLayouts);
            int excludedOut = beforeExclusion - orphans.Count;
            if (excludedOut > 0)
            {
                _logger.LogInformation(
                    "--exclude-layout: skipped {Count} orphan candidate(s) in {Collection} (excluded-layout documents and unattributable null-layoutId documents are never deleted while an exclusion is active)",
                    excludedOut, collection);
            }

            if (orphans.Count == 0)
                continue;

            // Track orphans in batch result as (docId -> identifier). Keyed by the unique docId
            // so two orphans that share an identifier across layouts are both represented and
            // counted (issue #17); the identifier stays available as the value for reporting.
            batch.OrphansDetected[collection] = orphans
                .ToDictionary(kvp => kvp.Value.DocumentId, kvp => kvp.Value.Identifier);

            foreach ((string _, RavenDbService.OrphanCandidate candidate) in orphans)
            {
                string docId = candidate.DocumentId;
                string identifier = candidate.Identifier;
                if (deleteOrphans && !dryRun)
                {
                    // Actually delete the orphan
                    bool deleted = await _ravenService.DeleteDocumentAsync(docId, ct);
                    if (deleted)
                    {
                        _logger.LogWarning("Deleted orphan: {Identifier} ({DocId}) from {Collection}", identifier, docId, collection);
                        batch.Results.Add(SyncResult.Succeeded(
                            new SyncDocument { Identifier = identifier, DocumentType = GetDocumentTypeForCollection(collection) },
                            SyncAction.OrphanDeleted,
                            docId
                        ));
                    }
                }
                else if (dryRun)
                {
                    _logger.LogWarning("Would delete orphan: {Identifier} ({DocId}) from {Collection}", identifier, docId, collection);
                }
                else
                {
                    // Just report orphan without deleting (cleanOrphans=false)
                    _logger.LogDebug("Orphan detected: {Identifier} ({DocId}) in {Collection}", identifier, docId, collection);
                }
            }

            if (orphans.Count > 0 && !deleteOrphans)
            {
                _logger.LogInformation("{Count} orphan(s) detected in {Collection}. Use --clean to remove.", orphans.Count, collection);
            }
        }
    }

    /// <summary>
    /// Pure decision helper: filter the raw orphan set by an optional layout scope.
    /// Extracted as <c>internal static</c> so it can be unit-tested without spinning up RavenDB.
    ///
    /// <list type="bullet">
    ///   <item><description>When <paramref name="scopedLayoutId"/> is null/empty, all candidates pass through (legacy unscoped behavior).</description></item>
    ///   <item><description>When <paramref name="scopedLayoutId"/> is set, only candidates whose <see cref="RavenDbService.OrphanCandidate.LayoutId"/> equals the scope are kept.</description></item>
    ///   <item><description>Candidates with a null <c>LayoutId</c> are conservatively dropped under a scoped run — a document stored without a layoutId (never stamped, and its file declared none) carries no tenant attribution, and a scoped operation must not delete documents it cannot prove belong to that tenant. Non-stamped documents whose file DID declare the scope's layoutId are attributed and eligible (issue #31).</description></item>
    /// </list>
    ///
    /// See issue #427 (and the predecessor data-loss incident #235).
    /// </summary>
    /// <param name="candidates">Raw orphan candidates already filtered to "in DB but not in synced files".</param>
    /// <param name="scopedLayoutId">The active layout scope (the value of <c>--layout</c>), or null/empty for no scope.</param>
    /// <returns>The subset of <paramref name="candidates"/> that should be considered for deletion under the active scope.</returns>
    public static Dictionary<string, RavenDbService.OrphanCandidate> FilterOrphansForScope(
        Dictionary<string, RavenDbService.OrphanCandidate> candidates,
        string? scopedLayoutId)
    {
        if (string.IsNullOrEmpty(scopedLayoutId))
        {
            return candidates;
        }

        return candidates
            .Where(kvp => kvp.Value.LayoutId == scopedLayoutId)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    /// <summary>
    /// Pure decision helper: does this result mark its document as having a local file, for
    /// orphan detection? True for every static-collection file that was read, whether or not its
    /// write succeeded. An orphan is a stored document with no local file, and a failed write
    /// (for example a replace refused because the document changed after it was compared) must
    /// not make the document look orphaned, or <c>--clean</c> would delete it. A file that could
    /// not be read carries no identifier, so it is not tracked.
    /// </summary>
    internal static bool CountsAsLocalFile(SyncResult result) =>
        result.Document.DocumentType.IsStaticCollection()
        && !string.IsNullOrEmpty(result.Document.Identifier);

    /// <summary>
    /// Composite orphan-tracking key: a document's identity for orphan detection is
    /// (effective layoutId, identifier), NOT identifier alone — two layouts may ship the same
    /// identifier in one collection (issue #17), and matching on identifier alone lets one mask
    /// the other's orphan. Encoded as a single string so the tracking buckets stay
    /// <c>HashSet&lt;string&gt;</c>. U+001F (Unit Separator) is the delimiter: a control character
    /// that cannot occur in a layoutId slug or a NanoID/identifier, so no real (layoutId,
    /// identifier) pair can collide with another.
    /// </summary>
    internal static string OrphanTrackingKey(string layoutId, string identifier) =>
        $"{layoutId}\u001f{identifier}";

    /// <summary>
    /// Pure decision helper: given every document currently in a collection (keyed by unique
    /// document id) and the set of (layoutId, identifier) composite keys synced from local files
    /// this run, return the subset that are orphans — present in the DB but absent from the synced
    /// set. Matching is by <see cref="OrphanTrackingKey"/>, so a document is retained (not an
    /// orphan) only when a local file with the SAME layoutId AND identifier was synced. This is the
    /// issue #17 fix: identifier-only matching let a cross-layout twin mask a genuine orphan. A
    /// candidate's null/empty <c>LayoutId</c> is normalized to "" to match the synced side (RavenDB
    /// projects a missing layoutId as ""; issue #13). Extracted <c>internal static</c> so the
    /// composite-matching decision is unit-testable without a live RavenDB session.
    /// </summary>
    internal static Dictionary<string, RavenDbService.OrphanCandidate> ComputeRawOrphans(
        Dictionary<string, RavenDbService.OrphanCandidate> existingByDocId,
        IReadOnlySet<string> syncedKeys)
    {
        return existingByDocId
            .Where(kvp => !syncedKeys.Contains(
                OrphanTrackingKey(kvp.Value.LayoutId ?? string.Empty, kvp.Value.Identifier)))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    /// <summary>
    /// Pure decision helper: builds the orphan-tracking map — one bucket per static
    /// collection eligible for orphan detection this run, keyed by RavenDB collection name
    /// (<see cref="DocumentTypeExtensions.GetCollection"/> output). Registry-driven
    /// (<see cref="CollectionFolders.Ordered"/>) so a future collection joins orphan
    /// detection by being added there. See issue #9.
    ///
    /// An excluded collection gets NO bucket, so <c>DetectOrphansAsync</c> (which iterates
    /// these keys) never queries it — orphan detection is skipped entirely, not merely
    /// filtered. <c>entities</c>/<c>identities</c> are already excluded from tracking by
    /// <see cref="DocumentTypeExtensions.IsStaticCollection"/> regardless of flags (user
    /// data is never orphan-cleaned — issue #282), so excluding them here is a deliberate
    /// no-op. <paramref name="excludeCollections"/> holds FOLDER names (the CLI vocabulary);
    /// the folder→collection mapping is non-identity for <c>write-policies</c>→<c>WritePolicies</c>
    /// and <c>themes</c>→<c>theme-definitions</c>.
    /// </summary>
    internal static Dictionary<string, HashSet<string>> BuildOrphanTracking(
        IReadOnlyCollection<string>? excludeCollections)
    {
        HashSet<string> excluded = new(excludeCollections ?? [], StringComparer.OrdinalIgnoreCase);

        Dictionary<string, HashSet<string>> tracking = [];
        foreach ((string folder, DocumentType type) in CollectionFolders.Ordered)
        {
            if (!type.IsStaticCollection() || excluded.Contains(folder))
                continue;

            tracking[type.GetCollection()] = [];
        }

        return tracking;
    }

    /// <summary>
    /// Pure decision helper: drop orphan candidates that may belong to an excluded layout.
    /// Extracted as <c>internal static</c> so it can be unit-tested without RavenDB.
    ///
    /// <list type="bullet">
    ///   <item><description>When <paramref name="excludedLayouts"/> is empty, all candidates pass through.</description></item>
    ///   <item><description>Candidates whose <see cref="RavenDbService.OrphanCandidate.LayoutId"/> matches an excluded layout (Ordinal) are dropped.</description></item>
    ///   <item><description>Candidates with a null OR EMPTY <c>LayoutId</c> are ALSO dropped while any
    ///   exclusion is active: a document stored without a layoutId (never stamped, and its file
    ///   declared none) cannot be proven
    ///   to lie OUTSIDE the excluded layout — deleting it under <c>--clean --exclude-layout X</c> could
    ///   destroy X's own documents. Mirrors the null-drop conservatism of
    ///   <see cref="FilterOrphansForScope"/>. Empty matters as much as null: RavenDB's dynamic
    ///   projection surfaces a MISSING <c>layoutId</c> field as a DynamicNullObject whose
    ///   <c>ToString()</c> is <c>""</c>, so unstamped documents reach this filter with an empty
    ///   string, never an actual null (verified against live data during issue #9).</description></item>
    /// </list>
    ///
    /// See issue #9.
    /// </summary>
    public static Dictionary<string, RavenDbService.OrphanCandidate> FilterOrphansForExcludedLayouts(
        Dictionary<string, RavenDbService.OrphanCandidate> candidates,
        IReadOnlyCollection<string> excludedLayouts)
    {
        if (excludedLayouts.Count == 0)
        {
            return candidates;
        }

        return candidates
            .Where(kvp => !string.IsNullOrEmpty(kvp.Value.LayoutId)
                && !excludedLayouts.Contains(kvp.Value.LayoutId, StringComparer.Ordinal))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    /// <summary>
    /// Maps collection name back to DocumentType.
    /// Collection names are lowercase to match GetCollection() output.
    /// </summary>
    private static DocumentType GetDocumentTypeForCollection(string collection) => collection switch
    {
        "sections" => DocumentType.Section,
        "layouts" => DocumentType.Layout,
        "menus" => DocumentType.Menu,
        "modals" => DocumentType.Modal,
        "manifests" => DocumentType.Manifest,
        "tags" => DocumentType.Tag,
        "workflows" => DocumentType.Workflow,
        "WritePolicies" => DocumentType.WritePolicy,
        "ReadPolicies" => DocumentType.ReadPolicy,
        "entity-configs" => DocumentType.EntityConfig,
        "email-templates" => DocumentType.EmailTemplate,
        "theme-definitions" => DocumentType.Theme,
        _ => DocumentType.Entity
    };

    /// <summary>
    /// Validates files and reports issues without making changes.
    /// </summary>
    public async Task<SyncBatchResult> ValidateAsync(string layoutsPath, string? layout = null, CancellationToken ct = default)
    {
        SyncBatchResult batch = new();
        List<SyncDocument> humanReadableIds = [];

        foreach (string filePath in _fileService.DiscoverFiles(
            layoutsPath, layout, _cliArgs.ExcludeCollections, _cliArgs.ExcludeLayouts))
        {
            if (ct.IsCancellationRequested)
                break;

            SyncDocument? doc = await _fileService.ReadDocumentAsync(filePath, layoutsPath);
            if (doc == null)
                continue;

            if (doc.HasHumanReadableId)
            {
                humanReadableIds.Add(doc);
                _logger.LogWarning("{Path}\n              id: \"{Id}\" <- human-readable", doc.RelativePath, doc.Id);
            }

            batch.Results.Add(SyncResult.Succeeded(doc, SyncAction.Validated));
        }

        _logger.LogInformation("");
        if (humanReadableIds.Count > 0)
        {
            _logger.LogWarning("Found {Count} files with human-readable IDs", humanReadableIds.Count);
            _logger.LogInformation("Run with --fix-ids to auto-generate NanoIDs");
        }
        else
        {
            _logger.LogInformation("All {Count} files have valid NanoIDs", batch.Results.Count);
        }

        return batch;
    }

    /// <summary>
    /// Fixes human-readable IDs by generating NanoIDs.
    /// </summary>
    public async Task<SyncBatchResult> FixIdsAsync(string layoutsPath, string? layout = null, bool dryRun = false, CancellationToken ct = default)
    {
        SyncBatchResult batch = new();
        int fixedCount = 0;

        foreach (string filePath in _fileService.DiscoverFiles(
            layoutsPath, layout, _cliArgs.ExcludeCollections, _cliArgs.ExcludeLayouts))
        {
            if (ct.IsCancellationRequested)
                break;

            SyncDocument? doc = await _fileService.ReadDocumentAsync(filePath, layoutsPath);
            if (doc == null || doc.Content == null)
                continue;

            if (doc.HasHumanReadableId)
            {
                string oldId = doc.Id!;
                string newId = NanoIdValidator.GenerateNanoId();

                if (dryRun)
                {
                    _logger.LogInformation("{Path}\n              \"{OldId}\" -> \"{NewId}\" (dry run)", doc.RelativePath, oldId, newId);
                    batch.Results.Add(SyncResult.Skipped(doc, "Dry run"));
                }
                else
                {
                    // Update the content
                    doc.Content["id"] = newId;
                    await _fileService.WriteDocumentAsync(filePath, doc.Content);

                    _logger.LogInformation("{Path}\n              \"{OldId}\" -> \"{NewId}\" [check]", doc.RelativePath, oldId, newId);
                    batch.Results.Add(SyncResult.Succeeded(doc, SyncAction.LocalFixed, localUpdated: true));
                    fixedCount++;
                }
            }
            else
            {
                batch.Results.Add(SyncResult.Skipped(doc, "ID is valid"));
            }
        }

        _logger.LogInformation("");
        if (fixedCount > 0)
        {
            _logger.LogInformation("Fixed {Count} files", fixedCount);
            _logger.LogInformation("Run --sync-once to sync changes to database");
        }
        else
        {
            _logger.LogInformation("No files needed fixing");
        }

        return batch;
    }

    /// <summary>
    /// Pure decision helper: does the stored document already hold what this sync would write?
    /// True means the write is skipped. Extracted <c>internal static</c> so the equality rules are
    /// unit-testable without RavenDB.
    ///
    /// <list type="bullet">
    ///   <item><description>Only the root <c>@metadata</c> is exempt: it is server-managed, and a file's copy (identity files name their id there) is not content. Everything else counts. Until issue #24 this check also ignored <c>$type</c> and the <c>createdDateTime</c> / <c>lastUpdatedDateTime</c> timestamps at every depth; that was harmless only while the check never matched. Live, it would keep a stored <c>$type</c> artifact forever and silently skip timestamp-only edits, which files do make (hand-maintained policy timestamps). Nothing stamps those fields at sync time any more.</description></item>
    ///   <item><description>Object key ORDER is significant. The write path preserves file order, so an untouched file always matches, and a reorder-only edit is a real change for any consumer that iterates keys.</description></item>
    ///   <item><description>Leaf values compare by value, not spelling. The write path normalizes numbers (<c>1.50</c> is stored as <c>1.5</c>, <c>1e3</c> as <c>1000.0</c>), so a textual compare would rewrite such a file on every sync without ever converging.</description></item>
    /// </list>
    /// </summary>
    internal static bool ContentEquals(JsonObject? stored, JsonObject? candidate)
    {
        if (stored == null && candidate == null) return true;
        if (stored == null || candidate == null) return false;

        return PropertiesEquivalent(
            stored.Where(property => property.Key != "@metadata"),
            candidate.Where(property => property.Key != "@metadata"));
    }

    /// <summary>
    /// Structural equality for <see cref="ContentEquals"/>: objects need the same keys in the same
    /// order, arrays the same elements in the same order, and leaves compare by value.
    /// </summary>
    private static bool JsonEquivalent(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (null, null) => true,
        (JsonObject x, JsonObject y) => PropertiesEquivalent(x, y),
        (JsonArray x, JsonArray y) => x.Count == y.Count
            && x.Zip(y).All(pair => JsonEquivalent(pair.First, pair.Second)),
        (JsonValue x, JsonValue y) => JsonNode.DeepEquals(x, y),
        _ => false
    };

    private static bool PropertiesEquivalent(
        IEnumerable<KeyValuePair<string, JsonNode?>> a,
        IEnumerable<KeyValuePair<string, JsonNode?>> b)
    {
        List<KeyValuePair<string, JsonNode?>> left = [.. a];
        List<KeyValuePair<string, JsonNode?>> right = [.. b];

        return left.Count == right.Count
            && left.Zip(right).All(pair => pair.First.Key == pair.Second.Key
                && JsonEquivalent(pair.First.Value, pair.Second.Value));
    }

    private void LogBatchSummary(SyncBatchResult batch, int fileCount)
    {
        _logger.LogInformation("");

        // Group results by collection for summary
        int sections = batch.Results.Count(r => r.Document.DocumentType == DocumentType.Section);
        int layouts = batch.Results.Count(r => r.Document.DocumentType == DocumentType.Layout);
        int menus = batch.Results.Count(r => r.Document.DocumentType == DocumentType.Menu);
        int modals = batch.Results.Count(r => r.Document.DocumentType == DocumentType.Modal);
        int manifests = batch.Results.Count(r => r.Document.DocumentType == DocumentType.Manifest);
        int tags = batch.Results.Count(r => r.Document.DocumentType == DocumentType.Tag);
        int workflows = batch.Results.Count(r => r.Document.DocumentType == DocumentType.Workflow);
        int writePolicies = batch.Results.Count(r => r.Document.DocumentType == DocumentType.WritePolicy);
        int readPolicies = batch.Results.Count(r => r.Document.DocumentType == DocumentType.ReadPolicy);
        int entityConfigs = batch.Results.Count(r => r.Document.DocumentType == DocumentType.EntityConfig);
        int themes = batch.Results.Count(r => r.Document.DocumentType == DocumentType.Theme);
        int emailTemplates = batch.Results.Count(r => r.Document.DocumentType == DocumentType.EmailTemplate);
        int entities = batch.Results.Count(r => r.Document.DocumentType == DocumentType.Entity);
        int identities = batch.Results.Count(r => r.Document.DocumentType == DocumentType.Identity);

        // Build summary parts (only show non-zero counts)
        List<string> parts = [];
        if (sections > 0) parts.Add($"{sections} sections");
        if (layouts > 0) parts.Add($"{layouts} layouts");
        if (menus > 0) parts.Add($"{menus} menus");
        if (modals > 0) parts.Add($"{modals} modals");
        if (manifests > 0) parts.Add($"{manifests} manifests");
        if (tags > 0) parts.Add($"{tags} tags");
        if (workflows > 0) parts.Add($"{workflows} workflows");
        if (writePolicies > 0) parts.Add($"{writePolicies} write policies");
        if (readPolicies > 0) parts.Add($"{readPolicies} read policies");
        if (entityConfigs > 0) parts.Add($"{entityConfigs} entity configs");
        if (themes > 0) parts.Add($"{themes} themes");
        if (emailTemplates > 0) parts.Add($"{emailTemplates} email templates");
        if (entities > 0) parts.Add($"{entities} entities");
        if (identities > 0) parts.Add($"{identities} identities");

        string summary = parts.Count > 0 ? string.Join(", ", parts) : "0 documents";
        // The unchanged count makes real drift readable at a glance: a sync with nothing to do
        // reports every document unchanged instead of rewriting them all (issue #11).
        _logger.LogInformation(
            "[check] {Summary} synced in {Duration:F1}s ({Unchanged} unchanged)",
            summary, batch.TotalDuration.TotalSeconds, batch.UnchangedCount);

        if (batch.FailedCount > 0)
        {
            _logger.LogWarning("{Failed} sync operations failed", batch.FailedCount);
        }

        if (DocumentCollisionCount > 0)
        {
            _logger.LogWarning(
                "{Count} document collision(s): see the 'Document collision' lines above for which files were not synced",
                DocumentCollisionCount);
        }

        if (PinnedIdMismatchCount > 0)
        {
            _logger.LogWarning(
                "{Count} pinned id mismatch(es): see the 'Pinned id mismatch' lines above for the files whose @metadata.@id is not the id their document is stored under",
                PinnedIdMismatchCount);
        }

        if (batch.HumanReadableIdCount > 0)
        {
            _logger.LogWarning("{Count} files have human-readable IDs", batch.HumanReadableIdCount);
        }

        // Report orphan summary
        if (batch.OrphanDeletedCount > 0)
        {
            _logger.LogWarning("{Count} orphan(s) deleted", batch.OrphanDeletedCount);
        }
        else if (batch.OrphansDetected.Count > 0)
        {
            int totalOrphans = batch.OrphansDetected.Values.Sum(d => d.Count);
            if (totalOrphans > 0)
            {
                _logger.LogInformation("{Count} orphan(s) detected. Use --clean to remove.", totalOrphans);
            }
        }
    }
}
