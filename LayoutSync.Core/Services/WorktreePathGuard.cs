using Microsoft.Extensions.Logging;

namespace LayoutSync.Services;

/// <summary>
/// Classification of how the current working directory relates to the resolved
/// <c>--layouts-path</c> with respect to git worktree boundaries.
///
/// <list type="bullet">
///   <item><description><see cref="NotInWorktree"/> — CWD is not under any
///     <c>.claude/worktrees/&lt;name&gt;/</c> directory. Any layouts-path is
///     allowed; the worktree-mismatch failure mode is impossible.</description></item>
///   <item><description><see cref="MatchedWorktree"/> — CWD is inside a worktree
///     at <c>.claude/worktrees/&lt;name&gt;/</c> AND the layouts-path is rooted
///     under the same worktree. Safe — the layouts being synced belong to this
///     checkout.</description></item>
///   <item><description><see cref="MismatchedWorktree"/> — CWD is inside a worktree
///     at <c>.claude/worktrees/&lt;name&gt;/</c> but the layouts-path is OUTSIDE
///     that worktree (likely the main repo or a sibling worktree). The
///     silent-failure pattern from issue #520 — refused unless the operator opts
///     in via <c>--allow-cross-worktree-sync</c>.</description></item>
/// </list>
/// </summary>
public enum WorktreePathClassification
{
    NotInWorktree,
    MatchedWorktree,
    MismatchedWorktree,
}

/// <summary>
/// Refuses LayoutSync runs when the current working directory is inside a git
/// worktree at <c>.claude/worktrees/&lt;name&gt;/</c> AND the resolved
/// <c>--layouts-path</c> points OUTSIDE that worktree.
///
/// Motivation: issue #520. Hardcoded LayoutSync command examples used
/// <c>--layouts-path "z:/Personal/w31rd.com/layouts"</c> (main repo). When a
/// session was running in a worktree, this command synced the MAIN repo's stale
/// content to RavenDB instead of the worktree's edits. Exit code 0 + a
/// "Replaced: &lt;section&gt;" log line — the failure was completely silent.
/// Operators chased visual drift for up to 30 minutes before diagnosing.
///
/// This guard catches the silent-failure shape at runtime by detecting the
/// CWD-inside-worktree + layouts-outside-worktree configuration and refusing with
/// a loud banner. The companion <see cref="LayoutsPathResolver"/> attacks the same
/// problem from the other direction by making the explicit flag unnecessary in the
/// first place.
///
/// The convention this guard relies on (see <c>CLAUDE.md</c> §4): all w31rd.com
/// git worktrees live under <c>.claude/worktrees/&lt;name&gt;/</c>. A path is
/// "in a worktree" if any ancestor's parent directory ends in
/// <c>.claude/worktrees</c>.
///
/// Tenant-agnostic: no layout identifier, tenant slug, or repo-specific path
/// appears in the classification logic.
/// </summary>
public sealed class WorktreePathGuard(ILogger<WorktreePathGuard> logger)
{
    private readonly ILogger<WorktreePathGuard> _logger = logger;

    /// <summary>
    /// Suffix segments (forward-slash separated) that identify the directory whose
    /// children are git worktree roots. Comparison is performed after normalizing
    /// both <see cref="Path.DirectorySeparatorChar"/> and
    /// <see cref="Path.AltDirectorySeparatorChar"/> to forward slashes, so the
    /// constant is identical on Windows and Unix.
    /// </summary>
    private const string WorktreesParentSegment = ".claude/worktrees";

    /// <summary>
    /// Classify <paramref name="currentDirectory"/> + <paramref name="layoutsPath"/>
    /// without side effects. Pure string operation — does NOT touch the filesystem.
    /// Both arguments are normalized via <see cref="Path.GetFullPath(string)"/>
    /// before comparison, so relative paths are tolerated.
    /// </summary>
    public static WorktreePathClassification Classify(string currentDirectory, string layoutsPath)
    {
        if (string.IsNullOrWhiteSpace(currentDirectory) || string.IsNullOrWhiteSpace(layoutsPath))
            return WorktreePathClassification.NotInWorktree;

        string normalizedCwd = Path.GetFullPath(currentDirectory);
        string normalizedLayouts = Path.GetFullPath(layoutsPath);

        string? worktreeRoot = FindWorktreeRoot(normalizedCwd);
        if (worktreeRoot == null)
            return WorktreePathClassification.NotInWorktree;

        // The layouts-path is "in the same worktree" if it equals or is a descendant
        // of the worktree root. Append the platform separator to prevent
        // partial-prefix false positives — e.g. ".claude/worktrees/foo" must NOT
        // match ".claude/worktrees/foobar".
        string worktreeRootWithSep = EnsureTrailingSeparator(worktreeRoot);
        string layoutsWithSep = EnsureTrailingSeparator(normalizedLayouts);

        bool layoutsInsideWorktree = layoutsWithSep.StartsWith(
            worktreeRootWithSep,
            StringComparison.OrdinalIgnoreCase);

        return layoutsInsideWorktree
            ? WorktreePathClassification.MatchedWorktree
            : WorktreePathClassification.MismatchedWorktree;
    }

