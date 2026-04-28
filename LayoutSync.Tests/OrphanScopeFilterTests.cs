using LayoutSync.Services;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Unit tests for <see cref="DocumentSyncService.FilterOrphansForScope"/> — the pure helper that
/// decides which orphan candidates remain eligible for deletion when <c>--layout X</c> scopes
/// the sync.
///
/// The contract is intentionally narrow:
/// <list type="bullet">
///   <item><description>No scope → every candidate passes through (legacy <c>--clean</c> behavior).</description></item>
///   <item><description>Scope set → keep candidates whose <c>LayoutId</c> equals the scope, drop the rest.</description></item>
///   <item><description>Scope set + candidate has null <c>LayoutId</c> → conservatively dropped (globally-shared docs cannot be safely attributed to a single tenant).</description></item>
/// </list>
///
/// Together these rules make <c>--clean</c> + <c>--layout X</c> safe across tenants without
/// reintroducing the data-loss class from issue #235. See issue #427.
/// </summary>
public class OrphanScopeFilterTests
{
    // ── No scope: preserves legacy unscoped --clean behavior ─────────────────

    [Fact]
    public void FilterOrphansForScope_NullScope_ReturnsAllCandidatesIncludingNullLayoutIds()
    {
        Dictionary<string, RavenDbService.OrphanCandidate> candidates = new()
        {
            ["dirt-life-policy"] = new("doc-1", "dirt-life"),
            ["neighborly-policy"] = new("doc-2", "neighborly"),
            ["legacy-section"] = new("doc-3", LayoutId: null),
        };

        Dictionary<string, RavenDbService.OrphanCandidate> result =
            DocumentSyncService.FilterOrphansForScope(candidates, scopedLayoutId: null);

        // Legacy: unscoped clean must still consider every candidate (incl. null-layoutId docs).
        Assert.Equal(3, result.Count);
        Assert.Contains("dirt-life-policy", result.Keys);
        Assert.Contains("neighborly-policy", result.Keys);
        Assert.Contains("legacy-section", result.Keys);
    }

    [Fact]
    public void FilterOrphansForScope_EmptyScopeString_BehavesLikeNullScope()
    {
        // Defensive: an empty string for --layout should be treated as "no scope".
        Dictionary<string, RavenDbService.OrphanCandidate> candidates = new()
        {
            ["a"] = new("doc-1", "dirt-life"),
            ["b"] = new("doc-2", LayoutId: null),
        };

        Dictionary<string, RavenDbService.OrphanCandidate> result =
            DocumentSyncService.FilterOrphansForScope(candidates, scopedLayoutId: string.Empty);

        Assert.Equal(2, result.Count);
    }

    // ── Scoped match: only candidates with the matching layoutId survive ─────

    [Fact]
    public void FilterOrphansForScope_ScopedMatch_KeepsOnlyMatchingLayout()
    {
        // Two-tenant scenario from the issue: dirt-life and neighborly orphan candidates
        // present, scoped clean to dirt-life must NOT touch neighborly's documents.
        Dictionary<string, RavenDbService.OrphanCandidate> candidates = new()
        {
            ["dl-policy-1"] = new("docs/dl-1", "dirt-life"),
            ["dl-policy-2"] = new("docs/dl-2", "dirt-life"),
            ["nb-policy-1"] = new("docs/nb-1", "neighborly"),
            ["nb-policy-2"] = new("docs/nb-2", "neighborly"),
        };

        Dictionary<string, RavenDbService.OrphanCandidate> result =
            DocumentSyncService.FilterOrphansForScope(candidates, scopedLayoutId: "dirt-life");

        Assert.Equal(2, result.Count);
        Assert.Contains("dl-policy-1", result.Keys);
        Assert.Contains("dl-policy-2", result.Keys);
        Assert.DoesNotContain("nb-policy-1", result.Keys);
        Assert.DoesNotContain("nb-policy-2", result.Keys);
    }

    [Fact]
    public void FilterOrphansForScope_ScopedMismatchOnly_ReturnsEmpty()
    {
        // All candidates belong to a different tenant. Scoped clean must produce zero deletions.
        Dictionary<string, RavenDbService.OrphanCandidate> candidates = new()
        {
            ["nb-1"] = new("docs/nb-1", "neighborly"),
            ["nb-2"] = new("docs/nb-2", "neighborly"),
        };

        Dictionary<string, RavenDbService.OrphanCandidate> result =
            DocumentSyncService.FilterOrphansForScope(candidates, scopedLayoutId: "dirt-life");

        Assert.Empty(result);
    }

