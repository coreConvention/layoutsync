using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Tests for the platform-theme discovery path added to <see cref="LocalFileService"/>.
/// Platform themes live in a sibling <c>themes/</c> directory next to the layouts root and
/// represent the catalogue every tenant can pick from. They must be discovered without a
/// stamped <c>layoutId</c> so the sync layer leaves them as platform-scoped documents.
/// </summary>
public class PlatformThemeDiscoveryTests : IDisposable
{
    private readonly string _root;
    private readonly string _layoutsPath;
    private readonly string _platformThemesPath;
    private readonly LocalFileService _service;

    public PlatformThemeDiscoveryTests()
    {
        // Self-contained scratch directory: <tmp>/{guid}/{layouts,themes}.
        // The GetPlatformThemesDirectory helper expects the platform themes path to be
        // a SIBLING of the layouts root, so we set up exactly that shape.
        _root = Path.Combine(Path.GetTempPath(), "layoutsync-tests-" + Guid.NewGuid().ToString("N"));
        _layoutsPath = Path.Combine(_root, "layouts");
        _platformThemesPath = Path.Combine(_root, "themes");
        Directory.CreateDirectory(_layoutsPath);
        Directory.CreateDirectory(_platformThemesPath);
        _service = new LocalFileService(NullLogger<LocalFileService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ── GetPlatformThemesDirectory ───────────────────────────────────────────

    [Fact]
    public void GetPlatformThemesDirectory_ReturnsSiblingPath()
    {
        // Sanity: the helper resolves to <layoutsParent>/themes, regardless of whether the
        // path ends with a separator. We compare normalized paths so any trailing-separator
        // differences (Windows vs POSIX) don't fail the assertion.
        string resolved = LocalFileService.GetPlatformThemesDirectory(_layoutsPath);
        Assert.Equal(
            Path.GetFullPath(_platformThemesPath),
            Path.GetFullPath(resolved));
    }

    [Fact]
    public void GetPlatformThemesDirectory_HandlesTrailingSeparator()
    {
        string withTrailing = _layoutsPath + Path.DirectorySeparatorChar;
        string resolved = LocalFileService.GetPlatformThemesDirectory(withTrailing);
        Assert.Equal(
            Path.GetFullPath(_platformThemesPath),
            Path.GetFullPath(resolved));
    }

    [Fact]
    public void GetPlatformThemesDirectory_ReturnsEmptyForEmptyInput()
    {
        // Defensive: an empty layouts path must NOT resolve to "/themes" (root-relative match
        // would be a security/correctness issue). The helper returns empty so the prefix
        // match in ReadDocumentAsync becomes a no-op.
        Assert.Equal(string.Empty, LocalFileService.GetPlatformThemesDirectory(string.Empty));
    }

    // ── DiscoverFiles: platform-themes inclusion / scoping ───────────────────

    [Fact]
    public void DiscoverFiles_IncludesPlatformThemesUnderUnscopedRun()
    {
        // Given a platform theme file in the sibling themes/ dir and no specificLayout,
        // discovery yields it alongside any layout files.
        WriteFile(Path.Combine(_platformThemesPath, "midnight-terminal.json"), MinimalThemeJson("midnight-terminal"));
        Directory.CreateDirectory(Path.Combine(_layoutsPath, "dirt-life", "themes"));
        WriteFile(
            Path.Combine(_layoutsPath, "dirt-life", "themes", "dirt-life-trail.json"),
            MinimalThemeJson("dirt-life-trail"));

        List<string> discovered = _service.DiscoverFiles(_layoutsPath).ToList();

        Assert.Contains(discovered, p => p.EndsWith("midnight-terminal.json"));
        Assert.Contains(discovered, p => p.EndsWith("dirt-life-trail.json"));
    }

    [Fact]
    public void DiscoverFiles_SkipsPlatformThemesUnderScopedLayoutRun()
    {
        // A tenant-scoped sync (--layout dirt-life) must NOT redundantly re-sync the platform
        // catalogue. Only files inside layouts/dirt-life/ should surface. This contract keeps
        // iteration cycles cheap and prevents cross-tenant churn under scoped clean runs.
        WriteFile(Path.Combine(_platformThemesPath, "midnight-terminal.json"), MinimalThemeJson("midnight-terminal"));
        Directory.CreateDirectory(Path.Combine(_layoutsPath, "dirt-life", "themes"));
        WriteFile(
            Path.Combine(_layoutsPath, "dirt-life", "themes", "dirt-life-trail.json"),
            MinimalThemeJson("dirt-life-trail"));

        List<string> discovered = _service.DiscoverFiles(_layoutsPath, specificLayout: "dirt-life").ToList();

        Assert.DoesNotContain(discovered, p => p.EndsWith("midnight-terminal.json"));
        Assert.Contains(discovered, p => p.EndsWith("dirt-life-trail.json"));
    }

    [Fact]
    public void DiscoverFiles_ReturnsEmptyWhenPlatformThemesDirMissing()
    {
        // Removing the platform themes directory must not break unscoped discovery —
        // it should silently yield no platform files (subset behavior).
        Directory.Delete(_platformThemesPath);

        List<string> discovered = _service.DiscoverFiles(_layoutsPath).ToList();

        Assert.Empty(discovered);
    }

    // ── ReadDocumentAsync: platform-theme detection ──────────────────────────

    [Fact]
    public async Task ReadDocumentAsync_PlatformThemeFile_ReturnsThemeWithNullLayoutId()
    {
        // The critical invariant: a platform theme file resolves to DocumentType.Theme
        // and LayoutId == null. The downstream sync service uses this null to skip
        // layoutId stamping (DocumentSyncService.SyncFileAsync line 158-ish).
        string filePath = Path.Combine(_platformThemesPath, "midnight-terminal.json");
        WriteFile(filePath, MinimalThemeJson("midnight-terminal"));

        SyncDocument? doc = await _service.ReadDocumentAsync(filePath, _layoutsPath);

        Assert.NotNull(doc);
        Assert.Equal(DocumentType.Theme, doc!.DocumentType);
        Assert.Null(doc.LayoutId);
        Assert.Equal("midnight-terminal", doc.Identifier);
    }

    [Fact]
    public async Task ReadDocumentAsync_LayoutScopedThemeFile_StillStampsLayoutId()
    {
        // Regression guard: the platform-theme branch must not steal layout-scoped overrides.
        // dirt-life-trail.json under layouts/dirt-life/themes/ must continue resolving with
        // LayoutId == "dirt-life" so the API resolver paints it for the matching tenant.
        string layoutThemesDir = Path.Combine(_layoutsPath, "dirt-life", "themes");
        Directory.CreateDirectory(layoutThemesDir);
        string filePath = Path.Combine(layoutThemesDir, "dirt-life-trail.json");
        WriteFile(filePath, MinimalThemeJson("dirt-life-trail"));

        SyncDocument? doc = await _service.ReadDocumentAsync(filePath, _layoutsPath);

        Assert.NotNull(doc);
        Assert.Equal(DocumentType.Theme, doc!.DocumentType);
        Assert.Equal("dirt-life", doc.LayoutId);
        Assert.Equal("dirt-life-trail", doc.Identifier);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string MinimalThemeJson(string identifier) =>
        $$"""
        {
          "identifier": "{{identifier}}",
          "type": "theme-definition",
          "active": true,
          "tags": [],
          "indexes": {},
          "data": { "id": "{{identifier}}", "name": "{{identifier}}" }
        }
        """;

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
