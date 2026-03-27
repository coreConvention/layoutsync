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
    SyncOptions options)
{
    private readonly ILogger<FileWatcherService> _logger = logger;
    private readonly DocumentSyncService _syncService = syncService;
    private readonly LocalFileService _fileService = fileService;
    private readonly SyncOptions _options = options;
    private readonly ConcurrentDictionary<string, DateTime> _pendingFiles = new();
    private readonly ConcurrentDictionary<string, DateTime> _pendingDeletions = new();
    private readonly ConcurrentDictionary<string, TrackedFile> _trackedFiles = new(StringComparer.OrdinalIgnoreCase);
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

        _logger.LogDebug("File changed: {Path}", e.FullPath);
        QueueFileForSync(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (!e.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return;

        _logger.LogDebug("File renamed: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);

        // Remove tracking for old path (treat as deletion)
        if (_trackedFiles.TryRemove(e.OldFullPath, out TrackedFile? oldTracked))
        {
            QueueFileForDeletion(e.OldFullPath, oldTracked);
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

        // Reset debounce timer
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(
            ProcessPendingFiles,
            null,
            _options.DebounceMs,
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
            ProcessPendingDeletions,
            null,
            _options.DebounceMs,
            Timeout.Infinite
        );
    }

    /// <summary>
    /// Tracked info for pending deletions (separate from _trackedFiles which gets cleared on delete).
    /// </summary>
    private readonly ConcurrentDictionary<string, TrackedFile> _pendingDeletionInfo = new(StringComparer.OrdinalIgnoreCase);

    private async void ProcessPendingFiles(object? state)
    {
        // Take snapshot of pending files
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

    private async void ProcessPendingDeletions(object? state)
    {
        // Take snapshot of pending deletions
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
}
