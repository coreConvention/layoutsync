using System.Text;
using System.Text.Json.Nodes;
using LayoutSync.Services;
using Sparrow.Json;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Unit tests for <see cref="RavenDbService.ToJsonObject"/> — how the sync turns a stored RavenDB
/// document into JSON before comparing it with the file (issue #24). Uses a real
/// <see cref="BlittableJsonReaderObject"/>, RavenDB's stored-document format, built in memory, so
/// no server is needed. The live end-to-end path is covered by <see cref="LiveSyncTests"/>.
/// </summary>
public class StoredDocumentReadTests
{
    private const string StoredJson = """
        {"identifier":"home-hero","active":true,"count":3,"ratio":1.5,"label":"café",
         "data":{"z":1,"a":[1,"x",null]},
         "@metadata":{"@collection":"sections","@id":"Abc123_-Def456Ghi789x"}}
        """;

    private static async Task<JsonObject?> ReadAsync(string json)
    {
        using JsonOperationContext context = JsonOperationContext.ShortTermSingleUse();
        using BlittableJsonReaderObject blittable = await context.ReadForMemoryAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(json)), "stored-document");
        return RavenDbService.ToJsonObject(blittable);
    }

    [Fact]
    public async Task ToJsonObject_KeepsEveryValueAndTheKeyOrder()
    {
        JsonObject? json = await ReadAsync(StoredJson);

        Assert.NotNull(json);
        Assert.Equal(JsonNode.Parse(StoredJson)!.ToJsonString(), json!.ToJsonString());
    }

    [Fact]
    public async Task ToJsonObject_KeepsMetadataAndAddsNoPhantomId()
    {
        // The old read went through `object` and came back with an extra "Id" property that no
        // file has — on its own enough to make every comparison fail.
        JsonObject? json = await ReadAsync(StoredJson);

        Assert.True(json!.ContainsKey("@metadata"));
        Assert.False(json.ContainsKey("Id"));
    }

    [Fact]
    public async Task StoredDocumentRead_MatchesItsUnchangedFile()
    {
        // End of the chain the sync relies on: stored JSON → ToJsonObject → ContentEquals.
        JsonObject file = JsonNode.Parse(StoredJson)!.AsObject();
        file.Remove("@metadata");

        Assert.True(DocumentSyncService.ContentEquals(await ReadAsync(StoredJson), file));
    }
}
