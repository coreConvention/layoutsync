using LayoutSync.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Unit tests for <see cref="WorktreePathGuard"/>. Three surfaces are covered:
///
/// <list type="bullet">
///   <item><description><see cref="WorktreePathGuard.Classify"/> — pure string
///     classification of CWD + layouts-path against the
///     <c>.claude/worktrees/&lt;name&gt;/</c> path pattern. Filesystem-free.</description></item>
///   <item><description><see cref="WorktreePathGuard.Authorize"/> — gate behavior.
///     Verifies that <see cref="WorktreePathClassification.MismatchedWorktree"/>
///     refuses without the opt-in flag, that
///     <see cref="WorktreePathClassification.NotInWorktree"/> and
///     <see cref="WorktreePathClassification.MatchedWorktree"/> always pass, and
///     that the refusal banner surfaces operator-relevant context (cwd, mismatched
///     path, suggested correction, opt-in flag name).</description></item>
///   <item><description><see cref="WorktreePathGuard.ParseRevParseOutput"/> — how the git
///     probe reads <c>git rev-parse</c> output, fed canned output.</description></item>
/// </list>
///
/// These tests pass <see cref="PatternOnly"/> as the worktree-root resolver, so they
/// never run git: the guard's default resolver asks git first, and git detection
/// against real repositories is covered by <see cref="WorktreePathGuardGitTests"/>.
///
/// All paths are constructed from <see cref="Path.GetTempPath"/> so the tests are
/// portable across Windows and Unix without assuming drive-letter or absolute-root
/// shapes. Comparisons in the guard are case-insensitive (Windows convention) and
/// path-separator agnostic (Windows AND Unix).
/// </summary>
public class WorktreePathGuardTests
{
    /// <summary>
    /// The path-pattern resolver on its own — the guard's only resolver before git
    /// detection, and still its fallback. Pinned so these tests keep classifying by
    /// path alone, independent of git and of whatever repository the paths sit in.
    /// </summary>
    private static readonly Func<string, string?> PatternOnly = WorktreePathGuard.FindWorktreeRootByPathPattern;

    /// <summary>
    /// Build an absolute path under the system temp directory using the platform's
    /// directory separator. Used to construct fake CWD / layouts paths without
    /// assuming any specific filesystem layout — the guard's Classify is pure
    /// string operation so the directories don't need to exist.
    /// </summary>
    private static string MakePath(params string[] segments)
    {
        string tempRoot = Path.GetTempPath();
        string combined = Path.Combine(new[] { tempRoot }.Concat(segments).ToArray());
        return Path.GetFullPath(combined);
    }

    // ── Classify: NotInWorktree ──────────────────────────────────────────────

    [Fact]
    public void Classify_CwdNotInWorktree_ReturnsNotInWorktree()
    {
        // CWD is in the temp tree but with no `.claude/worktrees/<name>` ancestor.
        // The layouts-path can be anywhere; the guard does not block.
        string cwd = MakePath("repo", "src", "feature");
        string layouts = MakePath("repo", "layouts");

        Assert.Equal(
            WorktreePathClassification.NotInWorktree,
            WorktreePathGuard.Classify(cwd, layouts, PatternOnly));
    }

    [Fact]
    public void Classify_CwdInDotClaudeButNotInWorktrees_ReturnsNotInWorktree()
    {
        // `.claude/something-else/foo` is NOT a worktree — only `.claude/worktrees/<name>`
        // counts. This protects against false positives on .claude/agents/, .claude/skills/,
        // etc.
        string cwd = MakePath("repo", ".claude", "agents", "some-agent");
        string layouts = MakePath("repo", "layouts");

        Assert.Equal(
            WorktreePathClassification.NotInWorktree,
            WorktreePathGuard.Classify(cwd, layouts, PatternOnly));
    }

    // ── Classify: MatchedWorktree ────────────────────────────────────────────

    [Fact]
    public void Classify_CwdAndLayoutsBothInSameWorktree_ReturnsMatchedWorktree()
    {
        // CWD: <tmp>/repo/.claude/worktrees/foo/src
        // layouts: <tmp>/repo/.claude/worktrees/foo/layouts
        // Worktree root: <tmp>/repo/.claude/worktrees/foo  → matched.
        string cwd = MakePath("repo", ".claude", "worktrees", "foo", "src");
        string layouts = MakePath("repo", ".claude", "worktrees", "foo", "layouts");

        Assert.Equal(
            WorktreePathClassification.MatchedWorktree,
            WorktreePathGuard.Classify(cwd, layouts, PatternOnly));
    }

    [Fact]
    public void Classify_LayoutsAtWorktreeRootItself_ReturnsMatchedWorktree()
    {
        // Edge case: layouts-path equals the worktree root (no subdirectory). This
        // is unusual but should still classify as matched — the trailing-separator
        // logic handles equality correctly.
        string cwd = MakePath("repo", ".claude", "worktrees", "foo", "src");
        string layouts = MakePath("repo", ".claude", "worktrees", "foo");

        Assert.Equal(
            WorktreePathClassification.MatchedWorktree,
            WorktreePathGuard.Classify(cwd, layouts, PatternOnly));
    }

