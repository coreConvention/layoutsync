using System.Text.Json.Nodes;
using LayoutSync.Models;
using LayoutSync.Services;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Unit tests for <see cref="DocumentClaims"/>, the per-run ownership record behind issue #28's
/// in-run collision detection and the adoption guard. End-to-end behavior (refused files, the
/// <c>--strict</c> exit code) is covered in <see cref="LayoutScopedSyncTests"/>.
/// </summary>
public class DocumentClaimsTests
{
    private static SyncDocument Doc(
        DocumentType type,
        string directory,
        string identifier,
        string? declaredLayoutId,
        string? id = null)
    {
        JsonObject content = new() { ["identifier"] = identifier };
        if (declaredLayoutId != null)
            content["layoutId"] = declaredLayoutId;

        return new SyncDocument
        {
            DocumentType = type,
            LayoutId = directory,
            Identifier = identifier,
            Id = id,
            Content = content,
            RelativePath = $"{directory}/{type}/{identifier}.json",
        };
    }

    // ── Declared collisions ────────────────────────────────────────────────────

    [Fact]
    public void SameIdentifierAndLayoutId_TwoFiles_CollideWithEachOther()
    {
        SyncDocument a = Doc(DocumentType.Section, "dirt-life", "hero", "dirt-life");
        SyncDocument b = Doc(DocumentType.Section, "dirt-life", "hero", "dirt-life");
        DocumentClaims claims = new([a, b]);

        Assert.Equal(1, claims.CollisionCount);
        Assert.Same(b, Assert.Single(claims.CoDeclarants(a)));
        Assert.Same(a, Assert.Single(claims.CoDeclarants(b)));
    }

    [Fact]
    public void RefusalReason_DifferentContent_RefusesEveryFile()
    {
        SyncDocument a = Doc(DocumentType.Section, "dirt-life", "hero", "dirt-life");
        SyncDocument b = Doc(DocumentType.Section, "dirt-life", "hero", "dirt-life");
        b.Content!["data"] = "edited";
        DocumentClaims claims = new([a, b]);

        Assert.NotNull(claims.RefusalReason(a));
        Assert.NotNull(claims.RefusalReason(b));
        Assert.True(claims.IsRefused(a));
    }

    [Fact]
    public void RefusalReason_IdenticalCopies_WritesTheFirstOnly_AndStillCounts()
    {
        // Byte-identical copies carry no conflict: the first is written, the rest refused, and it
        // is still a collision for --strict (the copies should go).
        SyncDocument first = Doc(DocumentType.Section, "dirt-life", "hero", "dirt-life");
        SyncDocument copy = Doc(DocumentType.Section, "dirt-life", "hero", "dirt-life");
        DocumentClaims claims = new([first, copy]);

        Assert.Null(claims.RefusalReason(first));
        Assert.StartsWith("Collision: identical copy", claims.RefusalReason(copy));
        Assert.Equal(1, claims.CollisionCount);
    }

    [Fact]
    public void SameSectionIdentifier_DifferentLayoutIds_DoNotCollide()
    {
        // #28: two layouts' copies of one identifier are two documents, not a collision.
        SyncDocument dirtLife = Doc(DocumentType.Section, "dirt-life", "full-width-layout", "dirt-life");
        SyncDocument creamPi = Doc(DocumentType.Section, "cream-pi", "full-width-layout", "cream-pi");
        DocumentClaims claims = new([dirtLife, creamPi]);

        Assert.Equal(0, claims.CollisionCount);
        Assert.Empty(claims.CoDeclarants(dirtLife));
    }

    [Fact]
    public void SameSectionIdentifier_NoLayoutIdInEitherLayout_Collides()
    {
        // Two layouts that both ship an unattributed "full-width-layout" still share ONE document —
        // the #28 clobber in its original form, now refused instead of flip-flopping.
        DocumentClaims claims = new(
        [
            Doc(DocumentType.Section, "dirt-life", "full-width-layout", declaredLayoutId: null),
            Doc(DocumentType.Section, "cream-pi", "full-width-layout", declaredLayoutId: null),
        ]);

        Assert.Equal(1, claims.CollisionCount);
    }

    [Fact]
    public void IdentifiersDifferingOnlyInCase_Collide_LikeRavenDbMatching()
    {
        DocumentClaims claims = new(
        [
            Doc(DocumentType.Section, "dirt-life", "Hero", "dirt-life"),
            Doc(DocumentType.Section, "dirt-life", "hero", "dirt-life"),
        ]);

        Assert.Equal(1, claims.CollisionCount);
    }

