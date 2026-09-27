using System.Text.Json.Nodes;
using LayoutSync.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Unit tests for <see cref="DocumentSyncService.ContentEquals"/> — the check that lets a sync skip
/// a document whose stored content already matches its file, instead of rewriting it.
///
/// Until issue #24 the stored side reached this check through a lossy read that turned every
/// value into <c>[]</c>, so it never matched: every sync rewrote every document, and
/// <c>--dry-run</c> reported "Would UPDATE" for all of them (issue #11). The rules pinned here are
/// the ones that keep a real stored document equal to its unchanged file — and nothing else.
/// </summary>
public class ContentEqualsTests
{
    private const string File = """
        {"identifier":"home-hero","type":"ui-schema-section","active":true,"layoutId":"dirt-life",
         "data":{"type":"container","props":{"gap":2,"ratio":1.5},"children":[{"type":"text","id":"title"}]},
         "tags":[],"indexes":{}}
        """;

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>The file's content as RavenDB stores it: same body plus server-managed @metadata.</summary>
    private static JsonObject Stored(string json = File)
    {
        JsonObject stored = Parse(json);
        stored["@metadata"] = new JsonObject
        {
            ["@collection"] = "sections",
            ["@change-vector"] = "A:12-abc",
            ["@id"] = "Abc123_-Def456Ghi789x",
            ["@last-modified"] = "2026-09-27T00:00:00.0000000Z",
        };
        return stored;
    }

    // ── What must compare equal (the write would change nothing) ─────────────

    [Fact]
    public void StoredCopyOfUnchangedFile_IsEqual()
        => Assert.True(DocumentSyncService.ContentEquals(Stored(), Parse(File)));

    [Fact]
    public void IdentityFileMetadata_IsNotContent()
    {
        // Identity files name their document id in @metadata; the stored document carries the
        // server's full @metadata instead. Only the root @metadata is exempt from comparison.
        const string identityFile = """
            {"type":"identity","identifier":"viewer","active":true,
             "@metadata":{"@id":"CnfViewerIdentity0001xYz","@collection":"identities"}}
            """;

        Assert.True(DocumentSyncService.ContentEquals(Stored(identityFile), Parse(identityFile)));
    }

    [Theory]
    [InlineData("1.50", "1.5")]
    [InlineData("1e3", "1000.0")]
    [InlineData("2", "2.0")]
    [InlineData("1.0E-7", "1E-07")]
    public void NumberSpellings_ThatTheWriterNormalizes_AreEqual(string fileNumber, string storedNumber)
    {
        // The write path stores 1.50 as 1.5 and 1e3 as 1000.0 (observed against RavenDB 7.1).
        // A textual compare would re-write such a file on every sync and never converge.
        JsonObject stored = Stored($$"""{"identifier":"n","value":{{storedNumber}}}""");

        Assert.True(DocumentSyncService.ContentEquals(stored, Parse($$"""{"identifier":"n","value":{{fileNumber}}}""")));
    }

    [Fact]
    public void EscapedAndLiteralUnicode_AreEqual()
    {
        // System.Text.Json's default encoder escapes non-ASCII, so the file side spells the label
        // with \u escapes while the stored side holds the literal characters.
        string escapedLabel = System.Text.Json.JsonSerializer.Serialize("café ✓");
        Assert.DoesNotContain("é", escapedLabel);

        Assert.True(DocumentSyncService.ContentEquals(
            Stored("""{"identifier":"u","label":"café ✓"}"""),
            Parse($$"""{"identifier":"u","label":{{escapedLabel}}}""")));
    }

    [Fact]
    public void BothNull_IsEqual()
        => Assert.True(DocumentSyncService.ContentEquals(null, null));

    // ── What must compare unequal (a write is needed) ────────────────────────

    [Fact]
    public void LossyReadShapeFromIssue24_IsNeverEqual()
    {
        // The read this check used to receive: with no CLR type stored, RavenDB materializes the
        // document as a Newtonsoft JObject, and System.Text.Json serializes each JValue leaf of a
        // JObject as an empty array. Pins both the mechanism and that the shape can never pass.
        JsonObject lossy = Parse(System.Text.Json.JsonSerializer.Serialize(JObject.Parse(File)));

        Assert.Equal("[]", lossy["identifier"]!.ToJsonString());
        Assert.False(DocumentSyncService.ContentEquals(lossy, Parse(File)));
    }

