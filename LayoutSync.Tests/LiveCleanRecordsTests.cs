using System.Text.Json.Nodes;
using LayoutSync.Configuration;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Opt-in tests of <c>--clean-records</c> (issue #62) against a real RavenDB server, for what the
/// in-memory store cannot show: that the server-side filter
/// (<c>layoutId = $l or primaryLayoutId = $l or layoutIds = $l</c>) finds a record through each
/// field on its own, including an array entry; that a query run right after a write sees it; and
/// that a delete against the change vector the query read is refused once the record has changed.
/// Skipped unless <see cref="LiveRavenDbFactAttribute.UrlVariable"/> names a local server; each
/// test creates its own throwaway database and deletes it afterwards.
/// </summary>
public sealed class LiveCleanRecordsTests : IAsyncLifetime
{
    private const string Layout = "test-layout";
    private const string OtherLayout = "other-layout";

    private readonly string _url = Environment.GetEnvironmentVariable(LiveRavenDbFactAttribute.UrlVariable) ?? string.Empty;
    private readonly string _database = $"layoutsync-test-{Guid.NewGuid():N}";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"layoutsync-test-{Guid.NewGuid():N}");
    private IDocumentStore? _admin;
    private RavenDbService? _raven;

    private string LayoutsPath => Path.Combine(_root, "layouts");

    public async Task InitializeAsync()
    {
        if (!LiveRavenDbFactAttribute.IsEnabled)
            return;

        _admin = new DocumentStore { Urls = [_url] }.Initialize();
        await _admin.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));
        _raven = new RavenDbService(NullLogger<RavenDbService>.Instance, new RavenDbOptions { Url = _url, Database = _database });
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
    public async Task TheQuery_FindsARecordThroughEachLayoutFieldOnItsOwn_RightAfterItWasWritten()
    {
        await StoreAsync("identities", new()
        {
            ["OnlyLayoutId"] = """{"layoutId":"test-layout"}""",
            ["OnlyPrimary"] = """{"primaryLayoutId":"test-layout"}""",
            ["OnlyLayoutIds"] = """{"layoutIds":["test-layout"]}""",
            ["SecondArrayEntry"] = """{"layoutIds":["other-layout","test-layout"]}""",
            ["OtherLayout"] = """{"layoutIds":["other-layout"],"primaryLayoutId":"other-layout"}""",
            ["PrefixOnly"] = """{"layoutId":"test-layout-2"}""",
        });

        // No index wait here: the query must wait for itself.
        IReadOnlyList<RavenDbService.RecordCandidate> found = await _raven!.GetRecordCandidatesAsync("identities", Layout);

        Assert.Equal(
            ["OnlyLayoutId", "OnlyLayoutIds", "OnlyPrimary", "SecondArrayEntry"],
            found.Select(c => c.DocumentId).Order(StringComparer.Ordinal));
        Assert.All(found, c => Assert.False(string.IsNullOrEmpty(c.ChangeVector)));

        // The shared record is found, and then kept by the decision code.
        Assert.Equal(
            ["OnlyLayoutId", "OnlyLayoutIds", "OnlyPrimary"],
            RecordCleanup.SelectOrphans(found, Layout, new HashSet<string>(), new HashSet<string>())
                .Select(c => c.DocumentId).Order(StringComparer.Ordinal));
    }

    [LiveRavenDbFact]
    public async Task ADeleteAgainstTheQueriedVersion_IsRefusedOnceTheRecordJoinedAnotherLayout()
    {
        await StoreAsync("identities", new() { ["Minted"] = """{"layoutIds":["test-layout"],"primaryLayoutId":"test-layout"}""" });
        RavenDbService.RecordCandidate judged = Assert.Single(await _raven!.GetRecordCandidatesAsync("identities", Layout));

        await _admin!.Operations.ForDatabase(_database).SendAsync(new PatchOperation(
            "Minted", changeVector: null, new PatchRequest { Script = "this.layoutIds.push('other-layout');" }));

        Assert.False(await _raven.DeleteRecordAsync(judged.DocumentId, judged.ChangeVector));
        Assert.True(await ExistsAsync("Minted"));
    }

    [LiveRavenDbFact]
    public async Task ASyncWithCleanRecords_LeavesExactlyTheTreesRecords()
    {
        string viewer = Path.Combine(LayoutsPath, Layout, "identities", "viewer.json");
        Directory.CreateDirectory(Path.GetDirectoryName(viewer)!);
        File.WriteAllText(viewer, """
            {"type":"identity","identifier":"viewer","layoutIds":["test-layout"],"primaryLayoutId":"test-layout",
             "@metadata":{"@id":"ViewerPinned","@collection":"identities"}}
            """);
        await StoreAsync("entities", new()
        {
            ["Leftover"] = """{"identifier":"old-item","layoutId":"test-layout"}""",
            ["Runtime"] = """{"layoutId":"test-layout"}""",
            ["Elsewhere"] = """{"identifier":"trip","layoutId":"other-layout"}""",
        });
        await StoreAsync("identities", new()
        {
            ["Minted"] = """{"layoutIds":["test-layout"],"primaryLayoutId":"test-layout"}""",
            ["Shared"] = """{"layoutIds":["test-layout","other-layout"],"primaryLayoutId":"other-layout"}""",
        });

        CommandLineArgs args = new() { Layout = Layout, SyncOnce = true, Clean = true, CleanRecords = true, PreserveIds = true };
        SyncBatchResult batch = await new DocumentSyncService(
                NullLogger<DocumentSyncService>.Instance,
                new LocalFileService(NullLogger<LocalFileService>.Instance),
                _raven!,
                new RelativeDateResolver(NullLogger<RelativeDateResolver>.Instance),
                [],
                new SyncOptions(),
                args)
            .SyncAllAsync(LayoutsPath, Layout, dryRun: false, cleanOrphans: true);

        Assert.Equal(0, batch.FailedCount);
        Assert.Equal(3, batch.OrphanDeletedCount);
        Assert.False(await ExistsAsync("Leftover"));
        Assert.False(await ExistsAsync("Runtime"));
        Assert.False(await ExistsAsync("Minted"));
        Assert.True(await ExistsAsync("ViewerPinned"));
        Assert.True(await ExistsAsync("Shared"));
        Assert.True(await ExistsAsync("Elsewhere"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores each document as raw JSON through the REST API. Storing a CLR dictionary through the
    /// client instead wraps every <c>object</c>-typed array as <c>{"$type":…,"$values":[…]}</c>,
    /// which is not how the platform stores a <c>layoutIds</c> array.
    /// </summary>
    private async Task StoreAsync(string collection, Dictionary<string, string> documents)
    {
        using HttpClient http = new();
        foreach ((string id, string json) in documents)
        {
            JsonObject body = JsonNode.Parse(json)!.AsObject();
            body["@metadata"] = new JsonObject { ["@collection"] = collection };
            using HttpResponseMessage response = await http.PutAsync(
                $"{_url.TrimEnd('/')}/databases/{_database}/docs?id={Uri.EscapeDataString(id)}",
                new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task<bool> ExistsAsync(string id)
    {
        using IAsyncDocumentSession session = _admin!.OpenAsyncSession(_database);
        return await session.Advanced.ExistsAsync(id);
    }
}
