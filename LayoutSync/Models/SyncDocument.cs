using System.Text.Json.Nodes;

namespace LayoutSync.Models;

/// <summary>
/// Represents a document to be synced between local files and RavenDB.
/// </summary>
public class SyncDocument
{
    /// <summary>
    /// The NanoID of the document (used as RavenDB document ID suffix).
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// The human-readable identifier for entities.
    /// This is used for lookup when syncing.
    /// </summary>
    public string? Identifier { get; set; }

    /// <summary>
    /// The type of document (determines handling logic).
    /// </summary>
    public DocumentType DocumentType { get; set; }

    /// <summary>
    /// The entity type string (e.g., "ui-schema-section", "modal-config").
    /// </summary>
    public string? EntityType { get; set; }

    /// <summary>
    /// The layout this document belongs to (e.g., "dirt-life").
    /// </summary>
    public string? LayoutId { get; set; }

    /// <summary>
    /// Full path to the source file.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Relative path from layouts directory.
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// The raw JSON content from the file.
    /// </summary>
    public JsonObject? Content { get; set; }

    /// <summary>
    /// The wrapped content (for sections, layouts, menus).
    /// For entities/identities, this is the same as Content.
    /// </summary>
    public JsonObject? WrappedContent { get; set; }

    /// <summary>
    /// Last modified time of the file.
    /// </summary>
    public DateTime LastModified { get; set; }

    /// <summary>
    /// Whether this document has a human-readable ID that needs replacement.
    /// </summary>
    public bool HasHumanReadableId { get; set; }

    /// <summary>
    /// The lookup key used to find this document in the database.
    /// For entities: identifier. For identities: id.
    /// </summary>
    public string LookupKey => DocumentType == DocumentType.Identity ? Id ?? string.Empty : Identifier ?? string.Empty;
}
