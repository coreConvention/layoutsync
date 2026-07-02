using System.Text.Json;
using System.Text.Json.Nodes;
using coreConvention.Core.Validation;
using LayoutSync.Models;
using Microsoft.Extensions.Logging;

namespace LayoutSync.Services;

/// <summary>
/// Service for reading and writing local JSON files.
/// Handles file detection, parsing, and document type determination.
/// </summary>
public class LocalFileService(ILogger<LocalFileService> logger)
{
    private readonly ILogger<LocalFileService> _logger = logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Reads a JSON file and parses it into a SyncDocument.
    /// </summary>
    public async Task<SyncDocument?> ReadDocumentAsync(string filePath, string layoutsBasePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("File not found: {Path}", filePath);
                return null;
            }

            string json = await File.ReadAllTextAsync(filePath);
            JsonObject? content = JsonNode.Parse(json)?.AsObject();

            if (content == null)
            {
                _logger.LogWarning("Failed to parse JSON from: {Path}", filePath);
                return null;
            }

            string relativePath;
            DocumentType docType;
            string? layoutId;

            // Platform-scoped themes live in `<layoutsParent>/themes/*.json` — a
            // sibling directory to the layouts root. They are recognized by path
            // prefix, get DocumentType.Theme, and carry NO layoutId (the API
            // resolver treats them as the platform catalogue available to every
            // tenant). All other files use the layout-scoped relative path under
            // `layoutsBasePath`.
            string platformThemesPath = GetPlatformThemesDirectory(layoutsBasePath);
            bool isPlatformTheme = !string.IsNullOrEmpty(platformThemesPath)
                && filePath.StartsWith(platformThemesPath, StringComparison.OrdinalIgnoreCase);

            if (isPlatformTheme)
            {
                // Anchor the relative path on the layouts parent so it reads as
                // `themes/{themeId}.json` — keeping log lines and validator output
                // human-readable without any synthetic sentinel segments.
                string layoutsParent = Path.GetDirectoryName(platformThemesPath) ?? layoutsBasePath;
                relativePath = Path.GetRelativePath(layoutsParent, filePath).Replace('\\', '/');
                docType = DocumentType.Theme;
                layoutId = null;
            }
            else
            {
                relativePath = Path.GetRelativePath(layoutsBasePath, filePath).Replace('\\', '/');
                docType = DetermineDocumentType(relativePath);
                layoutId = ExtractLayoutId(relativePath);
            }

            // Extract id and identifier from content
            // Check for top-level "id" first, then fall back to "@metadata.@id" (RavenDB format)
            string? id = content["id"]?.GetValue<string>()
                ?? content["@metadata"]?.AsObject()?["@id"]?.GetValue<string>();
            string? identifier = content["identifier"]?.GetValue<string>();

            // Check if ID is human-readable
            bool hasHumanReadableId = !string.IsNullOrEmpty(id) && NanoIdValidator.IsHumanReadable(id);

            // All JSON files should have wrapper structure with identifier field
            // Fall back to filename if not present
            if (string.IsNullOrEmpty(identifier))
            {
                identifier = Path.GetFileNameWithoutExtension(filePath);
                _logger.LogDebug("Derived identifier '{Identifier}' from filename for {DocType}", identifier, docType);
            }

            SyncDocument doc = new()
            {
                Id = id,
                Identifier = identifier,
                DocumentType = docType,
                EntityType = content["type"]?.GetValue<string>(),
                LayoutId = layoutId,
                FilePath = filePath,
                RelativePath = relativePath,
                Content = content,
                LastModified = File.GetLastWriteTimeUtc(filePath),
                HasHumanReadableId = hasHumanReadableId
            };

            return doc;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Invalid JSON in file: {Path}", filePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading file: {Path}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Writes a JSON document back to file.
    /// </summary>
    public async Task WriteDocumentAsync(string filePath, JsonObject content)
    {
        string json = content.ToJsonString(JsonOptions);
        await File.WriteAllTextAsync(filePath, json);
        _logger.LogDebug("Wrote file: {Path}", filePath);
    }