    // ── Classify: MismatchedWorktree (the issue #520 shape) ──────────────────

    [Fact]
    public void Classify_CwdInWorktreeButLayoutsInMainRepo_ReturnsMismatched()
    {
        // The exact silent-failure shape from issue #520:
        //   CWD: <tmp>/repo/.claude/worktrees/foo/src/feature
        //   layouts: <tmp>/repo/layouts   ← main repo, NOT the worktree.
        string cwd = MakePath("repo", ".claude", "worktrees", "foo", "src", "feature");
        string layouts = MakePath("repo", "layouts");

        Assert.Equal(
            WorktreePathClassification.MismatchedWorktree,
            WorktreePathGuard.Classify(cwd, layouts, PatternOnly));
    }

    [Fact]
    public void Classify_CwdInOneWorktreeAndLayoutsInSibling_ReturnsMismatched()
    {
        // Cross-worktree mismatch — operator passed a sibling worktree's layouts
        // path. Easy to do by accident when juggling several worktrees.
        string cwd = MakePath("repo", ".claude", "worktrees", "foo", "src");
        string layouts = MakePath("repo", ".claude", "worktrees", "bar", "layouts");

        Assert.Equal(
            WorktreePathClassification.MismatchedWorktree,
            WorktreePathGuard.Classify(cwd, layouts, PatternOnly));
    }

    [Fact]
    public void Classify_PartialPrefixMatch_DoesNotConfuseSimilarWorktreeNames()
    {
        // Edge case: `foo` and `foobar` share a prefix. Without a trailing separator
        // in the StartsWith comparison, `foo\layouts` would falsely match `foobar`.
        // Verify the trailing-separator logic handles this correctly.
        string cwd = MakePath("repo", ".claude", "worktrees", "foo", "src");
        string layouts = MakePath("repo", ".claude", "worktrees", "foobar", "layouts");

        Assert.Equal(
            WorktreePathClassification.MismatchedWorktree,
            WorktreePathGuard.Classify(cwd, layouts, PatternOnly));
    }

    // ── Classify: edge cases ─────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "/some/layouts")]
    [InlineData("", "/some/layouts")]
    [InlineData("/some/cwd", null)]
    [InlineData("/some/cwd", "")]
    public void Classify_NullOrEmptyInput_ReturnsNotInWorktree(string? cwd, string? layouts)
    {
        // Defensive: the guard does not throw on bad input. Empty/null inputs map
        // to NotInWorktree so the caller's other validation can take over.
        Assert.Equal(
            WorktreePathClassification.NotInWorktree,
            WorktreePathGuard.Classify(cwd!, layouts!));
    }

    // ── Authorize: gate behavior ─────────────────────────────────────────────

    [Fact]
    public void Authorize_NotInWorktree_ReturnsTrueAndEmitsNoBanner()
    {
        CapturingLogger logger = new();
        WorktreePathGuard guard = new(logger, PatternOnly);
        string cwd = MakePath("repo", "src");
        string layouts = MakePath("repo", "layouts");

        bool allowed = guard.Authorize(cwd, layouts, allowCrossWorktreeSync: false);

        Assert.True(allowed);
        Assert.Empty(logger.WarningEntries);
        Assert.Empty(logger.CriticalEntries);
    }

    [Fact]
    public void Authorize_MatchedWorktree_ReturnsTrueAndEmitsNoBanner()
    {
        CapturingLogger logger = new();
        WorktreePathGuard guard = new(logger, PatternOnly);
        string cwd = MakePath("repo", ".claude", "worktrees", "foo", "src");
        string layouts = MakePath("repo", ".claude", "worktrees", "foo", "layouts");

        bool allowed = guard.Authorize(cwd, layouts, allowCrossWorktreeSync: false);

        Assert.True(allowed);
        Assert.Empty(logger.WarningEntries);
        Assert.Empty(logger.CriticalEntries);
    }

    [Fact]
    public void Authorize_Mismatched_NoFlag_ReturnsFalseAndEmitsRefusalBanner()
    {
        CapturingLogger logger = new();
        WorktreePathGuard guard = new(logger, PatternOnly);
        string cwd = MakePath("repo", ".claude", "worktrees", "foo", "src", "feature");
        string layouts = MakePath("repo", "layouts");

        bool allowed = guard.Authorize(cwd, layouts, allowCrossWorktreeSync: false);

        Assert.False(allowed);

        // Refusal banner must surface: the failure mode, the opt-in flag, the
        // mismatched paths, the suggested correction, and the issue link.
        string joined = string.Join("\n", logger.CriticalEntries);
        Assert.Contains("WORKTREE PATH MISMATCH", joined);
        Assert.Contains("--allow-cross-worktree-sync", joined);
        Assert.Contains("#520", joined);
        Assert.Contains(Path.GetFullPath(cwd), joined);
        Assert.Contains(Path.GetFullPath(layouts), joined);
        // Suggested-path suggestion should point at the worktree's own layouts/.
        string expectedSuggestion = Path.Combine(
            MakePath("repo", ".claude", "worktrees", "foo"),
            "layouts");
        Assert.Contains(expectedSuggestion, joined);
    }

