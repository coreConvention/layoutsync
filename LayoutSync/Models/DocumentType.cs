namespace LayoutSync.Models;

/// <summary>
/// Types of documents that can be synced.
/// All files are pre-wrapped with entity structure (identifier, type, active, tags, indexes, data).
/// Determines which RavenDB collection to use.
/// </summary>
public enum DocumentType
{
    /// <summary>
    /// Entity document from entities/ folder.
    /// Pre-wrapped, synced to entities collection.
    /// </summary>
    Entity,

    /// <summary>
    /// Identity document from identities/ folder.
    /// Pre-wrapped, synced to identities collection.
    /// </summary>
    Identity,

    /// <summary>
    /// UI schema section from sections/ folder.
    /// Pre-wrapped with type "ui-schema-section".
    /// </summary>
    Section,

    /// <summary>
    /// UI schema layout from layouts/ folder (e.g., dirt-life-layout.json).
    /// Pre-wrapped with type "ui-schema-layout".
    /// </summary>
    Layout,

    /// <summary>
    /// UI schema menu from menus/ folder (e.g., dirt-life-menu.json).
    /// Pre-wrapped with type "ui-schema-menu".
    /// </summary>
    Menu,

    /// <summary>
    /// Modal configuration from modals/ folder.
    /// Pre-wrapped with type "modal-config".
    /// </summary>
    Modal,

    /// <summary>
    /// Layout manifest from manifests/ folder.
    /// Pre-wrapped with type "layout-manifest".
    /// </summary>
    Manifest,

    /// <summary>
    /// Tag entity from tags/ folder.
    /// Pre-wrapped with type "tag".
    /// </summary>
    Tag,

    /// <summary>
    /// Workflow definition from workflows/ folder (repo root).
    /// Pre-wrapped with type "workflow-definition".
    /// First-class app infrastructure - not layout-specific.
    /// </summary>
    Workflow
}

/// <summary>
/// Extension methods for DocumentType.
/// </summary>
public static class DocumentTypeExtensions
{
    /// <summary>
    /// Gets the API endpoint route for this document type.
    /// </summary>
    public static string GetApiRoute(this DocumentType type) => type switch
    {
        DocumentType.Identity => "i",
        _ => "e" // Entity, Section, Layout, Menu, Modal all go to /api/e
    };

    /// <summary>
    /// Gets the RavenDB collection name for this document type.
    /// Static layout/schema data goes to dedicated collections (safe for orphan cleanup).
    /// User data stays in entities/identities (never auto-delete).
    /// All collection names are lowercase.
    /// </summary>
    public static string GetCollection(this DocumentType type) => type switch
    {
        DocumentType.Section => "sections",
        DocumentType.Layout => "layouts",
        DocumentType.Menu => "menus",
        DocumentType.Modal => "modals",
        DocumentType.Manifest => "manifests",
        DocumentType.Tag => "tags",
        DocumentType.Workflow => "workflows",
        DocumentType.Identity => "identities",
        DocumentType.Entity => "entities",
        _ => "entities"
    };

    /// <summary>
    /// Returns true if the collection is safe for orphan cleanup (static data only).
    /// </summary>
    public static bool IsStaticCollection(this DocumentType type) =>
        type is DocumentType.Section or DocumentType.Layout or DocumentType.Menu or DocumentType.Modal or DocumentType.Manifest or DocumentType.Tag or DocumentType.Workflow;
}
