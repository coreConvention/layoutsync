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

        // Track synced identifiers per collection for orphan detection. Registry-driven;
        // an excluded collection gets no bucket, which is what "skip orphan detection"
        // means mechanically — DetectOrphansAsync iterates these KEYS. See issue #9.
        Dictionary<string, HashSet<string>> syncedIdentifiers =
            BuildOrphanTracking(_cliArgs.ExcludeCollections);

        IEnumerable<string> files = _fileService.DiscoverFiles(
            layoutsPath, layout, _cliArgs.ExcludeCollections, _cliArgs.ExcludeLayouts);
        int fileCount = 0;

        foreach (string filePath in files)
        {
            if (ct.IsCancellationRequested)
                break;

            fileCount++;
            SyncResult result = await SyncFileAsync(filePath, layoutsPath, dryRun, ct);
            batch.Results.Add(result);

            // Track synced identifier for orphan detection (static collections only)
            if (result.Success && result.Document.DocumentType.IsStaticCollection())
            {
                string collection = result.Document.DocumentType.GetCollection();
                string? identifier = result.Document.Identifier;
                if (!string.IsNullOrEmpty(identifier) && syncedIdentifiers.ContainsKey(collection))
                {
                    syncedIdentifiers[collection].Add(identifier);
                }
            }
        }

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
    /// Syncs a single file to RavenDB.
    /// </summary>
    public async Task<SyncResult> SyncFileAsync(string filePath, string layoutsPath, bool dryRun = false, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        // Read local file
        SyncDocument? doc = await _fileService.ReadDocumentAsync(filePath, layoutsPath);
        if (doc == null)
        {
            return SyncResult.Failed(
                new SyncDocument { FilePath = filePath },
                SyncAction.Skipped,
                "Failed to read file"
            );
        }

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

        // Inject layoutId for entity documents — entities must be scoped to a layout.
        // The layoutId is derived from the layout directory name (e.g., "layouts/dirt-life/" → "dirt-life").
        // We always overwrite layoutId in the content to ensure consistency with the directory name.
        // System collections (sections, layouts, menus, modals, manifests, tags, workflows) are EXEMPT.
        // Themes have two flavors: layout-scoped overrides (LayoutId set, the resolver matches
        // request tenant context) and the platform catalogue (LayoutId null/empty, available to
        // every tenant via /api/init). The null-guard below short-circuits stamping for the
        // platform-scoped flavor so those documents stay layoutId-less.
        if ((doc.DocumentType == DocumentType.Entity
             || doc.DocumentType == DocumentType.WritePolicy
             || doc.DocumentType == DocumentType.EntityConfig
             || doc.DocumentType == DocumentType.Theme) && !string.IsNullOrEmpty(doc.LayoutId))
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
        (string? existingDocId, JsonObject? existingDoc) = await _ravenService.FindDocumentAsync(doc, ct);

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
                // Check if content changed (ignoring $type metadata that may exist in DB)
                if (ContentEquals(existingDoc, contentToSync))
                {
                    _logger.LogDebug("No changes: {Identifier}", doc.Identifier);
                    return SyncResult.Skipped(doc, "No changes detected", ravenDocId: existingDocId);
                }

                // ALWAYS use replace (delete + create) instead of patch
                // This ensures $type metadata is removed from existing documents
                // Patching only updates values but doesn't remove existing $type properties
                string? newId = await _ravenService.ReplaceDocumentAsync(existingDocId, doc, contentToSync, ct);
                sw.Stop();
                _logger.LogInformation("Replaced: {Identifier}", doc.Identifier);
                return SyncResult.Succeeded(doc, SyncAction.Recreated, newId, duration: sw.Elapsed);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Sync failed: {Path}", doc.RelativePath);
            return SyncResult.Failed(doc, existingDocId == null ? SyncAction.Created : SyncAction.Patched, ex.Message, ex, sw.Elapsed);
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
            // If we don't have the RavenDB document ID, look it up
            if (string.IsNullOrEmpty(ravenDocumentId))
            {
                (string? foundDocId, _) = await _ravenService.FindDocumentAsync(doc, ct);
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

            // Get all identifiers currently in the collection (including their stamped layoutId).
            Dictionary<string, RavenDbService.OrphanCandidate> existingDocs =
                await _ravenService.GetAllIdentifiersAsync(collection, ct);

            // Find orphans (in DB but not synced from local files)
            Dictionary<string, RavenDbService.OrphanCandidate> rawOrphans = existingDocs
                .Where(kvp => !synced.Contains(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

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

            // Track orphans in batch result. Project the richer OrphanCandidate dict back
            // to (identifier -> docId) — the public batch model intentionally stays narrow.
            batch.OrphansDetected[collection] = orphans
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.DocumentId);

            foreach ((string identifier, RavenDbService.OrphanCandidate candidate) in orphans)
            {
                string docId = candidate.DocumentId;
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
    ///   <item><description>Candidates with a null <c>LayoutId</c> are conservatively dropped under a scoped run — they belong to globally-shared collections (sections, layouts, menus, modals, manifests, tags, workflows) where the data model carries no tenant attribution, and a scoped operation must not delete documents it cannot prove belong to that tenant.</description></item>
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
    ///   exclusion is active: most static collections (sections, layouts, menus, modals, manifests,
    ///   tags, workflows) are never stamped with <c>layoutId</c>, so such a candidate cannot be proven
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
        "entity-configs" => DocumentType.EntityConfig,
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
    /// Compares two JSON documents for equality (ignoring timestamps and $type metadata).
    /// </summary>
    private static bool ContentEquals(JsonObject? a, JsonObject? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;

        // Clone and remove metadata fields for comparison
        JsonObject aCopy = a.DeepClone().AsObject();
        JsonObject bCopy = b.DeepClone().AsObject();

        RemoveMetadata(aCopy);
        RemoveMetadata(bCopy);

        return aCopy.ToJsonString() == bCopy.ToJsonString();
    }

    /// <summary>
    /// Recursively removes timestamp and $type metadata from a JSON object.
    /// </summary>
    private static void RemoveMetadata(JsonObject obj)
    {
        // Remove top-level metadata
        obj.Remove("createdDateTime");
        obj.Remove("lastUpdatedDateTime");
        obj.Remove("@metadata");
        obj.Remove("$type");

        // Recursively process nested objects and arrays
        foreach (KeyValuePair<string, JsonNode?> kvp in obj.ToList())
        {
            if (kvp.Value is JsonObject nested)
            {
                RemoveMetadata(nested);
            }
            else if (kvp.Value is JsonArray array)
            {
                RemoveMetadataFromArray(array);
            }
        }
    }

    /// <summary>
    /// Recursively removes $type metadata from a JSON array and unwraps $values arrays.
    /// </summary>
    private static void RemoveMetadataFromArray(JsonArray array)
    {
        for (int i = 0; i < array.Count; i++)
        {
            JsonNode? item = array[i];
            if (item is JsonObject nested)
            {
                // Check if this is a wrapped array ({ "$type": "...", "$values": [...] })
                if (nested.ContainsKey("$values") && nested.ContainsKey("$type"))
                {
                    // This shouldn't happen at array element level, but handle it anyway
                    RemoveMetadata(nested);
                }
                else
                {
                    RemoveMetadata(nested);
                }
            }
            else if (item is JsonArray nestedArray)
            {
                RemoveMetadataFromArray(nestedArray);
            }
        }
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
        int entityConfigs = batch.Results.Count(r => r.Document.DocumentType == DocumentType.EntityConfig);
        int themes = batch.Results.Count(r => r.Document.DocumentType == DocumentType.Theme);
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
        if (entityConfigs > 0) parts.Add($"{entityConfigs} entity configs");
        if (themes > 0) parts.Add($"{themes} themes");
        if (entities > 0) parts.Add($"{entities} entities");
        if (identities > 0) parts.Add($"{identities} identities");

        string summary = parts.Count > 0 ? string.Join(", ", parts) : "0 documents";
        _logger.LogInformation("[check] {Summary} synced in {Duration:F1}s", summary, batch.TotalDuration.TotalSeconds);

        if (batch.FailedCount > 0)
        {
            _logger.LogWarning("{Failed} sync operations failed", batch.FailedCount);
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
