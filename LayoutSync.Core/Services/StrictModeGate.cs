namespace LayoutSync.Services;

/// <summary>
/// The <c>--strict</c> decision, extracted from the CLI entry point so it is testable: every
/// detection-only offense a sync accumulated, as the error lines to print. Any offense fails the
/// run with <see cref="ExitCode"/>. Detection-only throughout — nothing here mutates state.
/// </summary>
public static class StrictModeGate
{
    /// <summary>Exit code for a run that completed with <c>--strict</c> offenses.</summary>
    public const int ExitCode = 2;

    /// <summary>
    /// Collects the offenses: duplicate identifiers (<see cref="RavenDbService.DuplicateEntityIdentifierCount"/>),
    /// document collisions (<see cref="DocumentSyncService.DocumentCollisionCount"/>, issue #28),
    /// pinned id mismatches (<see cref="DocumentSyncService.PinnedIdMismatchCount"/>, issue #46), and
    /// every seed validator's warnings — looping the validators means a future one joins the gate
    /// automatically (issue #7). Empty when the run is clean.
    /// </summary>
    public static IReadOnlyList<string> Offenses(
        int duplicateIdentifierCount,
        int documentCollisionCount,
        int pinnedIdMismatchCount,
        IEnumerable<ISeedValidator> validators)
    {
        List<string> offenses = [];

        if (duplicateIdentifierCount > 0)
        {
            offenses.Add(
                $"--strict: {duplicateIdentifierCount} duplicate entity identifier(s) detected during sync. See WARN lines above for document IDs. LayoutSync does not auto-delete entity duplicates; purge manually via RavenDB.");
        }

        if (documentCollisionCount > 0)
        {
            offenses.Add(
                $"--strict: {documentCollisionCount} document collision(s): two or more files resolve to the same stored document. See the 'Document collision' WARN lines above for which files were not synced; give each file its own identifier, or delete the copies.");
        }

        if (pinnedIdMismatchCount > 0)
        {
            offenses.Add(
                $"--strict: {pinnedIdMismatchCount} pinned id mismatch(es): a file pins an @metadata.@id that is not the id its document is stored under, so no document has the pinned id. See the 'Pinned id mismatch' WARN lines above for each file, its pinned id and the stored id.");
        }

        foreach (ISeedValidator validator in validators)
        {
            if (validator.WarningCount > 0)
            {
                offenses.Add($"--strict: {validator.WarningCount} {validator.StrictWarningDetail}");
            }
        }

        return offenses;
    }

    /// <summary>The process exit code for a <c>--strict</c> run with these offenses.</summary>
    public static int ExitCodeFor(IReadOnlyCollection<string> offenses) =>
        offenses.Count > 0 ? ExitCode : 0;
}
