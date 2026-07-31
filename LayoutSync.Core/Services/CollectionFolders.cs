using LayoutSync.Models;

namespace LayoutSync.Services;

/// <summary>
/// Single source of truth for the collection folders that make up a layout directory
/// (<c>layouts/{layoutId}/{folder}/*.json</c>). Drives file discovery
/// (<see cref="LocalFileService.DiscoverFiles"/>), folder→type classification
/// (<see cref="LocalFileService.DetermineDocumentType"/>), orphan-tracking initialization
/// (<see cref="DocumentSyncService.BuildOrphanTracking"/>), and <c>--exclude-collection</c>
/// validation (<see cref="ExclusionValidator"/>). Adding a collection is ONE entry here —
/// discovery, classification, orphan detection, and flag validation all pick it up. See issue #9.
/// </summary>
public static class CollectionFolders
{
    /// <summary>
    /// Ordered folder→type pairs. The order is load-bearing: it preserves the historical
    /// <c>DiscoverFiles</c> yield order from before the registry refactor, so sync ordering
    /// and log output stay stable (pinned by a discovery-order test).
    /// </summary>
    public static readonly IReadOnlyList<(string Folder, DocumentType Type)> Ordered =
    [
        ("layouts", DocumentType.Layout),
        ("menus", DocumentType.Menu),
        ("manifests", DocumentType.Manifest),
        ("sections", DocumentType.Section),
        ("modals", DocumentType.Modal),
        ("entities", DocumentType.Entity),
        ("identities", DocumentType.Identity),
        ("tags", DocumentType.Tag),
        ("workflows", DocumentType.Workflow),
        ("write-policies", DocumentType.WritePolicy),
        ("read-policies", DocumentType.ReadPolicy),
        ("entity-configs", DocumentType.EntityConfig),
        ("themes", DocumentType.Theme),
        ("email-templates", DocumentType.EmailTemplate),
    ];

    /// <summary>
    /// Case-insensitive folder-name lookup derived from <see cref="Ordered"/>. Case
    /// insensitivity mirrors the historical <c>DetermineDocumentType</c> lowercasing.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, DocumentType> ByFolder =
        Ordered.ToDictionary(pair => pair.Folder, pair => pair.Type, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Folder names in canonical order — the valid-value set (and "did you mean" suggestion
    /// pool) for <c>--exclude-collection</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> FolderNames =
        [.. Ordered.Select(pair => pair.Folder)];
}
