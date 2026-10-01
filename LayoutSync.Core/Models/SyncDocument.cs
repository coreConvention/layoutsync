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
    /// The document id the file pins in <c>@metadata.@id</c>; null when it pins none, or when
    /// <see cref="Id"/> comes from a top-level <c>id</c> field instead. A pin is applied only when
    /// the document is created (under <c>--preserve-ids</c>): an existing document is found by
    /// identifier and keeps the id it already has, which the sync reports when the two differ
    /// (issue #46).
    /// </summary>
    public string? PinnedId { get; set; }

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
    /// The layout directory the source file lives in (e.g., "dirt-life"); null for the platform
    /// theme catalogue. Set for EVERY file under a layout directory, whether or not the stored
    /// document carries a <c>layoutId</c> — identity decisions use <see cref="StoredLayoutId"/>.
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

    /// <summary>
    /// True when sync stamps the layout directory onto the stored document as its <c>layoutId</c>
    /// (overwriting whatever the file says). The platform theme catalogue has no layout directory,
    /// so it is never stamped.
    /// </summary>
    public bool IsLayoutIdStamped => DocumentType.StampsLayoutId() && !string.IsNullOrEmpty(LayoutId);

    /// <summary>
    /// The <c>layoutId</c> the stored document carries, or <c>""</c> when it carries none: the
    /// stamped directory when <see cref="IsLayoutIdStamped"/>, otherwise the file's own top-level
    /// <c>layoutId</c> — every other document is written as authored, so that is what lands in the
    /// database. Lookup, collision and orphan identity all key on this, never on
    /// <see cref="LayoutId"/>: a section under <c>layouts/dirt-life/</c> whose file declares
    /// <c>"layoutId": "dirt-life"</c> is stored WITH it, while one whose file declares none is
    /// stored without one. Issues #16/#17 assumed non-stamped documents never carry the field,
    /// which let two layouts' same-identifier sections share one document (#28) and made
    /// <c>--clean</c> delete every section that declares one (#31).
    /// </summary>
    public string StoredLayoutId => IsLayoutIdStamped ? LayoutId! : ReadLayoutIdField(Content);

    /// <summary>
    /// Reads a top-level <c>layoutId</c> string; <c>""</c> when absent, JSON null, or not a string
    /// (missing and empty are the same thing everywhere layoutIds are compared — see issue #13).
    /// </summary>
    public static string ReadLayoutIdField(JsonObject? content) =>
        content?["layoutId"] is JsonValue value && value.TryGetValue(out string? layoutId)
            ? layoutId
            : string.Empty;
}
