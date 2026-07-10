using LayoutSync.Services;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Unit tests for the (layoutId, identifier)-composite orphan detection introduced in issue #17.
///
/// Before #16, the identifier-scoped clobber in <c>FindDocumentAsync</c> stopped two layouts from
/// coexisting with the same identifier in one collection. #16 lets them coexist — which exposed a
/// latent collapse in the orphan path: <c>GetAllOrphanCandidatesAsync</c> used to key by identifier
/// (now keyed by the unique docId), and orphan MATCHING used identifier alone, so a cross-layout
/// twin could mask a genuine orphan. Orphan identity is now (effective layoutId, identifier),
/// encoded by <see cref="DocumentSyncService.OrphanTrackingKey"/> and matched by
/// <see cref="DocumentSyncService.ComputeRawOrphans"/>.
/// </summary>
public class OrphanCompositeKeyTests
{
    // ── OrphanTrackingKey: composite, collision-free ─────────────────────────

    [Fact]
    public void OrphanTrackingKey_SameLayoutAndIdentifier_ProducesSameKey()
        => Assert.Equal(
            DocumentSyncService.OrphanTrackingKey("dirt-life", "identity-profile-owner"),
            DocumentSyncService.OrphanTrackingKey("dirt-life", "identity-profile-owner"));

    [Fact]
    public void OrphanTrackingKey_DifferentLayoutSameIdentifier_ProducesDifferentKeys()
        // The whole point of #17: the same identifier under two layouts is two distinct keys.
        => Assert.NotEqual(
            DocumentSyncService.OrphanTrackingKey("dirt-life", "identity-profile-owner"),
            DocumentSyncService.OrphanTrackingKey("__conformance__", "identity-profile-owner"));

    [Fact]
    public void OrphanTrackingKey_DelimiterPreventsBoundaryCollision()
        // Without a delimiter, ("a","bc") and ("ab","c") would both be "abc". The U+001F
        // separator keeps the split point unambiguous.
        => Assert.NotEqual(
            DocumentSyncService.OrphanTrackingKey("a", "bc"),
            DocumentSyncService.OrphanTrackingKey("ab", "c"));

    // ── ComputeRawOrphans: composite matching ────────────────────────────────

    private static RavenDbService.OrphanCandidate Candidate(string docId, string? layoutId, string identifier)
        => new(docId, layoutId, identifier);

    [Fact]
    public void ComputeRawOrphans_CrossLayoutTwin_DoesNotMaskTheOtherLayoutsOrphan()
    {
        // Two write-policies share identifier "identity-profile-owner" across layouts (coexisting
        // post-#16). __conformance__ removed its local file this run; dirt-life still ships its.
        // Only dirt-life's (layoutId, identifier) is synced → __conformance__'s doc is the orphan.
        // The pre-#17 identifier-only match would have seen "identity-profile-owner" as synced and
        // flagged NEITHER — masking the real orphan.
        Dictionary<string, RavenDbService.OrphanCandidate> existing = new()
        {
            ["docs/dl"] = Candidate("docs/dl", "dirt-life", "identity-profile-owner"),
            ["docs/cf"] = Candidate("docs/cf", "__conformance__", "identity-profile-owner"),
        };
        HashSet<string> synced =
        [
            DocumentSyncService.OrphanTrackingKey("dirt-life", "identity-profile-owner"),
        ];

        Dictionary<string, RavenDbService.OrphanCandidate> orphans =
            DocumentSyncService.ComputeRawOrphans(existing, synced);

        RavenDbService.OrphanCandidate only = Assert.Single(orphans.Values);
        Assert.Equal("docs/cf", only.DocumentId);
        Assert.Equal("__conformance__", only.LayoutId);
    }

    [Fact]
    public void ComputeRawOrphans_BothLayoutsStillSynced_YieldsNoOrphans()
    {
        Dictionary<string, RavenDbService.OrphanCandidate> existing = new()
        {
            ["docs/dl"] = Candidate("docs/dl", "dirt-life", "identity-profile-owner"),
            ["docs/cf"] = Candidate("docs/cf", "__conformance__", "identity-profile-owner"),
        };
        HashSet<string> synced =
        [
            DocumentSyncService.OrphanTrackingKey("dirt-life", "identity-profile-owner"),
            DocumentSyncService.OrphanTrackingKey("__conformance__", "identity-profile-owner"),
        ];

        Assert.Empty(DocumentSyncService.ComputeRawOrphans(existing, synced));
    }

    [Fact]
    public void ComputeRawOrphans_EmptyOrNullLayoutId_MatchesAgnosticSyncedKey()
    {
        // Layout-agnostic collections (sections, ...) store layoutId as "" (RavenDB projects a
        // missing field to "", issue #13). A synced section is tracked under ("", identifier); its
        // DB doc must match and NOT be flagged. A candidate with C# null must normalize to "" so
        // it matches the same synced key (guards against the false-orphan / data-loss class).
        Dictionary<string, RavenDbService.OrphanCandidate> existing = new()
        {
            ["docs/s1"] = Candidate("docs/s1", "", "home-hero"),
            ["docs/s2"] = Candidate("docs/s2", null, "footer"),
        };
        HashSet<string> synced =
        [
            DocumentSyncService.OrphanTrackingKey("", "home-hero"),
            DocumentSyncService.OrphanTrackingKey("", "footer"),
        ];

        Assert.Empty(DocumentSyncService.ComputeRawOrphans(existing, synced));
    }

    [Fact]
    public void ComputeRawOrphans_UnsyncedDocument_IsOrphan()
    {
        Dictionary<string, RavenDbService.OrphanCandidate> existing = new()
        {
            ["docs/gone"] = Candidate("docs/gone", "dirt-life", "removed-policy"),
        };
        HashSet<string> synced = [];

        RavenDbService.OrphanCandidate only =
            Assert.Single(DocumentSyncService.ComputeRawOrphans(existing, synced).Values);
        Assert.Equal("removed-policy", only.Identifier);
    }
}