    /// <summary>
    /// Walks up <paramref name="absolutePath"/> looking for the first ancestor
    /// whose parent directory's name ends with <c>.claude/worktrees</c>. That
    /// ancestor IS the worktree root. Returns <c>null</c> when no ancestor
    /// matches (the path is not inside a worktree).
    /// </summary>
    internal static string? FindWorktreeRoot(string absolutePath)
    {
        string? current = absolutePath;
        while (current != null)
        {
            string? parent = Path.GetDirectoryName(current);
            if (parent == null)
                return null;

            // If `parent` is the .claude/worktrees directory, then `current` is the
            // root of one of its child worktrees.
            if (PathEndsWithSegments(parent, WorktreesParentSegment))
                return current;

            // Move up one level. Detect the fixed-point at the filesystem root.
            if (string.Equals(parent, current, StringComparison.Ordinal))
                return null;
            current = parent;
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="path"/> ends with the directory segments in
    /// <paramref name="suffix"/>. Comparison is case-insensitive and treats
    /// <c>/</c> and <c>\</c> as equivalent separators so the same constant works
    /// on Windows and Unix.
    /// </summary>
    private static bool PathEndsWithSegments(string path, string suffix)
    {
        string normalizedPath = path
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        if (normalizedPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return true;

        // Tolerate a trailing slash on the input.
        return normalizedPath.EndsWith(suffix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.Length == 0)
            return path;
        char last = path[^1];
        if (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar)
            return path;
        return path + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Gate the run. If <see cref="Classify"/> returns
    /// <see cref="WorktreePathClassification.MismatchedWorktree"/> and
    /// <paramref name="allowCrossWorktreeSync"/> is <c>false</c>, returns
    /// <c>false</c> after emitting a Critical-level refusal banner. Otherwise
    /// returns <c>true</c>; an opt-in cross-worktree run also emits a Warning-level
    /// banner so the operator sees the abnormal mode in the log stream.
    /// </summary>
    public bool Authorize(string currentDirectory, string layoutsPath, bool allowCrossWorktreeSync)
    {
        WorktreePathClassification classification = Classify(currentDirectory, layoutsPath);

        _logger.LogInformation(
            "Worktree path classification: {Classification}",
            classification);

        if (classification != WorktreePathClassification.MismatchedWorktree)
            return true;

        if (!allowCrossWorktreeSync)
        {
            EmitRefusalBanner(currentDirectory, layoutsPath);
            return false;
        }

        EmitAllowBanner(currentDirectory, layoutsPath);
        return true;
    }

    /// <summary>
    /// Width of the banner bar. Matches <see cref="ProductionTargetGuard"/>'s
    /// banner width so mixed log streams have consistent visual weight.
    /// </summary>
    private const int BannerWidth = 76;

    private static string BannerBar() => new('=', BannerWidth);

    private void EmitRefusalBanner(string currentDirectory, string layoutsPath)
    {
        string normalizedCwd = Path.GetFullPath(currentDirectory);
        string normalizedLayouts = Path.GetFullPath(layoutsPath);
        string? worktreeRoot = FindWorktreeRoot(normalizedCwd);
        string suggested = worktreeRoot != null
            ? Path.Combine(worktreeRoot, "layouts")
            : "(unknown)";

        string bar = BannerBar();
        _logger.LogCritical("{Bar}", bar);
        _logger.LogCritical("FATAL: WORKTREE PATH MISMATCH — refusing to sync.");
        _logger.LogCritical("{Bar}", bar);
        _logger.LogCritical("Current directory:  {Cwd}", normalizedCwd);
        _logger.LogCritical("Worktree root:      {Root}", worktreeRoot ?? "(none)");
        _logger.LogCritical("--layouts-path:     {Path}", normalizedLayouts);
        _logger.LogCritical("");
        _logger.LogCritical(
            "The --layouts-path is OUTSIDE the current worktree. LayoutSync would");
        _logger.LogCritical(
            "sync stale content from a different working tree (likely main), not");
        _logger.LogCritical(
            "your worktree's edits — this is the silent-failure pattern from #520.");
        _logger.LogCritical("");
        _logger.LogCritical("Did you mean: {Suggested} ?", suggested);
        _logger.LogCritical("");
        _logger.LogCritical(
            "If intentional (e.g. cross-worktree maintenance sync), re-run with");
        _logger.LogCritical("--allow-cross-worktree-sync to opt in.");
        _logger.LogCritical("{Bar}", bar);
    }

    private void EmitAllowBanner(string currentDirectory, string layoutsPath)
    {
        string bar = BannerBar();
        _logger.LogWarning("{Bar}", bar);
        _logger.LogWarning("WARNING: syncing across worktrees with --allow-cross-worktree-sync.");
        _logger.LogWarning("{Bar}", bar);
        _logger.LogWarning("Current directory:  {Cwd}", Path.GetFullPath(currentDirectory));
        _logger.LogWarning("--layouts-path:     {Path}", Path.GetFullPath(layoutsPath));
        _logger.LogWarning("{Bar}", bar);
    }
}