    [Fact]
    public void ManifestIdentifier_ReusedAcrossLayouts_CollidesWhateverTheLayoutIds()
    {
        // A manifest's identifier is the tenant key, so manifests are identifier-keyed: a second
        // layout declaring the same one is refused, never stored as a second tenant manifest.
        DocumentClaims claims = new(
        [
            Doc(DocumentType.Manifest, "dirt-life", "dirt-life", "dirt-life"),
            Doc(DocumentType.Manifest, "cream-pi", "dirt-life", "cream-pi"),
        ]);

        Assert.Equal(1, claims.CollisionCount);
    }

    [Fact]
    public void Identities_SameId_Collide_AndMissingIdsNeverDo()
    {
        DocumentClaims sameId = new(
        [
            Doc(DocumentType.Identity, "dirt-life", "a", "dirt-life", id: "abc123"),
            Doc(DocumentType.Identity, "cream-pi", "b", "cream-pi", id: "ABC123"),
        ]);
        DocumentClaims noIds = new(
        [
            Doc(DocumentType.Identity, "dirt-life", "a", "dirt-life"),
            Doc(DocumentType.Identity, "dirt-life", "b", "dirt-life"),
        ]);

        Assert.Equal(1, sameId.CollisionCount);
        Assert.Equal(0, noIds.CollisionCount);
    }

    // ── Resolved collisions (TryClaim) ─────────────────────────────────────────

    [Fact]
    public void TryClaim_SecondFileResolvingToAClaimedDocument_IsRefusedAndCounted()
    {
        SyncDocument first = Doc(DocumentType.Section, "dirt-life", "hero", "dirt-life");
        SyncDocument second = Doc(DocumentType.Section, "cream-pi", "banner", "cream-pi");
        DocumentClaims claims = new([first, second]);

        Assert.True(claims.TryClaim("docs/1", first, out SyncDocument? noOwner));
        Assert.Null(noOwner);
        Assert.False(claims.TryClaim("docs/1", second, out SyncDocument? owner));
        Assert.Same(first, owner);
        Assert.Equal(1, claims.CollisionCount);
        Assert.True(claims.IsRefused(second));
        Assert.False(claims.IsRefused(first));
    }

    // ── Adoption guard ─────────────────────────────────────────────────────────

    [Fact]
    public void AdoptionScopeFor_SectionDeclaringALayoutId_IsGranted()
    {
        SyncDocument section = Doc(DocumentType.Section, "dirt-life", "hero", "dirt-life");

        Assert.NotNull(new DocumentClaims([section]).AdoptionScopeFor(section));
    }

    [Fact]
    public void AdoptionScopeFor_StampedTypesAndFilesWithoutLayoutId_IsDenied()
    {
        SyncDocument policy = Doc(DocumentType.WritePolicy, "dirt-life", "owner", declaredLayoutId: null);
        SyncDocument unattributed = Doc(DocumentType.Section, "dirt-life", "footer", declaredLayoutId: null);
        DocumentClaims claims = new([policy, unattributed]);

        Assert.Null(claims.AdoptionScopeFor(policy));
        Assert.Null(claims.AdoptionScopeFor(unattributed));
    }

    [Fact]
    public void AdoptionScopeFor_IsDenied_WhenAnotherFileDeclaresTheIdentifierWithoutLayoutId()
    {
        // That file is the unattributed document's rightful owner; a tenant file discovered first
        // must not steal it.
        SyncDocument tenant = Doc(DocumentType.Section, "dirt-life", "footer", "dirt-life");
        SyncDocument shared = Doc(DocumentType.Section, "commons", "footer", declaredLayoutId: null);

        Assert.Null(new DocumentClaims([tenant, shared]).AdoptionScopeFor(tenant));
    }

    [Fact]
    public void AdoptionScopeFor_IsDenied_WhenTheUnattributedOwnerIsOutsideAScopedRun()
    {
        // Under --layout the owner is not synced, but it still owns the unattributed document.
        SyncDocument tenant = Doc(DocumentType.Section, "dirt-life", "footer", "dirt-life");
        SyncDocument sharedElsewhere = Doc(DocumentType.Section, "commons", "footer", declaredLayoutId: null);
        DocumentClaims claims = new([tenant], outsideRun: [sharedElsewhere]);

        Assert.Null(claims.AdoptionScopeFor(tenant));
        Assert.Equal(0, claims.CollisionCount); // outside files never collide with the run
    }

    [Fact]
    public void AdoptionScope_SeesDocumentsClaimedAfterItWasGranted()
    {
        SyncDocument first = Doc(DocumentType.Section, "dirt-life", "hero", "dirt-life");
        SyncDocument second = Doc(DocumentType.Section, "cream-pi", "hero", "cream-pi");
        DocumentClaims claims = new([first, second]);
        RavenDbService.AdoptionScope scope = claims.AdoptionScopeFor(second)!;

        claims.TryClaim("docs/legacy", first, out _);

        Assert.Contains("docs/legacy", scope.ClaimedDocumentIds);
    }
}
