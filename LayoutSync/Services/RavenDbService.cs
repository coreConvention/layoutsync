using System.Dynamic;
using System.Text.Json;
using System.Text.Json.Nodes;
using coreConvention.Core.JsonConverters;
using coreConvention.Core.Validation;
using LayoutSync.Configuration;
using LayoutSync.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Session;
using Raven.Client.Json.Serialization.NewtonsoftJson;

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

    _store = new DocumentStore
    {
      Urls = [options.Url],
      Database = options.Database,
      Conventions =
      {
        // CRITICAL: Prevent CLR type name storage in @metadata
        // This stops RavenDB from adding Raven-Clr-Type metadata
        FindClrTypeName = _ => null,
        FindClrTypeNameForDynamic = _ => null,
        // Disable CLR type metadata storage - we want clean JSON without $type properties
        Serialization = new NewtonsoftJsonSerializationConventions
        {
          CustomizeJsonSerializer = serializer =>
          {
            serializer.ContractResolver = new CamelCasePropertyNamesContractResolver();
            serializer.TypeNameHandling = TypeNameHandling.None;
            serializer.PreserveReferencesHandling = PreserveReferencesHandling.None;
            // CRITICAL: Custom ExpandoObject converter to prevent $type on List<object>
            // TypeNameHandling.None doesn't prevent $type for polymorphic types like List<object>
            // This converter explicitly writes clean JSON without type metadata
            serializer.Converters.Add(new ExpandoObjectNewtonsoftConverter());
          }
        }
      }
    };

    _store.Initialize();
    _logger.LogDebug(
      "RavenDB connection initialized: {Url}/{Database}",
      options.Url,
      options.Database
    );
  }

  /// <summary>
  /// Looks up a document by identifier (for entities) or id (for identities).
  /// For identities, uses direct document ID load since ID is in @metadata.@id, not a top-level field.
  /// </summary>
  public async Task<(string? DocumentId, JsonObject? Document)> FindDocumentAsync(
    SyncDocument doc,
    CancellationToken ct = default
  )
  {
    using IAsyncDocumentSession session = _store.OpenAsyncSession();

    // Route to correct collection based on document type
    string collection = doc.DocumentType.GetCollection();
    string lookupValue = doc.LookupKey;

    if (string.IsNullOrEmpty(lookupValue))
    {
      _logger.LogWarning("Cannot lookup document without identifier/id");
      return (null, null);
    }

    try
    {
      // For identities, load directly by document ID (stored in @metadata.@id)
      // since identities don't have a top-level "id" field in the document
      if (doc.DocumentType == DocumentType.Identity)
      {
        object? loaded = await session.LoadAsync<object>(lookupValue, ct);
        if (loaded == null)
        {
          _logger.LogDebug("Identity not found by id: {Id}", lookupValue);
          return (null, null);
        }

        string? docId = session.Advanced.GetDocumentId(loaded);
        string json = System.Text.Json.JsonSerializer.Serialize(loaded);
        JsonObject? jsonObj = JsonNode.Parse(json)?.AsObject();

        _logger.LogDebug("Found identity by id: {DocumentId}", docId);
        return (docId, jsonObj);
      }

      // For entities, query by identifier field
      string query = $"from {collection} where identifier = $lookupValue";

      IAsyncRawDocumentQuery<object> results = session
        .Advanced.AsyncRawQuery<object>(query)
        .AddParameter("lookupValue", lookupValue);

      List<object> documents = await results.ToListAsync(ct);

      if (documents.Count == 0)
      {
        _logger.LogDebug(
          "Document not found: identifier={LookupValue}",
          lookupValue
        );
        return (null, null);
      }

      object first = documents[0];
      string? entityDocId = session.Advanced.GetDocumentId(first);

      // Convert to JsonObject by serializing and re-parsing
      string entityJson = System.Text.Json.JsonSerializer.Serialize(first);
      JsonObject? entityJsonObj = JsonNode.Parse(entityJson)?.AsObject();

      _logger.LogDebug("Found document: {DocumentId} in {Collection}", entityDocId, collection);
      return (entityDocId, entityJsonObj);
    }
    catch (Exception ex)
    {
      _logger.LogError(
        ex,
        "Error looking up document: {LookupValue}",
        lookupValue
      );
      return (null, null);
    }
  }

  /// <summary>
  /// JsonSerializerOptions with ExpandoObjectConverter for proper deserialization.
  /// </summary>
  private static readonly System.Text.Json.JsonSerializerOptions ExpandoSerializerOptions = new()
  {
    Converters = { new ExpandoObjectConverter() }
  };

  /// <summary>
  /// Creates a new document in RavenDB using raw JSON (no CLR type metadata).
  /// TypeNameHandling.None is configured in the DocumentStore constructor to prevent $type properties.
  /// </summary>
  /// <param name="doc">The sync document metadata.</param>
  /// <param name="content">The document content to store.</param>
  /// <param name="existingDocId">Optional: existing @id to preserve (for replace operations).</param>
  /// <param name="ct">Cancellation token.</param>
  public async Task<string?> CreateDocumentAsync(
    SyncDocument doc,
    JsonObject content,
    string? existingDocId = null,
    CancellationToken ct = default
  )
  {
    using IAsyncDocumentSession session = _store.OpenAsyncSession();

    try
    {
      // Route to correct collection based on document type
      string collection = doc.DocumentType.GetCollection();

      // Convert JsonObject to ExpandoObject using custom converter for proper native types
      string json = content.ToJsonString();
      ExpandoObject entity = System.Text.Json.JsonSerializer.Deserialize<ExpandoObject>(json, ExpandoSerializerOptions)
        ?? new ExpandoObject();

      // Use existing @id if provided (for replacements), otherwise generate new NanoID
      // Note: Just use NanoID without collection prefix - collection is set via @metadata
      string docId = existingDocId ?? NanoIdValidator.GenerateNanoId();

      // Store with explicit ID
      await session.StoreAsync(entity, docId, ct);

      // Explicitly set collection metadata (RavenDB determines collection from this)
      IMetadataDictionary metadata = session.Advanced.GetMetadataFor(entity);
      metadata["@collection"] = collection;

      await session.SaveChangesAsync(ct);

      _logger.LogInformation("Created document: {DocumentId} in {Collection}", docId, collection);
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
  /// The patch script iterates over args.data keys and updates the document.
  /// </summary>
  public async Task<bool> PatchDocumentAsync(
    string documentId,
    JsonObject patchOperations,
    CancellationToken ct = default
  )
  {
    try
    {
      // Convert JsonObject to ExpandoObject for RavenDB serialization
      string json = patchOperations.ToJsonString();
      ExpandoObject patchData = System.Text.Json.JsonSerializer.Deserialize<ExpandoObject>(json, ExpandoSerializerOptions)
        ?? new ExpandoObject();

      // Use the RavenDB patch API with Values to pass data to the script
      PatchRequest patchRequest = new()
      {
        Script = BuildPatchScript(patchOperations),
        Values = new Dictionary<string, object> { { "data", patchData } }
      };

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
  /// Gets all identifiers from a specific collection.
  /// Used for orphan detection in static collections.
  /// </summary>
  /// <param name="collection">The RavenDB collection name (e.g., "Sections").</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>Dictionary mapping identifier to document ID.</returns>
  public async Task<Dictionary<string, string>> GetAllIdentifiersAsync(
    string collection,
    CancellationToken ct = default
  )
  {
    using IAsyncDocumentSession session = _store.OpenAsyncSession();
    Dictionary<string, string> result = [];

    try
    {
      // Query all documents in the collection, getting identifier and @id
      string query = $"from {collection} select identifier, id()";
      IAsyncRawDocumentQuery<dynamic> results = session.Advanced.AsyncRawQuery<dynamic>(query);
      List<dynamic> documents = await results.ToListAsync(ct);

      foreach (dynamic doc in documents)
      {
        string? identifier = doc.identifier?.ToString();
        string? docId = doc["id()"]?.ToString();

        if (!string.IsNullOrEmpty(identifier) && !string.IsNullOrEmpty(docId))
        {
          result[identifier] = docId;
        }
      }

      _logger.LogDebug("Found {Count} documents in {Collection}", result.Count, collection);
      return result;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error querying identifiers from {Collection}", collection);
      return result;
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
  /// Replaces an entire document (delete + create with same @id).
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

      // Create new with same @id to preserve references
      return await CreateDocumentAsync(doc, newContent, existingDocId: documentId, ct: ct);
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
