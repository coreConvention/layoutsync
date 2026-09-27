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
using Sparrow.Json;

namespace LayoutSync.Services;

/// <summary>
/// Service for interacting with RavenDB.
/// Handles CRUD operations for entities and identities.
/// </summary>
/// <remarks>
/// The storage primitives (<see cref="LoadByIdAsync"/>, <see cref="QueryByIdentifierAsync"/>,
/// <see cref="CreateDocumentAsync"/>, <see cref="ReplaceDocumentAsync"/>, <see cref="DeleteDocumentAsync"/>,
/// <see cref="GetAllOrphanCandidatesAsync"/>) are virtual so tests can substitute an in-memory
/// store and drive the real lookup and sync decisions end to end. Everything that decides WHICH
/// document a file maps to stays non-virtual.
/// </remarks>
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
        // CreateDocumentAsync catches and re-tries once; ReplaceDocumentAsync
        // writes against the change vector it compared and fails on a mismatch.
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
  /// Exact lookup, with no fallback to an unattributed document: what a single file can safely
  /// resolve on its own (watch mode, deletions). See
  /// <see cref="FindDocumentAsync(SyncDocument, AdoptionScope, CancellationToken)"/>.
  /// </summary>
  public Task<(string? DocumentId, JsonObject? Document, string? ChangeVector)> FindDocumentAsync(
    SyncDocument doc,
    CancellationToken ct = default
  ) => FindDocumentAsync(doc, adoption: null, ct);

  /// <summary>
  /// Looks up the stored document a local file maps to: by id for identities (the id lives in
  /// @metadata.@id, not a top-level field), by identifier for everything else. Layout-scoped
  /// types (<see cref="DocumentTypeExtensions.IsLayoutScoped"/>) are then narrowed to the
  /// document carrying the file's <see cref="SyncDocument.StoredLayoutId"/> — see
  /// <see cref="ResolveLayoutScopedLookup"/>.
  /// </summary>
  /// <param name="doc">The local file's document.</param>
  /// <param name="adoption">Permission to fall back to an unattributed document; null disables it.
  /// Only a full sync run can grant it, because only a run knows which documents its other files
  /// claim (see <see cref="AdoptionScope"/>).</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>
  /// The document id, the stored document exactly as persisted (including <c>@metadata</c>), and
  /// its change vector — the version <see cref="ReplaceDocumentAsync"/> writes against, so a
  /// document modified after it was compared is never silently overwritten. All null when absent.
  /// </returns>
  /// <remarks>
  /// Documents are read as raw <see cref="BlittableJsonReaderObject"/>, never as <c>object</c>.
  /// With no CLR type stored (<c>FindClrTypeName</c> returns null above), <c>object</c>
  /// materializes a Newtonsoft <c>JObject</c>; System.Text.Json then serializes each of its
  /// <c>JValue</c> leaves as <c>[]</c> (it enumerates JToken children), and the client adds a
  /// phantom <c>Id</c> property. Every stored document came back as
  /// <c>{"identifier":[],...,"Id":[]}</c>, so the sync's equality check could never match and
  /// every document was deleted and recreated on every sync (issue #24). The blittable is the
  /// stored JSON verbatim — values, key order and <c>@metadata</c> intact.
  /// </remarks>
  public async Task<(string? DocumentId, JsonObject? Document, string? ChangeVector)> FindDocumentAsync(
    SyncDocument doc,
    AdoptionScope? adoption,
    CancellationToken ct = default
  )
  {
    // Route to correct collection based on document type
    string collection = doc.DocumentType.GetCollection();
    string lookupValue = doc.LookupKey;

    if (string.IsNullOrEmpty(lookupValue))
    {
      _logger.LogWarning("Cannot lookup document without identifier/id");
      return (null, null, null);
    }

    try
    {
      // For identities, load directly by document ID (stored in @metadata.@id)
      // since identities don't have a top-level "id" field in the document
      if (doc.DocumentType == DocumentType.Identity)
      {
        return await LoadByIdAsync(lookupValue, ct);
      }

      // For everything else, fetch every document with the identifier — whatever its layoutId —
      // and resolve in memory.
      // NOTE: Historical seed uploads have been known to leave multiple entity documents
      // with the same Identifier in the DB (see issue #282). Entity orphan cleanup is
      // intentionally disabled to protect user data, so we CANNOT auto-delete extras —
      // but we must at least detect and warn so these ghosts do not persist silently.
      IReadOnlyList<LookupCandidate> candidates = await QueryByIdentifierAsync(collection, lookupValue, ct);

      DuplicateEntityLookupResult resolved = doc.DocumentType.IsLayoutScoped()
        ? ResolveLayoutScopedLookup(collection, lookupValue, doc.StoredLayoutId, candidates, adoption, _logger)
        : ResolveEntityLookup(collection, lookupValue, [.. candidates.Select(c => (c.DocumentId, c.Document))], _logger);

      if (resolved.IsDuplicate)
      {
        DuplicateEntityIdentifierCount++;
      }

      if (resolved.DocumentId == null)
      {
        _logger.LogDebug(
          "Document not found: identifier={LookupValue}",
          lookupValue
        );
        return (null, null, null);
      }

      // The change vector of whichever candidate was picked, for the guarded in-place replace.
      string? changeVector = candidates.First(c => c.DocumentId == resolved.DocumentId).ChangeVector;
      return (resolved.DocumentId, resolved.Document, changeVector);
    }
    catch (Exception ex)
    {
      // NOTE: reported as "not found", so the caller creates a document — a transient error can
      // therefore mint a duplicate (issue #32).
      _logger.LogError(
        ex,
        "Error looking up document: {LookupValue}",
        lookupValue
      );
      return (null, null, null);
    }
  }

  /// <summary>
  /// Storage primitive: loads one document by its id. Virtual — like the other storage
  /// primitives — so tests can substitute an in-memory store and drive the real lookup and sync
  /// decisions end to end; production has exactly one implementation.
  /// </summary>
  protected virtual async Task<(string? DocumentId, JsonObject? Document, string? ChangeVector)> LoadByIdAsync(
    string id,
    CancellationToken ct
  )
  {
    using IAsyncDocumentSession session = _store.OpenAsyncSession();
    BlittableJsonReaderObject? loaded = await session.LoadAsync<BlittableJsonReaderObject>(id, ct);
    if (loaded == null)
    {
      _logger.LogDebug("Identity not found by id: {Id}", id);
      return (null, null, null);
    }

    string? docId = session.Advanced.GetDocumentId(loaded);

    _logger.LogDebug("Found identity by id: {DocumentId}", docId);
    return (docId, ToJsonObject(loaded), session.Advanced.GetChangeVectorFor(loaded));
  }

  /// <summary>
  /// Storage primitive: every document in <paramref name="collection"/> whose <c>identifier</c>
  /// matches <paramref name="identifier"/> (RavenDB equality is case-insensitive), whatever its
  /// <c>layoutId</c> — the caller resolves among them. Each carries its stored layoutId, read from
  /// the lossless blittable, and its change vector. Virtual for in-memory test stores.
  /// </summary>
  protected virtual async Task<IReadOnlyList<LookupCandidate>> QueryByIdentifierAsync(
    string collection,
    string identifier,
    CancellationToken ct
  )
  {
    using IAsyncDocumentSession session = _store.OpenAsyncSession();
    List<BlittableJsonReaderObject> documents = await session
      .Advanced.AsyncRawQuery<BlittableJsonReaderObject>($"from {collection} where identifier = $lookupValue")
      .AddParameter("lookupValue", identifier)
      .ToListAsync(ct);

    List<LookupCandidate> candidates = [];
    foreach (BlittableJsonReaderObject item in documents)
    {
      string? itemDocId = session.Advanced.GetDocumentId(item);
      if (string.IsNullOrEmpty(itemDocId))
      {
        continue;
      }

      JsonObject? json = ToJsonObject(item);
      candidates.Add(new LookupCandidate(
        itemDocId,
        SyncDocument.ReadLayoutIdField(json),
        json,
        session.Advanced.GetChangeVectorFor(item)));
    }

    return candidates;
  }

  /// <summary>
  /// Pure helper: converts a stored document to a <see cref="JsonObject"/> by parsing the
  /// blittable's own JSON text, which is lossless (see <see cref="FindDocumentAsync(SyncDocument, AdoptionScope, CancellationToken)"/>'s
  /// remarks for why a CLR round-trip is not). Keeps key order and <c>@metadata</c>, adds nothing.
  /// </summary>
  internal static JsonObject? ToJsonObject(BlittableJsonReaderObject document) =>
    JsonNode.Parse(document.ToString())?.AsObject();

  /// <summary>
  /// A stored document <see cref="FindDocumentAsync(SyncDocument, AdoptionScope, CancellationToken)"/>
  /// can resolve to: its id, the <c>layoutId</c> it carries (<c>""</c> when none), its content as
  /// read, and its change vector.
  /// </summary>
  public sealed record LookupCandidate(string DocumentId, string LayoutId, JsonObject? Document, string? ChangeVector = null);

  /// <summary>
  /// Permission for <see cref="ResolveLayoutScopedLookup"/> to fall back to an UNATTRIBUTED
  /// document — one stored without a <c>layoutId</c> — when no document carries the file's
  /// layoutId. This is the migration path for documents written before their file declared a
  /// layoutId: without it, the first layout-scoped sync would leave the old document behind and
  /// create a second one beside it (issue #28). A sync run grants it only to a file of a type that
  /// newly scopes its lookup (<see cref="DocumentTypeExtensions.AdoptsUnattributedDocuments"/>),
  /// and only when no file declares that identifier WITHOUT a layoutId — such a file is the
  /// unattributed document's rightful owner. <see cref="ClaimedDocumentIds"/> are documents other
  /// files already resolved to this run; they are never adopted.
  /// </summary>
  public sealed record AdoptionScope(IReadOnlySet<string> ClaimedDocumentIds);

  /// <summary>
  /// Pure helper: resolves a layout-scoped lookup among every document sharing the identifier.
  /// Identity is (identifier, stored layoutId), so a document matches only when its layoutId
  /// equals <paramref name="layoutId"/> — compared case-insensitively, like RavenDB query equality.
  /// A document carrying ANOTHER layout's id is never matched: that is how two layouts' copies of
  /// one identifier stay two documents instead of clobbering one (issues #16, #28).
  /// <list type="number">
  ///   <item><description>Exact: documents carrying <paramref name="layoutId"/> — the first wins,
  ///   and more than one is a duplicate (<see cref="ResolveEntityLookup"/>). With an empty
  ///   <paramref name="layoutId"/> (a file that declares none) only unattributed documents match,
  ///   which is identifier-only matching for genuinely shared documents, minus the ability to
  ///   capture a tenant's document.</description></item>
  ///   <item><description>Adoption, only when <paramref name="adoption"/> is granted and the file has
  ///   a layoutId: the unclaimed unattributed documents, first wins, more than one is a duplicate.
  ///   The write re-attributes the adopted document to this layout.</description></item>
  ///   <item><description>Otherwise nothing matches, and the caller creates the file's own
  ///   document.</description></item>
  /// </list>
  /// </summary>
  public static DuplicateEntityLookupResult ResolveLayoutScopedLookup(
    string collection,
    string lookupValue,
    string layoutId,
    IReadOnlyList<LookupCandidate> candidates,
    AdoptionScope? adoption,
    ILogger logger
  )
  {
    List<(string DocId, JsonObject? Json)> exact =
    [
      .. candidates
        .Where(c => string.Equals(c.LayoutId, layoutId, StringComparison.OrdinalIgnoreCase))
        .Select(c => (c.DocumentId, c.Document)),
    ];
    if (exact.Count > 0)
    {
      return ResolveEntityLookup(collection, lookupValue, exact, logger);
    }

    if (adoption is not null && layoutId.Length > 0)
    {
      List<(string DocId, JsonObject? Json)> unattributed =
      [
        .. candidates
          .Where(c => c.LayoutId.Length == 0 && !adoption.ClaimedDocumentIds.Contains(c.DocumentId))
          .Select(c => (c.DocumentId, c.Document)),
      ];
      if (unattributed.Count > 0)
      {
        logger.LogWarning(
          "Adopting unattributed document {DocumentId} ('{Identifier}' in {Collection}) for layoutId '{LayoutId}': it was stored without a layoutId, and this sync re-attributes it instead of creating a second document. See issue #28.",
          unattributed[0].DocId,
          lookupValue,
          collection,
          layoutId
        );
        return ResolveEntityLookup(collection, lookupValue, unattributed, logger);
      }
    }

    if (candidates.Count > 0)
    {
      // Expected once per identifier on the first layout-scoped sync of a database where another
      // layout's copy won the old identifier-only lookup; afterwards this layout's own document
      // exists and matches exactly.
      logger.LogInformation(
        "No document for '{Identifier}' in {Collection} has layoutId {LayoutId}; the {Count} existing one(s) (layoutId {ExistingLayoutIds}) are left untouched and this file gets its own document.",
        lookupValue,
        collection,
        DescribeLayoutId(layoutId),
        candidates.Count,
        string.Join(", ", candidates.Select(c => DescribeLayoutId(c.LayoutId)).Distinct())
      );
    }

    return new DuplicateEntityLookupResult(null, null, IsDuplicate: false);
  }

  private static string DescribeLayoutId(string layoutId) =>
    layoutId.Length > 0 ? $"'{layoutId}'" : "(none)";

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
  /// <param name="existingDocId">Optional: the @id to create under (the file's id under --preserve-ids); a fresh NanoID otherwise.</param>
  /// <param name="ct">Cancellation token.</param>
  public virtual async Task<string?> CreateDocumentAsync(
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
  /// Information about a candidate orphan returned by <see cref="GetAllOrphanCandidatesAsync"/>.
  /// <see cref="LayoutId"/> is the <c>layoutId</c> the stored document carries, empty when it
  /// carries none: stamped for the per-tenant collections (WritePolicies, ReadPolicies,
  /// entity-configs, email-templates, theme-definitions), and whatever the source file declared for
  /// everything else — sections, manifests and the other non-stamped collections carry one exactly
  /// when their file does (issue #31). Used by orphan-scope filtering to keep <c>--clean</c> +
  /// <c>--layout</c> combos safe across tenants. See issue #427.
  /// </summary>
  /// <remarks>
  /// RavenDB's dynamic projection surfaces a MISSING <c>layoutId</c> as the empty string, never
  /// C# null (issue #13) — treat null and "" identically for attribution. <see cref="Identifier"/>
  /// is a trailing parameter (default "") only so pre-existing 2-arg constructions still compile;
  /// <see cref="GetAllOrphanCandidatesAsync"/> always populates it. Orphan detection keys on
  /// (<see cref="LayoutId"/>, <see cref="Identifier"/>) because two layouts may legitimately ship
  /// the same identifier in one collection (issue #17).
  /// </remarks>
  public sealed record OrphanCandidate(string DocumentId, string? LayoutId, string Identifier = "");

  /// <summary>
  /// Gets every document in a static collection as an <see cref="OrphanCandidate"/>
  /// (identifier + stored <c>layoutId</c>), keyed by its unique RavenDB document id.
  /// Used for orphan detection in static collections.
  /// </summary>
  /// <remarks>
  /// Keyed by DOCUMENT ID, not identifier: two layouts may ship the same identifier in one
  /// collection (issue #17), and keying by identifier would silently collapse them to one
  /// entry — hiding one physical document from orphan detection entirely. The document id is
  /// unique, so every document is represented; callers match by (layoutId, identifier).
  /// </remarks>
  /// <param name="collection">The RavenDB collection name (e.g., "Sections").</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>Dictionary mapping document id to its <see cref="OrphanCandidate"/>.</returns>
  public virtual async Task<Dictionary<string, OrphanCandidate>> GetAllOrphanCandidatesAsync(
    string collection,
    CancellationToken ct = default
  )
  {
    using IAsyncDocumentSession session = _store.OpenAsyncSession();
    Dictionary<string, OrphanCandidate> result = [];

    try
    {
      // Project identifier, document id, and (optional) layoutId so callers can scope
      // orphan detection by tenant. Documents without a layoutId field return the empty
      // string (RavenDB dynamic projection, issue #13), not null — the projection succeeds
      // rather than throwing for missing fields.
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
          result[docId] = new OrphanCandidate(docId, layoutId, identifier);
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
  public virtual async Task<bool> DeleteDocumentAsync(string documentId, CancellationToken ct = default)
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
  /// Replaces an entire document in place: one PUT of the full body at the same @id.
  /// </summary>
  /// <remarks>
  /// A full-document PUT drops every property the new content lacks — stale <c>$type</c>
  /// artifacts included, the reason this path used to delete and re-create (8052cd9) — without
  /// the delete. Delete + create ran as two transactions, so the document was absent in between
  /// (readers could find nothing mid-sync) and each document fired a delete plus a put on the
  /// RavenDB Changes API.
  ///
  /// <paramref name="expectedChangeVector"/> is the version the caller compared against: RavenDB
  /// rejects the PUT with <see cref="ConcurrencyException"/> if anything modified the document
  /// since, so a concurrent edit is surfaced instead of silently overwritten. Deliberately not
  /// retried (unlike <see cref="CreateDocumentAsync"/>): a retry would re-apply this content over
  /// a version it never compared. The next sync re-reads and re-compares instead. Null disables
  /// the check.
  /// </remarks>
  public virtual async Task<string?> ReplaceDocumentAsync(
    string documentId,
    string? expectedChangeVector,
    SyncDocument doc,
    JsonObject newContent,
    CancellationToken ct = default
  )
  {
    string collection = doc.DocumentType.GetCollection();

    try
    {
      using IAsyncDocumentSession session = _store.OpenAsyncSession();
      ExpandoObject entity = System.Text.Json.JsonSerializer.Deserialize<ExpandoObject>(newContent.ToJsonString(), ExpandoSerializerOptions)
        ?? new ExpandoObject();

      await session.StoreAsync(entity, expectedChangeVector, documentId, ct);
      IMetadataDictionary metadata = session.Advanced.GetMetadataFor(entity);
      metadata["@collection"] = collection;
      await session.SaveChangesAsync(ct);

      _logger.LogInformation("Replaced document: {DocumentId} in {Collection}", documentId, collection);
      return documentId;
    }
    catch (ConcurrencyException ex)
    {
      _logger.LogWarning(
        "Document {DocumentId} in {Collection} changed after it was compared, so it was not overwritten. Re-run the sync (or save the file again in watch mode) to re-compare. {Message}",
        documentId, collection, ex.Message);
      throw;
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
