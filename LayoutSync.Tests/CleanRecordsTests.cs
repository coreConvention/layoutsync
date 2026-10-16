using System.Text.Json.Nodes;
using LayoutSync.Configuration;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Issue #62: <c>--clean-records</c> extends a scoped <c>--clean</c> to the scoped layout's
/// entities and identities, so a test database's records end up exactly what the tree defines
/// (the w31rd e2e gate resets its <c>__conformance__</c> layout this way before each run, w31rd
/// #3963). The contract: without the flag nothing changes; with it, a record is deleted only when
/// it belongs to the scoped layout and no other, and no file of the run declares it.
/// </summary>
public sealed class CleanRecordsTests : IDisposable
{
    private const string Layout = "__conformance__";
    private const string OtherLayout = "dirt-life";

    private static readonly string Entities = DocumentType.Entity.GetCollection();
    private static readonly string Identities = DocumentType.Identity.GetCollection();

    private readonly string _root;
    private readonly string _layoutsPath;
    private readonly CapturingLogger _log = new();

    public CleanRecordsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "layoutsync-tests-" + Guid.NewGuid().ToString("N"));
        _layoutsPath = Path.Combine(_root, "layouts");
        Directory.CreateDirectory(_layoutsPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static CommandLineArgs Args(bool cleanRecords = true, string[]? excludeCollections = null) => new()
    {
        Layout = Layout,
        SyncOnce = true,
        Clean = true,
        CleanRecords = cleanRecords,
        PreserveIds = true,
        ExcludeCollections = excludeCollections ?? [],
    };

    private Task<SyncBatchResult> SyncAsync(RavenDbService store, CommandLineArgs args, bool dryRun = false) =>
        new DocumentSyncService(
                _log,
                new LocalFileService(NullLogger<LocalFileService>.Instance),
                store,
                new RelativeDateResolver(NullLogger<RelativeDateResolver>.Instance),
                [],
                new SyncOptions(),
                args)
            .SyncAllAsync(_layoutsPath, args.Layout, dryRun, cleanOrphans: args.Clean);

    private void WriteFile(string folder, string name, JsonObject content)
    {
        string directory = Path.Combine(_layoutsPath, Layout, folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), content.ToJsonString());
    }

    private static JsonObject Entity(string identifier, string layoutId, string? pinnedId = null)
    {
        JsonObject entity = new()
        {
            ["type"] = "conf-item",
            ["identifier"] = identifier,
            ["layoutId"] = layoutId,
            ["data"] = new JsonObject { ["title"] = identifier },
        };
        if (pinnedId != null)
            entity["@metadata"] = new JsonObject { ["@id"] = pinnedId, ["@collection"] = "entities" };
        return entity;
    }

    /// <summary>An identity as the platform mints it: no layoutId field, only layoutIds and primaryLayoutId.</summary>
    private static JsonObject MintedIdentity(params string[] layoutIds) => new()
    {
        ["type"] = "identity",
        ["layoutIds"] = new JsonArray([.. layoutIds.Select(id => (JsonNode)JsonValue.Create(id))]),
        ["primaryLayoutId"] = layoutIds[0],
        ["data"] = new JsonObject(),
    };

    private static JsonObject SeededIdentity(string identifier, string pinnedId) => new()
    {
        ["type"] = "identity",
        ["identifier"] = identifier,
        ["layoutId"] = Layout,
        ["layoutIds"] = new JsonArray(JsonValue.Create(Layout)),
        ["primaryLayoutId"] = Layout,
        ["@metadata"] = new JsonObject { ["@id"] = pinnedId, ["@collection"] = "identities" },
    };

    /// <summary>
    /// The w31rd #3963 situation: the tree defines one entity and one identity; the database also
    /// holds an earlier tree's entity, a record a run created at runtime (no identifier), a
    /// platform-minted identity, and another layout's records.
    /// </summary>
    private InMemoryRavenDbService SeedLeftovers(InMemoryRavenDbService store)
    {
        WriteFile("entities", "item-alpha.json", Entity("conf-item-alpha", Layout, pinnedId: "AlphaItemPinned"));
        WriteFile("identities", "viewer.json", SeededIdentity("conf-viewer", "ViewerIdentityPinned"));

        store.Seed(Entities, "OldTreeItem", Entity("conf-item-removed", Layout));
        store.Seed(Entities, "RuntimeItem", new JsonObject { ["type"] = "conf-item", ["layoutId"] = Layout });
        store.Seed(Identities, "MintedIdentity", MintedIdentity(Layout));
        store.Seed(Entities, "OtherLayoutItem", Entity("trip-report", OtherLayout));
        store.Seed(Identities, "OtherLayoutIdentity", MintedIdentity(OtherLayout));
        return store;
    }

    // ── End to end ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CleanRecords_DeletesTheLayoutsLeftoverRecords_KeepsTheTreesOwn_AndLeavesOtherLayoutsAlone()
    {
        using InMemoryRavenDbService store = SeedLeftovers(new InMemoryRavenDbService());

        SyncBatchResult batch = await SyncAsync(store, Args());

        Assert.Equal(["AlphaItemPinned", "OtherLayoutItem"], store.DocumentsIn(Entities).Keys.Order());
        Assert.Equal(["OtherLayoutIdentity", "ViewerIdentityPinned"], store.DocumentsIn(Identities).Keys.Order());
        Assert.Equal(3, batch.OrphanDeletedCount);
        Assert.Equal(0, batch.FailedCount);
        Assert.Contains(_log.Entries, e => e.Message == $"Deleted orphan record: conf-item-removed (OldTreeItem) from {Entities}");
        Assert.Contains(_log.Entries, e => e.Message == $"Deleted orphan record:  (MintedIdentity) from {Identities}");
    }

    [Fact]
    public async Task WithoutCleanRecords_ARecordIsNeverDeleted()
    {
        using InMemoryRavenDbService store = SeedLeftovers(new InMemoryRavenDbService());

        SyncBatchResult batch = await SyncAsync(store, Args(cleanRecords: false));

        Assert.Equal(4, store.DocumentsIn(Entities).Count);
        Assert.Equal(3, store.DocumentsIn(Identities).Count);
        Assert.Equal(0, batch.OrphanDeletedCount);
    }

    [Fact]
    public async Task ARecordWhoseFileIsStoredUnderAnotherId_IsKeptByItsIdentifier()
    {
        using InMemoryRavenDbService store = new();
        WriteFile("entities", "item-alpha.json", Entity("conf-item-alpha", Layout, pinnedId: "AlphaItemPinned"));
        // Created before the pin existed: the sync updates it in place and keeps its id.
        store.Seed(Entities, "AlphaItemLegacyId", Entity("conf-item-alpha", Layout));

        await SyncAsync(store, Args());

        Assert.Equal(["AlphaItemLegacyId"], store.DocumentsIn(Entities).Keys);
    }

    [Fact]
    public async Task AnIdentitySharedWithAnotherLayout_IsKept()
    {
        using InMemoryRavenDbService store = new();
        WriteFile("identities", "viewer.json", SeededIdentity("conf-viewer", "ViewerIdentityPinned"));
        store.Seed(Identities, "SharedIdentity", MintedIdentity(Layout, OtherLayout));

        await SyncAsync(store, Args());

        Assert.True(store.DocumentsIn(Identities).ContainsKey("SharedIdentity"));
    }

    [Fact]
    public async Task DryRun_ReportsTheRecords_AndDeletesNothing()
    {
        using InMemoryRavenDbService store = SeedLeftovers(new InMemoryRavenDbService());

        SyncBatchResult batch = await SyncAsync(store, Args(), dryRun: true);

        // Exactly the seeded leftovers: a dry run neither deletes them nor creates the tree's own records.
        Assert.Equal(["OldTreeItem", "OtherLayoutItem", "RuntimeItem"], store.DocumentsIn(Entities).Keys.Order());
        Assert.Equal(["MintedIdentity", "OtherLayoutIdentity"], store.DocumentsIn(Identities).Keys.Order());
        Assert.Equal(0, batch.OrphanDeletedCount);
        Assert.Equal(["OldTreeItem", "RuntimeItem"], batch.OrphansDetected[Entities].Keys.Order());
        Assert.Equal(["MintedIdentity"], batch.OrphansDetected[Identities].Keys);
        Assert.Contains(_log.Entries, e => e.Message == $"Would delete orphan record: conf-item-removed (OldTreeItem) from {Entities}");
    }

    [Fact]
    public async Task AnExcludedRecordCollection_IsLeftAlone()
    {
        using InMemoryRavenDbService store = SeedLeftovers(new InMemoryRavenDbService());

        await SyncAsync(store, Args(excludeCollections: ["identities"]));

        Assert.Equal(["AlphaItemPinned", "OtherLayoutItem"], store.DocumentsIn(Entities).Keys.Order());
        Assert.True(store.DocumentsIn(Identities).ContainsKey("MintedIdentity"));
    }

    [Fact]
    public async Task AnUnreadableFile_SkipsTheRecordClean()
    {
        using InMemoryRavenDbService store = SeedLeftovers(new InMemoryRavenDbService());
        File.WriteAllText(Path.Combine(_layoutsPath, Layout, "entities", "broken.json"), "{ not json");

        SyncBatchResult batch = await SyncAsync(store, Args());

        Assert.True(store.DocumentsIn(Entities).ContainsKey("OldTreeItem"));
        Assert.True(store.DocumentsIn(Identities).ContainsKey("MintedIdentity"));
        Assert.Equal(0, batch.OrphanDeletedCount);
        Assert.Contains(_log.Entries, e => e.Message.StartsWith("--clean-records skipped: 1 file(s) could not be read"));
    }

    [Fact]
    public async Task AFailedDelete_IsAFailedResult_AndTheRunExitsFive()
    {
        using FailingStore store = new(failQuery: false);
        SeedLeftovers(store);

        SyncBatchResult batch = await SyncAsync(store, Args());

        Assert.Equal(3, batch.OrphanDeleteFailedCount);
        Assert.Equal(SyncFailureGate.ExitCode, SyncFailureGate.ExitCodeFor(batch.FailedCount, strictExitCode: 0));
        Assert.True(store.DocumentsIn(Entities).ContainsKey("OldTreeItem"));
    }

    [Fact]
    public async Task AFailedQuery_IsAFailedResult_NotACleanThatFoundNothing()
    {
        using FailingStore store = new(failQuery: true);
        SeedLeftovers(store);

        SyncBatchResult batch = await SyncAsync(store, Args());

        Assert.Equal(2, batch.FailedCount);
        Assert.All(batch.Results.Where(r => !r.Success), r => Assert.StartsWith("Could not query", r.ErrorMessage));
        Assert.Equal(SyncFailureGate.ExitCode, SyncFailureGate.ExitCodeFor(batch.FailedCount, strictExitCode: 0));
    }

    private sealed class FailingStore(bool failQuery) : InMemoryRavenDbService
    {
        public override Task<IReadOnlyList<RecordCandidate>> GetRecordCandidatesAsync(
            string collection, string layoutId, CancellationToken ct = default) =>
            failQuery
                ? throw new InvalidOperationException("index unavailable")
                : base.GetRecordCandidatesAsync(collection, layoutId, ct);

        public override Task<bool> DeleteRecordAsync(string documentId, string? expectedChangeVector, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    [Fact]
    public async Task ARecordThatJoinsAnotherLayoutAfterTheQuery_IsNotDeleted()
    {
        using JoinsAnotherLayoutAfterQueryStore store = new();
        SeedLeftovers(store);

        SyncBatchResult batch = await SyncAsync(store, Args());

        // The identity was judged while it named only the scoped layout; the delete is refused.
        Assert.True(store.DocumentsIn(Identities).ContainsKey("MintedIdentity"));
        SyncResult refused = Assert.Single(batch.Results, r => !r.Success);
        Assert.Equal(SyncAction.OrphanDeleted, refused.Action);
        Assert.Equal(DocumentType.Identity, refused.Document.DocumentType);
        Assert.Equal(2, batch.OrphanDeletedCount);
    }

    /// <summary>The app adds the identity to another layout between the query and the delete.</summary>
    private sealed class JoinsAnotherLayoutAfterQueryStore : InMemoryRavenDbService
    {
        public override async Task<IReadOnlyList<RecordCandidate>> GetRecordCandidatesAsync(
            string collection, string layoutId, CancellationToken ct = default)
        {
            IReadOnlyList<RecordCandidate> candidates = await base.GetRecordCandidatesAsync(collection, layoutId, ct);
            if (collection == Identities)
                Seed(Identities, "MintedIdentity", MintedIdentity(Layout, OtherLayout));
            return candidates;
        }
    }

    [Fact]
    public async Task ARecordWithAMalformedLayoutField_IsKept_AndReported()
    {
        using InMemoryRavenDbService store = new();
        WriteFile("entities", "item-alpha.json", Entity("conf-item-alpha", Layout, pinnedId: "AlphaItemPinned"));
        store.Seed(Identities, "ScalarLayoutIds", JsonNode.Parse("""{"layoutId":"__conformance__","layoutIds":"dirt-life"}""")!.AsObject());

        await SyncAsync(store, Args());

        Assert.True(store.DocumentsIn(Identities).ContainsKey("ScalarLayoutIds"));
        Assert.Contains(_log.Entries, e => e.Message.StartsWith("--clean-records kept ScalarLayoutIds in identities"));
    }

    // ── Pure rules ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"layoutId":"__conformance__"}""", true)]
    [InlineData("""{"layoutIds":["__conformance__"],"primaryLayoutId":"__conformance__"}""", true)]
    [InlineData("""{"layoutId":"__conformance__","layoutIds":["__conformance__","dirt-life"]}""", false)]
    [InlineData("""{"layoutId":"__conformance__","primaryLayoutId":"dirt-life"}""", false)]
    [InlineData("""{"layoutId":"dirt-life"}""", false)]
    [InlineData("""{"layoutId":"","layoutIds":[]}""", false)]
    [InlineData("""{}""", false)]
    [InlineData("""{"layoutId":"__conformance__","layoutIds":"dirt-life"}""", false)]
    [InlineData("""{"layoutId":"__conformance__","primaryLayoutId":["dirt-life"]}""", false)]
    [InlineData("""{"layoutId":"__conformance__","layoutIds":["__conformance__",7]}""", false)]
    [InlineData("""{"layoutId":"__conformance__","layoutIds":[null]}""", false)]
    [InlineData("""{"layoutId":"__conformance__","primaryLayoutId":null}""", true)]
    public void BelongsOnlyTo_NeedsEveryLayoutTheRecordNamesToBeTheScope(string json, bool expected) =>
        Assert.Equal(expected, RecordCleanup.BelongsOnlyTo(RecordCleanup.ReadLayoutRefs(JsonNode.Parse(json)!.AsObject()), Layout));

    [Fact]
    public void ValidateArgs_AllowsTheGateCombination_AndIgnoresRunsWithoutTheFlag()
    {
        Assert.Null(RecordCleanup.ValidateArgs(Args()));
        Assert.Null(RecordCleanup.ValidateArgs(new CommandLineArgs()));
    }

    [Fact]
    public void ValidateArgs_RefusesEveryCombinationThatCouldReachMoreThanOneLayoutOrRunInWatchMode()
    {
        Assert.Contains("needs --clean", RecordCleanup.ValidateArgs(new CommandLineArgs { CleanRecords = true, Layout = Layout, SyncOnce = true }));
        Assert.Contains("needs --layout", RecordCleanup.ValidateArgs(new CommandLineArgs { CleanRecords = true, Clean = true, SyncOnce = true }));
        Assert.Contains("needs --sync-once", RecordCleanup.ValidateArgs(new CommandLineArgs { CleanRecords = true, Clean = true, Layout = Layout }));
        Assert.Contains("needs --sync-once", RecordCleanup.ValidateArgs(new CommandLineArgs { CleanRecords = true, Clean = true, Layout = Layout, SyncOnce = true, ValidateOnly = true }));
    }

    [Theory]
    [InlineData("http://localhost:8080", false)]
    [InlineData("http://127.0.0.1:8080", false)]
    [InlineData("https://a.cc-dev.example.ravendb.cloud", true)]
    [InlineData(null, true)]
    public void RefusesTarget_EverythingButALocalDatabase(string? url, bool refused) =>
        Assert.Equal(refused, RecordCleanup.RefusesTarget(url));

    private sealed class CapturingLogger : ILogger<DocumentSyncService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
