namespace LayoutSync.Models;

/// <summary>
/// Result of a sync operation for a single document.
/// </summary>
public class SyncResult
{
    /// <summary>
    /// The document that was synced.
    /// </summary>
    public SyncDocument Document { get; init; } = null!;

    /// <summary>
    /// The action taken during sync.
    /// </summary>
    public SyncAction Action { get; init; }

    /// <summary>
    /// Whether the sync was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error message if sync failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Exception details if sync failed.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// The RavenDB document ID after sync (e.g., "Entities/abc123").
    /// </summary>
    public string? RavenDocumentId { get; init; }

    /// <summary>
    /// Whether the local file was updated (e.g., ID fix).
    /// </summary>
    public bool LocalFileUpdated { get; init; }

    /// <summary>
    /// Duration of the sync operation.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Creates a successful sync result.
    /// </summary>
    public static SyncResult Succeeded(SyncDocument document, SyncAction action, string? ravenDocId = null, bool localUpdated = false, TimeSpan? duration = null) =>
        new()
        {
            Document = document,
            Action = action,
            Success = true,
            RavenDocumentId = ravenDocId,
            LocalFileUpdated = localUpdated,
            Duration = duration ?? TimeSpan.Zero
        };

    /// <summary>
    /// Creates a failed sync result.
    /// </summary>
    public static SyncResult Failed(SyncDocument document, SyncAction action, string errorMessage, Exception? ex = null, TimeSpan? duration = null) =>
        new()
        {
            Document = document,
            Action = action,
            Success = false,
            ErrorMessage = errorMessage,
            Exception = ex,
            Duration = duration ?? TimeSpan.Zero
        };

    /// <summary>
    /// Creates a skipped sync result.
    /// </summary>
    public static SyncResult Skipped(SyncDocument document, string reason) =>
        new()
        {
            Document = document,
            Action = SyncAction.Skipped,
            Success = true,
            ErrorMessage = reason
        };
}

/// <summary>
/// Actions that can be taken during sync.
/// </summary>
public enum SyncAction
{
    /// <summary>Document was skipped (no changes detected).</summary>
    Skipped,

    /// <summary>New document was created in database.</summary>
    Created,

    /// <summary>Existing document was updated via JSON Patch.</summary>
    Patched,

    /// <summary>Document was deleted and recreated (patch failed).</summary>
    Recreated,

    /// <summary>Document was deleted from database.</summary>
    Deleted,

    /// <summary>Only validated, no changes made (--validate-only mode).</summary>
    Validated,

    /// <summary>Local file was updated (--fix-ids mode).</summary>
    LocalFixed
}

/// <summary>
/// Summary of a sync batch operation.
/// </summary>
public class SyncBatchResult
{
    /// <summary>Individual results for each document.</summary>
    public List<SyncResult> Results { get; } = [];

    /// <summary>Total documents processed.</summary>
    public int TotalCount => Results.Count;

    /// <summary>Number of successful operations.</summary>
    public int SuccessCount => Results.Count(r => r.Success);

    /// <summary>Number of failed operations.</summary>
    public int FailedCount => Results.Count(r => !r.Success);

    /// <summary>Number of documents created.</summary>
    public int CreatedCount => Results.Count(r => r.Action == SyncAction.Created);

    /// <summary>Number of documents updated.</summary>
    public int UpdatedCount => Results.Count(r => r.Action is SyncAction.Patched or SyncAction.Recreated);

    /// <summary>Number of documents skipped.</summary>
    public int SkippedCount => Results.Count(r => r.Action == SyncAction.Skipped);

    /// <summary>Number of documents with human-readable IDs.</summary>
    public int HumanReadableIdCount => Results.Count(r => r.Document.HasHumanReadableId);

    /// <summary>Total duration of the batch.</summary>
    public TimeSpan TotalDuration { get; set; }
}
