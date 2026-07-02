using LayoutSync.Services;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Tests for the two pure orphan-detection helpers behind --exclude-collection /
/// --exclude-layout (issue #9): <see cref="DocumentSyncService.BuildOrphanTracking"/>
/// (which collections get orphan-scanned at all) and
/// <see cref="DocumentSyncService.FilterOrphansForExcludedLayouts"/> (which candidates
/// survive when layout exclusions are active). Companion to
/// <see cref="OrphanScopeFilterTests"/>, which covers the --layout scope filter.
/// </summary>
public class OrphanExclusionTests
{
    /// <summary>
    /// The exact orphan-tracking keys (RavenDB collection names — note the non-identity
    /// folder mappings: WritePolicies, ReadPolicies, theme-definitions).
    /// entities/identities are deliberately absent: user data is never orphan-scanned
    /// (issue #282).
    /// </summary>
    private static readonly string[] HistoricalTrackingKeys =
    [
        "layouts", "menus", "manifests", "sections", "modals", "tags",
        "workflows", "WritePolicies", "ReadPolicies", "entity-configs", "theme-definitions",
    ];

    // ── BuildOrphanTracking ──────────────────────────────────────────────────

    [Fact]
    public void BuildOrphanTracking_NoExclusions_ReproducesHistoricalKeys()
    {
        Dictionary<string, HashSet<string>> tracking = DocumentSyncService.BuildOrphanTracking(null);

        Assert.Equal(
            HistoricalTrackingKeys.OrderBy(k => k),
            tracking.Keys.OrderBy(k => k));
        Assert.All(tracking.Values, bucket => Assert.Empty(bucket));
    }

    [Fact]
    public void BuildOrphanTracking_ExcludedCollection_HasNoBucket()
    {
        // No bucket means DetectOrphansAsync never queries the collection — orphan
        // detection is skipped entirely, which is the issue #9 contract.
        Dictionary<string, HashSet<string>> tracking =
            DocumentSyncService.BuildOrphanTracking(["sections"]);

        Assert.DoesNotContain("sections", tracking.Keys);
        Assert.Equal(HistoricalTrackingKeys.Length - 1, tracking.Count);
    }

    [Theory]
    [InlineData("write-policies", "WritePolicies")]
    [InlineData("read-policies", "ReadPolicies")]
    [InlineData("themes", "theme-definitions")]
    public void BuildOrphanTracking_ExclusionByFolderName_RemovesCollectionKey(
        string excludedFolder, string absentCollection)
    {
        // The CLI vocabulary is folder names; the tracking keys are collection names.
        // These two mappings are non-identity, so they're pinned explicitly.
        Dictionary<string, HashSet<string>> tracking =
            DocumentSyncService.BuildOrphanTracking([excludedFolder]);

        Assert.DoesNotContain(absentCollection, tracking.Keys);
    }

    [Theory]
    [InlineData("entities")]
    [InlineData("identities")]
    public void BuildOrphanTracking_ExcludingUserDataCollections_IsANoOp(string folder)
    {
        // entities/identities were never tracked (IsStaticCollection excludes them), so
        // excluding them changes nothing here — and must not throw.
        Dictionary<string, HashSet<string>> tracking =
            DocumentSyncService.BuildOrphanTracking([folder]);

        Assert.Equal(HistoricalTrackingKeys.Length, tracking.Count);
    }

    [Fact]
    public void BuildOrphanTracking_ExclusionMatchIsCaseInsensitive()
    {
        Dictionary<string, HashSet<string>> tracking =
            DocumentSyncService.BuildOrphanTracking(["SECTIONS"]);

        Assert.DoesNotContain("sections", tracking.Keys);
    }

    // ── FilterOrphansForExcludedLayouts ──────────────────────────────────────

    [Fact]
    public void FilterOrphansForExcludedLayouts_NoExclusions_AllCandidatesPass()
    {
        Dictionary<string, RavenDbService.OrphanCandidate> candidates = new()
        {
            ["stamped"] = new("docs/1", "alpha"),
            ["unstamped"] = new("docs/2", null),
        };

        Dictionary<string, RavenDbService.OrphanCandidate> result =
            DocumentSyncService.FilterOrphansForExcludedLayouts(candidates, []);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FilterOrphansForExcludedLayouts_DropsExcludedLayoutCandidates()
    {
        Dictionary<string, RavenDbService.OrphanCandidate> candidates = new()
        {
            ["fixture-doc"] = new("docs/1", "__conformance__"),
            ["real-doc"] = new("docs/2", "alpha"),
        };

        Dictionary<string, RavenDbService.OrphanCandidate> result =
            DocumentSyncService.FilterOrphansForExcludedLayouts(candidates, ["__conformance__"]);

        string only = Assert.Single(result.Keys);
        Assert.Equal("real-doc", only);
    }

    [Fact]
    public void FilterOrphansForExcludedLayouts_DropsUnattributableCandidates_WhenExclusionActive()
    {
        // The safety hinge: most static collections (sections, menus, ...) are never
        // stamped with layoutId, so their orphan candidates come back unattributed. An
        // unattributed candidate cannot be proven to lie OUTSIDE the excluded layout —
        // under `--clean --exclude-layout X` deleting it could destroy X's own documents.
        // Both null AND empty must drop: RavenDB's dynamic projection turns a MISSING
        // layoutId field into "", so live unstamped docs arrive as empty strings, never
        // null (verified against local RavenDB data during issue #9).
        Dictionary<string, RavenDbService.OrphanCandidate> candidates = new()
        {
            ["unstamped-null"] = new("docs/1", null),
            ["unstamped-empty"] = new("docs/2", ""),
            ["other-layout-doc"] = new("docs/3", "alpha"),
        };

        Dictionary<string, RavenDbService.OrphanCandidate> result =
            DocumentSyncService.FilterOrphansForExcludedLayouts(candidates, ["__conformance__"]);

        string only = Assert.Single(result.Keys);
        Assert.Equal("other-layout-doc", only);
    }

    [Fact]
    public void FilterOrphansForExcludedLayouts_MatchIsCaseSensitive()
    {
        // Ordinal, consistent with FilterOrphansForScope's LayoutId comparison.
        Dictionary<string, RavenDbService.OrphanCandidate> candidates = new()
        {
            ["doc"] = new("docs/1", "alpha"),
        };

        Dictionary<string, RavenDbService.OrphanCandidate> result =
            DocumentSyncService.FilterOrphansForExcludedLayouts(candidates, ["Alpha"]);

        Assert.Single(result.Keys);
    }
}
