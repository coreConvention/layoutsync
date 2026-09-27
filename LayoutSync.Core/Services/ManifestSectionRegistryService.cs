using System.Text.Json;
using System.Text.Json.Nodes;
using LayoutSync.Models;
using Microsoft.Extensions.Logging;

namespace LayoutSync.Services;

/// <summary>
/// Adds, renames and removes entries in a layout manifest's <c>entities.sections</c> registry:
/// the declared set of section identifiers that <see cref="ManifestSectionValidator"/> checks
/// every route patch against. Companion to <see cref="ManifestMutationService"/>, which owns
/// route-level changes. The CLI (<c>manifest add-section</c> / <c>rename-section</c> /
/// <c>remove-section</c>) and the MCP section tools both call this service, so the two
/// surfaces apply identical validation.
///
/// What counts as a reference: section identifiers are named from more places than
/// <c>routeConfigs</c> (<c>routing.notFoundSection</c>, <c>onboarding.structuralSection</c>,
/// <c>authoring.editableSections[]</c>, ...). Rather than hard-code that list and silently miss
/// the next field someone adds, a reference is any string value outside the registry whose
/// nearest enclosing property name contains "section" and whose value equals the identifier.
/// A string that equals the identifier under any other property is never rewritten; it is
/// reported as a warning, so an unconventionally named reference field surfaces instead of
/// dangling silently.
///
/// Writes: the manifest goes through <see cref="LocalFileService"/>, exactly like
/// <c>set-route</c>. A rename also rewrites the section file's own top-level
/// <c>identifier</c> (unless asked not to) by splicing just that JSON token, so the rest of
/// the file stays byte-for-byte identical. A parse/serialize round-trip would rewrite the rest of
/// the file too: whitespace and one-line arrays are reformatted, existing escapes are rewritten,
/// and the final newline changes. A rename
/// lands in both files or in neither: one whose section file cannot be updated is refused
/// before anything is written, and if the section-file write fails after the manifest was
/// written, the manifest's original bytes are put back. Section files are overwritten in
/// place rather than written to a temp file and renamed, because watch mode mishandles
/// rename-style saves (coreConvention/layoutsync#23).
/// </summary>
public class ManifestSectionRegistryService(
    LocalFileService fileService,
    ILogger<ManifestSectionRegistryService> logger)
{
    private readonly LocalFileService _fileService = fileService;
    private readonly ILogger<ManifestSectionRegistryService> _logger = logger;

    /// <summary>The <c>type</c> every existing registry entry uses; the default for new entries.</summary>
    public const string DefaultSectionType = "ui-schema-section";

    /// <summary>
    /// Encodes a renamed identifier with the same encoder <see cref="LocalFileService"/> writes
    /// the manifest with (issue #38), so the section file and the manifest spell it the same way.
    /// </summary>
    private static readonly JsonSerializerOptions IdentifierTokenOptions = new() { Encoder = LiteralJsonEncoder.Instance };

    /// <summary>JSON Pointer of the registry. Its subtree holds declarations, never references.</summary>
    private const string RegistryPointer = "/entities/sections";

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Declares a new section in <c>entities.sections</c>, so route patches may reference it.
    /// Refuses an identifier that is already declared. Warns (without refusing) when the
    /// section file is missing or declares a different identifier, since the entry would then
    /// point at a document the sync never creates.
    /// </summary>
    /// <param name="layoutsPath">Absolute path to the <c>layouts/</c> directory.</param>
    /// <param name="layoutId">The tenant layout, e.g. <c>dirt-life</c>.</param>
    /// <param name="entry">The entry to declare; unset fields take the conventional defaults.</param>
    /// <param name="dryRun">If true, compute and report the change without writing it.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<SectionMutationResult> AddSectionAsync(
        string layoutsPath,
        string layoutId,
        SectionEntryInput entry,
        bool dryRun,
        CancellationToken ct = default)
    {
        // Resolved first so every result, refusals included, names the file it targeted.
        string manifestPath = ManifestPath(layoutsPath, layoutId);
        string identifier = entry.Identifier;
        if (ValidateIdentifierArgument(identifier, "identifier") is { } inputError)
            return Refused(manifestPath, identifier, null, inputError);

        // Blank optional values count as omitted: MCP clients often send "" for "not given".
        string file = string.IsNullOrWhiteSpace(entry.File) ? $"sections/{identifier}.json" : entry.File;
        if (ResolveSectionFilePath(layoutsPath, layoutId, file) is null)
            return Refused(manifestPath, identifier, null, $"{OutsideLayoutMessage(file, layoutId)}.");

        if (await LoadManifestAsync(manifestPath, layoutsPath) is not { } manifest)
            return Refused(manifestPath, identifier, null, ManifestNotFoundMessage(manifestPath));

        if (RegistryShapeError(manifest) is { } shapeError)
            return Refused(manifestPath, identifier, null, shapeError);

        if (FindEntryIndexes(GetRegistry(manifest), identifier).Count > 0)
            return Refused(manifestPath, identifier, null, $"Section '{identifier}' is already declared in entities.sections.");

        // Key order matches the existing entries: identifier, type, file, description.
        JsonObject newEntry = new()
        {
            ["identifier"] = identifier,
            ["type"] = string.IsNullOrWhiteSpace(entry.Type) ? DefaultSectionType : entry.Type,
            ["file"] = file,
        };
        if (!string.IsNullOrWhiteSpace(entry.Description))
            newEntry["description"] = entry.Description;

        List<string> warnings = [];
        SectionFile sectionFile = await ReadSectionFileAsync(layoutsPath, layoutId, file, ct);
        if (sectionFile.Identifier?.Value != identifier)
            warnings.Add(SectionFileMismatchMessage(sectionFile, identifier));

        // An add can resolve references that were dangling before it; report them.
        List<string> references = [.. FindMentions(manifest, identifier)
            .Where(mention => mention.IsReference)
            .Select(mention => mention.Pointer)];

        JsonObject patchOp = AppendEntry(manifest, newEntry);

        if (!dryRun)
        {
            if (await TryWriteManifestAsync(manifestPath, manifest) is { } writeError)
                return Refused(manifestPath, identifier, null, writeError, references: references);
            _logger.LogInformation("Section registry: added '{Identifier}' to {Layout}.", identifier, layoutId);
        }

        return new SectionMutationResult(
            Success: true,
            ManifestPath: manifestPath,
            Identifier: identifier,
            NewIdentifier: null,
            Before: null,
            After: newEntry.DeepClone().AsObject(),
            Patch: [patchOp],
            References: references,
            FilesChanged: [manifestPath],
            Errors: [],
            Warnings: warnings);
    }

    /// <summary>
    /// Renames a section: the registry entry and every section reference in the manifest are
    /// rewritten in one write, and (when <paramref name="updateSectionFile"/> is true) so is
    /// the section file's own top-level <c>identifier</c>. The section file keeps its name.
    /// Refuses when <paramref name="identifier"/> is not declared exactly once, when
    /// <paramref name="newIdentifier"/> is already declared, or when the section file should
    /// be updated but cannot be (unreadable, not valid JSON, or not declaring the old name).
    /// </summary>
    /// <param name="layoutsPath">Absolute path to the <c>layouts/</c> directory.</param>
    /// <param name="layoutId">The tenant layout, e.g. <c>dirt-life</c>.</param>
    /// <param name="identifier">The section's current identifier.</param>
    /// <param name="newIdentifier">The identifier to rename it to.</param>
    /// <param name="updateSectionFile">
    /// If true, also rewrite the section file's top-level <c>identifier</c>, refusing the
    /// whole rename when that is impossible; a missing file is only a warning. If false, the
    /// file is left alone and any mismatch is a warning.
    /// </param>
    /// <param name="dryRun">If true, compute and report the change without writing it.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<SectionMutationResult> RenameSectionAsync(
        string layoutsPath,
        string layoutId,
        string identifier,
        string newIdentifier,
        bool updateSectionFile,
        bool dryRun,
        CancellationToken ct = default)
    {
        string manifestPath = ManifestPath(layoutsPath, layoutId);
        string? inputError = ValidateIdentifierArgument(identifier, "identifier")
            ?? ValidateIdentifierArgument(newIdentifier, "new identifier");
        if (inputError is null && identifier == newIdentifier)
            inputError = $"The new identifier is the same as the current one ('{identifier}').";
        if (inputError is not null)
            return Refused(manifestPath, identifier, newIdentifier, inputError);

        if (await LoadManifestAsync(manifestPath, layoutsPath) is not { } manifest)
            return Refused(manifestPath, identifier, newIdentifier, ManifestNotFoundMessage(manifestPath));

        if (RegistryShapeError(manifest) is { } shapeError)
            return Refused(manifestPath, identifier, newIdentifier, shapeError);

        JsonArray? registry = GetRegistry(manifest);
        (int index, string? lookupError) = LocateEntry(manifest, registry, identifier);
        if (lookupError is not null)
            return Refused(manifestPath, identifier, newIdentifier, lookupError);

        if (FindEntryIndexes(registry, newIdentifier).Count > 0)
        {
            return Refused(manifestPath, identifier, newIdentifier,
                $"Section '{newIdentifier}' is already declared in entities.sections; a rename cannot merge two sections.");
        }

        JsonObject entry = registry![index]!.AsObject();
        JsonObject before = entry.DeepClone().AsObject();
        List<string> warnings = [];

        // Decide about the section file before touching the manifest, so a problem with it
        // refuses the whole rename instead of surfacing after the manifest has changed.
        SectionFile sectionFile = await ReadSectionFileAsync(layoutsPath, layoutId, EntryFile(entry, identifier), ct);
        string? declared = sectionFile.Identifier?.Value;
        byte[]? sectionFileUpdate = null;
        if (declared == newIdentifier)
        {
            // Already renamed (e.g. by hand, first): nothing to change in the file.
        }
        else if (!updateSectionFile || sectionFile.IsMissing)
        {
            // Asked to leave the file alone, or there is no file to keep in step.
            warnings.Add(SectionFileMismatchMessage(sectionFile, newIdentifier));
        }
        else if (declared == identifier)
        {
            sectionFileUpdate = ReplaceToken(sectionFile.Content!, sectionFile.Identifier!, newIdentifier);
        }
        else
        {
            // The caller asked for both files. Renaming only the manifest here would quietly
            // break the "both or neither" contract, so refuse and name the escape hatch.
            string reason = sectionFile.Problem
                ?? $"Section file {sectionFile.Path} declares identifier '{declared}', not '{identifier}'";
            return Refused(manifestPath, identifier, newIdentifier,
                $"{reason}, so the rename cannot update it. Fix the file first, or rename with "
                + "manifest-only to change just the manifest.",
                before);
        }

        // Rewrite the declaration and every reference in memory; one write below persists all
        // of them together.
        JsonArray patch = [ReplaceOp($"{RegistryPointer}/{index}/identifier", newIdentifier)];
        List<string> references = [];
        entry["identifier"] = newIdentifier;
        foreach (SectionMention mention in FindMentions(manifest, identifier))
        {
            if (!mention.IsReference)
            {
                warnings.Add(UnrelatedMentionMessage(mention, identifier));
                continue;
            }

            mention.Node.ReplaceWith(JsonValue.Create(newIdentifier));
            patch.Add(ReplaceOp(mention.Pointer, newIdentifier));
            references.Add(mention.Pointer);
        }

        List<string> filesChanged = [manifestPath];
        if (sectionFileUpdate is not null)
            filesChanged.Add(sectionFile.Path!);

        if (!dryRun)
        {
            if (sectionFileUpdate is null)
            {
                if (await TryWriteManifestAsync(manifestPath, manifest) is { } writeError)
                    return Refused(manifestPath, identifier, newIdentifier, writeError, before, references);
            }
            else
            {
                (string? writeError, bool manifestLeftChanged) = await WriteManifestThenSectionFileAsync(
                    manifestPath, manifest, sectionFile.Path!, sectionFileUpdate, identifier, newIdentifier, ct);

                if (writeError is not null && !manifestLeftChanged)
                    return Refused(manifestPath, identifier, newIdentifier, writeError, before, references);

                if (writeError is not null)
                {
                    // The restore failed too: report what is on disk now, not a clean refusal.
                    return new SectionMutationResult(
                        Success: false,
                        ManifestPath: manifestPath,
                        Identifier: identifier,
                        NewIdentifier: newIdentifier,
                        Before: before,
                        After: entry.DeepClone().AsObject(),
                        Patch: patch,
                        References: references,
                        FilesChanged: [manifestPath],
                        Errors: [writeError],
                        Warnings: warnings);
                }
            }

            _logger.LogInformation(
                "Section registry: renamed '{Identifier}' to '{NewIdentifier}' in {Layout} ({ReferenceCount} reference(s) rewritten).",
                identifier, newIdentifier, layoutId, references.Count);
        }

        return new SectionMutationResult(
            Success: true,
            ManifestPath: manifestPath,
            Identifier: identifier,
            NewIdentifier: newIdentifier,
            Before: before,
            After: entry.DeepClone().AsObject(),
            Patch: patch,
            References: references,
            FilesChanged: filesChanged,
            Errors: [],
            Warnings: warnings);
    }

    /// <summary>
    /// Removes a section from <c>entities.sections</c>. Refuses while any section reference
    /// still names it, listing every reference, unless <paramref name="force"/> is set; a
    /// forced removal reports each reference it leaves dangling as a warning. The section file
    /// itself is never deleted.
    /// </summary>
    /// <param name="layoutsPath">Absolute path to the <c>layouts/</c> directory.</param>
    /// <param name="layoutId">The tenant layout, e.g. <c>dirt-life</c>.</param>
    /// <param name="identifier">The section to remove.</param>
    /// <param name="force">Remove the entry even though references still name it.</param>
    /// <param name="dryRun">If true, compute and report the change without writing it.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<SectionMutationResult> RemoveSectionAsync(
        string layoutsPath,
        string layoutId,
        string identifier,
        bool force,
        bool dryRun,
        CancellationToken ct = default)
    {
        string manifestPath = ManifestPath(layoutsPath, layoutId);
        if (ValidateIdentifierArgument(identifier, "identifier") is { } inputError)
            return Refused(manifestPath, identifier, null, inputError);

        if (await LoadManifestAsync(manifestPath, layoutsPath) is not { } manifest)
            return Refused(manifestPath, identifier, null, ManifestNotFoundMessage(manifestPath));

        if (RegistryShapeError(manifest) is { } shapeError)
            return Refused(manifestPath, identifier, null, shapeError);

        JsonArray? registry = GetRegistry(manifest);
        (int index, string? lookupError) = LocateEntry(manifest, registry, identifier);
        if (lookupError is not null)
            return Refused(manifestPath, identifier, null, lookupError);

        JsonObject before = registry![index]!.DeepClone().AsObject();
        List<SectionMention> mentions = FindMentions(manifest, identifier);
        List<string> references = [.. mentions.Where(m => m.IsReference).Select(m => m.Pointer)];

        if (references.Count > 0 && !force)
        {
            return Refused(manifestPath, identifier, null,
                $"Section '{identifier}' is still referenced by {references.Count} field(s). "
                + "Point them at another section first, or force the removal to leave them dangling.",
                before, references);
        }

        List<string> warnings =
        [
            .. references.Select(pointer => $"{pointer} still references the removed section '{identifier}'."),
            .. mentions.Where(m => !m.IsReference).Select(m => UnrelatedMentionMessage(m, identifier)),
        ];

        registry.RemoveAt(index);

        if (!dryRun)
        {
            if (await TryWriteManifestAsync(manifestPath, manifest) is { } writeError)
                return Refused(manifestPath, identifier, null, writeError, before, references);
            _logger.LogInformation(
                "Section registry: removed '{Identifier}' from {Layout} ({DanglingCount} reference(s) left dangling).",
                identifier, layoutId, references.Count);
        }

        return new SectionMutationResult(
            Success: true,
            ManifestPath: manifestPath,
            Identifier: identifier,
            NewIdentifier: null,
            Before: before,
            After: null,
            Patch: [RemoveOp($"{RegistryPointer}/{index}")],
            References: references,
            FilesChanged: [manifestPath],
            Errors: [],
            Warnings: warnings);
    }

    // ───── references ─────

    /// <summary>
    /// One string value in the manifest (outside the registry) that equals a section identifier.
    /// </summary>
    /// <param name="Pointer">RFC 6901 JSON Pointer to the value, from the manifest root.</param>
    /// <param name="Node">The value node, so a rename can replace it in place.</param>
    /// <param name="IsReference">True when the value sits in a section-reference field.</param>
    internal sealed record SectionMention(string Pointer, JsonNode Node, bool IsReference);

    /// <summary>
    /// Returns every string value outside <c>entities.sections</c> that equals
    /// <paramref name="identifier"/>, in document order, each classified by
    /// <see cref="IsSectionReferenceField"/>.
    /// </summary>
    internal static List<SectionMention> FindMentions(JsonObject manifest, string identifier)
    {
        List<SectionMention> mentions = [];
        CollectMentions(manifest, pointer: string.Empty, owningProperty: null, identifier, mentions);
        return mentions;
    }

    /// <summary>
    /// True when a string held by <paramref name="propertyName"/> names a section. The w31rd
    /// manifests follow this convention everywhere: <c>structuralSection</c>,
    /// <c>sectionIdentifiers</c>, <c>notFoundSection</c>, <c>formSection</c>,
    /// <c>targetSection</c>, <c>editableSections</c>.
    /// </summary>
    internal static bool IsSectionReferenceField(string? propertyName)
        => propertyName is not null && propertyName.Contains("section", StringComparison.OrdinalIgnoreCase);

    private static void CollectMentions(
        JsonNode? node,
        string pointer,
        string? owningProperty,
        string identifier,
        List<SectionMention> mentions)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> property in obj)
                {
                    string childPointer = $"{pointer}/{ManifestMutationService.EscapeJsonPointer(property.Key)}";
                    if (childPointer != RegistryPointer)
                        CollectMentions(property.Value, childPointer, property.Key, identifier, mentions);
                }
                break;

            case JsonArray array:
                // Array elements belong to the property that holds the array, so the strings
                // in "sectionIdentifiers": [...] are judged by "sectionIdentifiers".
                for (int i = 0; i < array.Count; i++)
                    CollectMentions(array[i], $"{pointer}/{i}", owningProperty, identifier, mentions);
                break;

            case JsonValue value when value.TryGetValue(out string? text) && text == identifier:
                mentions.Add(new SectionMention(pointer, value, IsSectionReferenceField(owningProperty)));
                break;
        }
    }

    private static string UnrelatedMentionMessage(SectionMention mention, string identifier)
        => $"{mention.Pointer} also holds the text '{identifier}', but its field is not a section "
         + "reference, so it was left unchanged. Check that it does not name this section.";

    // ───── registry ─────

    /// <summary>
    /// Refuses a registry that exists with the wrong JSON type, rather than letting an add
    /// overwrite it or a rename/remove report its sections as "not declared". A missing or
    /// <c>null</c> node is fine: an add creates it.
    /// </summary>
    private static string? RegistryShapeError(JsonObject manifest)
    {
        JsonNode? entities = manifest["entities"];
        if (entities is not (null or JsonObject))
            return "The manifest's \"entities\" is not a JSON object, so the section registry cannot be read. Fix the manifest first.";

        if (entities?["sections"] is not (null or JsonArray))
            return "The manifest's \"entities.sections\" is not a JSON array, so the section registry cannot be read. Fix the manifest first.";

        return null;
    }

    private static JsonArray? GetRegistry(JsonObject manifest)
        => manifest["entities"] is JsonObject entities && entities["sections"] is JsonArray sections
            ? sections
            : null;

    private static List<int> FindEntryIndexes(JsonArray? registry, string identifier)
    {
        List<int> indexes = [];
        if (registry is null) return indexes;

        for (int i = 0; i < registry.Count; i++)
        {
            if (registry[i] is JsonObject entry
                && entry["identifier"] is JsonValue value
                && value.TryGetValue(out string? id)
                && id == identifier)
            {
                indexes.Add(i);
            }
        }
        return indexes;
    }

    /// <summary>
    /// Locates the single registry entry declaring <paramref name="identifier"/>. A missing
    /// identifier gets "did you mean" suggestions; a duplicated one is refused because which
    /// entry to change would be a guess.
    /// </summary>
    private static (int Index, string? Error) LocateEntry(JsonObject manifest, JsonArray? registry, string identifier)
    {
        List<int> indexes = FindEntryIndexes(registry, identifier);
        if (indexes.Count == 1) return (indexes[0], null);

        if (indexes.Count > 1)
        {
            return (-1, $"Section '{identifier}' is declared {indexes.Count} times in entities.sections, "
                      + "so which entry to change is ambiguous.");
        }

        IReadOnlyList<string> suggestions = ManifestSectionValidator.NearestMatches(
            identifier,
            ManifestSectionValidator.CollectDeclaredSectionIdentifiers(manifest),
            max: 3);
        string hint = suggestions.Count > 0 ? $" Did you mean: {string.Join(", ", suggestions)}?" : string.Empty;
        return (-1, $"Section '{identifier}' is not declared in entities.sections.{hint}");
    }

    /// <summary>
    /// Appends <paramref name="entry"/> to <c>entities.sections</c>, creating <c>entities</c>
    /// and/or <c>sections</c> when the manifest has none, and returns the RFC 6902 operation
    /// that makes the same change.
    /// </summary>
    private static JsonObject AppendEntry(JsonObject manifest, JsonObject entry)
    {
        if (manifest["entities"] is not JsonObject entities)
        {
            JsonObject created = new() { ["sections"] = new JsonArray(entry) };
            manifest["entities"] = created;
            return AddOp("/entities", created.DeepClone());
        }

        if (entities["sections"] is not JsonArray sections)
        {
            JsonArray created = [entry];
            entities["sections"] = created;
            return AddOp(RegistryPointer, created.DeepClone());
        }

        sections.Add(entry);
        return AddOp($"{RegistryPointer}/-", entry.DeepClone());
    }

    /// <summary>
    /// The entry's section file, relative to the layout directory. Falls back to the naming
    /// convention when the entry carries no usable <c>file</c>.
    /// </summary>
    private static string EntryFile(JsonObject entry, string identifier)
        => entry["file"] is JsonValue value && value.TryGetValue(out string? file) && !string.IsNullOrWhiteSpace(file)
            ? file
            : $"sections/{identifier}.json";

    // ───── section file ─────

    /// <summary>
    /// The top-level <c>identifier</c> string token of a section file.
    /// </summary>
    /// <param name="Value">The decoded identifier.</param>
    /// <param name="Start">Byte offset of the token's opening quote.</param>
    /// <param name="Length">Byte length of the token, both quotes included.</param>
    internal sealed record IdentifierToken(string Value, int Start, int Length);

    /// <summary>
    /// What a section file on disk says about itself. <see cref="Problem"/> explains why
    /// <see cref="Identifier"/> is null: the path escapes the layout directory, or the file is
    /// missing, unreadable, not valid JSON, or has no top-level string <c>identifier</c>.
    /// </summary>
    private sealed record SectionFile(string? Path, byte[]? Content, IdentifierToken? Identifier, string? Problem)
    {
        /// <summary>
        /// True when the file simply is not there: the one problem a rename can proceed past,
        /// because there is no file to keep in step with the registry.
        /// </summary>
        public bool IsMissing { get; init; }
    }

    private async Task<SectionFile> ReadSectionFileAsync(
        string layoutsPath,
        string layoutId,
        string relativeFile,
        CancellationToken ct)
    {
        string? path = ResolveSectionFilePath(layoutsPath, layoutId, relativeFile);
        if (path is null)
            return new SectionFile(null, null, null, OutsideLayoutMessage(relativeFile, layoutId));

        if (!File.Exists(path))
            return new SectionFile(path, null, null, $"Section file {path} does not exist") { IsMissing = true };

        byte[] content;
        try
        {
            content = await _fileService.ReadAllBytesAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SectionFile(path, null, null, $"Section file {path} could not be read ({ex.Message})");
        }

        try
        {
            IdentifierToken? token = FindTopLevelIdentifier(content);
            return token is null
                ? new SectionFile(path, content, null, $"Section file {path} has no top-level \"identifier\"")
                : new SectionFile(path, content, token, null);
        }
        catch (JsonException ex)
        {
            return new SectionFile(path, content, null, $"Section file {path} is not valid JSON ({ex.Message})");
        }
    }

    /// <summary>
    /// Writes the renamed manifest, then the renamed section file. If the section-file write
    /// fails, for any reason, the manifest's original bytes are put back so the pair changes
    /// together or not at all. Expected I/O failures come back as an error message; anything
    /// else (cancellation, a bug) is rethrown once the restore has been attempted.
    /// </summary>
    /// <returns>
    /// <c>(null, false)</c> on success. Otherwise the error and whether the manifest is still
    /// changed on disk, which only happens when the restore failed as well.
    /// </returns>
    private async Task<(string? Error, bool ManifestLeftChanged)> WriteManifestThenSectionFileAsync(
        string manifestPath,
        JsonObject manifest,
        string sectionFilePath,
        byte[] sectionFileContent,
        string identifier,
        string newIdentifier,
        CancellationToken ct)
    {
        byte[] originalManifest;
        try
        {
            originalManifest = await _fileService.ReadAllBytesAsync(manifestPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing written yet; without the original bytes a rollback would be impossible.
            return ($"Failed to read {manifestPath} before writing: {ex.Message}", false);
        }

        // Nothing else has been written yet, so a failure here is a plain refusal.
        if (await TryWriteManifestAsync(manifestPath, manifest) is { } manifestWriteError)
            return (manifestWriteError, false);

        try
        {
            await _fileService.WriteAllBytesAsync(sectionFilePath, sectionFileContent, ct);
            return (null, false);
        }
        catch (Exception writeFailure)
        {
            string? restoreFailure = null;
            try
            {
                await _fileService.WriteAllBytesAsync(manifestPath, originalManifest, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                restoreFailure = ex.Message;
                _logger.LogError(ex, "Could not restore {Manifest} after the section-file write failed.", manifestPath);
            }

            if (writeFailure is not (IOException or UnauthorizedAccessException))
                throw;

            _logger.LogError(writeFailure, "Could not write section file {Path}.", sectionFilePath);
            if (restoreFailure is null)
            {
                return ($"Could not write section file {sectionFilePath} ({writeFailure.Message}). "
                      + "layout-manifest.json was restored, so nothing was renamed.", false);
            }

            return ($"Could not write section file {sectionFilePath} ({writeFailure.Message}), and restoring "
                  + $"layout-manifest.json failed too ({restoreFailure}). The manifest now says '{newIdentifier}' "
                  + $"while the section file still says '{identifier}': set the file's identifier to "
                  + $"'{newIdentifier}', or restore layout-manifest.json from version control.", true);
        }
    }

    private static string SectionFileMismatchMessage(SectionFile file, string expected)
        => file.Problem is not null
            ? $"{file.Problem}; the registry entry expects identifier '{expected}'."
            : $"Section file {file.Path} declares identifier '{file.Identifier!.Value}', but the registry "
            + $"entry says '{expected}'. The section will not resolve after sync until they match.";

    /// <summary>
    /// Resolves a registry <c>file</c> (relative to <c>layouts/{layoutId}/</c>) to an absolute
    /// path, or returns <c>null</c> when it points outside that directory. The path comes from
    /// manifest data and this service writes to it, so it must not reach another layout or
    /// anything else on disk.
    /// </summary>
    internal static string? ResolveSectionFilePath(string layoutsPath, string layoutId, string relativeFile)
    {
        string layoutDirectory = Path.GetFullPath(Path.Combine(layoutsPath, layoutId));
        string candidate = Path.GetFullPath(Path.Combine(layoutDirectory, relativeFile));
        string prefix = Path.EndsInDirectorySeparator(layoutDirectory)
            ? layoutDirectory
            : layoutDirectory + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.Ordinal) ? candidate : null;
    }

    /// <summary>
    /// Finds the top-level <c>identifier</c> string token without materializing the document,
    /// so the caller can replace exactly those bytes. Nested <c>identifier</c> properties are
    /// skipped. A leading UTF-8 BOM is tolerated and counted in the offsets. Returns
    /// <c>null</c> when the root is not an object or has no string <c>identifier</c>. Reads
    /// the whole document, so malformed JSON anywhere (not just before the identifier) throws
    /// <see cref="JsonException"/> instead of being spliced.
    /// </summary>
    internal static IdentifierToken? FindTopLevelIdentifier(byte[] utf8Json)
    {
        int offset = utf8Json.AsSpan().StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;
        Utf8JsonReader reader = new(utf8Json.AsSpan(offset));

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return null;

        IdentifierToken? found = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool isIdentifier = reader.ValueTextEquals("identifier"u8);
            reader.Read();
            if (isIdentifier && found is null && reader.TokenType == JsonTokenType.String)
            {
                // TokenStartIndex is the opening quote; ValueSpan is the raw (still escaped)
                // text between the quotes, so the whole token is ValueSpan.Length + 2 bytes.
                found = new IdentifierToken(
                    reader.GetString()!,
                    offset + (int)reader.TokenStartIndex,
                    reader.ValueSpan.Length + 2);
            }
            reader.Skip();
        }

        // Past the root object's end: anything but trailing whitespace makes Read() throw.
        reader.Read();
        return found;
    }

    /// <summary>
    /// Returns <paramref name="content"/> with <paramref name="token"/> replaced by
    /// <paramref name="replacement"/> encoded as a JSON string. Every other byte is untouched.
    /// </summary>
    internal static byte[] ReplaceToken(byte[] content, IdentifierToken token, string replacement)
    {
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(replacement, IdentifierTokenOptions);
        return [.. content[..token.Start], .. encoded, .. content[(token.Start + token.Length)..]];
    }

    // ───── shared helpers ─────

    /// <summary>
    /// <see cref="ManifestMutationService.GetManifestPath"/> (the one place the manifest's
    /// location is defined), normalized so it compares equal to the section-file paths that
    /// <see cref="ResolveSectionFilePath"/> reports alongside it in <c>filesChanged</c>.
    /// </summary>
    private static string ManifestPath(string layoutsPath, string layoutId)
        => Path.GetFullPath(ManifestMutationService.GetManifestPath(layoutsPath, layoutId));

    private async Task<JsonObject?> LoadManifestAsync(string manifestPath, string layoutsPath)
        => (await _fileService.ReadDocumentAsync(manifestPath, layoutsPath))?.Content;

    /// <summary>
    /// Writes the manifest and turns an I/O failure into an error naming the file, in the
    /// same words as <see cref="ManifestMutationService"/> (issue #29): an escaping exception
    /// reaches MCP clients only as a generic "An error occurred" message.
    /// </summary>
    private async Task<string?> TryWriteManifestAsync(string manifestPath, JsonObject manifest)
    {
        try
        {
            await _fileService.WriteDocumentAsync(manifestPath, manifest);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not write {Manifest}.", manifestPath);
            return $"Failed to write {manifestPath}: {ex.Message}";
        }
    }

    private static string? ValidateIdentifierArgument(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return $"The {name} must not be empty.";
        if (value.Trim() != value) return $"The {name} '{value}' has leading or trailing whitespace.";
        return null;
    }

    private static string ManifestNotFoundMessage(string manifestPath)
        => $"Manifest not found or unparseable at {manifestPath}.";

    private static string OutsideLayoutMessage(string relativeFile, string layoutId)
        => $"Section file '{relativeFile}' resolves outside the {layoutId} layout directory";

    private static JsonObject AddOp(string path, JsonNode value)
        => new() { ["op"] = "add", ["path"] = path, ["value"] = value };

    private static JsonObject ReplaceOp(string path, string value)
        => new() { ["op"] = "replace", ["path"] = path, ["value"] = value };

    private static JsonObject RemoveOp(string path)
        => new() { ["op"] = "remove", ["path"] = path };

    private static SectionMutationResult Refused(
        string manifestPath,
        string identifier,
        string? newIdentifier,
        string error,
        JsonObject? before = null,
        IReadOnlyList<string>? references = null)
        => new(
            Success: false,
            ManifestPath: manifestPath,
            Identifier: identifier,
            NewIdentifier: newIdentifier,
            Before: before,
            After: null,
            Patch: null,
            References: references ?? [],
            FilesChanged: [],
            Errors: [error],
            Warnings: []);
}
