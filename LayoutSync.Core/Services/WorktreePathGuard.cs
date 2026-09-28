using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LayoutSync.Services;

/// <summary>
/// Classification of how the current working directory relates to the resolved
/// <c>--layouts-path</c> with respect to git worktree boundaries. What counts as a
/// worktree is described on <see cref="WorktreePathGuard"/>.
///
/// <list type="bullet">
///   <item><description><see cref="NotInWorktree"/> — CWD is not inside a worktree
///     (typically it is in a primary checkout, or in no repository at all). Any
///     layouts-path is allowed; the worktree-mismatch failure mode is
///     impossible.</description></item>
///   <item><description><see cref="MatchedWorktree"/> — CWD is inside a worktree AND
///     the layouts-path is rooted under the same worktree. Safe — the layouts being
///     synced belong to this checkout.</description></item>
///   <item><description><see cref="MismatchedWorktree"/> — CWD is inside a worktree
///     but the layouts-path is OUTSIDE that worktree (likely the main repo or a
///     sibling worktree). The silent-failure pattern from issue #520 — refused unless
///     the operator opts in via <c>--allow-cross-worktree-sync</c>.</description></item>
/// </list>
/// </summary>
public enum WorktreePathClassification
{
    NotInWorktree,
    MatchedWorktree,
    MismatchedWorktree,
}

