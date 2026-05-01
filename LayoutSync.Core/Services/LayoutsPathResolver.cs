namespace LayoutSync.Services;

/// <summary>
/// Resolves a <c>layouts/</c> directory by walking up the directory tree from a
/// starting directory. Used by the LayoutSync CLI when <c>--layouts-path</c> is
/// omitted: instead of failing, the tool auto-discovers the nearest <c>layouts/</c>
/// ancestor of the current working directory and uses it.
///
/// Motivation: prior to issue #520, omitting <c>--layouts-path</c> failed outright
/// and every example command in <c>CLAUDE.md</c> hardcoded the main-repo path. When
/// a session was running in a worktree, that hardcoded path silently synced the
/// MAIN repo's stale layout content to RavenDB instead of the worktree's edits —
/// exit code 0, a "Replaced: &lt;section&gt;" log line, but the wrong content went
/// to the database. Auto-resolution makes the common case (run LayoutSync from
/// inside the directory you are editing) implicitly correct.
///
/// The companion <see cref="WorktreePathGuard"/> handles the inverse failure mode:
/// when an explicit <c>--layouts-path</c> is passed but points outside the current
/// worktree.
///
/// Pure function — no logger dependency, no caching, no side effects beyond the
/// <see cref="System.IO.Directory.Exists(string)"/> probes performed during the walk.
/// </summary>
public sealed class LayoutsPathResolver
{
    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> looking for a <c>layouts/</c>
    /// subdirectory. Returns the absolute path of the first <c>layouts/</c> found,
    /// or <c>null</c> if walking reaches the filesystem root without finding one.
    /// </summary>
    /// <param name="startDirectory">Directory to begin the walk from. Typically the
    /// process's current working directory at startup.</param>
    /// <returns>Absolute path of the nearest <c>layouts/</c> ancestor, or <c>null</c>
    /// when no ancestor contains a <c>layouts/</c> subdirectory.</returns>
    public static string? Resolve(string startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
            return null;

        // Resolve to an absolute path so the loop terminates predictably at the
        // filesystem root regardless of the caller's CWD.
        string current = Path.GetFullPath(startDirectory);

        while (true)
        {
            string candidate = Path.Combine(current, "layouts");
            if (Directory.Exists(candidate))
                return candidate;

            string? parent = Path.GetDirectoryName(current);
            if (parent == null || string.Equals(parent, current, StringComparison.Ordinal))
            {
                // Path.GetDirectoryName returns null at the drive root on Windows
                // and the empty string at the filesystem root on Unix. Either way,
                // a fixed-point on `current` means we have walked past the top.
                return null;
            }

            current = parent;
        }
    }
}
