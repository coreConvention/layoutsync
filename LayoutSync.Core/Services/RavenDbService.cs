using System.Dynamic;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using coreConvention.Core.Serialization.Converters.Newtonsoft;
using coreConvention.Core.Serialization.Converters.SystemTextJson;
using coreConvention.Core.Validation;
using LayoutSync.Configuration;
using LayoutSync.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
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

  /// <summary>
  /// Number of entity identifiers that resolved to more than one document during this
  /// service's lifetime. Used by --strict mode to fail the sync when duplicates are present.
  /// Detection only — duplicate entities are never auto-deleted (user-data protection).
  /// </summary>
  public int DuplicateEntityIdentifierCount { get; private set; }

  public RavenDbService(ILogger<RavenDbService> logger, RavenDbOptions options)
  {
    _logger = logger;
    _options = options;

    // Load certificate if path is provided (required for RavenDB Cloud)
    X509Certificate2? certificate = null;
    if (!string.IsNullOrEmpty(options.CertificatePath))
    {
      if (!File.Exists(options.CertificatePath))
      {
        throw new FileNotFoundException($"Certificate file not found: {options.CertificatePath}");
      }

      certificate = string.IsNullOrEmpty(options.CertificatePassword)
        ? new X509Certificate2(options.CertificatePath)
        : new X509Certificate2(options.CertificatePath, options.CertificatePassword);

      _logger.LogInformation("Loaded certificate: {Subject}", certificate.Subject);
    }

    _store = new DocumentStore
    {
      Urls = [options.Url],
      Database = options.Database,
      Certificate = certificate,
      Conventions =
      {
        // Defense in depth against ordering races. With this on, a StoreAsync
        // for an @id that already exists on the server (from a concurrent
        // writer — another LayoutSync, a human editing in RavenDB Studio)
        // throws ConcurrencyException instead of silently overwriting.
        // CreateDocumentAsync / ReplaceDocumentAsync catch and re-try once.
        UseOptimisticConcurrency = true,

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

      // For entities, query by identifier field.
      // NOTE: Historical seed uploads have been known to leave multiple entity documents
      // with the same Identifier in the DB (see issue #282). Entity orphan cleanup is
      // intentionally disabled to protect user data, so we CANNOT auto-delete extras —
      // but we must at least detect and warn so these ghosts do not persist silently.
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

      // Map to (docId, JsonObject) tuples so the duplicate-detection helper can stay pure.
      List<(string DocId, JsonObject? Json)> mapped = [];
      foreach (object item in documents)
      {
        string? itemDocId = session.Advanced.GetDocumentId(item);
        string itemJson = System.Text.Json.JsonSerializer.Serialize(item);
        JsonObject? itemJsonObj = JsonNode.Parse(itemJson)?.AsObject();
        if (!string.IsNullOrEmpty(itemDocId))
        {
          mapped.Add((itemDocId, itemJsonObj));
        }
      }

      DuplicateEntityLookupResult resolved = ResolveEntityLookup(
        collection,
        lookupValue,
        mapped,
        _logger
      );

      if (resolved.IsDuplicate)
      {
        DuplicateEntityIdentifierCount++;
      }

      return (resolved.DocumentId, resolved.Document);
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
  /// Result of <see cref="ResolveEntityLookup"/>: the first document (used as today) plus
  /// a flag indicating whether multiple documents shared the same Identifier.
  /// </summary>
  public record DuplicateEntityLookupResult(
    string? DocumentId,
    JsonObject? Document,
    bool IsDuplicate
  );

  /// <summary>
  /// Pure helper: inspects a list of entity documents returned for a single Identifier
  /// and — when more than one match is present — emits a <c>LogWarning</c> line per
  /// duplicate document id so operators can purge them manually. Always returns the
  /// first match so sync behavior is unchanged for single-match documents.
  /// </summary>
  /// <remarks>
  /// Detection is report-only by design. Entity orphan deletion is intentionally disabled
  /// in LayoutSync (user-data protection); auto-deleting entity duplicates would violate
  /// that contract. Callers can opt into hard-failing via <c>--strict</c>, which consults
  /// <see cref="DuplicateEntityIdentifierCount"/> at shutdown.
  /// </remarks>
  public static DuplicateEntityLookupResult ResolveEntityLookup(
    string collection,
    string lookupValue,
    IReadOnlyList<(string DocId, JsonObject? Json)> documents,
    ILogger logger
  )
  {
    if (documents.Count == 0)
    {
      return new DuplicateEntityLookupResult(null, null, IsDuplicate: false);
    }

    (string firstDocId, JsonObject? firstJson) = documents[0];

    if (documents.Count == 1)
    {
      logger.LogDebug(
        "Found document: {DocumentId} in {Collection}",
        firstDocId,
        collection
      );
      return new DuplicateEntityLookupResult(firstDocId, firstJson, IsDuplicate: false);
    }

    // More than one match — WARN per doc id so the audit trail survives log aggregation.
    logger.LogWarning(
      "Duplicate entity identifier detected: '{Identifier}' in {Collection} ({Count} documents). Sync will update the first match; the rest persist as ghosts. Manual cleanup required (LayoutSync does not auto-delete entities).",
      lookupValue,
      collection,
      documents.Count
    );

    foreach ((string docId, JsonObject? _) in documents)
    {
      logger.LogWarning(
        "  duplicate: collection={Collection} identifier={Identifier} documentId={DocumentId}",
        collection,
        lookupValue,
        docId
      );
    }

    return new DuplicateEntityLookupResult(firstDocId, firstJson, IsDuplicate: true);
  }

  /// <summary>
  /// JsonSerializerOptions with ExpandoObjectConverter for proper deserialization.
  /// </summary>
  private static readonly System.Text.Json.JsonSerializerOptions ExpandoSerializerOptions = new()
  {
    Converters = { new ExpandoObjectSystemTextJsonConverter() }
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
    string collection = doc.DocumentType.GetCollection();
    // Convert once — re-used by the retry path.
    string json = content.ToJsonString();
    // NanoID for fresh creates is stable across the retry (regenerating would
    // mask the conflict by sidestepping it).
    string docId = existingDocId ?? NanoIdValidator.GenerateNanoId();

    async Task<string?> AttemptAsync()
    {
      using IAsyncDocumentSession session = _store.OpenAsyncSession();
      ExpandoObject entity = System.Text.Json.JsonSerializer.Deserialize<ExpandoObject>(json, ExpandoSerializerOptions)
        ?? new ExpandoObject();

      await session.StoreAsync(entity, docId, ct);
      IMetadataDictionary metadata = session.Advanced.GetMetadataFor(entity);
      metadata["@collection"] = collection;
      await session.SaveChangesAsync(ct);
      return docId;
    }

    try
    {
      string? result = await AttemptAsync();
      _logger.LogInformation("Created document: {DocumentId} in {Collection}", docId, collection);
      return result;
    }
    catch (ConcurrencyException ex)
    {
      // Concurrent writer beat us to this @id. Retry once with a fresh session;
      // if the conflicting write has already cleared we'll succeed, otherwise we
      // surface the conflict so the next file event can re-trigger with the
      // up-to-date local content (which may now agree with what landed).
      _logger.LogWarning(
        "Concurrency conflict creating {DocumentId} in {Collection}: {Message}. Retrying once.",
        docId, collection, ex.Message);

      try
      {
        string? result = await AttemptAsync();
        _logger.LogInformation("Created document (after retry): {DocumentId} in {Collection}", docId, collection);
        return result;
      }
      catch (ConcurrencyException retryEx)
      {
        _logger.LogError(
          retryEx,
          "Concurrency conflict creating {DocumentId} in {Collection} persists after retry. " +
          "Another writer (LayoutSync instance, RavenDB Studio edit, etc.) holds this @id. " +
          "Save the local file again to re-sync once the conflict clears.",
          docId, collection);
        throw;
      }
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
  /// Information about a candidate orphan returned by <see cref="GetAllIdentifiersAsync"/>.
  /// <see cref="LayoutId"/> is null when the document does not stamp a <c>layoutId</c> field
  /// (true for sections / layouts / menus / modals / manifests / tags / workflows). Layout-scoped
  /// collections (WritePolicies, ReadPolicies, entity-configs, theme-definitions) populate it. Used by
  /// orphan-scope filtering to keep <c>--clean</c> + <c>--layout</c> combos safe across tenants.
  /// See issue #427.
  /// </summary>
  public sealed record OrphanCandidate(string DocumentId, string? LayoutId);

  /// <summary>
  /// Gets all identifiers from a specific collection plus their stamped <c>layoutId</c>
  /// (when present). Used for orphan detection in static collections.
  /// </summary>
  /// <param name="collection">The RavenDB collection name (e.g., "Sections").</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>Dictionary mapping identifier to <see cref="OrphanCandidate"/> (document id + optional layoutId).</returns>
  public async Task<Dictionary<string, OrphanCandidate>> GetAllIdentifiersAsync(
    string collection,
    CancellationToken ct = default
  )
  {
    using IAsyncDocumentSession session = _store.OpenAsyncSession();
    Dictionary<string, OrphanCandidate> result = [];

    try
    {
      // Project identifier, document id, and (optional) layoutId so callers can scope
      // orphan detection by tenant. Documents without a layoutId field return null
      // — the projection succeeds rather than throwing for missing fields.
      string query = $"from {collection} select identifier, id(), layoutId";
      IAsyncRawDocumentQuery<dynamic> results = session.Advanced.AsyncRawQuery<dynamic>(query);
      List<dynamic> documents = await results.ToListAsync(ct);

      foreach (dynamic doc in documents)
      {
        string? identifier = doc.identifier?.ToString();
        string? docId = doc["id()"]?.ToString();
        string? layoutId = doc["layoutId"]?.ToString();

        if (!string.IsNullOrEmpty(identifier) && !string.IsNullOrEmpty(docId))
        {
          result[identifier] = new OrphanCandidate(docId, layoutId);
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
