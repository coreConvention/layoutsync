using System.Text.Json.Nodes;
using LayoutSync.Configuration;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutSync.Tests;

/// <summary>
/// A <see cref="RavenDbService"/> whose storage primitives live in memory, so tests can drive the
/// real <see cref="DocumentSyncService"/> and the real lookup resolution end to end without a
/// RavenDB server. Only the primitives are replaced; every decision about WHICH document a file
/// maps to runs the production code. Mirrors RavenDB where it matters to those decisions:
/// identifier matching is case-insensitive, and a missing <c>layoutId</c> reads as <c>""</c>.
/// Change vectors are not modelled (always null, which disables the replace guard).
/// </summary>
/// <remarks>
/// The base constructor builds a real, never-used DocumentStore; initializing one opens no
/// connection, so the unroutable URL is never dialed.
/// </remarks>
internal sealed class InMemoryRavenDbService(ILogger<RavenDbService>? logger = null)
    : RavenDbService(
        logger ?? NullLogger<RavenDbService>.Instance,
        new RavenDbOptions { Url = "http://127.0.0.1:1", Database = "in-memory" })
{
    private readonly Dictionary<string, (string Collection, JsonObject Content)> _documents =
        new(StringComparer.OrdinalIgnoreCase);
    private int _nextId;

    /// <summary>Puts a document straight into the store, bypassing sync (pre-existing database state).</summary>
    public void Seed(string collection, string documentId, JsonObject content) =>
        _documents[documentId] = (collection, content);

    /// <summary>Every stored document in <paramref name="collection"/>, by id.</summary>
    public IReadOnlyDictionary<string, JsonObject> DocumentsIn(string collection) =>
        _documents
            .Where(kvp => kvp.Value.Collection == collection)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Content);

    protected override Task<(string? DocumentId, JsonObject? Document, string? ChangeVector)> LoadByIdAsync(
        string id,
        CancellationToken ct) =>
        Task.FromResult<(string?, JsonObject?, string?)>(
            _documents.TryGetValue(id, out (string Collection, JsonObject Content) stored)
                ? (id, stored.Content.DeepClone().AsObject(), null)
                : (null, null, null));

    protected override Task<IReadOnlyList<LookupCandidate>> QueryByIdentifierAsync(
        string collection,
        string identifier,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LookupCandidate>>(
        [
            .. _documents
                .Where(kvp => kvp.Value.Collection == collection
                    && string.Equals(kvp.Value.Content["identifier"]?.GetValue<string>(), identifier, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => new LookupCandidate(
                    kvp.Key,
                    SyncDocument.ReadLayoutIdField(kvp.Value.Content),
                    kvp.Value.Content.DeepClone().AsObject())),
        ]);

    public override Task<string?> CreateDocumentAsync(
        SyncDocument doc,
        JsonObject content,
        string? existingDocId = null,
        CancellationToken ct = default)
    {
        string documentId = existingDocId ?? $"doc-{++_nextId}";
        _documents[documentId] = (doc.DocumentType.GetCollection(), content.DeepClone().AsObject());
        return Task.FromResult<string?>(documentId);
    }

    public override Task<string?> ReplaceDocumentAsync(
        string documentId,
        string? expectedChangeVector,
        SyncDocument doc,
        JsonObject newContent,
        CancellationToken ct = default)
    {
        _documents[documentId] = (doc.DocumentType.GetCollection(), newContent.DeepClone().AsObject());
        return Task.FromResult<string?>(documentId);
    }

    public override Task<bool> DeleteDocumentAsync(string documentId, CancellationToken ct = default) =>
        Task.FromResult(_documents.Remove(documentId));

    public override Task<Dictionary<string, OrphanCandidate>> GetAllOrphanCandidatesAsync(
        string collection,
        CancellationToken ct = default) =>
        Task.FromResult(
            _documents
                .Where(kvp => kvp.Value.Collection == collection
                    && !string.IsNullOrEmpty(kvp.Value.Content["identifier"]?.GetValue<string>()))
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => new OrphanCandidate(
                        kvp.Key,
                        SyncDocument.ReadLayoutIdField(kvp.Value.Content),
                        kvp.Value.Content["identifier"]!.GetValue<string>())));
}
