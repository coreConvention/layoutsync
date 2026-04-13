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

            string relativePath = Path.GetRelativePath(layoutsBasePath, filePath).Replace('\\', '/');
            DocumentType docType = DetermineDocumentType(relativePath);
            string? layoutId = ExtractLayoutId(relativePath);

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
    /// Discovers all JSON files in a layouts directory.
    /// </summary>
    public IEnumerable<string> DiscoverFiles(string layoutsPath, string? specificLayout = null)
    {
        if (!Directory.Exists(layoutsPath))
        {
            _logger.LogWarning("Layouts directory not found: {Path}", layoutsPath);
            yield break;
        }

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
            // Layouts folder (collection-based structure)
            foreach (string file in GetJsonFiles(Path.Combine(layoutDir, "layouts")))
                yield return file;

            // Menus folder (collection-based structure)
            foreach (string file in GetJsonFiles(Path.Combine(layoutDir, "menus")))
                yield return file;

            // Manifests folder (collection-based structure)
            foreach (string file in GetJsonFiles(Path.Combine(layoutDir, "manifests")))
                yield return file;

            // Sections folder
            foreach (string file in GetJsonFiles(Path.Combine(layoutDir, "sections")))
                yield return file;

            // Modals folder
            foreach (string file in GetJsonFiles(Path.Combine(layoutDir, "modals")))
                yield return file;

            // Entities folder
            foreach (string file in GetJsonFiles(Path.Combine(layoutDir, "entities")))
                yield return file;

            // Identities folder
            foreach (string file in GetJsonFiles(Path.Combine(layoutDir, "identities")))
                yield return file;

            // Tags folder
            foreach (string file in GetJsonFiles(Path.Combine(layoutDir, "tags")))
                yield return file;

            // Workflows folder
            foreach (string file in GetJsonFiles(Path.Combine(layoutDir, "workflows")))
                yield return file;

            // Write policies folder
            foreach (string file in GetJsonFiles(Path.Combine(layoutDir, "write-policies")))
                yield return file;

            // Entity configs folder
            foreach (string file in GetJsonFiles(Path.Combine(layoutDir, "entity-configs")))
                yield return file;
        }
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
    /// </summary>
    public static DocumentType DetermineDocumentType(string relativePath)
    {
        string[] parts = relativePath.Split('/');

        // Check folder-based types (collection-based structure)
        if (parts.Length >= 2)
        {
            string folder = parts[1].ToLowerInvariant();
            return folder switch
            {
                "layouts" => DocumentType.Layout,
                "menus" => DocumentType.Menu,
                "manifests" => DocumentType.Manifest,
                "sections" => DocumentType.Section,
                "modals" => DocumentType.Modal,
                "entities" => DocumentType.Entity,
                "identities" => DocumentType.Identity,
                "tags" => DocumentType.Tag,
                "workflows" => DocumentType.Workflow,
                "write-policies" => DocumentType.WritePolicy,
                "entity-configs" => DocumentType.EntityConfig,
                _ => DocumentType.Entity // Default to entity
            };
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
