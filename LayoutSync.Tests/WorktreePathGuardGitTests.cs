using System.Diagnostics;
using LayoutSync.Services;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Covers how <see cref="WorktreePathGuard"/> recognizes a worktree by asking git, against
/// real temporary repositories (<see cref="GitWorktreeRepositories"/>):
///
/// <list type="bullet">
///   <item><description>a linked worktree OUTSIDE its primary checkout, the layout w31rd.com's
///     worktrees moved to (<c>Worktrees/&lt;repo&gt;/&lt;name&gt;</c>), which the
///     <c>.claude/worktrees</c> path pattern cannot see;</description></item>
///   <item><description>the primary checkout itself, which stays unguarded;</description></item>
///   <item><description>a directory in no repository, where the path pattern still
///     decides;</description></item>
///   <item><description>a legacy linked worktree inside the repository, at
///     <c>.claude/worktrees/&lt;name&gt;</c>.</description></item>
/// </list>
///
/// Every test goes through the guard's default resolver, so each one runs the real git
/// probe. Skipped only when git is not on PATH.
/// </summary>
public sealed class WorktreePathGuardGitTests(GitWorktreeRepositories repositories)
    : IClassFixture<GitWorktreeRepositories>
{
    // ── A linked worktree outside its primary checkout ───────────────────────

    [GitFact]
    public void Classify_ExternalWorktree_LayoutsInPrimary_ReturnsMismatched()
    {
        // The #520 shape from a worktree the path pattern cannot see: no ancestor is named
        // .claude/worktrees, so before git detection this classified as NotInWorktree and
        // the primary's layouts were synced without a word.
        string primaryLayouts = Path.Combine(repositories.Primary, "layouts");

        Assert.Equal(
            WorktreePathClassification.MismatchedWorktree,
            WorktreePathGuard.Classify(repositories.ExternalWorktree, primaryLayouts));
        Assert.Equal(
            WorktreePathClassification.MismatchedWorktree,
            WorktreePathGuard.Classify(repositories.ExternalWorktreeSubdirectory, primaryLayouts));
    }

    [GitFact]
    public void Classify_ExternalWorktree_OwnLayouts_ReturnsMatched()
    {
        string ownLayouts = Path.Combine(repositories.ExternalWorktree, "layouts");

        Assert.Equal(
            WorktreePathClassification.MatchedWorktree,
            WorktreePathGuard.Classify(repositories.ExternalWorktree, ownLayouts));
        Assert.Equal(
            WorktreePathClassification.MatchedWorktree,
            WorktreePathGuard.Classify(repositories.ExternalWorktreeSubdirectory, ownLayouts));
    }

    [GitFact]
    public void Authorize_ExternalWorktree_RefusalBannerNamesThatWorktree()
    {
        // The banner's "Worktree root" and "Did you mean" lines use the root the decision
        // was made against, so they name the external worktree rather than "(none)".
        string worktree = repositories.ExternalWorktree;
        WorktreePathGuardTests.CapturingLogger logger = new();
        WorktreePathGuard guard = new(logger);

        bool allowed = guard.Authorize(
            worktree,
            Path.Combine(repositories.Primary, "layouts"),
            allowCrossWorktreeSync: false);

        Assert.False(allowed);
        Assert.Contains(
            logger.CriticalEntries,
            entry => entry.StartsWith("Worktree root:", StringComparison.Ordinal)
                && entry.EndsWith(worktree, StringComparison.Ordinal));
        Assert.Contains(
            logger.CriticalEntries,
            entry => entry.StartsWith("Did you mean:", StringComparison.Ordinal)
                && entry.Contains(Path.Combine(worktree, "layouts"), StringComparison.Ordinal));
    }

    // ── The primary checkout ─────────────────────────────────────────────────

    [GitFact]
    public void Classify_PrimaryCheckout_ReturnsNotInWorktree()
    {
        // Unchanged semantics: git reports a primary checkout (its git dir IS the common
        // dir), and a primary checkout is not guarded — even one that has linked worktrees.
        WorktreePathGuard.GitCheckout? checkout = WorktreePathGuard.ProbeGitCheckout(repositories.Primary);

        Assert.NotNull(checkout);
        Assert.False(checkout.IsLinkedWorktree);
        Assert.Equal(
            WorktreePathClassification.NotInWorktree,
            WorktreePathGuard.Classify(repositories.Primary, Path.Combine(repositories.Primary, "layouts")));
    }

    // ── No repository: the path-pattern fallback ─────────────────────────────

    [GitFact]
    public void Classify_OutsideAnyRepository_FallsBackToThePathPattern()
    {
        // Nothing here is in a repository, so git reports no worktree ("not a git
        // repository", a non-zero exit) and the .claude/worktrees path pattern decides,
        // giving the answers the guard gave before it asked git.
        string worktreeRoot = repositories.WorktreeShapedDirectory;
        string cwd = Path.Combine(worktreeRoot, "src");
        string enclosingLayouts = Path.Combine(repositories.Root, "no-repo", "layouts");

        Assert.Equal(
            WorktreePathClassification.MismatchedWorktree,
            WorktreePathGuard.Classify(cwd, enclosingLayouts));
        Assert.Equal(
            WorktreePathClassification.MatchedWorktree,
            WorktreePathGuard.Classify(cwd, Path.Combine(worktreeRoot, "layouts")));
    }

    // ── A legacy linked worktree inside the repository ───────────────────────

    [GitFact]
    public void Classify_LegacyInRepoWorktree_IsRecognizedByGit()
    {
        string worktree = repositories.LegacyWorktree;

        // git itself reports the linked worktree; the path pattern is not what found it.
        WorktreePathGuard.GitCheckout? checkout = WorktreePathGuard.ProbeGitCheckout(worktree);

        Assert.NotNull(checkout);
        Assert.True(checkout.IsLinkedWorktree);
        Assert.Equal(worktree, checkout.Root);
        Assert.Equal(
            WorktreePathClassification.MismatchedWorktree,
            WorktreePathGuard.Classify(worktree, Path.Combine(repositories.Primary, "layouts")));
        Assert.Equal(
            WorktreePathClassification.MatchedWorktree,
            WorktreePathGuard.Classify(worktree, Path.Combine(worktree, "layouts")));
    }

    // ── The git-unavailable warning stays quiet when git answered ────────────

    [GitFact]
    public void ProbeGitCheckout_GitAnswered_ReportsNothing()
    {
        // "Not a git repository" is git answering, not git being unavailable: running
        // LayoutSync outside a repository is ordinary and must not produce the warning.
        // Neither may an ordinary answer inside a repository.
        List<string> reasons = [];

        Assert.Null(WorktreePathGuard.ProbeGitCheckout(repositories.WorktreeShapedDirectory, reasons.Add));
        Assert.NotNull(WorktreePathGuard.ProbeGitCheckout(repositories.ExternalWorktree, reasons.Add));

        Assert.Empty(reasons);
    }
}