    [Fact]
    public void Authorize_Mismatched_WithFlag_ReturnsTrueAndEmitsWarningBanner()
    {
        CapturingLogger logger = new();
        WorktreePathGuard guard = new(logger, PatternOnly);
        string cwd = MakePath("repo", ".claude", "worktrees", "foo", "src");
        string layouts = MakePath("repo", "layouts");

        bool allowed = guard.Authorize(cwd, layouts, allowCrossWorktreeSync: true);

        Assert.True(allowed);

        // Opt-in path: WARN-level banner with the abnormal mode surfaced. No
        // critical banner.
        string joined = string.Join("\n", logger.WarningEntries);
        Assert.Contains("--allow-cross-worktree-sync", joined);
        Assert.Contains(Path.GetFullPath(cwd), joined);
        Assert.Contains(Path.GetFullPath(layouts), joined);
        Assert.Empty(logger.CriticalEntries);
    }

    // ── ParseRevParseOutput: reading the git probe's answer ──────────────────

    [Fact]
    public void ParseRevParseOutput_WorktreeReachedThroughALink_KeepsTheCallersSpelling()
    {
        // git canonicalizes --show-toplevel: reached through a symlink, a junction, or a
        // substituted drive, it names the link's target. The layouts path keeps the
        // caller's spelling, so the root is rebuilt from the caller's own path with
        // --show-cdup; taken from --show-toplevel, a worktree reached through a link would
        // refuse its own layouts/.
        string callerRoot = MakePath("link-to-feature");
        string gitRoot = GitStyle(MakePath("worktrees", "app", "feature"));
        string commonDir = GitStyle(MakePath("checkouts", "app", ".git"));
        string output = $"{gitRoot}\n{commonDir}/worktrees/feature\n{commonDir}\n../../\n";

        WorktreePathGuard.GitCheckout? checkout = WorktreePathGuard.ParseRevParseOutput(
            output,
            Path.Combine(callerRoot, "src", "feature"));

        Assert.NotNull(checkout);
        Assert.True(checkout.IsLinkedWorktree);
        Assert.Equal(callerRoot, checkout.Root);
    }

    [Theory]
    // git older than 2.31 does not know --path-format and echoes it back as a first line.
    [InlineData("--path-format=absolute\n{repo}\n{repo}/.git\n{repo}/.git\n\n")]
    // Relative git dirs instead of the absolute ones asked for.
    [InlineData("{repo}\n.git\n.git\n\n")]
    // Too few lines.
    [InlineData("{repo}\n")]
    [InlineData("")]
    // A --show-cdup that is not a straight walk up.
    [InlineData("{repo}\n{repo}/.git\n{repo}/.git\nsrc/\n")]
    public void ParseRevParseOutput_UnrecognizedOutput_ReturnsNull(string template)
    {
        // null sends the guard to the path-pattern fallback rather than acting on a misread.
        string output = template.Replace("{repo}", GitStyle(MakePath("repo")));

        Assert.Null(WorktreePathGuard.ParseRevParseOutput(output, MakePath("repo", "src")));
    }

    // ── ProbeGitCheckout: saying so when git cannot be asked ─────────────────

    [Fact]
    public void ProbeGitCheckout_GitNotFound_ReportsWhyAndReturnsNull()
    {
        // The fallback cannot see a worktree outside the repository, so a run that could not
        // ask git must say so; Authorize turns this reason into its warning. The finder is
        // injected so the test never touches the process-wide PATH other tests rely on.
        string? reason = null;

        WorktreePathGuard.GitCheckout? checkout = WorktreePathGuard.ProbeGitCheckout(
            MakePath("repo"),
            onGitUnavailable: r => reason = r,
            findGit: () => null);

        Assert.Null(checkout);
        Assert.Equal("git was not found on PATH", reason);
    }

    /// <summary>A path the way git prints it: forward slashes on every platform.</summary>
    private static string GitStyle(string path) => path.Replace('\\', '/');

    // ── Test doubles ─────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="ILogger{T}"/> that records formatted messages for WARN
    /// and Critical levels — mirrors the same shape used by
    /// <see cref="ProductionTargetGuardTests"/> so the two guards' banner
    /// assertions stay symmetric. Also used by <see cref="WorktreePathGuardGitTests"/>.
    /// </summary>
    internal sealed class CapturingLogger : ILogger<WorktreePathGuard>
    {
        public List<string> WarningEntries { get; } = [];
        public List<string> CriticalEntries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullLogger.Instance.BeginScope(state)!;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            if (logLevel == LogLevel.Warning)
                WarningEntries.Add(message);
            else if (logLevel == LogLevel.Critical)
                CriticalEntries.Add(message);
        }
    }
}
