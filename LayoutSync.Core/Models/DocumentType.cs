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
    /// Workflow definition from a layout's workflows/ folder.
    /// Pre-wrapped with type "workflow-definition".
    /// Written as authored (not stamped); a seed that declares a layoutId is attributed to that
    /// layout, and tenant workflow starts look definitions up by identifier and layoutId.
    /// </summary>
    Workflow,

    /// <summary>
    /// Write policy definition from write-policies/ folder.
    /// Pre-wrapped with type "write-policy".
    /// </summary>
    WritePolicy,

    /// <summary>
    /// Read policy definition from read-policies/ folder.
    /// Pre-wrapped with type "read-policy".
    /// </summary>
    ReadPolicy,

    /// <summary>
    /// Entity config definition from entity-configs/ folder.
    /// Pre-wrapped with type "entity-config".
    /// Layout-scoped — layoutId is injected automatically.
    /// </summary>
    EntityConfig,

    /// <summary>
    /// Email template definition from email-templates/ folder.
    /// Pre-wrapped with type "email-template".
    /// Layout-scoped — layoutId is injected automatically.
    /// </summary>
    EmailTemplate,

    /// <summary>
    /// Theme definition from a <c>themes/</c> folder.
    /// Pre-wrapped with type "theme-definition". Two scopes are supported:
    /// <list type="bullet">
    ///   <item><description><b>Layout-scoped overrides</b> live in <c>layouts/{layoutId}/themes/*.json</c>.
    ///   LayoutSync stamps <c>layoutId</c> from the directory name; the API
    ///   resolver paints these on top of the active platform theme when the
    ///   request's tenant context matches.</description></item>
    ///   <item><description><b>Platform catalogue</b> lives in <c>&lt;layoutsParent&gt;/themes/*.json</c>
    ///   (sibling to the layouts root). No <c>layoutId</c> is stamped; the
    ///   API serves these as the base catalogue available to every tenant
    ///   via <c>/api/init</c>'s <c>availableThemes</c> field.</description></item>
    /// </list>
    /// Tenant-agnostic by design — no platform code or LayoutSync logic
    /// branches on specific tenant identifiers; scope is determined by the
    /// presence (or absence) of <c>layoutId</c> on the document.
    /// </summary>
    Theme,
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
        DocumentType.WritePolicy => "e", // Write policies use entity API route
        DocumentType.ReadPolicy => "e", // Read policies use entity API route
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
        DocumentType.WritePolicy => "WritePolicies",
        DocumentType.ReadPolicy => "ReadPolicies",
        DocumentType.EntityConfig => "entity-configs",
        DocumentType.EmailTemplate => "email-templates",
        DocumentType.Theme => "theme-definitions",
        DocumentType.Identity => "identities",
        DocumentType.Entity => "entities",
        _ => "entities"
    };

    /// <summary>
    /// Returns true if the collection is safe for orphan cleanup (static data only).
    /// </summary>
    public static bool IsStaticCollection(this DocumentType type) =>
        type is DocumentType.Section or DocumentType.Layout or DocumentType.Menu or DocumentType.Modal or DocumentType.Manifest or DocumentType.Tag or DocumentType.Workflow or DocumentType.WritePolicy or DocumentType.ReadPolicy or DocumentType.EntityConfig or DocumentType.EmailTemplate or DocumentType.Theme;

    /// <summary>
    /// Returns true for document types whose stored <c>layoutId</c> is STAMPED from the layout
    /// directory name at sync time, overwriting whatever the file says. The other collections
    /// (sections, layouts, menus, modals, manifests, tags, workflows) are written as authored, so
    /// they carry a <c>layoutId</c> exactly when their file declares one — this is intentionally
    /// narrower than <see cref="IsStaticCollection"/>.
    /// </summary>
    /// <remarks>
    /// Consulted through <see cref="SyncDocument.IsLayoutIdStamped"/>, which the sync writer uses to
    /// decide whether to stamp the field and <see cref="SyncDocument.StoredLayoutId"/> uses to
    /// report the value the stored document ends up with — so the write and every read of that
    /// value never disagree. See issues #16 and #28.
    /// </remarks>
    public static bool StampsLayoutId(this DocumentType type) =>
        type is DocumentType.Entity or DocumentType.WritePolicy or DocumentType.ReadPolicy or DocumentType.EntityConfig or DocumentType.EmailTemplate or DocumentType.Theme;

    /// <summary>
    /// Returns true for document types identified by (identifier, stored <c>layoutId</c>): each
    /// layout may ship its own document under one identifier, and a lookup resolves only to the
    /// document carrying the file's <see cref="SyncDocument.StoredLayoutId"/> — a document without
    /// one matches only other documents without one. This mirrors how the platform reads these
    /// collections (per tenant, by identifier and layoutId), so two layouts' same-identifier copies
    /// stay two documents instead of the last writer taking over the other's (issues #16, #28).
    /// </summary>
    /// <remarks>
    /// The exceptions: <see cref="DocumentType.Identity"/> is looked up by document id, and
    /// <see cref="DocumentType.Manifest"/> by identifier alone — a manifest's identifier IS the
    /// tenant key (the platform resolves tenants by it and fails closed when two equivalent ones
    /// exist), so two layouts shipping one manifest identifier is a conflict to refuse, not two
    /// documents to keep (the in-run collision check refuses it; see <c>DocumentClaims</c>). Any
    /// future type is layout-scoped unless listed here, the tenant-isolating default.
    /// </remarks>
    public static bool IsLayoutScoped(this DocumentType type) =>
        type is not (DocumentType.Identity or DocumentType.Manifest);

    /// <summary>
    /// Returns true for the layout-scoped types whose <c>layoutId</c> is authored rather than stamped.
    /// Their lookups were identifier-only before issue #28, so a database can still hold documents
    /// stored before their file declared a <c>layoutId</c>; a full sync run may let such a file adopt
    /// its unattributed document instead of leaving it behind and creating a second one (see
    /// <c>RavenDbService.AdoptionScope</c>). Stamped types have been strictly scoped since #16 and
    /// never adopt.
    /// </summary>
    public static bool AdoptsUnattributedDocuments(this DocumentType type) =>
        type.IsLayoutScoped() && !type.StampsLayoutId();
}