/// <summary>
/// The repositories <see cref="WorktreePathGuardGitTests"/> classifies against, built once
/// for the class: creating them takes several git runs, and every test only reads them.
/// Laid out under a fresh temp directory the way w31rd.com's are:
///
/// <list type="bullet">
///   <item><description><c>checkouts/app</c> — the primary checkout; its one commit tracks
///     <c>layouts/</c>, so every worktree has its own copy.</description></item>
///   <item><description><c>worktrees/app/feature</c> — a linked worktree beside the primary
///     checkout, the layout w31rd.com uses now.</description></item>
///   <item><description><c>checkouts/app/.claude/worktrees/legacy</c> — a linked worktree
///     inside the primary checkout, the layout it used before.</description></item>
///   <item><description><c>no-repo/.claude/worktrees/feature</c> — a directory shaped like a
///     worktree but in no repository.</description></item>
/// </list>
/// </summary>
public sealed class GitWorktreeRepositories : IDisposable
{
    private static readonly string? GitExecutable = WorktreePathGuard.FindGitOnPath();

    public GitWorktreeRepositories()
    {
        Root = Path.Combine(Path.GetTempPath(), $"worktree-guard-git-tests-{Guid.NewGuid():N}");
        Primary = Path.Combine(Root, "checkouts", "app");
        ExternalWorktree = Path.Combine(Root, "worktrees", "app", "feature");
        ExternalWorktreeSubdirectory = Path.Combine(ExternalWorktree, "src", "feature");
        LegacyWorktree = Path.Combine(Primary, ".claude", "worktrees", "legacy");
        WorktreeShapedDirectory = Path.Combine(Root, "no-repo", ".claude", "worktrees", "feature");

        // Without git every test is skipped; building nothing keeps those skips from being
        // reported as a fixture failure instead.
        if (GitExecutable == null)
            return;

        Directory.CreateDirectory(Path.Combine(Primary, "layouts"));
        File.WriteAllText(Path.Combine(Primary, "layouts", ".gitkeep"), "");
        Git(Primary, "init", "--quiet");
        Git(Primary, "add", "layouts");
        Git(Primary, "commit", "--quiet", "--no-verify", "-m", "Initial commit");
        Git(Primary, "worktree", "add", "--quiet", "--detach", ExternalWorktree);
        Git(Primary, "worktree", "add", "--quiet", "--detach", LegacyWorktree);

        Directory.CreateDirectory(ExternalWorktreeSubdirectory);
        Directory.CreateDirectory(Path.Combine(WorktreeShapedDirectory, "src"));
    }