    [Fact]
    public void ChangedNestedValue_IsNotEqual()
    {
        JsonObject file = Parse(File);
        file["data"]!["props"]!["gap"] = 3;

        Assert.False(DocumentSyncService.ContentEquals(Stored(), file));
    }

    [Fact]
    public void DifferentNumber_IsNotEqual()
    {
        JsonObject file = Parse(File);
        file["data"]!["props"]!["ratio"] = 1.51;

        Assert.False(DocumentSyncService.ContentEquals(Stored(), file));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimestampOnlyEdit_IsAChange(bool nested)
    {
        // Files carry hand-maintained createdDateTime / lastUpdatedDateTime (policies at the root,
        // entity payloads nested). These used to be ignored, which would now silently skip a
        // timestamp-only edit.
        const string before = """{"identifier":"p","lastUpdatedDateTime":"2026-07-05T00:00:00Z","data":{"lastUpdatedDateTime":"2026-07-05T00:00:00Z"}}""";
        JsonObject file = Parse(before);
        (nested ? file["data"]!.AsObject() : file)["lastUpdatedDateTime"] = "2026-07-12T00:00:00Z";

        Assert.False(DocumentSyncService.ContentEquals(Stored(before), file));
    }

    [Fact]
    public void NestedMetadataKey_IsContent()
        => Assert.False(DocumentSyncService.ContentEquals(
            Stored("""{"identifier":"m","data":{"@metadata":{"x":1}}}"""),
            Parse("""{"identifier":"m","data":{"@metadata":{"x":2}}}""")));

    [Fact]
    public void AddedProperty_IsNotEqual()
    {
        JsonObject file = Parse(File);
        file["data"]!["props"]!["padding"] = 4;

        Assert.False(DocumentSyncService.ContentEquals(Stored(), file));
    }

    [Fact]
    public void KeyOrderChange_IsNotEqual()
    {
        // The writer preserves file key order, so a reorder-only edit is a real change for any
        // consumer that iterates keys — it must be written, not skipped.
        Assert.False(DocumentSyncService.ContentEquals(
            Stored("""{"identifier":"o","routes":{"/a":1,"/b":2}}"""),
            Parse("""{"identifier":"o","routes":{"/b":2,"/a":1}}""")));
    }

    [Fact]
    public void ArrayOrderChange_IsNotEqual()
        => Assert.False(DocumentSyncService.ContentEquals(
            Stored("""{"identifier":"a","items":[1,2]}"""),
            Parse("""{"identifier":"a","items":[2,1]}""")));

    [Fact]
    public void StoredTypeArtifact_IsNotEqual()
    {
        // $type is a serializer artifact the writer never emits. It used to be ignored here, which
        // was harmless only while every document was rewritten anyway; with unchanged documents
        // skipped, ignoring it would keep a stored $type forever. A difference forces the rewrite
        // that cleans it — the reason sync replaces whole documents in the first place.
        JsonObject stored = Stored();
        stored["data"]!["$type"] = "System.Dynamic.ExpandoObject, System.Linq.Expressions";

        Assert.False(DocumentSyncService.ContentEquals(stored, Parse(File)));
    }

    [Fact]
    public void NullVersusMissing_IsNotEqual()
        => Assert.False(DocumentSyncService.ContentEquals(
            Stored("""{"identifier":"n","value":null}"""),
            Parse("""{"identifier":"n"}""")));

    [Fact]
    public void NumberVersusNumericString_IsNotEqual()
        => Assert.False(DocumentSyncService.ContentEquals(
            Stored("""{"identifier":"n","value":1}"""),
            Parse("""{"identifier":"n","value":"1"}""")));

    [Fact]
    public void OneSideNull_IsNotEqual()
    {
        Assert.False(DocumentSyncService.ContentEquals(null, Parse(File)));
        Assert.False(DocumentSyncService.ContentEquals(Stored(), null));
    }

    // ── Hygiene ───────────────────────────────────────────────────────────────

    [Fact]
    public void Comparison_DoesNotMutateEitherInput()
    {
        JsonObject stored = Stored();
        JsonObject file = Parse(File);
        string storedBefore = stored.ToJsonString();
        string fileBefore = file.ToJsonString();

        DocumentSyncService.ContentEquals(stored, file);

        Assert.Equal(storedBefore, stored.ToJsonString());
        Assert.Equal(fileBefore, file.ToJsonString());
    }
}
