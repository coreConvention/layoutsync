using System.Collections.Concurrent;
using LayoutSync.Configuration;
using LayoutSync.Models;
using Microsoft.Extensions.Logging;

namespace LayoutSync.Services;

/// <summary>
/// Service that watches the layouts directory for file changes
/// and triggers sync operations with debouncing.
/// Tracks synced files to enable deletion from RavenDB when files are removed.
/// </summary>
public class FileWatcherService(
    ILogger<FileWatcherService> logger,
    DocumentSyncService syncService,
    LocalFileService fileService,
    SyncOptions options,
    CommandLineArgs cliArgs)
{
    private readonly ILogger<FileWatcherService> _logger = logger;
    private readonly DocumentSyncService _syncService = syncService;
    private readonly LocalFileService _fileService = fileService;
    private readonly SyncOptions _options = options;
    private readonly CommandLineArgs _cliArgs = cliArgs;
    private readonly ConcurrentDictionary<string, DateTime> _pendingFiles = new();
    private readonly ConcurrentDictionary<string, DateTime> _pendingDeletions = new();
    private readonly ConcurrentDictionary<string, TrackedFile> _trackedFiles = new(StringComparer.OrdinalIgnoreCase);
    // Serializes batch processing so two timer fires can't overlap and produce
    // concurrent SyncFileAsync calls for the same path (older payload would
    // silently overwrite the newer one if its round-trip finished last).
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private Timer? _debounceTimer;
    private Timer? _deletionDebounceTimer;
    private string _layoutsPath = string.Empty;
    private string? _specificLayout;
    private bool _dryRun;

    /// <summary>
    /// Tracks a synced file for later deletion handling.
    /// </summary>
    private record TrackedFile(
        string Identifier,
        DocumentType DocumentType,
        string? RavenDocumentId);

    /// <summary>
    /// Watches the layouts directory for changes and syncs files.
    /// Accepts optional initial sync result to populate file tracking for deletion support.
    /// </summary>
    public async Task WatchAsync(
        string layoutsPath,
        string? specificLayout = null,
        bool dryRun = false,
        SyncBatchResult? initialSyncResult = null,
        CancellationToken ct = default)
    {
        _layoutsPath = layoutsPath;
        _specificLayout = specificLayout;
        _dryRun = dryRun;

        // Populate tracking from initial sync results
        if (initialSyncResult != null)
        {
            PopulateTracking(initialSyncResult);
        }

        string watchPath = specificLayout != null
            ? Path.Combine(layoutsPath, specificLayout)
            : layoutsPath;

        if (!Directory.Exists(watchPath))
        {
            _logger.LogError("Watch path does not exist: {Path}", watchPath);
            return;
        }

        using FileSystemWatcher watcher = new(watchPath)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            Filter = "*.json",
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };

        watcher.Changed += OnFileChanged;
        watcher.Created += OnFileChanged;
        watcher.Renamed += OnFileRenamed;
        watcher.Deleted += OnFileDeleted;

        _logger.LogInformation("Watching: {Path} ({TrackedCount} files tracked)", watchPath, _trackedFiles.Count);

        // Wait until cancellation
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("Watch cancelled");
        }
    }

    /// <summary>
    /// Populates the file tracking dictionary from initial sync results.
    /// This enables deletion handling — when a file is deleted, we look up
    /// the tracked metadata to know which RavenDB document to remove.
    /// </summary>
    private void PopulateTracking(SyncBatchResult batchResult)
    {
        int tracked = 0;
        foreach (SyncResult result in batchResult.Results)
        {
            if (!result.Success || string.IsNullOrEmpty(result.Document.Identifier))
                continue;

            string filePath = result.Document.FilePath;
            if (string.IsNullOrEmpty(filePath))
                continue;

            _trackedFiles[filePath] = new TrackedFile(
                result.Document.Identifier,
                result.Document.DocumentType,
                result.RavenDocumentId);
            tracked++;
        }

        _logger.LogDebug("Populated tracking for {Count} files from initial sync", tracked);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!e.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return;

        // Skip temp files and backups
        if (e.Name?.StartsWith('.') == true || e.Name?.Contains('~') == true)
            return;

        if (IsExcluded(e.FullPath))
        {
            _logger.LogDebug("Skipping excluded path: {Path}", e.FullPath);
            return;
        }

        _logger.LogDebug("File changed: {Path}", e.FullPath);
        QueueFileForSync(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (!e.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return;

        _logger.LogDebug("File renamed: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);

        // Remove tracking for old path (treat as deletion). Judged per-side, not
        // per-event: an included→excluded move must still delete the old DB doc
        // (its synced source just left the included space), which the tracking
        // invariant handles — files inside excluded dirs are never tracked, so
        // an excluded OLD path simply misses here. See issue #9.
        if (_trackedFiles.TryRemove(e.OldFullPath, out TrackedFile? oldTracked))
        {
            QueueFileForDeletion(e.OldFullPath, oldTracked);
        }

        // Sync side judged by the NEW path: excluded→included moves must sync,
        // included→excluded moves must not.
        if (IsExcluded(e.FullPath))
        {
            _logger.LogDebug("Skipping excluded rename target: {Path}", e.FullPath);
            return;
        }

        // Queue new path for sync (will create new tracking entry)
        QueueFileForSync(e.FullPath);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (!e.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return;

        // Skip temp files and backups
        if (e.Name?.StartsWith('.') == true || e.Name?.Contains('~') == true)
            return;

        // Guard BEFORE the untracked-deletion fallback below: DeriveTrackingFromPath
        // would otherwise happily produce a deletion candidate for a file inside an
        // excluded directory. See issue #9.
        if (IsExcluded(e.FullPath))
        {
            _logger.LogDebug("Skipping excluded path deletion: {Path}", e.FullPath);
            return;
        }

        _logger.LogInformation("File deleted: {Path}", e.FullPath);

        // Look up tracked info for this file
        if (_trackedFiles.TryRemove(e.FullPath, out TrackedFile? tracked))
        {
            QueueFileForDeletion(e.FullPath, tracked);
        }
        else
        {
            // Fallback: derive metadata from file path
            TrackedFile? derived = DeriveTrackingFromPath(e.FullPath);
            if (derived != null)
            {
                _logger.LogDebug("Using derived tracking for untracked file: {Path}", e.FullPath);
                QueueFileForDeletion(e.FullPath, derived);
            }
            else
            {
                _logger.LogWarning("Cannot determine document info for deleted file: {Path}", e.FullPath);
            }
        }
    }

    /// <summary>
    /// Instance wrapper over <see cref="IsExcludedPath"/> bound to this watcher's
    /// layouts root and the run's CLI exclusions.
    /// </summary>
    private bool IsExcluded(string fullPath) =>
        IsExcludedPath(_layoutsPath, fullPath, _cliArgs.ExcludeCollections, _cliArgs.ExcludeLayouts);

    /// <summary>
    /// Pure decision helper: does <paramref name="fullPath"/> fall inside an excluded
    /// layout directory or an excluded collection folder? Extracted as
    /// <c>internal static</c> so it can be unit-tested without a live watcher.
    ///
    /// Separators are normalized (<c>\</c>→<c>/</c>) BEFORE splitting — FileSystemWatcher
    /// hands back OS-native paths, and on Windows <c>Path.GetRelativePath</c> yields
    /// backslashes that a <c>'/'</c>-split would treat as one giant segment.
    /// Segment 0 is the layout directory (Ordinal — Linux CI is case-sensitive);
    /// segment 1 is the collection folder (OrdinalIgnoreCase, mirroring
    /// <see cref="LocalFileService.DetermineDocumentType"/>). See issue #9.
    /// </summary>
    internal static bool IsExcludedPath(
        string layoutsPath,
        string fullPath,
        IReadOnlyCollection<string> excludeCollections,
        IReadOnlyCollection<string> excludeLayouts)
    {
        if (excludeCollections.Count == 0 && excludeLayouts.Count == 0)
            return false;

        string relativePath = Path.GetRelativePath(layoutsPath, fullPath).Replace('\\', '/');
        string[] parts = relativePath.Split('/');

        if (parts.Length > 0 && excludeLayouts.Contains(parts[0], StringComparer.Ordinal))
            return true;

        return parts.Length > 1 && excludeCollections.Contains(parts[1], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Derives tracking metadata from a file path when the file wasn't tracked.
    /// Uses the folder structure to determine document type and filename as identifier.
    /// </summary>
    private TrackedFile? DeriveTrackingFromPath(string fullPath)
    {
        try
        {
            string relativePath = Path.GetRelativePath(_layoutsPath, fullPath);
            DocumentType documentType = LocalFileService.DetermineDocumentType(relativePath);

            // Use filename without extension as identifier (convention in w31rd.com layouts)
            string identifier = Path.GetFileNameWithoutExtension(fullPath);

            return new TrackedFile(identifier, documentType, RavenDocumentId: null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to derive tracking from path: {Path}", fullPath);
            return null;
        }
    }

    private void QueueFileForSync(string filePath)
    {
        _pendingFiles[filePath] = DateTime.UtcNow;

        // Reset debounce timer. Timer callback is void; the lambda discards the
        // Task. ProcessPendingFiles has its own gate + per-file try/catch, so any
        // exception that escapes (e.g. semaphore disposed during shutdown) only
        // hits UnobservedTaskException — by design no longer fatal in .NET 6+.
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(
            state => _ = ProcessPendingFiles(state),
            null,
            EffectiveDebounceMs(),
            Timeout.Infinite
        );
    }

    private void QueueFileForDeletion(string filePath, TrackedFile tracked)
    {
        _pendingDeletions[filePath] = DateTime.UtcNow;

        // Store tracked info for processing (use a separate dictionary keyed by path)
        _pendingDeletionInfo[filePath] = tracked;

        // Reset deletion debounce timer
        _deletionDebounceTimer?.Dispose();
        _deletionDebounceTimer = new Timer(
            state => _ = ProcessPendingDeletions(state),
            null,
            EffectiveDebounceMs(),
            Timeout.Infinite
        );
    }

    /// <summary>
    /// DebounceMs == 0 means "fire on the next thread-pool tick" — we still go
    /// through the Timer so the ConcurrentDictionary coalescing path is shared
    /// with the normal debounce case.
    /// </summary>
    private int EffectiveDebounceMs() => _options.DebounceMs == 0 ? 1 : _options.DebounceMs;

    /// <summary>
    /// Tracked info for pending deletions (separate from _trackedFiles which gets cleared on delete).
    /// </summary>
    private readonly ConcurrentDictionary<string, TrackedFile> _pendingDeletionInfo = new(StringComparer.OrdinalIgnoreCase);

    private async Task ProcessPendingFiles(object? state)
    {
        // Serialize with any in-flight batch (and with deletions) so two timer
        // fires cannot produce concurrent SyncFileAsync calls for the same
        // path. Events that arrive while we wait coalesce into _pendingFiles
        // (it was cleared by the prior batch), so the next batch picks them
        // up with a fresh file-read at the start of SyncFileAsync.
        await _processGate.WaitAsync();
        try
        {
            List<string> filesToProcess = _pendingFiles.Keys.ToList();
            _pendingFiles.Clear();

            foreach (string filePath in filesToProcess)
            {
                try
                {
                    _logger.LogInformation("Changed: {Path}", Path.GetRelativePath(_layoutsPath, filePath));
                    SyncResult result = await _syncService.SyncFileAsync(filePath, _layoutsPath, _dryRun);

                    if (result.Success)
                    {
                        string action = result.Action switch
                        {
                            SyncAction.Created => "Created",
                            SyncAction.Patched => "Patched",
                            SyncAction.Recreated => "Recreated",
                            _ => "Synced"
                        };
                        _logger.LogInformation("-> {Action} '{Identifier}'", action, result.Document.Identifier);

                        // Update tracking for this file
                        if (!string.IsNullOrEmpty(result.Document.Identifier))
                        {
                            _trackedFiles[filePath] = new TrackedFile(
                                result.Document.Identifier,
                                result.Document.DocumentType,
                                result.RavenDocumentId);
                        }

                        if (result.Document.HasHumanReadableId)
                        {
                            _logger.LogWarning("-> Human-readable id '{Id}' detected", result.Document.Id);
                        }
                    }
                    else
                    {
                        _logger.LogError("-> Sync failed: {Error}", result.ErrorMessage);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing file: {Path}", filePath);
                }
            }
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task ProcessPendingDeletions(object? state)
    {
        // Shares the gate with ProcessPendingFiles: a delete must not race a
        // sync for the same path (e.g. rename = delete-old + create-new).
        await _processGate.WaitAsync();
        try
        {
            List<string> filesToProcess = _pendingDeletions.Keys.ToList();
            _pendingDeletions.Clear();

            int deletedCount = 0;
            int failedCount = 0;

            foreach (string filePath in filesToProcess)
            {
                if (!_pendingDeletionInfo.TryRemove(filePath, out TrackedFile? tracked))
                    continue;

                try
                {
                    string relativePath = Path.GetRelativePath(_layoutsPath, filePath);
                    _logger.LogInformation("Deleting from DB: {Path} ({Identifier})", relativePath, tracked.Identifier);

                    SyncResult result = await _syncService.DeleteTrackedDocumentAsync(
                        tracked.Identifier,
                        tracked.DocumentType,
                        tracked.RavenDocumentId,
                        _dryRun);

                    if (result.Success && result.Action == SyncAction.Deleted)
                    {
                        deletedCount++;
                    }
                    else if (result.Action == SyncAction.Skipped)
                    {
                        _logger.LogDebug("-> Skipped: {Reason}", result.ErrorMessage);
                    }
                    else
                    {
                        failedCount++;
                        _logger.LogError("-> Delete failed: {Error}", result.ErrorMessage);
                    }
                }
                catch (Exception ex)
                {
                    failedCount++;
                    _logger.LogError(ex, "Error deleting document for file: {Path}", filePath);
                }
            }

            if (deletedCount > 0 || failedCount > 0)
            {
                _logger.LogInformation("Deletion batch: {Deleted} deleted, {Failed} failed", deletedCount, failedCount);
            }
        }
        finally
        {
            _processGate.Release();
        }
    }
}
