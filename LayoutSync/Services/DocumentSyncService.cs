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
    SyncOptions options)
{
    private readonly ILogger<DocumentSyncService> _logger = logger;
    private readonly LocalFileService _fileService = fileService;
    private readonly RavenDbService _ravenService = ravenService;
    private readonly SyncOptions _options = options;

    /// <summary>
    /// Syncs all files in the layouts directory.
    /// </summary>
    public async Task<SyncBatchResult> SyncAllAsync(string layoutsPath, string? layout = null, bool dryRun = false, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        SyncBatchResult batch = new();

        IEnumerable<string> files = _fileService.DiscoverFiles(layoutsPath, layout);
        int fileCount = 0;

        foreach (string filePath in files)
        {
            if (ct.IsCancellationRequested)
                break;

            fileCount++;
            SyncResult result = await SyncFileAsync(filePath, layoutsPath, dryRun, ct);
            batch.Results.Add(result);
        }

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

        // Wrap content if needed (sections, layouts, menus)
        JsonObject contentToSync = WrapIfNeeded(doc);
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
                // Create new document
                string? newId = await _ravenService.CreateDocumentAsync(doc, contentToSync, ct);
                sw.Stop();
                _logger.LogInformation("Created: {Identifier}", doc.Identifier);
                return SyncResult.Succeeded(doc, SyncAction.Created, newId, duration: sw.Elapsed);
            }
            else
            {
                // Check if content changed
                if (ContentEquals(existingDoc, contentToSync))
                {
                    _logger.LogDebug("No changes: {Identifier}", doc.Identifier);
                    return SyncResult.Skipped(doc, "No changes detected");
                }

                // Try to patch
                bool patched = await _ravenService.PatchDocumentAsync(existingDocId, contentToSync, ct);
                if (patched)
                {
                    sw.Stop();
                    _logger.LogInformation("Patched: {Identifier}", doc.Identifier);
                    return SyncResult.Succeeded(doc, SyncAction.Patched, existingDocId, duration: sw.Elapsed);
                }

                // Fallback: delete and recreate
                string? newId = await _ravenService.ReplaceDocumentAsync(existingDocId, doc, contentToSync, ct);
                sw.Stop();
                _logger.LogInformation("Recreated: {Identifier}", doc.Identifier);
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
    /// Wraps raw UI schemas in Entity wrapper for database storage.
    /// </summary>
    private JsonObject WrapIfNeeded(SyncDocument doc)
    {
        if (!doc.DocumentType.RequiresWrapping() || doc.Content == null)
        {
            return doc.Content ?? new JsonObject();
        }

        // The raw schema's "id" becomes the entity's "identifier"
        string? schemaId = doc.Content["id"]?.GetValue<string>();

        // Generate a proper NanoID for the entity's id
        string entityId = NanoIdValidator.IsValidNanoId(doc.Id) ? doc.Id! : NanoIdValidator.GenerateNanoId();

        JsonObject wrapped = new()
        {
            ["id"] = entityId,
            ["identifier"] = schemaId ?? doc.Identifier ?? Path.GetFileNameWithoutExtension(doc.FilePath),
            ["type"] = doc.DocumentType.GetEntityType(),
            ["active"] = true,
            ["createdDateTime"] = DateTime.UtcNow.ToString("o"),
            ["lastUpdatedDateTime"] = DateTime.UtcNow.ToString("o"),
            ["data"] = doc.Content.DeepClone(),
            ["tags"] = new JsonArray(),
            ["indexes"] = new JsonObject()
        };

        return wrapped;
    }

    /// <summary>
    /// Compares two JSON documents for equality (ignoring timestamps).
    /// </summary>
    private static bool ContentEquals(JsonObject? a, JsonObject? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;

        // Clone and remove timestamp fields for comparison
        JsonObject aCopy = a.DeepClone().AsObject();
        JsonObject bCopy = b.DeepClone().AsObject();

        RemoveTimestamps(aCopy);
        RemoveTimestamps(bCopy);

        return aCopy.ToJsonString() == bCopy.ToJsonString();
    }

    private static void RemoveTimestamps(JsonObject obj)
    {
        obj.Remove("createdDateTime");
        obj.Remove("lastUpdatedDateTime");
        obj.Remove("@metadata");
    }

    private void LogBatchSummary(SyncBatchResult batch, int fileCount)
    {
        _logger.LogInformation("");
        _logger.LogInformation(
            "[check] {Entities} entities, {Identities} identities, {Sections} sections synced in {Duration:F1}s",
            batch.Results.Count(r => r.Document.DocumentType == DocumentType.Entity),
            batch.Results.Count(r => r.Document.DocumentType == DocumentType.Identity),
            batch.Results.Count(r => r.Document.DocumentType == DocumentType.Section),
            batch.TotalDuration.TotalSeconds
        );

        if (batch.FailedCount > 0)
        {
            _logger.LogWarning("{Failed} sync operations failed", batch.FailedCount);
        }

        if (batch.HumanReadableIdCount > 0)
        {
            _logger.LogWarning("{Count} files have human-readable IDs", batch.HumanReadableIdCount);
        }
    }
}
