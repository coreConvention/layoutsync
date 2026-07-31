using LayoutSync.Models;
using LayoutSync.Services;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Unit tests for the layoutId-scoped document lookup introduced in issue #16.
///
/// Two layouts that declare documents with the SAME identifier in the SAME collection
/// (e.g. an "identity-profile-owner" write-policy in both dirt-life and __conformance__)
/// used to clobber each other on a shared-DB sync, because <c>FindDocumentAsync</c> matched
/// by identifier alone: the second layout's sync found the first's document and replaced it
/// in place. The lookup is now scoped by <c>layoutId</c> for the per-tenant document types
/// that stamp a <c>layoutId</c> field — and ONLY those. Layout-agnostic documents (sections,
/// menus, manifests, …) keep matching by identifier alone; scoping THEM would match zero rows
/// (they carry no <c>layoutId</c> field) and spuriously re-create the document every sync.
///
/// <see cref="DocumentTypeExtensions.StampsLayoutId"/> is the single source of truth shared by
/// the sync writer (stamp) and the lookup reader (scope), so the two can never disagree.
/// </summary>
public class LayoutScopedLookupTests
{
    // ── StampsLayoutId: which document types carry a layoutId field ──────────────

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
    public void StampsLayoutId_LayoutAgnosticTypes_ReturnFalse(DocumentType type)
        => Assert.False(type.StampsLayoutId());

    // ── BuildEntityLookupQuery: scoped vs unscoped RQL ──────────────────────────

    [Fact]
    public void BuildEntityLookupQuery_Scoped_ConstrainsByLayoutId()
    {
        string query = RavenDbService.BuildEntityLookupQuery("WritePolicies", scopeByLayoutId: true);

        Assert.Equal(
            "from WritePolicies where identifier = $lookupValue and layoutId = $layoutId",
            query);
    }

    [Fact]
    public void BuildEntityLookupQuery_Unscoped_MatchesByIdentifierAlone()
    {
        string query = RavenDbService.BuildEntityLookupQuery("sections", scopeByLayoutId: false);

        Assert.Equal("from sections where identifier = $lookupValue", query);
        Assert.DoesNotContain("layoutId", query);
    }

    [Fact]
    public void BuildEntityLookupQuery_Scoped_BindsTheLayoutIdParameterItReferences()
    {
        // Guards the invariant that lets FindDocumentAsync bind $layoutId conditionally:
        // the scoped query MUST reference $layoutId (else the bound parameter is dead) and
        // the unscoped query MUST NOT (else RavenDB throws on an unbound parameter).
        string scoped = RavenDbService.BuildEntityLookupQuery("theme-definitions", scopeByLayoutId: true);
        string unscoped = RavenDbService.BuildEntityLookupQuery("theme-definitions", scopeByLayoutId: false);

        Assert.Contains("$layoutId", scoped);
        Assert.DoesNotContain("$layoutId", unscoped);
    }
}
