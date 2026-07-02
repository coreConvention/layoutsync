namespace LayoutSync.Services;

/// <summary>
/// Validates <c>--exclude-collection</c> / <c>--exclude-layout</c> values before a run
/// starts. Pure + static so Program.cs can gate on it (exit 1 on any error) and tests can
/// drive it without DI. See issue #9.
///
/// Collections are validated against the <see cref="CollectionFolders"/> registry — the
/// closed set of folder names that can ever be synced — NOT against directories present on
/// disk. Tree-presence would false-reject legitimate flags whenever the folder happens to
/// be absent: <c>themes</c> existing only as the platform sibling catalogue, an empty
/// layouts tree, or a <c>--layout</c>-scoped run. Registry membership still catches every
/// typo, which is the point of validating at all.
///
/// Layouts have no registry — they ARE directory names — so they validate against the
/// actual top-level layout directories, compared Ordinal: Linux CI filesystems are
/// case-sensitive even though Windows dev boxes aren't, and a case-insensitive match here
/// would accept a flag that silently excludes nothing in CI.
/// </summary>
public static class ExclusionValidator
{
    /// <summary>
    /// Validates both exclusion lists. Returns one human-readable error per offending
    /// value (empty list = valid). Unknown names carry a "Did you mean" suggestion when a
    /// near match exists (reuses <see cref="ManifestSectionValidator.NearestMatches"/>).
    /// </summary>
    /// <param name="layoutsPath">Resolved layouts root (already verified to exist).</param>
    /// <param name="excludeCollections">Values of <c>--exclude-collection</c>.</param>
    /// <param name="excludeLayouts">Values of <c>--exclude-layout</c>.</param>
    /// <param name="scopedLayout">Value of <c>--layout</c>, if set — excluding the very
    /// layout a run is scoped to is a contradiction and is rejected.</param>
    public static IReadOnlyList<string> Validate(
        string layoutsPath,
        IReadOnlyCollection<string> excludeCollections,
        IReadOnlyCollection<string> excludeLayouts,
        string? scopedLayout = null)
    {
        List<string> errors = [];

        foreach (string value in excludeCollections)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add("--exclude-collection: value is empty.");
                continue;
            }

            if (!CollectionFolders.ByFolder.ContainsKey(value))
            {
                errors.Add(FormatUnknownName("--exclude-collection", value, CollectionFolders.FolderNames));
            }
        }

        if (excludeLayouts.Count > 0)
        {
            // Enumerate + compare rather than Directory.Exists-probe: probing is
            // case-insensitive on Windows and would let a case typo through that
            // Linux CI then fails to match.
            string[] layoutDirNames = Directory.Exists(layoutsPath)
                ? [.. Directory.GetDirectories(layoutsPath).Select(dir => Path.GetFileName(dir)!)]
                : [];
            HashSet<string> knownLayouts = new(layoutDirNames, StringComparer.Ordinal);

            foreach (string value in excludeLayouts)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    errors.Add("--exclude-layout: value is empty.");
                    continue;
                }

                if (!string.IsNullOrEmpty(scopedLayout)
                    && string.Equals(value, scopedLayout, StringComparison.Ordinal))
                {
                    errors.Add(
                        $"--exclude-layout '{value}' contradicts --layout '{scopedLayout}' — a run scoped to a layout cannot also exclude it.");
                    continue;
                }

                if (!knownLayouts.Contains(value))
                {
                    errors.Add(FormatUnknownName("--exclude-layout", value, layoutDirNames));
                }
            }
        }

        return errors;
    }

    private static string FormatUnknownName(string flag, string value, IReadOnlyCollection<string> known)
    {
        IReadOnlyList<string> suggestions = ManifestSectionValidator.NearestMatches(value, known, max: 3);
        string message = $"{flag}: unknown name '{value}'.";
        return suggestions.Count == 0
            ? message
            : $"{message} Did you mean: {string.Join(", ", suggestions)}?";
    }
}
