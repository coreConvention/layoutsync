using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Tests for the --exclude-collection / --exclude-layout discovery behavior (issue #9) and
/// the registry-driven DiscoverFiles yield order. Uses the same self-contained scratch-dir
/// harness as <see cref="PlatformThemeDiscoveryTests"/>.
/// </summary>
public class DiscoveryExclusionTests : IDisposable
{
    private readonly string _root;
    private readonly string _layoutsPath;
    private readonly string _platformThemesPath;
    private readonly LocalFileService _service;

    public DiscoveryExclusionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "layoutsync-tests-" + Guid.NewGuid().ToString("N"));
        _layoutsPath = Path.Combine(_root, "layouts");
        _platformThemesPath = Path.Combine(_root, "themes");
        Directory.CreateDirectory(_layoutsPath);
        _service = new LocalFileService(NullLogger<LocalFileService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void AddFile(string layout, string collection, string fileName)
    {
        string dir = Path.Combine(_layoutsPath, layout, collection);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), "{}");
    }

    private void AddPlatformTheme(string fileName)
    {
        Directory.CreateDirectory(_platformThemesPath);
        File.WriteAllText(Path.Combine(_platformThemesPath, fileName), "{}");
    }

    // ── Yield order (registry refactor is behavior-preserving) ───────────────

    [Fact]
    public void DiscoverFiles_YieldsCollectionsInHistoricalOrder()
    {
        // One layout with a file in every collection folder, plus the platform catalogue.
        // Before issue #9 the order was pinned by 12 copy-pasted blocks; now it is pinned
        // by CollectionFolders.Ordered — this test fails if a registry edit reorders it.
        foreach (string folder in CollectionFolders.FolderNames)
        {
            AddFile("alpha", folder, "doc.json");
        }
        AddPlatformTheme("platform.json");

        List<string> discovered = _service.DiscoverFiles(_layoutsPath).ToList();

        List<string> parentFolders = discovered
            .Select(path => Path.GetFileName(Path.GetDirectoryName(path))!)
            .ToList();
        List<string> expected = [.. CollectionFolders.FolderNames, "themes"];
        Assert.Equal(expected, parentFolders);
    }

    [Fact]
    public void DiscoverFiles_NoExclusionArguments_MatchesEmptyExclusions()
    {
        // Passing null and passing empty lists are the same run.
        AddFile("alpha", "sections", "s.json");
        AddFile("alpha", "entities", "e.json");

        List<string> withNulls = _service.DiscoverFiles(_layoutsPath).ToList();
        List<string> withEmpty = _service.DiscoverFiles(
            _layoutsPath, specificLayout: null, excludeCollections: [], excludeLayouts: []).ToList();

        Assert.Equal(withNulls, withEmpty);
        Assert.Equal(2, withNulls.Count);
    }

    // ── Collection exclusion ─────────────────────────────────────────────────

    [Fact]
    public void DiscoverFiles_ExcludedCollection_SkippedInEveryLayout()
    {
        AddFile("alpha", "entities", "e1.json");
        AddFile("alpha", "sections", "s1.json");
        AddFile("beta", "entities", "e2.json");
        AddFile("beta", "sections", "s2.json");

        List<string> discovered = _service.DiscoverFiles(
            _layoutsPath, excludeCollections: ["entities"]).ToList();

        Assert.Equal(2, discovered.Count);
        Assert.All(discovered, path => Assert.Contains("sections", path));
    }

    [Fact]
    public void DiscoverFiles_MultipleExcludedCollections_AllSkipped()
    {
        AddFile("alpha", "entities", "e.json");
        AddFile("alpha", "identities", "i.json");
        AddFile("alpha", "sections", "s.json");

        List<string> discovered = _service.DiscoverFiles(
            _layoutsPath, excludeCollections: ["entities", "identities"]).ToList();

        string only = Assert.Single(discovered);
        Assert.Contains("sections", only);
    }

    [Fact]
    public void DiscoverFiles_ExcludedCollection_MatchIsCaseInsensitive()
    {
        // Mirrors DetermineDocumentType's case-insensitive folder classification.
        AddFile("alpha", "entities", "e.json");

        List<string> discovered = _service.DiscoverFiles(
            _layoutsPath, excludeCollections: ["ENTITIES"]).ToList();

        Assert.Empty(discovered);
    }

    [Fact]
    public void DiscoverFiles_ExcludedThemes_AlsoSkipsPlatformCatalogue()
    {
        // "themes" is one collection with two scopes (layout-keyed + platform sibling);
        // excluding it excludes both. See DiscoverFiles' platform-catalogue guard.
        AddFile("alpha", "themes", "override.json");
        AddFile("alpha", "sections", "s.json");
        AddPlatformTheme("catalogue.json");

        List<string> discovered = _service.DiscoverFiles(
            _layoutsPath, excludeCollections: ["themes"]).ToList();

        string only = Assert.Single(discovered);
        Assert.Contains("sections", only);
    }

    // ── Layout exclusion ─────────────────────────────────────────────────────

    [Fact]
    public void DiscoverFiles_ExcludedLayout_EntireDirectorySkipped()
    {
        AddFile("alpha", "sections", "s1.json");
        AddFile("__conformance__", "sections", "s2.json");
        AddFile("__conformance__", "entities", "e2.json");

        List<string> discovered = _service.DiscoverFiles(
            _layoutsPath, excludeLayouts: ["__conformance__"]).ToList();

        string only = Assert.Single(discovered);
        Assert.Contains("alpha", only);
    }

    [Fact]
    public void DiscoverFiles_ExcludedLayout_MatchIsCaseSensitive()
    {
        // Ordinal on purpose: Linux CI filesystems are case-sensitive, and a
        // case-insensitive match here would diverge from what the OS actually syncs.
        AddFile("alpha", "sections", "s.json");

        List<string> discovered = _service.DiscoverFiles(
            _layoutsPath, excludeLayouts: ["Alpha"]).ToList();

        Assert.Single(discovered);
    }

    [Fact]
    public void DiscoverFiles_CollectionAndLayoutExclusionsCompose()
    {
        AddFile("alpha", "entities", "e1.json");
        AddFile("alpha", "sections", "s1.json");
        AddFile("__conformance__", "sections", "s2.json");

        List<string> discovered = _service.DiscoverFiles(
            _layoutsPath,
            excludeCollections: ["entities"],
            excludeLayouts: ["__conformance__"]).ToList();

        string only = Assert.Single(discovered);
        Assert.Contains(Path.Combine("alpha", "sections"), only);
    }
}
