using System.Text.Json.Nodes;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Unit tests for layout-scoped document identity (issues #16 and #28).
///
/// Two layouts that declare documents with the SAME identifier in the SAME collection (an
/// "identity-profile-owner" write-policy in #16, a "full-width-layout" section in #28) used to
/// clobber each other on a shared-DB sync: the lookup matched by identifier alone, so the second
/// layout's sync found the first's document and replaced it in place. #16 scoped only the types
/// that STAMP a layoutId, on the premise that sections and the other system collections carry
/// none — but they carry exactly what their file declares. Identity is now
/// (identifier, <see cref="SyncDocument.StoredLayoutId"/>) for every layout-scoped type, resolved
/// by <see cref="RavenDbService.ResolveLayoutScopedLookup"/>. End-to-end coverage through the sync
/// service lives in <see cref="LayoutScopedSyncTests"/>.
/// </summary>
public class LayoutScopedLookupTests
{
    // ── StampsLayoutId: which document types get the directory stamped as layoutId ──

    [Theory]
    [InlineData(DocumentType.Entity)]
    [InlineData(DocumentType.WritePolicy)]
    [InlineData(DocumentType.ReadPolicy)]
    [InlineData(DocumentType.EntityConfig)]
    [InlineData(DocumentType.EmailTemplate)]
    [InlineData(DocumentType.Theme)]
    public void StampsLayoutId_PerTenantTypes_ReturnTrue(DocumentType type)
        => Assert.True(type.StampsLayoutId());

    [Theory]
    [InlineData(DocumentType.Section)]
    [InlineData(DocumentType.Layout)]
    [InlineData(DocumentType.Menu)]
    [InlineData(DocumentType.Modal)]
    [InlineData(DocumentType.Manifest)]
    [InlineData(DocumentType.Tag)]
    [InlineData(DocumentType.Workflow)]
    [InlineData(DocumentType.Identity)]
    public void StampsLayoutId_AuthoredTypes_ReturnFalse(DocumentType type)
        => Assert.False(type.StampsLayoutId());

    // ── IsLayoutScoped / AdoptsUnattributedDocuments: the per-collection policy ────

    [Theory]
    [InlineData(DocumentType.Entity)]
    [InlineData(DocumentType.WritePolicy)]
    [InlineData(DocumentType.ReadPolicy)]
    [InlineData(DocumentType.EntityConfig)]
    [InlineData(DocumentType.EmailTemplate)]
    [InlineData(DocumentType.Theme)]
    [InlineData(DocumentType.Section)]
    [InlineData(DocumentType.Layout)]
    [InlineData(DocumentType.Menu)]
    [InlineData(DocumentType.Modal)]
    [InlineData(DocumentType.Tag)]
    [InlineData(DocumentType.Workflow)]
    public void IsLayoutScoped_TenantScopedTypes_ReturnTrue(DocumentType type)
        => Assert.True(type.IsLayoutScoped());

    [Theory]
    [InlineData(DocumentType.Manifest)] // the identifier IS the tenant key
    [InlineData(DocumentType.Identity)] // looked up by document id
    public void IsLayoutScoped_ManifestsAndIdentities_ReturnFalse(DocumentType type)
        => Assert.False(type.IsLayoutScoped());

    [Theory]
    [InlineData(DocumentType.Section)]
    [InlineData(DocumentType.Layout)]
    [InlineData(DocumentType.Menu)]
    [InlineData(DocumentType.Modal)]
    [InlineData(DocumentType.Tag)]
    [InlineData(DocumentType.Workflow)]
    public void AdoptsUnattributedDocuments_AuthoredLayoutScopedTypes_ReturnTrue(DocumentType type)
        => Assert.True(type.AdoptsUnattributedDocuments());

    [Theory]
    [InlineData(DocumentType.Entity)] // stamped types have been strictly scoped since #16
    [InlineData(DocumentType.WritePolicy)]
    [InlineData(DocumentType.ReadPolicy)]
    [InlineData(DocumentType.EntityConfig)]
    [InlineData(DocumentType.EmailTemplate)]
    [InlineData(DocumentType.Theme)]
    [InlineData(DocumentType.Manifest)]
    [InlineData(DocumentType.Identity)]
    public void AdoptsUnattributedDocuments_StampedAndUnscopedTypes_ReturnFalse(DocumentType type)
        => Assert.False(type.AdoptsUnattributedDocuments());

    // ── StoredLayoutId: the value the stored document actually carries ────────────

    [Fact]
    public void StoredLayoutId_Section_IsTheFilesDeclaredLayoutId_NotTheDirectory()
    {
        // Non-stamped documents are written as authored, so the declared value is what lands in
        // the database — even when it disagrees with the directory.
        SyncDocument doc = new()
        {
            DocumentType = DocumentType.Section,
            LayoutId = "cream-pi",
            Content = new JsonObject { ["layoutId"] = "dirt-life" },
        };

        Assert.False(doc.IsLayoutIdStamped);
        Assert.Equal("dirt-life", doc.StoredLayoutId);
    }

    [Fact]
    public void StoredLayoutId_SectionWithoutDeclaredLayoutId_IsEmpty_UnderALayoutDirectory()
    {
        // SyncDocument.LayoutId is populated from the directory for every file; it is NOT what a
        // non-stamped document is stored with. This is the premise #16 got wrong.
        SyncDocument doc = new()
        {
            DocumentType = DocumentType.Section,
            LayoutId = "dirt-life",
            Content = new JsonObject { ["identifier"] = "shared-footer" },
        };

        Assert.Equal(string.Empty, doc.StoredLayoutId);
    }

    [Fact]
    public void StoredLayoutId_StampedType_IsTheDirectory_WhateverTheFileSays()
    {
        SyncDocument doc = new()
        {
            DocumentType = DocumentType.WritePolicy,
            LayoutId = "dirt-life",
            Content = new JsonObject { ["layoutId"] = "something-else" },
        };

        Assert.True(doc.IsLayoutIdStamped);
        Assert.Equal("dirt-life", doc.StoredLayoutId);
    }

    [Fact]
    public void StoredLayoutId_PlatformTheme_IsNeverStamped()
    {
        SyncDocument doc = new()
        {
            DocumentType = DocumentType.Theme,
            LayoutId = null,
            Content = new JsonObject { ["identifier"] = "tokyo-night" },
        };

        Assert.False(doc.IsLayoutIdStamped);
        Assert.Equal(string.Empty, doc.StoredLayoutId);
    }

    [Theory]
    [InlineData("""{ "layoutId": 42 }""")]
    [InlineData("""{ "layoutId": null }""")]
    [InlineData("""{ "layoutId": { "nested": "dirt-life" } }""")]
    public void StoredLayoutId_NonStringLayoutId_IsEmpty(string json)
    {
        SyncDocument doc = new()
        {
            DocumentType = DocumentType.Section,
            LayoutId = "dirt-life",
            Content = JsonNode.Parse(json)!.AsObject(),
        };

        Assert.Equal(string.Empty, doc.StoredLayoutId);
    }

    // ── ResolveLayoutScopedLookup: which stored document a file resolves to ────────

    private static RavenDbService.LookupCandidate Candidate(string documentId, string layoutId)
        => new(documentId, layoutId, Document: null);

    private static RavenDbService.AdoptionScope Adoption(params string[] claimedDocumentIds)
        => new(new HashSet<string>(claimedDocumentIds, StringComparer.OrdinalIgnoreCase));

    private static RavenDbService.DuplicateEntityLookupResult Resolve(
        string layoutId,
        IReadOnlyList<RavenDbService.LookupCandidate> candidates,
        RavenDbService.AdoptionScope? adoption = null)
        => RavenDbService.ResolveLayoutScopedLookup(
            "sections", "full-width-layout", layoutId, candidates, adoption, NullLogger.Instance);

    [Fact]
    public void Resolve_TwoLayoutsCopies_EachLayoutResolvesToItsOwn()
    {
        RavenDbService.LookupCandidate[] candidates =
            [Candidate("docs/dl", "dirt-life"), Candidate("docs/cp", "cream-pi")];

        Assert.Equal("docs/dl", Resolve("dirt-life", candidates).DocumentId);
        Assert.Equal("docs/cp", Resolve("cream-pi", candidates).DocumentId);
    }

    [Fact]
    public void Resolve_OnlyAnotherLayoutsDocument_MatchesNothing_EvenWithAdoption()
    {
        // #28's database after the outage: one document, holding cream-pi's layoutId. dirt-life's
        // file must NOT take it back (that would re-open the flip-flop); it gets its own document.
        RavenDbService.LookupCandidate[] candidates = [Candidate("docs/cp", "cream-pi")];

        RavenDbService.DuplicateEntityLookupResult result = Resolve("dirt-life", candidates, Adoption());

        Assert.Null(result.DocumentId);
        Assert.False(result.IsDuplicate);
    }

    [Fact]
    public void Resolve_FileWithoutLayoutId_StillMatchesAnUnattributedDocumentByIdentifier()
    {
        // #16's intent for genuinely shared documents is preserved.
        RavenDbService.DuplicateEntityLookupResult result = Resolve(string.Empty, [Candidate("docs/shared", string.Empty)]);

        Assert.Equal("docs/shared", result.DocumentId);
    }

    [Fact]
    public void Resolve_FileWithoutLayoutId_NeverCapturesAnAttributedDocument()
        => Assert.Null(Resolve(string.Empty, [Candidate("docs/dl", "dirt-life")]).DocumentId);

    [Fact]
    public void Resolve_LayoutIdComparison_IsCaseInsensitive_LikeRavenDbQueries()
        => Assert.Equal("docs/dl", Resolve("dirt-life", [Candidate("docs/dl", "Dirt-Life")]).DocumentId);

    [Fact]
    public void Resolve_SeveralExactMatches_FirstWinsAndIsFlaggedDuplicate()
    {
        RavenDbService.DuplicateEntityLookupResult result = Resolve(
            "dirt-life",
            [Candidate("docs/dl-1", "dirt-life"), Candidate("docs/cp", "cream-pi"), Candidate("docs/dl-2", "dirt-life")]);

        Assert.Equal("docs/dl-1", result.DocumentId);
        Assert.True(result.IsDuplicate);
    }

    [Fact]
    public void Resolve_UnattributedDocument_IsAdoptedOnlyWhenGranted()
    {
        RavenDbService.LookupCandidate[] candidates = [Candidate("docs/legacy", string.Empty)];

        Assert.Equal("docs/legacy", Resolve("dirt-life", candidates, Adoption()).DocumentId);
        Assert.Null(Resolve("dirt-life", candidates, adoption: null).DocumentId);
    }

    [Fact]
    public void Resolve_Adoption_SkipsDocumentsOtherFilesAlreadyClaimed()
        => Assert.Null(Resolve("dirt-life", [Candidate("docs/legacy", string.Empty)], Adoption("docs/legacy")).DocumentId);

    [Fact]
    public void Resolve_Adoption_NeverOverridesAnExactMatch()
    {
        RavenDbService.DuplicateEntityLookupResult result = Resolve(
            "dirt-life",
            [Candidate("docs/legacy", string.Empty), Candidate("docs/dl", "dirt-life")],
            Adoption());

        Assert.Equal("docs/dl", result.DocumentId);
        Assert.False(result.IsDuplicate);
    }

    [Fact]
    public void Resolve_SeveralUnattributedDocuments_AdoptsTheFirstAndFlagsDuplicate()
    {
        RavenDbService.DuplicateEntityLookupResult result = Resolve(
            "dirt-life",
            [Candidate("docs/legacy-1", string.Empty), Candidate("docs/legacy-2", string.Empty)],
            Adoption());

        Assert.Equal("docs/legacy-1", result.DocumentId);
        Assert.True(result.IsDuplicate);
    }

    // ── Reading a fetched document's layoutId ────────────────────────────────────

    [Fact]
    public void FetchedDocument_LayoutIdIsReadFromTheLosslessBlittable()
    {
        // QueryByIdentifierAsync reads each candidate's layoutId from the raw blittable, through
        // ToJsonObject — the lossless read of issue #24. The old System.Text.Json copy erased every
        // value to [], which would have made every stored document look unattributed.
        using JsonOperationContext context = JsonOperationContext.ShortTermSingleUse();
        BlittableJsonReaderObject attributed =
            context.ReadObject(new DynamicJsonValue { ["layoutId"] = "dirt-life", ["identifier"] = "hero" }, "doc");
        BlittableJsonReaderObject unattributed =
            context.ReadObject(new DynamicJsonValue { ["identifier"] = "footer" }, "doc");

        Assert.Equal("dirt-life", SyncDocument.ReadLayoutIdField(RavenDbService.ToJsonObject(attributed)));
        Assert.Equal(string.Empty, SyncDocument.ReadLayoutIdField(RavenDbService.ToJsonObject(unattributed)));
    }
}
