using LayoutSync.Models;

namespace LayoutSync.Services;

/// <summary>
/// The failed-documents decision, extracted from the CLI entry point so it is testable. A sync that
/// could not read a layout file, or could not write its document, did not do its job, so the run
/// fails with <see cref="ExitCode"/> whether or not <c>--strict</c> is set: <c>--strict</c>
/// escalates validator warnings, and a failed document is not a warning (issue #36).
/// </summary>
public static class SyncFailureGate
{
    /// <summary>
    /// Exit code for a sync in which one or more documents failed. Distinct from every other code
    /// the CLI returns: downstream CI documents 0–4 and reads 2 as "every write succeeded".
    /// </summary>
    public const int ExitCode = 5;

    /// <summary>How many failed documents <see cref="Describe"/> names before it counts the rest.</summary>
    internal const int MaxNamedFailures = 10;

    /// <summary>
    /// The error line explaining a failed run: the count and the failed files, so the reason for a
    /// red CI check sits at the bottom of the log instead of among per-file stack traces. Null when
    /// nothing failed.
    /// </summary>
    public static string? Describe(SyncBatchResult batch)
    {
        List<string> names = [.. batch.Results.Where(r => !r.Success).Select(r => NameOf(r.Document))];
        if (names.Count == 0)
            return null;

        string named = string.Join(", ", names.Take(MaxNamedFailures));
        string more = names.Count > MaxNamedFailures ? $" and {names.Count - MaxNamedFailures} more" : "";
        return $"{names.Count} document(s) failed to sync, so the run exits {ExitCode}: {named}{more}. See the errors above for each cause.";
    }

    /// <summary>
    /// The process exit code once failures are known. <paramref name="strictExitCode"/> is the
    /// <see cref="StrictModeGate"/> decision (0 or 2); a failure wins over it, because exit 2 tells
    /// CI that every write succeeded.
    /// </summary>
    public static int ExitCodeFor(int failedCount, int strictExitCode) =>
        failedCount > 0 ? ExitCode : strictExitCode;

    /// <summary>
    /// The best name a failed result carries: its path under layouts/, else its absolute path (a
    /// file that could not be read has no relative path), else its identifier (a failed delete
    /// names no file).
    /// </summary>
    private static string NameOf(SyncDocument document)
    {
        string?[] candidates = [document.RelativePath, document.FilePath, document.Identifier];
        return candidates.FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? "(unnamed document)";
    }
}
