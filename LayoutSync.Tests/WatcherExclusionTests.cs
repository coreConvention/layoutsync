using LayoutSync.Services;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Tests for <see cref="FileWatcherService.IsExcludedPath"/> (issue #9) — the pure
/// classifier watch-mode events run through before queueing sync or deletion work. Paths
/// are built with <see cref="Path.Combine"/>, so on Windows every case also exercises the
/// backslash→slash normalization the helper performs before splitting (the pre-existing
/// DeriveTrackingFromPath fallback lacks that normalization — tracked separately).
/// </summary>
public class WatcherExclusionTests
{
    private static readonly string LayoutsPath = Path.Combine(Path.GetTempPath(), "wx-root", "layouts");

    private static string FileIn(params string[] segments) =>
        Path.Combine([LayoutsPath, .. segments]);

    [Fact]
    public void NoExclusions_NothingIsExcluded()
    {
        Assert.False(FileWatcherService.IsExcludedPath(
            LayoutsPath, FileIn("alpha", "entities", "e.json"), [], []));
    }

    [Fact]
    public void ExcludedCollection_FileInsideIt_IsExcluded()
    {
        Assert.True(FileWatcherService.IsExcludedPath(
            LayoutsPath, FileIn("alpha", "entities", "e.json"), ["entities"], []));
    }

    [Fact]
    public void ExcludedCollection_MatchIsCaseInsensitive()
    {
        Assert.True(FileWatcherService.IsExcludedPath(
            LayoutsPath, FileIn("alpha", "Entities", "e.json"), ["entities"], []));
    }

    [Fact]
    public void OtherCollections_AreNotExcluded()
    {
        Assert.False(FileWatcherService.IsExcludedPath(
            LayoutsPath, FileIn("alpha", "sections", "s.json"), ["entities"], []));
    }

    [Fact]
    public void ExcludedLayout_AnyFileUnderIt_IsExcluded()
    {
        Assert.True(FileWatcherService.IsExcludedPath(
            LayoutsPath, FileIn("__conformance__", "sections", "s.json"), [], ["__conformance__"]));
    }

    [Fact]
    public void ExcludedLayout_MatchIsCaseSensitive()
    {
        // Ordinal, consistent with discovery and validation: Linux CI is case-sensitive.
        Assert.False(FileWatcherService.IsExcludedPath(
            LayoutsPath, FileIn("alpha", "sections", "s.json"), [], ["Alpha"]));
    }

    [Fact]
    public void FileDirectlyUnderLayoutsRoot_IsNotExcluded()
    {
        // parts[1] doesn't exist for a root-level file — must not throw, must not match.
        Assert.False(FileWatcherService.IsExcludedPath(
            LayoutsPath, FileIn("stray.json"), ["entities"], ["alpha"]));
    }

    [Fact]
    public void PlatformThemesSibling_ExcludedWithThemesCollection()
    {
        // The platform catalogue lives OUTSIDE layoutsPath (relative path starts with ..),
        // so segment 1 lands on "themes" — consistent with DiscoverFiles, excluding the
        // themes collection covers both of its scopes. (The watcher only watches
        // layoutsPath today, so this is future-proofing, not a live event path.)
        string platformTheme = Path.Combine(Path.GetTempPath(), "wx-root", "themes", "t.json");
        Assert.True(FileWatcherService.IsExcludedPath(
            LayoutsPath, platformTheme, ["themes"], []));
    }
}
