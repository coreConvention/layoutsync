using LayoutSync.Services;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Unit tests for <see cref="LayoutsPathResolver"/>. The resolver walks up from a
/// starting directory looking for a <c>layouts/</c> subdirectory; tests cover the
/// "found at start", "found several levels up", "not found anywhere", and edge
/// cases (null/whitespace input, filesystem root). Each test creates a fresh
/// temporary directory tree under <see cref="Path.GetTempPath"/>, exercises the
/// resolver, and removes the tree in a try/finally — so the suite leaves no
/// residue behind even on failure.
/// </summary>
public class LayoutsPathResolverTests : IDisposable
{
    private readonly string _tempRoot;

    public LayoutsPathResolverTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"LayoutsPathResolverTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    // ── Found cases ──────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_LayoutsAtStartDirectory_ReturnsThatLayouts()
    {
        // Tree: <tempRoot>/layouts. Start at <tempRoot> — should find immediately.
        string layouts = Path.Combine(_tempRoot, "layouts");
        Directory.CreateDirectory(layouts);

        string? result = LayoutsPathResolver.Resolve(_tempRoot);

        Assert.NotNull(result);
        Assert.Equal(
            Path.GetFullPath(layouts),
            Path.GetFullPath(result!),
            ignoreCase: true);
    }

    [Fact]
    public void Resolve_LayoutsSeveralLevelsUp_ReturnsTheNearestLayouts()
    {
        // Tree: <tempRoot>/layouts AND <tempRoot>/some/nested/cwd/. Walking up from
        // cwd hits layouts at <tempRoot>.
        string layouts = Path.Combine(_tempRoot, "layouts");
        Directory.CreateDirectory(layouts);
        string cwd = Path.Combine(_tempRoot, "some", "nested", "cwd");
        Directory.CreateDirectory(cwd);

        string? result = LayoutsPathResolver.Resolve(cwd);

        Assert.NotNull(result);
        Assert.Equal(
            Path.GetFullPath(layouts),
            Path.GetFullPath(result!),
            ignoreCase: true);
    }

    [Fact]
    public void Resolve_LayoutsAtMultipleLevels_ReturnsTheNEAREST()
    {
        // Tree: <tempRoot>/layouts AND <tempRoot>/some/layouts AND .../some/cwd.
        // Walking up from cwd should hit the .../some/layouts BEFORE the top-level one.
        string topLayouts = Path.Combine(_tempRoot, "layouts");
        Directory.CreateDirectory(topLayouts);
        string nearLayouts = Path.Combine(_tempRoot, "some", "layouts");
        Directory.CreateDirectory(nearLayouts);
        string cwd = Path.Combine(_tempRoot, "some", "cwd");
        Directory.CreateDirectory(cwd);

        string? result = LayoutsPathResolver.Resolve(cwd);

        Assert.NotNull(result);
        Assert.Equal(
            Path.GetFullPath(nearLayouts),
            Path.GetFullPath(result!),
            ignoreCase: true);
    }

    // ── Not-found cases ──────────────────────────────────────────────────────

    [Fact]
    public void Resolve_NoLayoutsAnywhereInAncestry_ReturnsNull()
    {
        // Tree: <tempRoot>/some/nested/cwd. No `layouts` anywhere in the ancestry
        // until the temp-root parent, which is system-dependent — tests run under
        // <temp>/LayoutsPathResolverTests-<guid>/some/nested/cwd, and the system
        // temp directory typically has no `layouts/` ancestor either. We assert
        // null.
        string cwd = Path.Combine(_tempRoot, "some", "nested", "cwd");
        Directory.CreateDirectory(cwd);

        string? result = LayoutsPathResolver.Resolve(cwd);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_LayoutsIsAFile_NotADirectory_DoesNotMatch()
    {
        // Filesystem entries named "layouts" that are FILES (not directories) must
        // not be returned — Directory.Exists is the contract.
        string fakeLayoutsFile = Path.Combine(_tempRoot, "layouts");
        File.WriteAllText(fakeLayoutsFile, "not a directory");

        string? result = LayoutsPathResolver.Resolve(_tempRoot);

        Assert.Null(result);
    }

    // ── Edge cases ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NullOrWhitespace_ReturnsNull(string? input)
    {
        // The resolver is defensive: it does not throw on bad input; null /
        // whitespace inputs map to null (no resolution) so the caller's existing
        // "Layouts path is required" error path can fire normally.
        Assert.Null(LayoutsPathResolver.Resolve(input!));
    }
}
