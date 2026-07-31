using LayoutSync.Models;
using LayoutSync.Services;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Tests for the <see cref="CollectionFolders"/> registry — the single source of truth for
/// layout collection folders introduced by issue #9. Discovery, classification, orphan
/// tracking, and --exclude-collection validation all derive from it, so its shape (content,
/// order, folder→collection mapping) is pinned here.
/// </summary>
public class CollectionFoldersTests
{
    /// <summary>
    /// The historical DiscoverFiles enumeration order from before the registry refactor.
    /// Neither enum order nor alphabetical — it is load-bearing only in the sense that
    /// changing it silently reorders sync logs and per-file processing, so a change must
    /// be deliberate (update this test when it is).
    /// </summary>
    private static readonly string[] HistoricalOrder =
    [
        "layouts", "menus", "manifests", "sections", "modals", "entities",
        "identities", "tags", "workflows", "write-policies", "read-policies",
        "entity-configs", "themes", "email-templates",
    ];

    [Fact]
    public void Ordered_PreservesHistoricalDiscoveryOrder()
    {
        Assert.Equal(HistoricalOrder, CollectionFolders.Ordered.Select(pair => pair.Folder));
    }

    [Fact]
    public void FolderNames_MatchOrderedEntries()
    {
        Assert.Equal(HistoricalOrder, CollectionFolders.FolderNames);
    }

    [Fact]
    public void ByFolder_IsCaseInsensitive()
    {
        // Mirrors the historical DetermineDocumentType lowercasing.
        Assert.Equal(DocumentType.Entity, CollectionFolders.ByFolder["ENTITIES"]);
        Assert.Equal(DocumentType.WritePolicy, CollectionFolders.ByFolder["Write-Policies"]);
    }

    [Fact]
    public void ByFolder_CoversEveryDocumentType()
    {
        // Every DocumentType must be reachable from exactly one folder — a new enum member
        // without a registry entry would be undiscoverable (and vice versa).
        IEnumerable<DocumentType> mapped = CollectionFolders.Ordered.Select(pair => pair.Type);
        IEnumerable<DocumentType> all = Enum.GetValues<DocumentType>();
        Assert.Equal(all.OrderBy(t => t), mapped.OrderBy(t => t));
    }

    [Theory]
    [InlineData("alpha/entities/user.json", DocumentType.Entity)]
    [InlineData("alpha/write-policies/policy.json", DocumentType.WritePolicy)]
    [InlineData("alpha/themes/dark.json", DocumentType.Theme)]
    [InlineData("alpha/ENTITIES/user.json", DocumentType.Entity)]
    public void DetermineDocumentType_MapsFolderToType(string relativePath, DocumentType expected)
    {
        Assert.Equal(expected, LocalFileService.DetermineDocumentType(relativePath));
    }

    [Theory]
    [InlineData("alpha/unknown-folder/file.json")]
    [InlineData("orphan.json")]
    public void DetermineDocumentType_UnknownFolderDefaultsToEntity(string relativePath)
    {
        // Historical behavior preserved through the registry refactor.
        Assert.Equal(DocumentType.Entity, LocalFileService.DetermineDocumentType(relativePath));
    }

    [Theory]
    [InlineData("write-policies", "WritePolicies")]
    [InlineData("read-policies", "ReadPolicies")]
    [InlineData("themes", "theme-definitions")]
    public void FolderToCollection_NonIdentityPairsRoundTrip(string folder, string expectedCollection)
    {
        // The CLI vocabulary is FOLDER names; orphan tracking is keyed by RavenDB
        // COLLECTION names. These pairs are the only non-identity mappings — pinned so
        // an exclusion by folder name provably lands on the right orphan-tracking key.
        DocumentType type = CollectionFolders.ByFolder[folder];
        Assert.Equal(expectedCollection, type.GetCollection());
    }
}
