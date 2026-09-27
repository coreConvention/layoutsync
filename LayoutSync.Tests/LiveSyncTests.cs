using System.Text.Json.Nodes;
using coreConvention.Core.Validation;
using LayoutSync.Configuration;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Sparrow.Json;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Opt-in end-to-end tests of sync idempotency against a real RavenDB server (issues #24 / #11):
/// unchanged files cost no write, a changed file is overwritten in place with exactly one write
/// (never delete + create), dry-run is a true diff, a stale <c>$type</c> gets cleaned, and an edit
/// made after the comparison is not clobbered.
///
/// These cover what unit tests cannot: that the lookup really reads documents losslessly through
/// the RavenDB client. Skipped unless <see cref="LiveRavenDbFactAttribute.UrlVariable"/> names a
/// LOCAL, unsecured server (e.g. <c>http://localhost:8080</c>). Each test creates its own
/// throwaway database and deletes it afterwards.
/// </summary>
public sealed class LiveSyncTests : IAsyncLifetime
{
    private const string Layout = "test-layout";

    private readonly string _url = Environment.GetEnvironmentVariable(LiveRavenDbFactAttribute.UrlVariable) ?? string.Empty;
    private readonly string _database = $"layoutsync-test-{Guid.NewGuid():N}";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"layoutsync-test-{Guid.NewGuid():N}");
    private IDocumentStore? _admin;
    private RavenDbService? _raven;
    private DocumentSyncService? _sync;

    private string LayoutsPath => Path.Combine(_root, "layouts");
    private string HeroPath => Path.Combine(LayoutsPath, Layout, "sections", "hero.json");
    private string PolicyPath => Path.Combine(LayoutsPath, Layout, "write-policies", "policy.json");
    private string ViewerPath => Path.Combine(LayoutsPath, Layout, "identities", "viewer.json");

    public async Task InitializeAsync()
    {
        // xUnit skips via the attribute; this guard only protects a run that bypasses it.
        if (!LiveRavenDbFactAttribute.IsEnabled)
            return;

        _admin = new DocumentStore { Urls = [_url] }.Initialize();
        await _admin.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));

        // A layout-agnostic section and a layout-scoped (layoutId-stamped) write policy, with a
        // non-canonical number (1.50 is stored as 1.5) and non-ASCII text in the mix — plus an
        // identity, which is looked up by id rather than by query and names that id in @metadata.
        WriteFile(HeroPath, $$$"""
            {"id":"{{{NanoIdValidator.GenerateNanoId()}}}","identifier":"hero","type":"ui-schema-section","active":true,
             "data":{"type":"container","props":{"gap":2,"ratio":1.50},"children":[{"type":"text","content":"café"}]},
             "tags":[],"indexes":{}}
            """);
        WriteFile(PolicyPath, $$$"""
            {"id":"{{{NanoIdValidator.GenerateNanoId()}}}","identifier":"policy","type":"write-policy",
             "data":{"rules":[{"field":"title","allow":true}]}}
            """);
        WriteFile(ViewerPath, $$$"""
            {"type":"identity","identifier":"viewer","active":true,"layoutIds":["{{{Layout}}}"],
             "data":{"displayName":"Test Viewer"},
             "@metadata":{"@id":"{{{NanoIdValidator.GenerateNanoId()}}}","@collection":"identities"}}
            """);

        _raven = new RavenDbService(
            NullLogger<RavenDbService>.Instance,
            new RavenDbOptions { Url = _url, Database = _database });
        _sync = new DocumentSyncService(
            NullLogger<DocumentSyncService>.Instance,
            new LocalFileService(NullLogger<LocalFileService>.Instance),
            _raven,
            new RelativeDateResolver(NullLogger<RelativeDateResolver>.Instance),
            [],
            new SyncOptions(),
            new CommandLineArgs { PreserveIds = true });
    }

    public async Task DisposeAsync()
    {
        _raven?.Dispose();
        if (_admin is not null)
        {
            await _admin.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_database, hardDelete: true));
            _admin.Dispose();
        }

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [LiveRavenDbFact]
    public async Task SecondSyncOfUnchangedFiles_WritesNothing()
    {
        SyncBatchResult first = await _sync!.SyncAllAsync(LayoutsPath);
        Assert.All(first.Results, r => Assert.Equal(SyncAction.Created, r.Action));
        await WaitForIndexingAsync();

        long before = await LastDocEtagAsync();
        SyncBatchResult second = await _sync.SyncAllAsync(LayoutsPath);

        Assert.All(second.Results, r => Assert.Equal(SyncAction.Unchanged, r.Action));
        Assert.Equal(before, await LastDocEtagAsync());
    }

    [LiveRavenDbFact]
    public async Task ChangedFile_IsReplacedInPlaceWithExactlyOneWrite()
    {
        SyncBatchResult first = await _sync!.SyncAllAsync(LayoutsPath);
        string heroId = Result(first, "hero").RavenDocumentId!;
        await WaitForIndexingAsync();
        EditHeroProps(props => props["gap"] = 3);

        long before = await LastDocEtagAsync();
        SyncBatchResult second = await _sync.SyncAllAsync(LayoutsPath);

        SyncResult hero = Result(second, "hero");
        Assert.Equal(SyncAction.Replaced, hero.Action);
        Assert.Equal(heroId, hero.RavenDocumentId);
        Assert.Equal(SyncAction.Unchanged, Result(second, "policy").Action);
        Assert.Equal(SyncAction.Unchanged, Result(second, "viewer").Action);
        // One PUT consumes one etag (the throwaway database has no revisions configured). The old
        // delete + create consumed two (tombstone, then put) and left the document absent between.
        Assert.Equal(before + 1, await LastDocEtagAsync());
        Assert.Equal(3, (await LoadStoredAsync(heroId))["data"]!["props"]!["gap"]!.GetValue<int>());
    }

    [LiveRavenDbFact]
    public async Task DryRun_ReportsWouldUpdateOnlyForTheChangedFile()
    {
        await _sync!.SyncAllAsync(LayoutsPath);
        await WaitForIndexingAsync();
        EditHeroProps(props => props["gap"] = 3);

        long before = await LastDocEtagAsync();
        SyncBatchResult dryRun = await _sync.SyncAllAsync(LayoutsPath, dryRun: true);

        SyncResult hero = Result(dryRun, "hero");
        Assert.Equal(SyncAction.Skipped, hero.Action);
        Assert.Equal("Dry run: Would UPDATE", hero.ErrorMessage);
        Assert.Equal(SyncAction.Unchanged, Result(dryRun, "policy").Action);
        Assert.Equal(before, await LastDocEtagAsync());
    }

    [LiveRavenDbFact]
    public async Task StoredTypeArtifact_IsCleanedByTheNextSync()
    {
        SyncBatchResult first = await _sync!.SyncAllAsync(LayoutsPath);
        string heroId = Result(first, "hero").RavenDocumentId!;
        await PatchStoredAsync(heroId, "this.data['$type'] = 'System.Dynamic.ExpandoObject';");
        await WaitForIndexingAsync();

        SyncBatchResult second = await _sync.SyncAllAsync(LayoutsPath);

        Assert.Equal(SyncAction.Replaced, Result(second, "hero").Action);
        Assert.False((await LoadStoredAsync(heroId))["data"]!.AsObject().ContainsKey("$type"));
    }

    [LiveRavenDbFact]
    public async Task EditMadeAfterTheComparison_IsNotOverwritten()
    {
        await _sync!.SyncAllAsync(LayoutsPath);
        await WaitForIndexingAsync();
        SyncDocument hero = (await new LocalFileService(NullLogger<LocalFileService>.Instance)
            .ReadDocumentAsync(HeroPath, LayoutsPath))!;
        (string? heroId, JsonObject? _, string? comparedChangeVector) = await _raven!.FindDocumentAsync(hero);

        // Another writer lands between the sync's comparison and its write.
        await PatchStoredAsync(heroId!, "this.data.props.gap = 99;");

        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            _raven.ReplaceDocumentAsync(heroId!, comparedChangeVector, hero, hero.Content!));
        Assert.Equal(99, (await LoadStoredAsync(heroId!))["data"]!["props"]!["gap"]!.GetValue<int>());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SyncResult Result(SyncBatchResult batch, string identifier)
        => Assert.Single(batch.Results, r => r.Document.Identifier == identifier);

    private static void WriteFile(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private void EditHeroProps(Action<JsonObject> edit)
    {
        JsonObject hero = JsonNode.Parse(File.ReadAllText(HeroPath))!.AsObject();
        edit(hero["data"]!["props"]!.AsObject());
        File.WriteAllText(HeroPath, hero.ToJsonString());
    }

    private async Task<long> LastDocEtagAsync()
    {
        DatabaseStatistics stats = await _admin!.Maintenance.ForDatabase(_database)
            .SendAsync(new GetStatisticsOperation());
        return stats.LastDocEtag ?? 0;
    }

    /// <summary>
    /// The sync looks documents up by query, served from auto-indexes. Back-to-back syncs in a
    /// test run milliseconds apart, so wait for those indexes to catch up before the next pass.
    /// </summary>
    private async Task WaitForIndexingAsync()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            DatabaseStatistics stats = await _admin!.Maintenance.ForDatabase(_database)
                .SendAsync(new GetStatisticsOperation());
            if (stats.Indexes.All(index => !index.IsStale))
                return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"RavenDB indexes in {_database} stayed stale for 10s.");
    }

    private async Task<JsonObject> LoadStoredAsync(string id)
    {
        using IAsyncDocumentSession session = _admin!.OpenAsyncSession(_database);
        BlittableJsonReaderObject stored = await session.LoadAsync<BlittableJsonReaderObject>(id);
        return RavenDbService.ToJsonObject(stored)!;
    }

    private Task PatchStoredAsync(string id, string script)
        => _admin!.Operations.ForDatabase(_database)
            .SendAsync(new PatchOperation(id, changeVector: null, new PatchRequest { Script = script }));
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips unless <see cref="UrlVariable"/> names a RavenDB server
/// the CLI itself would treat as local (<see cref="ProductionTargetGuard.Classify"/>: loopback or a
/// private-network address), so the opt-in cannot point these tests at a cloud cluster. They only
/// ever create and drop their own uniquely named databases, never touching existing ones.
/// </summary>
public sealed class LiveRavenDbFactAttribute : FactAttribute
{
    public const string UrlVariable = "LAYOUTSYNC_TEST_RAVENDB_URL";

    public static bool IsEnabled =>
        ProductionTargetGuard.Classify(Environment.GetEnvironmentVariable(UrlVariable))
            == ProductionTargetClassification.Local;

    public LiveRavenDbFactAttribute()
    {
        if (!IsEnabled)
            Skip = $"Set {UrlVariable} to a local, unsecured RavenDB server (e.g. http://localhost:8080) to run live sync tests.";
    }
}