/// <summary>
/// Refuses LayoutSync runs when the current working directory is inside a git worktree
/// AND the resolved <c>--layouts-path</c> points OUTSIDE that worktree.
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
/// Worktree detection asks git (<see cref="ResolveWorktreeRoot"/>): a directory is in a
/// LINKED worktree exactly when <c>git rev-parse --git-dir</c> differs from
/// <c>--git-common-dir</c>, wherever that worktree lives. The guard used to recognize a
/// worktree by its path alone — an ancestor whose parent directory ends in
/// <c>.claude/worktrees</c> — which held only while w31rd.com kept its worktrees inside the
/// repository. Once they moved out (to a <c>Worktrees/&lt;repo&gt;/&lt;name&gt;</c>
/// directory beside the checkouts), every worktree classified as
/// <see cref="WorktreePathClassification.NotInWorktree"/> and the guard failed open: an
/// explicit <c>--layouts-path</c> at the primary checkout silently synced the primary's
/// layouts — the #520 failure again.
///
/// That path pattern is still the fallback whenever git does not report a linked worktree
/// (git is missing, fails, or times out, or the directory is in a primary checkout or in no
/// repository), so a directory the pattern recognized stays guarded where git cannot help.
/// When git could not be asked at all (not on PATH, not answering, not startable),
/// <see cref="Authorize"/> logs a warning: the pattern alone cannot see a worktree outside
/// the repository, so that run is guarded the old way and should say so.
///
/// Tenant-agnostic: no layout identifier, tenant slug, or repo-specific path
/// appears in the classification logic.
/// </summary>
/// <param name="logger">Receives the classification line and the refusal/opt-in banners.</param>
/// <param name="worktreeRootResolver">Maps an absolute directory to the root of the worktree
/// containing it, or <c>null</c> when it is not in one. Defaults to
/// <see cref="ResolveWorktreeRoot"/>, which here also logs a warning through
/// <paramref name="logger"/> when git cannot be asked; tests pass
/// <see cref="FindWorktreeRootByPathPattern"/> to classify without running git.</param>
public sealed class WorktreePathGuard(
    ILogger<WorktreePathGuard> logger,
    Func<string, string?>? worktreeRootResolver = null)
{
    private readonly ILogger<WorktreePathGuard> _logger = logger;

    // Null unless a caller (a test) injects one. Authorize then uses the git-first resolver
    // wired to this instance's logger (ResolveWorktreeRootWithWarning); a field initializer
    // cannot reference an instance method, so the choice is made at the call.
    private readonly Func<string, string?>? _injectedResolver = worktreeRootResolver;

    /// <summary>
    /// Suffix segments (forward-slash separated) that identify the directory whose
    /// children are git worktree roots, for the path-pattern fallback. Comparison is
    /// performed after normalizing both <see cref="Path.DirectorySeparatorChar"/> and
    /// <see cref="Path.AltDirectorySeparatorChar"/> to forward slashes, so the
    /// constant is identical on Windows and Unix.
    /// </summary>
    private const string WorktreesParentSegment = ".claude/worktrees";

    /// <summary>
    /// How long the git probe may run before it is abandoned and the path pattern decides.
    /// <c>git rev-parse</c> answers in milliseconds; the bound exists so that a git stalled
    /// on a slow or unreachable filesystem can never hang the run waiting for it.
    /// </summary>
    private static readonly TimeSpan GitProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// What git reported about the checkout containing a directory.
    /// </summary>
    /// <param name="Root">The checkout's root directory, spelled the way the caller spelled the
    /// directory it asked about (see <see cref="ParseRevParseOutput"/>).</param>
    /// <param name="IsLinkedWorktree"><c>true</c> for a linked worktree (one made by
    /// <c>git worktree add</c>); <c>false</c> for a primary checkout.</param>
    internal sealed record GitCheckout(string Root, bool IsLinkedWorktree);

    /// <summary>
    /// Classify <paramref name="currentDirectory"/> + <paramref name="layoutsPath"/>.
    /// Both arguments are normalized via <see cref="Path.GetFullPath(string)"/> before
    /// comparison, so relative paths are tolerated. The comparison is a pure string
    /// operation; the only side effect is the <paramref name="worktreeRootResolver"/> call,
    /// which by default runs one short <c>git rev-parse</c> (see
    /// <see cref="ResolveWorktreeRoot"/>).
    /// </summary>
    /// <param name="currentDirectory">The directory LayoutSync was started in.</param>
    /// <param name="layoutsPath">The resolved <c>--layouts-path</c>.</param>
    /// <param name="worktreeRootResolver">Maps an absolute directory to the root of the
    /// worktree containing it, or <c>null</c>. Defaults to <see cref="ResolveWorktreeRoot"/>.</param>
    public static WorktreePathClassification Classify(
        string currentDirectory,
        string layoutsPath,
        Func<string, string?>? worktreeRootResolver = null)
        => ClassifyWithRoot(currentDirectory, layoutsPath, worktreeRootResolver ?? ResolveWorktreeRoot)
            .Classification;

    /// <summary>
    /// <see cref="Classify"/>, also returning the worktree root it compared against, so the
    /// refusal banner can name that same root instead of resolving it a second time.
    /// </summary>
    private static (WorktreePathClassification Classification, string? WorktreeRoot) ClassifyWithRoot(
        string currentDirectory,
        string layoutsPath,
        Func<string, string?> worktreeRootResolver)
    {
        if (string.IsNullOrWhiteSpace(currentDirectory) || string.IsNullOrWhiteSpace(layoutsPath))
            return (WorktreePathClassification.NotInWorktree, null);

        string normalizedCwd = Path.GetFullPath(currentDirectory);
        string normalizedLayouts = Path.GetFullPath(layoutsPath);

        string? worktreeRoot = worktreeRootResolver(normalizedCwd);
        if (worktreeRoot == null)
            return (WorktreePathClassification.NotInWorktree, null);

        // The layouts-path is "in the same worktree" if it equals or is a descendant
        // of the worktree root. Append the platform separator to prevent
        // partial-prefix false positives — e.g. ".claude/worktrees/foo" must NOT
        // match ".claude/worktrees/foobar".
        string worktreeRootWithSep = EnsureTrailingSeparator(worktreeRoot);
        string layoutsWithSep = EnsureTrailingSeparator(normalizedLayouts);

        bool layoutsInsideWorktree = layoutsWithSep.StartsWith(
            worktreeRootWithSep,
            StringComparison.OrdinalIgnoreCase);

        WorktreePathClassification classification = layoutsInsideWorktree
            ? WorktreePathClassification.MatchedWorktree
            : WorktreePathClassification.MismatchedWorktree;
        return (classification, worktreeRoot);
    }

    /// <summary>
    /// The default worktree-root resolver. Asks git first (<see cref="ProbeGitCheckout"/>):
    /// when git reports a linked worktree, that worktree's root is the answer, wherever it
    /// lives. In every other case — a primary checkout, no repository, or no answer from git
    /// at all — the <c>.claude/worktrees</c> path pattern decides
    /// (<see cref="FindWorktreeRootByPathPattern"/>), so no directory the pattern recognized
    /// before git detection loses its protection.
    /// </summary>
    /// <param name="absolutePath">An absolute directory path, normally the process's CWD.</param>
    /// <returns>The absolute worktree root, or <c>null</c> when the path is not in a worktree.</returns>
    internal static string? ResolveWorktreeRoot(string absolutePath)
        => ResolveWorktreeRootReporting(absolutePath, onGitUnavailable: null);

    /// <summary>
    /// <see cref="ResolveWorktreeRoot"/>, also passing <paramref name="onGitUnavailable"/> to the
    /// git probe so a caller can report why git could not be asked. A separate name rather than
    /// an overload, so <see cref="ResolveWorktreeRoot"/> stays unambiguous as a method group.
    /// </summary>
    internal static string? ResolveWorktreeRootReporting(string absolutePath, Action<string>? onGitUnavailable)
        => ProbeGitCheckout(absolutePath, onGitUnavailable) is { IsLinkedWorktree: true } checkout
            ? checkout.Root
            : FindWorktreeRootByPathPattern(absolutePath);

    /// <summary>
    /// The resolver <see cref="Authorize"/> uses by default: <see cref="ResolveWorktreeRoot"/>,
    /// plus a warning when git could not be asked at all. The fallback that follows cannot see
    /// a worktree outside the repository, so without the warning a missing or stalled git would
    /// silently reopen the #520 failure for exactly those worktrees.
    /// </summary>
    private string? ResolveWorktreeRootWithWarning(string absolutePath)
        => ResolveWorktreeRootReporting(absolutePath, reason => _logger.LogWarning(
            "Could not ask git whether {Directory} is in a linked worktree ({Reason}); using the "
            + ".claude/worktrees path pattern instead, which does not recognize worktrees outside "
            + "the repository.",
            absolutePath,
            reason));

    /// <summary>
    /// Asks git which checkout contains <paramref name="absolutePath"/>, with a single
    /// <c>git rev-parse</c> run bounded by <see cref="GitProbeTimeout"/>. Returns <c>null</c>
    /// whenever git cannot answer — not installed, not a repository, an error, a timeout, or
    /// output this parser does not recognize — so the caller falls back instead of guessing.
    /// </summary>
    /// <param name="absolutePath">The directory to ask about.</param>
    /// <param name="onGitUnavailable">Called with a short reason when git could not be asked
    /// at all: not found on PATH, no answer within <see cref="GitProbeTimeout"/>, or not
    /// startable. NOT called when git answers "not a repository" or any other refusal — that
    /// is git answering, and running outside a repository is ordinary.</param>
    /// <param name="findGit">Locates the git executable; defaults to <see cref="FindGitOnPath"/>.
    /// Lets a test reach the not-found branch without changing the process-wide PATH, which
    /// would race every other test that runs git.</param>
    internal static GitCheckout? ProbeGitCheckout(
        string absolutePath,
        Action<string>? onGitUnavailable = null,
        Func<string?>? findGit = null)
    {
        string? git = (findGit ?? FindGitOnPath)();
        if (git == null)
        {
            onGitUnavailable?.Invoke("git was not found on PATH");
            return null;
        }

        ProcessStartInfo startInfo = new(git)
        {
            RedirectStandardOutput = true,
            // Redirected only to be discarded: "not a git repository" is an expected answer
            // here, not an error to show the operator.
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // git writes paths as UTF-8 on every platform, Windows included.
            StandardOutputEncoding = Encoding.UTF8,
        };

        // --show-toplevel is requested for its failure as much as its output: git refuses it
        // outside a work tree (inside a .git directory, or in a bare repository), which sends
        // such a directory to the fallback instead of letting it pass for a checkout root.
        // --show-cdup locates the root from absolutePath itself (see ParseRevParseOutput).
        string[] arguments =
        [
            "-C", absolutePath,
            "rev-parse", "--path-format=absolute",
            "--show-toplevel", "--git-dir", "--git-common-dir", "--show-cdup",
        ];
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        // git exports GIT_DIR to the hooks it runs, among others. Inherited, these would make
        // git describe THAT repository instead of discovering the one containing absolutePath.
        startInfo.Environment.Remove("GIT_DIR");
        startInfo.Environment.Remove("GIT_WORK_TREE");
        startInfo.Environment.Remove("GIT_COMMON_DIR");

        try
        {
            using Process process = new() { StartInfo = startInfo };
            process.Start();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            // Drained so git can never block on a full stderr pipe.
            _ = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(GitProbeTimeout))
            {
                TryKill(process);
                onGitUnavailable?.Invoke($"git did not answer within {GitProbeTimeout.TotalSeconds:0} seconds");
                return null;
            }

            // The process has exited and closed its end of the pipe, so this read is complete.
            return process.ExitCode == 0
                ? ParseRevParseOutput(output.GetAwaiter().GetResult(), absolutePath)
                : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // git could not be started or read: no different from having no git at all.
            onGitUnavailable?.Invoke("git could not be run");
            return null;
        }
    }

    /// <summary>
    /// Parses the probe's <c>git rev-parse</c> output for <paramref name="absolutePath"/>:
    /// exactly four lines, in option order — <c>--show-toplevel</c>, <c>--git-dir</c>,
    /// <c>--git-common-dir</c>, <c>--show-cdup</c> (empty at the root). Returns <c>null</c>
    /// for anything else — for example from a git older than 2.31, which does not know
    /// <c>--path-format</c> and echoes it back as an extra first line.
    /// </summary>
    internal static GitCheckout? ParseRevParseOutput(string output, string absolutePath)
    {
        string[] lines = (output.EndsWith('\n') ? output[..^1] : output).Split('\n');
        if (lines.Length != 4)
            return null;

        string[] gitPaths = [.. lines[..3].Select(line => line.TrimEnd('\r'))];
        if (!gitPaths.All(Path.IsPathFullyQualified))
            return null;

        // --show-cdup is the way up from absolutePath to the root: empty, or "../" repeated.
        string cdup = lines[3].TrimEnd('\r');
        if (cdup.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment != ".."))
            return null;

        // The root is rebuilt from absolutePath rather than taken from --show-toplevel,
        // because git canonicalizes the toplevel — it resolves symlinks, junctions, and
        // substituted drives (and on macOS turns /var into /private/var) — while the layouts
        // path it is compared with keeps the caller's spelling. Walking up from the caller's
        // own path keeps both in one spelling, so a worktree reached through a link still
        // contains its own layouts/.
        string root = NormalizeDirectory(Path.Combine(absolutePath, cdup));

        // A linked worktree's git dir is <common dir>/worktrees/<id>; in a primary checkout
        // the two are the same directory. Ignoring case is safe on every platform, because
        // the two can never differ by case alone.
        bool isLinkedWorktree = !string.Equals(
            NormalizeDirectory(gitPaths[1]),
            NormalizeDirectory(gitPaths[2]),
            StringComparison.OrdinalIgnoreCase);

        return new GitCheckout(root, isLinkedWorktree);
    }

    /// <summary>
    /// Finds the git executable on <c>PATH</c>, deliberately never in the current directory.
    /// Starting a bare <c>"git"</c> would let Windows (and .NET on Unix) look in the current
    /// directory first — and the current directory is the checkout being classified, so a
    /// <c>git</c> executable committed to a repository would run instead of the real one.
    /// </summary>
    /// <returns>The full path of git, or <c>null</c> when no <c>PATH</c> directory has it.</returns>
    internal static string? FindGitOnPath()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        string fileName = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (string entry in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Windows allows quoted PATH entries. A relative entry such as "." names the
            // current directory, which is exactly what this lookup must not search.
            string directory = entry.Trim('"');
            if (!Path.IsPathFullyQualified(directory))
                continue;

            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Best effort: the probe has already given up on this git process, so a kill that fails
    /// (it exited in the meantime, or cannot be killed) changes nothing about the fallback.
    /// </summary>
    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
        {
        }
    }

    private static string NormalizeDirectory(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// The path-pattern fallback, and before git detection the only way a worktree was
    /// recognized: walks up <paramref name="absolutePath"/> looking for the first ancestor
    /// whose parent directory's name ends with <c>.claude/worktrees</c>. That ancestor IS the
    /// worktree root. Returns <c>null</c> when no ancestor matches — including for every
    /// worktree that lives anywhere else, which is why <see cref="ResolveWorktreeRoot"/> asks
    /// git first.
    /// </summary>
    internal static string? FindWorktreeRootByPathPattern(string absolutePath)
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
        // Resolved once: the refusal banner then names the very root the decision was made
        // against, and the default resolver's git process runs once per run, not twice.
        (WorktreePathClassification classification, string? worktreeRoot) =
            ClassifyWithRoot(currentDirectory, layoutsPath, _injectedResolver ?? ResolveWorktreeRootWithWarning);

        _logger.LogInformation(
            "Worktree path classification: {Classification}",
            classification);

        if (classification != WorktreePathClassification.MismatchedWorktree)
            return true;

        if (!allowCrossWorktreeSync)
        {
            EmitRefusalBanner(currentDirectory, layoutsPath, worktreeRoot);
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

    private void EmitRefusalBanner(string currentDirectory, string layoutsPath, string? worktreeRoot)
    {
        string normalizedCwd = Path.GetFullPath(currentDirectory);
        string normalizedLayouts = Path.GetFullPath(layoutsPath);
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
