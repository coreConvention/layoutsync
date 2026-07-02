using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Tests for the read-policies/ folder mapping added to <see cref="LocalFileService"/>.
/// Read policies live in <c>layouts/{layoutId}/read-policies/</c> and sync to the
/// <c>ReadPolicies</c> RavenDB collection, mirroring the existing WritePolicy pattern.
/// </summary>
public class ReadPolicyFolderMappingTests : IDisposable
{
    private readonly string _root;
    private readonly string _layoutsPath;
    private readonly LocalFileService _service;

    public ReadPolicyFolderMappingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "layoutsync-rp-tests-" + Guid.NewGuid().ToString("N"));
        _layoutsPath = Path.Combine(_root, "layouts");
        Directory.CreateDirectory(_layoutsPath);
        _service = new LocalFileService(NullLogger<LocalFileService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // ── DetermineDocumentType ────────────────────────────────────────────────

    [Fact]
    public void DetermineDocumentType_ReadPoliciesFolder_ReturnsReadPolicy()
    {
        DocumentType result = LocalFileService.DetermineDocumentType("dirt-life/read-policies/global-read.json");
        Assert.Equal(DocumentType.ReadPolicy, result);
    }

    [Fact]
    public void DetermineDocumentType_WritePoliciesFolder_ReturnsWritePolicy()
    {
        // Regression guard: mirroring must not break the existing WritePolicy mapping.
        DocumentType result = LocalFileService.DetermineDocumentType("dirt-life/write-policies/global-write.json");
        Assert.Equal(DocumentType.WritePolicy, result);
    }

    // ── GetCollection ────────────────────────────────────────────────────────

    [Fact]
    public void GetCollection_ReadPolicy_ReturnsReadPolicies()
    {
        Assert.Equal("ReadPolicies", DocumentType.ReadPolicy.GetCollection());
    }

    [Fact]
    public void GetCollection_WritePolicy_ReturnsWritePolicies()
    {
        // Regression guard: existing collection name must be unchanged.
        Assert.Equal("WritePolicies", DocumentType.WritePolicy.GetCollection());
    }

    // ── GetApiRoute ──────────────────────────────────────────────────────────

    [Fact]
    public void GetApiRoute_ReadPolicy_ReturnsEntityRoute()
    {
        Assert.Equal("e", DocumentType.ReadPolicy.GetApiRoute());
    }

    // ── IsStaticCollection ───────────────────────────────────────────────────

    [Fact]
    public void IsStaticCollection_ReadPolicy_ReturnsTrue()
    {
        Assert.True(DocumentType.ReadPolicy.IsStaticCollection());
    }

    // ── DiscoverFiles ────────────────────────────────────────────────────────

    [Fact]
    public void DiscoverFiles_ReadPoliciesFolder_YieldsFiles()
    {
        string readPoliciesDir = Path.Combine(_layoutsPath, "dirt-life", "read-policies");
        Directory.CreateDirectory(readPoliciesDir);
        WriteFile(Path.Combine(readPoliciesDir, "global-read.json"), MinimalReadPolicyJson("global-read"));

        List<string> discovered = _service.DiscoverFiles(_layoutsPath).ToList();

        Assert.Contains(discovered, p => p.EndsWith("global-read.json"));
    }

    [Fact]
    public void DiscoverFiles_ReadPoliciesFolderAbsent_DoesNotThrow()
    {
        // layouts/dirt-life exists but has no read-policies/ subfolder — must yield nothing
        // for that subfolder without throwing.
        Directory.CreateDirectory(Path.Combine(_layoutsPath, "dirt-life"));

        List<string> discovered = _service.DiscoverFiles(_layoutsPath).ToList();

        Assert.DoesNotContain(discovered, p => p.Contains("read-policies"));
    }

    // ── ReadDocumentAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task ReadDocumentAsync_ReadPolicyFile_ReturnsReadPolicyTypeWithLayoutId()
    {
        string dir = Path.Combine(_layoutsPath, "dirt-life", "read-policies");
        Directory.CreateDirectory(dir);
        string filePath = Path.Combine(dir, "global-read.json");
        WriteFile(filePath, MinimalReadPolicyJson("global-read"));

        SyncDocument? doc = await _service.ReadDocumentAsync(filePath, _layoutsPath);

        Assert.NotNull(doc);
        Assert.Equal(DocumentType.ReadPolicy, doc!.DocumentType);
        Assert.Equal("dirt-life", doc.LayoutId);
        Assert.Equal("global-read", doc.Identifier);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string MinimalReadPolicyJson(string identifier) =>
        $$"""
        {
          "identifier": "{{identifier}}",
          "type": "read-policy",
          "active": true,
          "tags": [],
          "indexes": {},
          "data": {}
        }
        """;

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
