using System.Collections.Concurrent;
using LayoutSync.Configuration;
using Microsoft.Extensions.Logging;

namespace LayoutSync.Services;

/// <summary>
/// Service that watches the layouts directory for file changes
/// and triggers sync operations with debouncing.
/// </summary>
public class FileWatcherService(
    ILogger<FileWatcherService> logger,
    DocumentSyncService syncService,
    SyncOptions options)
{
    private readonly ILogger<FileWatcherService> _logger = logger;
    private readonly DocumentSyncService _syncService = syncService;
    private readonly SyncOptions _options = options;
    private readonly ConcurrentDictionary<string, DateTime> _pendingFiles = new();
    private Timer? _debounceTimer;
    private string _layoutsPath = string.Empty;
    private string? _specificLayout;
    private bool _dryRun;

    /// <summary>
    /// Watches the layouts directory for changes and syncs files.
    /// </summary>
    public async Task WatchAsync(string layoutsPath, string? specificLayout = null, bool dryRun = false, CancellationToken ct = default)
    {
        _layoutsPath = layoutsPath;
        _specificLayout = specificLayout;
        _dryRun = dryRun;

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

        _logger.LogInformation("Watching: {Path}", watchPath);

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
        QueueFileForSync(e.FullPath);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (!e.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return;

        _logger.LogWarning("File deleted: {Path}", e.FullPath);
        // TODO: Handle deletion - remove from database?
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
                Models.SyncResult result = await _syncService.SyncFileAsync(filePath, _layoutsPath, _dryRun);

                if (result.Success)
                {
                    string action = result.Action switch
                    {
                        Models.SyncAction.Created => "Created",
                        Models.SyncAction.Patched => "Patched",
                        Models.SyncAction.Recreated => "Recreated",
                        _ => "Synced"
                    };
                    _logger.LogInformation("-> {Action} '{Identifier}'", action, result.Document.Identifier);

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
}
