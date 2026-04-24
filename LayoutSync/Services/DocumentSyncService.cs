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
    SeedAuthorshipValidator seedAuthorshipValidator,
    SeedCrossReferenceValidator seedCrossReferenceValidator,
    SyncOptions options,
    CommandLineArgs cliArgs)
{
    private readonly ILogger<DocumentSyncService> _logger = logger;
    private readonly LocalFileService _fileService = fileService;
    private readonly RavenDbService _ravenService = ravenService;
    private readonly RelativeDateResolver _relativeDateResolver = relativeDateResolver;
    private readonly SeedAuthorshipValidator _seedAuthorshipValidator = seedAuthorshipValidator;
    private readonly SeedCrossReferenceValidator _seedCrossReferenceValidator = seedCrossReferenceValidator;
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

        // Reset accumulator state at the start of every batch so prior-batch seeds
        // (e.g. earlier watch-mode re-syncs) don't leak into this cross-reference pass.
        _seedCrossReferenceValidator.Reset();

        // Track synced identifiers per collection for orphan detection
        // Keys must match GetCollection() output (lowercase)
        Dictionary<string, HashSet<string>> syncedIdentifiers = new()
        {
            ["sections"] = [],
            ["layouts"] = [],
            ["menus"] = [],
            ["modals"] = [],
            ["manifests"] = [],
            ["tags"] = [],
            ["workflows"] = [],
            ["WritePolicies"] = [],
            ["entity-configs"] = []
        };

        IEnumerable<string> files = _fileService.DiscoverFiles(layoutsPath, layout);
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

        // Cross-reference phase: after every file in the batch has been recorded, emit
        // one WARN per offending referencer file where outbound NanoID refs point at
        // a missing or unpinned owner. See issue #300.
        _seedCrossReferenceValidator.ValidateAll();

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

        // Non-blocking policy nudge: flag raw NanoIDs in identity-bearing fields on entity seeds
        // so authors migrate to stable `ext:{provider}:{externalId}` refs. See issue #308.
        _seedAuthorshipValidator.Validate(doc.DocumentType, doc.RelativePath, contentToSync);

        // Record this seed for the batch-end cross-reference check. The validator accumulates
        // (declared id, pinned-ness, outbound NanoID-shaped references) per file and emits
        // per-referencer warnings at batch end. See issue #300.
        _seedCrossReferenceValidator.RecordSeed(doc.DocumentType, doc.RelativePath, contentToSync);

        // Inject layoutId for entity documents — entities must be scoped to a layout.
        // The layoutId is derived from the layout directory name (e.g., "layouts/dirt-life/" → "dirt-life").
        // We always overwrite layoutId in the content to ensure consistency with the directory name.
        // System collections (sections, layouts, menus, modals, manifests, tags, workflows) are EXEMPT.
        if ((doc.DocumentType == DocumentType.Entity || doc.DocumentType == DocumentType.WritePolicy || doc.DocumentType == DocumentType.EntityConfig) && !string.IsNullOrEmpty(doc.LayoutId))
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
    /// </summary>
    private async Task DetectOrphansAsync(
        SyncBatchResult batch,
        Dictionary<string, HashSet<string>> syncedIdentifiers,
        bool deleteOrphans,
        bool dryRun,
        CancellationToken ct)
    {
        foreach ((string collection, HashSet<string> synced) in syncedIdentifiers)
        {
            if (ct.IsCancellationRequested)
                break;

            // Get all identifiers currently in the collection
            Dictionary<string, string> existingDocs = await _ravenService.GetAllIdentifiersAsync(collection, ct);

            // Find orphans (in DB but not synced from local files)
            Dictionary<string, string> orphans = existingDocs
                .Where(kvp => !synced.Contains(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            if (orphans.Count == 0)
                continue;

            // Track orphans in batch result
            batch.OrphansDetected[collection] = orphans;

            foreach ((string identifier, string docId) in orphans)
            {
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
        _ => DocumentType.Entity
    };

    /// <summary>
    /// Validates files and reports issues without making changes.
    /// </summary>
    public async Task<SyncBatchResult> ValidateAsync(string layoutsPath, string? layout = null, CancellationToken ct = default)
    {
        SyncBatchResult batch = new();
        List<SyncDocument> humanReadableIds = [];

        foreach (string filePath in _fileService.DiscoverFiles(layoutsPath, layout))
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

        foreach (string filePath in _fileService.DiscoverFiles(layoutsPath, layout))
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
