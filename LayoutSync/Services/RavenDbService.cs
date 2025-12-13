using System.Text.Json.Nodes;
using LayoutSync.Configuration;
using LayoutSync.Models;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Session;

namespace LayoutSync.Services;

/// <summary>
/// Service for interacting with RavenDB.
/// Handles CRUD operations for entities and identities.
/// </summary>
public class RavenDbService : IDisposable
{
  private readonly ILogger<RavenDbService> _logger;
  private readonly RavenDbOptions _options;
  private readonly IDocumentStore _store;
  private bool _disposed;

  public RavenDbService(ILogger<RavenDbService> logger, RavenDbOptions options)
  {
    _logger = logger;
    _options = options;

    _store = new DocumentStore { Urls = [options.Url], Database = options.Database };

    _store.Initialize();
    _logger.LogDebug(
      "RavenDB connection initialized: {Url}/{Database}",
      options.Url,
      options.Database
    );
  }

  /// <summary>
  /// Looks up a document by identifier (for entities) or id (for identities).
  /// </summary>
  public async Task<(string? DocumentId, JsonObject? Document)> FindDocumentAsync(
    SyncDocument doc,
    CancellationToken ct = default
  )
  {
    using IAsyncDocumentSession session = _store.OpenAsyncSession();

    string collection = doc.DocumentType == DocumentType.Identity ? "Identities" : "Entities";
    string lookupField = doc.DocumentType == DocumentType.Identity ? "Id" : "Identifier";
    string lookupValue = doc.LookupKey;

    if (string.IsNullOrEmpty(lookupValue))
    {
      _logger.LogWarning("Cannot lookup document without {Field}", lookupField);
      return (null, null);
    }

    try
    {
      // Use RQL to query by identifier/id
      string query =
        doc.DocumentType == DocumentType.Identity
          ? $"from {collection} where Id = $lookupValue"
          : $"from {collection} where Identifier = $lookupValue";

      IAsyncRawDocumentQuery<object> results = session
        .Advanced.AsyncRawQuery<object>(query)
        .AddParameter("lookupValue", lookupValue);

      List<object> documents = await results.ToListAsync(ct);

      if (documents.Count == 0)
      {
        _logger.LogDebug(
          "Document not found: {LookupField}={LookupValue}",
          lookupField,
          lookupValue
        );
        return (null, null);
      }

      object first = documents[0];
      string? docId = session.Advanced.GetDocumentId(first);

      // Convert to JsonObject by serializing and re-parsing
      string json = System.Text.Json.JsonSerializer.Serialize(first);
      JsonObject? jsonObj = JsonNode.Parse(json)?.AsObject();

      _logger.LogDebug("Found document: {DocumentId}", docId);
      return (docId, jsonObj);
    }
    catch (Exception ex)
    {
      _logger.LogError(
        ex,
        "Error looking up document: {LookupField}={LookupValue}",
        lookupField,
        lookupValue
      );
      return (null, null);
    }
  }

  /// <summary>
  /// Creates a new document in RavenDB.
  /// </summary>
  public async Task<string?> CreateDocumentAsync(
    SyncDocument doc,
    JsonObject content,
    CancellationToken ct = default
  )
  {
    using IAsyncDocumentSession session = _store.OpenAsyncSession();

    try
    {
      string collection = doc.DocumentType == DocumentType.Identity ? "Identities" : "Entities";

      // Store as dynamic object
      string json = content.ToJsonString();
      dynamic entity = System.Text.Json.JsonSerializer.Deserialize<dynamic>(json)!;

      // Use the document's ID if it has one
      string? docId = doc.Id;
      if (!string.IsNullOrEmpty(docId))
      {
        await session.StoreAsync(entity, $"{collection}/{docId}", ct);
      }
      else
      {
        await session.StoreAsync(entity, ct);
        docId = session.Advanced.GetDocumentId(entity);
      }

      await session.SaveChangesAsync(ct);

      _logger.LogInformation("Created document: {DocumentId}", docId);
      return docId;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error creating document: {Identifier}", doc.Identifier);
      throw;
    }
  }

  /// <summary>
  /// Updates a document using JSON Patch operations.
  /// </summary>
  public async Task<bool> PatchDocumentAsync(
    string documentId,
    JsonObject patchOperations,
    CancellationToken ct = default
  )
  {
    try
    {
      // Use the RavenDB patch API
      PatchRequest patchRequest = new() { Script = BuildPatchScript(patchOperations) };

      PatchOperation operation = new(documentId, null, patchRequest);
      await _store.Operations.SendAsync(operation, token: ct);

      _logger.LogInformation("Patched document: {DocumentId}", documentId);
      return true;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error patching document: {DocumentId}", documentId);
      return false;
    }
  }

  /// <summary>
  /// Deletes a document from RavenDB.
  /// </summary>
  public async Task<bool> DeleteDocumentAsync(string documentId, CancellationToken ct = default)
  {
    using IAsyncDocumentSession session = _store.OpenAsyncSession();

    try
    {
      session.Delete(documentId);
      await session.SaveChangesAsync(ct);

      _logger.LogInformation("Deleted document: {DocumentId}", documentId);
      return true;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error deleting document: {DocumentId}", documentId);
      return false;
    }
  }

  /// <summary>
  /// Replaces an entire document (delete + create with same IDs).
  /// </summary>
  public async Task<string?> ReplaceDocumentAsync(
    string documentId,
    SyncDocument doc,
    JsonObject newContent,
    CancellationToken ct = default
  )
  {
    try
    {
      // Delete old
      await DeleteDocumentAsync(documentId, ct);

      // Create new with same identifier/id
      return await CreateDocumentAsync(doc, newContent, ct);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error replacing document: {DocumentId}", documentId);
      throw;
    }
  }

  /// <summary>
  /// Builds a RavenDB patch script from JSON patch operations.
  /// </summary>
  private static string BuildPatchScript(JsonObject patchOperations)
  {
    // For simplicity, just replace the whole data object
    // In production, you'd want to translate JSON Patch ops to RavenDB patch script
    return $@"
            for (var key in args.data) {{
                this[key] = args.data[key];
            }}
            this.lastUpdatedDateTime = new Date().toISOString();
        ";
  }

  public void Dispose()
  {
    if (!_disposed)
    {
      _store.Dispose();
      _disposed = true;
    }
    GC.SuppressFinalize(this);
  }
}
