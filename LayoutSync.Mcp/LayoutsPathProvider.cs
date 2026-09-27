using LayoutSync.Services;
using ModelContextProtocol;

namespace LayoutSync.Mcp;

/// <summary>
/// The server's startup-default <c>layouts/</c> directory, and the resolution of the
/// optional per-call <c>layoutsPath</c> tool argument against it.
///
/// Why a per-call override exists (issue #29): the default is resolved ONCE, at startup,
/// usually from a relative <c>LAYOUTSYNC_LAYOUTS_PATH</c> against the directory the client
/// launched the server in — for Claude Code, the directory the session STARTED in. A
/// session that later edits a different git worktree would otherwise read, and write, the
/// startup checkout's manifests. The server cannot see the caller's working directory (and
/// MCP roots describe where the session started, not where it moved), so it cannot detect
/// the mismatch itself; the caller has to name its checkout. Every response echoes the
/// resolved <c>manifestPath</c> so the target is never invisible.
/// </summary>
/// <param name="DefaultPath">Absolute <c>layouts/</c> path resolved at startup.</param>
public sealed record LayoutsPathProvider(string DefaultPath)
{
    /// <summary>Description of the <c>layoutsPath</c> parameter, shared by every manifest tool.</summary>
    internal const string ParameterDescription =
        "Absolute path to the layouts/ directory of the checkout you are editing (a checkout or "
        + "worktree root that contains layouts/ also works). Omit it only when you are editing the "
        + "checkout the MCP server was launched in. If your session edits a different git worktree, "
        + "pass this on every call; otherwise the tool reads from, and writes to, the launch checkout.";

    /// <summary>Sentence appended to each read tool's description.</summary>
    internal const string ReadToolNote =
        " Reads the checkout named by layoutsPath (default: the checkout the MCP server was launched "
        + "in); the response's manifestPath shows which file was read.";

    /// <summary>Sentence appended to each mutation tool's description.</summary>
    internal const string WriteToolNote =
        " Writes to the checkout named by layoutsPath (default: the checkout the MCP server was "
        + "launched in, so pass layoutsPath when editing a different git worktree); the envelope's "
        + "manifestPath shows which file was targeted.";

    /// <summary>
    /// Resolves the <c>layouts/</c> directory a tool call targets. An omitted (null or blank)
    /// argument selects <see cref="DefaultPath"/>. An explicit argument must be absolute and
    /// may name either the <c>layouts/</c> directory itself or a checkout root that contains
    /// one.
    ///
    /// The explicit form is deliberately strict — no walk-up to an ancestor <c>layouts/</c>
    /// the way the CLI auto-resolves from its CWD. Worktrees commonly live INSIDE the primary
    /// checkout (<c>&lt;primary&gt;/.claude/worktrees/&lt;name&gt;</c>), so walking up from a
    /// worktree path that lacks <c>layouts/</c> would silently land on the primary's — the
    /// very wrong-checkout failure this argument exists to prevent.
    /// </summary>
    /// <exception cref="McpException">
    /// The argument is relative, or names a directory that does not exist. Thrown as
    /// <see cref="McpException"/> so the message reaches the model (the SDK replaces other
    /// exception messages with a generic one).
    /// </exception>
    public ResolvedLayoutsPath Resolve(string? layoutsPath)
    {
        if (string.IsNullOrWhiteSpace(layoutsPath))
            return new ResolvedLayoutsPath(DefaultPath, IsExplicit: false);

        // IsPathFullyQualified, not IsPathRooted: on Windows "\repo\layouts" and
        // "C:repo\layouts" are rooted yet still resolve against the process's current
        // drive or directory — the server's launch location, which is the thing to escape.
        if (!Path.IsPathFullyQualified(layoutsPath))
        {
            throw new McpException(
                $"layoutsPath must be an absolute path (got '{layoutsPath}'). A relative path would "
                + "resolve against the MCP server's launch directory, not your working directory. "
                + "Pass the full path to the layouts/ directory of the checkout you are editing.");
        }

        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(layoutsPath));

        string nestedLayouts = Path.Combine(fullPath, "layouts");
        if (Directory.Exists(nestedLayouts))
            return new ResolvedLayoutsPath(nestedLayouts, IsExplicit: true);

        if (Directory.Exists(fullPath))
            return new ResolvedLayoutsPath(fullPath, IsExplicit: true);

        throw new McpException(
            $"layoutsPath does not exist: {fullPath}. Pass the absolute path of the layouts/ "
            + "directory (or the checkout root containing it) of the checkout you are editing.");
    }
}

/// <summary>The <c>layouts/</c> directory that one tool call targets.</summary>
/// <param name="LayoutsPath">Absolute path of the resolved <c>layouts/</c> directory.</param>
/// <param name="IsExplicit">
/// <c>true</c> when the caller passed <c>layoutsPath</c>; <c>false</c> when the call fell
/// back to the server's startup default, which may not be the caller's checkout.
/// </param>
public sealed record ResolvedLayoutsPath(string LayoutsPath, bool IsExplicit)
{
    /// <summary>The manifest file for <paramref name="layoutId"/> under this layouts directory.</summary>
    public string ManifestPath(string layoutId)
        => ManifestMutationService.GetManifestPath(LayoutsPath, layoutId);
}
