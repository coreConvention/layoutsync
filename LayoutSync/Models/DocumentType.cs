namespace LayoutSync.Models;

/// <summary>
/// Types of documents that can be synced.
/// Determines how the document is handled (wrapped vs direct) and which API endpoint to use.
/// </summary>
public enum DocumentType
{
    /// <summary>
    /// Full Entity document stored in entities/ folder.
    /// Synced directly without wrapping.
    /// </summary>
    Entity,

    /// <summary>
    /// Full Identity document stored in identities/ folder.
    /// Synced directly without wrapping.
    /// </summary>
    Identity,

    /// <summary>
    /// Raw UI schema from sections/ folder.
    /// Wrapped in an Entity with type "ui-schema-section" before syncing.
    /// </summary>
    Section,

    /// <summary>
    /// Raw UI schema for layout (e.g., dirt-life-layout.json).
    /// Wrapped in an Entity with type "ui-schema-layout" before syncing.
    /// </summary>
    Layout,

    /// <summary>
    /// Raw UI schema for menu (e.g., dirt-life-menu.json).
    /// Wrapped in an Entity with type "ui-schema-menu" before syncing.
    /// </summary>
    Menu,

    /// <summary>
    /// Modal configuration stored in modals/ folder.
    /// Full Entity document with type "modal-config".
    /// </summary>
    Modal
}

/// <summary>
/// Extension methods for DocumentType.
/// </summary>
public static class DocumentTypeExtensions
{
    /// <summary>
    /// Determines if the document type requires wrapping in an Entity.
    /// </summary>
    public static bool RequiresWrapping(this DocumentType type) =>
        type is DocumentType.Section or DocumentType.Layout or DocumentType.Menu;

    /// <summary>
    /// Gets the entity type string for documents that require wrapping.
    /// </summary>
    public static string? GetEntityType(this DocumentType type) => type switch
    {
        DocumentType.Section => "ui-schema-section",
        DocumentType.Layout => "ui-schema-layout",
        DocumentType.Menu => "ui-schema-menu",
        _ => null
    };

    /// <summary>
    /// Gets the API endpoint route for this document type.
    /// </summary>
    public static string GetApiRoute(this DocumentType type) => type switch
    {
        DocumentType.Identity => "i",
        _ => "e" // Entity, Section, Layout, Menu, Modal all go to /api/e
    };
}
