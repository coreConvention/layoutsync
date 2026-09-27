using LayoutSync.Mcp;
using ModelContextProtocol;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Covers <see cref="LayoutsPathProvider.Resolve"/> — how the MCP tools' optional
/// <c>layoutsPath</c> argument picks the checkout a call targets (issue #29). Uses real
/// temp directories because resolution probes the filesystem.
/// </summary>
public class LayoutsPathProviderTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _defaultLayouts;
    private readonly LayoutsPathProvider _provider;

    public LayoutsPathProviderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"layouts-path-provider-tests-{Guid.NewGuid()}");
        _defaultLayouts = Path.Combine(_tempRoot, "launch-checkout", "layouts");
        Directory.CreateDirectory(_defaultLayouts);
        _provider = new LayoutsPathProvider(_defaultLayouts);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_OmittedArgument_UsesServerDefault(string? layoutsPath)
    {
        ResolvedLayoutsPath resolved = _provider.Resolve(layoutsPath);

        Assert.Equal(_defaultLayouts, resolved.LayoutsPath);
        Assert.False(resolved.IsExplicit);
    }

    [Fact]
    public void Resolve_AbsoluteLayoutsDirectory_IsUsedAsIs()
    {
        string otherLayouts = CreateCheckout("other-worktree");

        ResolvedLayoutsPath resolved = _provider.Resolve(otherLayouts);

        Assert.Equal(otherLayouts, resolved.LayoutsPath);
        Assert.True(resolved.IsExplicit);
    }

    [Fact]
    public void Resolve_CheckoutRoot_UsesItsLayoutsSubdirectory()
    {
        string otherLayouts = CreateCheckout("other-worktree");
        string checkoutRoot = Path.GetDirectoryName(otherLayouts)!;

        ResolvedLayoutsPath resolved = _provider.Resolve(checkoutRoot);

        Assert.Equal(otherLayouts, resolved.LayoutsPath);
        Assert.True(resolved.IsExplicit);
    }

    [Fact]
    public void Resolve_TrailingSeparator_IsTrimmedSoEchoedPathsStayClean()
    {
        string otherLayouts = CreateCheckout("other-worktree");

        ResolvedLayoutsPath resolved = _provider.Resolve(otherLayouts + Path.DirectorySeparatorChar);

        Assert.Equal(otherLayouts, resolved.LayoutsPath);
    }

    [Fact]
    public void Resolve_ExplicitDefault_CountsAsExplicit()
    {
        // Naming the launch checkout on purpose is a statement of intent — no warning needed.
        ResolvedLayoutsPath resolved = _provider.Resolve(_defaultLayouts);

        Assert.Equal(_defaultLayouts, resolved.LayoutsPath);
        Assert.True(resolved.IsExplicit);
    }

    /// <summary>
    /// The regression this strictness exists for: worktrees commonly live INSIDE the
    /// primary checkout (<c>&lt;primary&gt;/.claude/worktrees/&lt;name&gt;</c>). A worktree
    /// directory without its own <c>layouts/</c> must not resolve upward to the primary's
    /// <c>layouts/</c> — that would silently re-target the wrong checkout.
    /// </summary>
    [Fact]
    public void Resolve_WorktreeWithoutLayouts_DoesNotWalkUpToEnclosingCheckout()
    {
        string primaryRoot = Path.Combine(_tempRoot, "primary");
        string primaryLayouts = Path.Combine(primaryRoot, "layouts");
        string nestedWorktree = Path.Combine(primaryRoot, ".claude", "worktrees", "no-layouts-here");
        Directory.CreateDirectory(primaryLayouts);
        Directory.CreateDirectory(nestedWorktree);

        ResolvedLayoutsPath resolved = _provider.Resolve(nestedWorktree);

        Assert.NotEqual(primaryLayouts, resolved.LayoutsPath);
        Assert.Equal(nestedWorktree, resolved.LayoutsPath);
    }

    [Theory]
    [InlineData("layouts")]
    [InlineData("./layouts")]
    [InlineData("../other-worktree/layouts")]
    // Rooted but not fully qualified on Windows (current-drive / drive-relative);
    // plain relative names elsewhere. Rejected on every platform.
    [InlineData(@"\repo\layouts")]
    [InlineData("C:repo\\layouts")]
    public void Resolve_RelativePath_IsRejected(string layoutsPath)
    {
        McpException ex = Assert.Throws<McpException>(() => _provider.Resolve(layoutsPath));

        Assert.Contains("absolute", ex.Message);
        Assert.Contains(layoutsPath, ex.Message);
    }

    [Fact]
    public void Resolve_MissingDirectory_IsRejectedWithThePath()
    {
        string missing = Path.Combine(_tempRoot, "deleted-worktree", "layouts");

        McpException ex = Assert.Throws<McpException>(() => _provider.Resolve(missing));

        Assert.Contains("does not exist", ex.Message);
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void ManifestPath_FollowsTheLayoutsConvention()
    {
        ResolvedLayoutsPath resolved = _provider.Resolve(null);

        Assert.Equal(
            Path.Combine(_defaultLayouts, "dirt-life", "manifests", "layout-manifest.json"),
            resolved.ManifestPath("dirt-life"));
    }

    [Fact]
    public void ServerInstructions_NameTheDefaultAndTheOverride()
    {
        // Fully qualified: an unqualified `Program` would bind to the CLI's LayoutSync.Program
        // first if the test project ever referenced the CLI (enclosing-namespace lookup wins).
        string instructions = LayoutSync.Mcp.Program.BuildServerInstructions(_defaultLayouts);

        Assert.Contains(_defaultLayouts, instructions);
        Assert.Contains("layoutsPath", instructions);
        Assert.Contains("manifestPath", instructions);
    }

    private string CreateCheckout(string name)
    {
        string layouts = Path.Combine(_tempRoot, name, "layouts");
        Directory.CreateDirectory(layouts);
        return layouts;
    }
}