    /// <summary>
    /// Discovers all JSON files in a layouts directory, plus platform-scoped
    /// theme files from a sibling <c>themes/</c> directory (the platform
    /// catalogue, available to every tenant). Platform-scoped discovery is
    /// SKIPPED when <paramref name="specificLayout"/> is set — a tenant-scoped
    /// sync should not touch the platform catalogue.
    /// Collection folders are enumerated from <see cref="CollectionFolders.Ordered"/>
    /// (single source of truth; see issue #9).
    /// </summary>
    /// <param name="layoutsPath">Path to the layouts root.</param>
    /// <param name="specificLayout">Optional single layout to scan (<c>--layout</c>).</param>
    /// <param name="excludeCollections">Collection folder names to skip (<c>--exclude-collection</c>,
    /// case-insensitive). <c>themes</c> also skips the platform sibling catalogue.</param>
    /// <param name="excludeLayouts">Layout directory names to skip entirely
    /// (<c>--exclude-layout</c>, case-sensitive — Linux CI filesystems are).</param>
    public IEnumerable<string> DiscoverFiles(
        string layoutsPath,
        string? specificLayout = null,
        IReadOnlyCollection<string>? excludeCollections = null,
        IReadOnlyCollection<string>? excludeLayouts = null)
    {
        if (!Directory.Exists(layoutsPath))
        {
            _logger.LogWarning("Layouts directory not found: {Path}", layoutsPath);
            yield break;
        }

        HashSet<string> excludedCollections = new(excludeCollections ?? [], StringComparer.OrdinalIgnoreCase);
        HashSet<string> excludedLayouts = new(excludeLayouts ?? [], StringComparer.Ordinal);

        // If a specific layout is requested, only scan that directory
        IEnumerable<string> layoutDirs;
        if (!string.IsNullOrEmpty(specificLayout))
        {
            string layoutDir = Path.Combine(layoutsPath, specificLayout);
            if (!Directory.Exists(layoutDir))
            {
                _logger.LogWarning("Layout directory not found: {Path}", layoutDir);
                yield break;
            }
            layoutDirs = [layoutDir];
        }
        else
        {
            layoutDirs = Directory.GetDirectories(layoutsPath);
        }

        foreach (string layoutDir in layoutDirs)
        {
            if (excludedLayouts.Contains(Path.GetFileName(layoutDir)))
            {
                _logger.LogDebug("Skipping excluded layout: {Layout}", Path.GetFileName(layoutDir));
                continue;
            }

            foreach ((string folder, _) in CollectionFolders.Ordered)
            {
                if (excludedCollections.Contains(folder))
                    continue;

                foreach (string file in GetJsonFiles(Path.Combine(layoutDir, folder)))
                    yield return file;
            }
        }

        // Platform theme catalogue — `<layoutsParent>/themes/*.json` (sibling to
        // the layouts root). These are platform-scoped: no layoutId, available
        // to every tenant. Skipped under `--layout` because a tenant-scoped run
        // should not redundantly re-sync the catalogue on every iteration, and
        // under `--exclude-collection themes` because excluding the collection
        // means excluding it in BOTH of its scopes (layout-keyed + platform).
        if (string.IsNullOrEmpty(specificLayout) && !excludedCollections.Contains("themes"))
        {
            string platformThemesPath = GetPlatformThemesDirectory(layoutsPath);
            foreach (string file in GetJsonFiles(platformThemesPath))
                yield return file;
        }
    }

    /// <summary>
    /// Resolves the canonical platform-themes directory: a <c>themes/</c> folder
    /// sibling to the layouts root. Returns an empty string if the parent path
    /// cannot be derived (defensive: prevents matching against an empty prefix
    /// in <c>ReadDocumentAsync</c>).
    /// </summary>
    public static string GetPlatformThemesDirectory(string layoutsBasePath)
    {
        if (string.IsNullOrEmpty(layoutsBasePath))
        {
            return string.Empty;
        }

        string trimmed = layoutsBasePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? parent = Path.GetDirectoryName(trimmed);
        return string.IsNullOrEmpty(parent)
            ? string.Empty
            : Path.Combine(parent, "themes");
    }

    private static IEnumerable<string> GetJsonFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return [];
        return Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
    }

    /// <summary>
    /// Determines the document type based on the file path.
    /// Files are organized in collection-based folders: layouts/, menus/, manifests/, sections/, etc.
    /// Folder→type mapping lives in <see cref="CollectionFolders.ByFolder"/> (case-insensitive);
    /// unknown folders default to <see cref="DocumentType.Entity"/> (historical behavior).
    /// </summary>
    public static DocumentType DetermineDocumentType(string relativePath)
    {
        string[] parts = relativePath.Split('/');

        // Check folder-based types (collection-based structure)
        if (parts.Length >= 2 && CollectionFolders.ByFolder.TryGetValue(parts[1], out DocumentType type))
        {
            return type;
        }

        return DocumentType.Entity;
    }

    /// <summary>
    /// Extracts the layout ID from a relative path.
    /// </summary>
    private static string? ExtractLayoutId(string relativePath)
    {
        string[] parts = relativePath.Split('/');
        return parts.Length > 0 ? parts[0] : null;
    }
}