    // ── Conservative-skip rule: null LayoutId is dropped under any scope ─────

    [Fact]
    public void FilterOrphansForScope_ScopedAndCandidateHasNoLayoutId_FiltersOut()
    {
        // Sections / layouts / menus / modals / manifests / tags / workflows do NOT stamp
        // layoutId. Scoped clean must not delete them — there is no way to prove they
        // belong to the scoped tenant. Operator must run unscoped clean for those.
        Dictionary<string, RavenDbService.OrphanCandidate> candidates = new()
        {
            ["not-found-section"] = new("docs/section-1", LayoutId: null),
            ["legacy-layout"] = new("docs/layout-1", LayoutId: null),
        };

        Dictionary<string, RavenDbService.OrphanCandidate> result =
            DocumentSyncService.FilterOrphansForScope(candidates, scopedLayoutId: "dirt-life");

        Assert.Empty(result);
    }

    [Fact]
    public void FilterOrphansForScope_ScopedMixed_KeepsOnlyExactMatches()
    {
        // Realistic mix mirroring the issue's environment: some matching, some cross-tenant,
        // some unattributed. Filter must keep only the exact-match subset.
        Dictionary<string, RavenDbService.OrphanCandidate> candidates = new()
        {
            ["dl-policy"] = new("docs/dl-1", "dirt-life"),
            ["nb-policy"] = new("docs/nb-1", "neighborly"),
            ["legacy-section"] = new("docs/section-1", LayoutId: null),
            ["dl-config"] = new("docs/dl-2", "dirt-life"),
        };

        Dictionary<string, RavenDbService.OrphanCandidate> result =
            DocumentSyncService.FilterOrphansForScope(candidates, scopedLayoutId: "dirt-life");

        Assert.Equal(2, result.Count);
        Assert.Contains("dl-policy", result.Keys);
        Assert.Contains("dl-config", result.Keys);
    }

    // ── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public void FilterOrphansForScope_EmptyCandidates_ReturnsEmpty_WithOrWithoutScope()
    {
        Dictionary<string, RavenDbService.OrphanCandidate> empty = [];

        Assert.Empty(DocumentSyncService.FilterOrphansForScope(empty, scopedLayoutId: null));
        Assert.Empty(DocumentSyncService.FilterOrphansForScope(empty, scopedLayoutId: "dirt-life"));
    }

    [Fact]
    public void FilterOrphansForScope_PreservesIdentifierKeysAndCandidateValues()
    {
        // Guard against subtle key/value drift through the LINQ pipeline.
        RavenDbService.OrphanCandidate dl = new("docs/dl-1", "dirt-life");
        Dictionary<string, RavenDbService.OrphanCandidate> candidates = new()
        {
            ["dl-policy"] = dl,
            ["nb-policy"] = new("docs/nb-1", "neighborly"),
        };

        Dictionary<string, RavenDbService.OrphanCandidate> result =
            DocumentSyncService.FilterOrphansForScope(candidates, scopedLayoutId: "dirt-life");

        Assert.Single(result);
        Assert.Same(dl, result["dl-policy"]);
        Assert.Equal("docs/dl-1", result["dl-policy"].DocumentId);
        Assert.Equal("dirt-life", result["dl-policy"].LayoutId);
    }

    [Fact]
    public void FilterOrphansForScope_LayoutIdComparisonIsCaseSensitive()
    {
        // RavenDB layoutId values are always lowercase NanoIDs / slugs. Case sensitivity
        // is the safer default — a "dirt-life" scope must not silently match "Dirt-Life",
        // since the data model never produces such a casing in practice.
        Dictionary<string, RavenDbService.OrphanCandidate> candidates = new()
        {
            ["a"] = new("docs/a", "dirt-life"),
            ["b"] = new("docs/b", "Dirt-Life"),
        };

        Dictionary<string, RavenDbService.OrphanCandidate> result =
            DocumentSyncService.FilterOrphansForScope(candidates, scopedLayoutId: "dirt-life");

        Assert.Single(result);
        Assert.Contains("a", result.Keys);
    }
}