    /// <summary>The temp directory holding everything below.</summary>
    public string Root { get; }

    /// <summary>The primary checkout.</summary>
    public string Primary { get; }

    /// <summary>A linked worktree outside the primary checkout.</summary>
    public string ExternalWorktree { get; }

    /// <summary>An untracked directory two levels inside <see cref="ExternalWorktree"/>.</summary>
    public string ExternalWorktreeSubdirectory { get; }

    /// <summary>A linked worktree at <c>.claude/worktrees/legacy</c> inside the primary checkout.</summary>
    public string LegacyWorktree { get; }

    /// <summary>A <c>.claude/worktrees/&lt;name&gt;</c> directory in no repository.</summary>
    public string WorktreeShapedDirectory { get; }

    /// <summary>
    /// Deletes the temp tree. git writes its object files read-only, which
    /// <see cref="Directory.Delete(string, bool)"/> refuses on Windows, so the attribute is
    /// cleared first. Best effort: a leftover temp directory is harmless, while failing the
    /// run over it would report a defect the code under test does not have.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Runs one git command to build the repositories and throws if it fails. Isolated from
    /// the machine it runs on: every inherited <c>GIT_*</c> variable is dropped (a test run
    /// started from a git hook inherits <c>GIT_DIR</c>, which would aim these commands at the
    /// developer's own repository), and system and global git config are ignored (a signing
    /// or hooks setting there would otherwise reach into the temp repositories). The
    /// identity is passed with <c>-c</c> because a CI runner has none configured.
    /// </summary>
    private void Git(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new(GitExecutable!)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        List<string> inheritedGitVariables =
        [
            .. startInfo.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)),
        ];
        foreach (string name in inheritedGitVariables)
            startInfo.Environment.Remove(name);
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        // A path with no file behind it: git treats a missing global config as empty.
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Root, "no-global-config");

        string[] fullArguments =
        [
            "-c", "user.name=LayoutSync Tests",
            "-c", "user.email=layoutsync-tests@example.invalid",
            "-c", "commit.gpgsign=false",
            .. arguments,
        ];
        foreach (string argument in fullArguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = new() { StartInfo = startInfo };
        process.Start();
        _ = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();

        string command = $"git {string.Join(' ', arguments)}";
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            throw new InvalidOperationException($"{command} did not finish within 60 seconds.");
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{command} failed with exit code {process.ExitCode}: {errors.GetAwaiter().GetResult()}");
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> skipped when git is not on PATH, the one condition under
/// which git detection cannot be exercised. It uses the guard's own lookup, so a skip means
/// exactly "here the guard would fall back to the path pattern".
/// </summary>
internal sealed class GitFactAttribute : FactAttribute
{
    public GitFactAttribute()
    {
        if (WorktreePathGuard.FindGitOnPath() == null)
            Skip = "git is not on PATH, so there is no git to detect a worktree with.";
    }
}
