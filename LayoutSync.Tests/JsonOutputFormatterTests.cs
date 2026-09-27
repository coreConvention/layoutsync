using System.Text.Json.Nodes;
using LayoutSync.Configuration;
using LayoutSync.Models;
using Xunit;

namespace LayoutSync.Tests;

public class JsonOutputFormatterTests
{
    [Fact]
    public void Format_TopLevelEnvelope_HasStableShape()
    {
        MutationResult empty = new(Success: true, Changes: [], Errors: [], Warnings: []);

        JsonObject envelope = JsonOutputFormatter.Format(
            command: "manifest set-route",
            layoutId: "dirt-life",
            dryRun: true,
            empty);

        Assert.Equal("manifest set-route", envelope["command"]?.GetValue<string>());
        Assert.Equal("dirt-life", envelope["layoutId"]?.GetValue<string>());
        Assert.True(envelope.ContainsKey("manifestPath"));
        Assert.True(envelope["dryRun"]?.GetValue<bool>());
        Assert.True(envelope["success"]?.GetValue<bool>());
        Assert.IsType<JsonArray>(envelope["changes"]);
        Assert.IsType<JsonArray>(envelope["warnings"]);
        Assert.IsType<JsonArray>(envelope["errors"]);
    }

    [Fact]
    public void Format_EchoesManifestPath()
    {
        string manifestPath = Path.Combine("layouts", "dirt-life", "manifests", "layout-manifest.json");
        MutationResult result = new(Success: true, Changes: [], Errors: [], Warnings: [])
        {
            ManifestPath = manifestPath,
        };

        JsonObject envelope = JsonOutputFormatter.Format("manifest set-route", "dirt-life", false, result);

        Assert.Equal(manifestPath, envelope["manifestPath"]?.GetValue<string>());
    }

    [Fact]
    public void Format_AppliedChange_SerializesAllFields()
    {
        JsonObject before = new() { ["structuralSection"] = "old" };
        JsonObject after = new() { ["structuralSection"] = "new" };
        JsonArray patch =
        [
            new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "/structuralSection",
                ["value"] = "new",
            },
        ];

        MutationResult result = new(
            Success: true,
            Changes:
            [
                new RouteChange(
                    Route: "/r",
                    Before: before,
                    After: after,
                    Patch: patch,
                    Status: RouteChangeStatus.Applied,
                    Error: null),
            ],
            Errors: [],
            Warnings: []);

        JsonObject envelope = JsonOutputFormatter.Format("manifest set-route", "dl", false, result);
        JsonArray changes = envelope["changes"]!.AsArray();
        JsonObject change = changes[0]!.AsObject();

        Assert.Equal("/r", change["route"]?.GetValue<string>());
        Assert.Equal("applied", change["status"]?.GetValue<string>());
        Assert.Equal("old", change["before"]!["structuralSection"]?.GetValue<string>());
        Assert.Equal("new", change["after"]!["structuralSection"]?.GetValue<string>());
        Assert.NotNull(change["patch"]);
        Assert.Null(change["error"]);
    }

    [Fact]
    public void Format_AbortedChange_HasErrorAndAbortedStatus()
    {
        MutationResult result = new(
            Success: false,
            Changes:
            [
                new RouteChange(
                    Route: "/r",
                    Before: null,
                    After: null,
                    Patch: null,
                    Status: RouteChangeStatus.Aborted,
                    Error: "section 'foo' not found in entities.sections."),
            ],
            Errors: [],
            Warnings: []);

        JsonObject envelope = JsonOutputFormatter.Format("manifest set-route", "dl", false, result);
        JsonObject change = envelope["changes"]!.AsArray()[0]!.AsObject();

        Assert.Equal("aborted", change["status"]?.GetValue<string>());
        Assert.Contains("foo", change["error"]?.GetValue<string>() ?? "");
        Assert.Null(change["before"]);
        Assert.Null(change["after"]);
        Assert.Null(change["patch"]);
    }

    [Fact]
    public void Format_TopLevelErrors_AreEnumerated()
    {
        MutationResult result = new(
            Success: false,
            Changes: [],
            Errors: ["manifest not found", "schema invalid"],
            Warnings: []);

        JsonObject envelope = JsonOutputFormatter.Format("manifest from-json", "dl", false, result);
        JsonArray errors = envelope["errors"]!.AsArray();

        Assert.Equal(2, errors.Count);
        Assert.Equal("manifest not found", errors[0]?.GetValue<string>());
        Assert.Equal("schema invalid", errors[1]?.GetValue<string>());
    }

    [Fact]
    public void FormatAsString_ProducesIndentedJson()
    {
        MutationResult empty = new(Success: true, Changes: [], Errors: [], Warnings: []);

        string json = JsonOutputFormatter.FormatAsString("c", "l", false, empty);

        // Pretty-printed JSON has newlines.
        Assert.Contains("\n", json);
        // Round-trip: re-parse and verify shape.
        JsonObject roundTripped = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("c", roundTripped["command"]?.GetValue<string>());
    }

    [Fact]
    public void Format_SectionEnvelope_KeepsEveryKeyEvenWhenNull()
    {
        SectionMutationResult refused = new(
            Success: false,
            ManifestPath: "/repo/layouts/dl/manifests/layout-manifest.json",
            Identifier: "hero",
            NewIdentifier: null,
            Before: null,
            After: null,
            Patch: null,
            References: [],
            FilesChanged: [],
            Errors: ["Section 'hero' is not declared in entities.sections."],
            Warnings: []);

        JsonObject envelope = JsonOutputFormatter.Format("manifest remove-section", "dl", false, refused);

        // Stable shape: a consumer never has to tell "missing" from "null".
        string[] expectedKeys =
        [
            "command", "layoutId", "manifestPath", "dryRun", "success", "identifier", "newIdentifier",
            "before", "after", "patch", "references", "filesChanged", "warnings", "errors",
        ];
        Assert.Equal(expectedKeys, envelope.Select(property => property.Key));
        Assert.Equal("/repo/layouts/dl/manifests/layout-manifest.json", envelope["manifestPath"]?.GetValue<string>());
        Assert.Equal("hero", envelope["identifier"]?.GetValue<string>());
        Assert.False(envelope["success"]?.GetValue<bool>());
        Assert.Single(envelope["errors"]!.AsArray());
    }

    [Theory]
    [InlineData(RouteChangeStatus.Applied, "applied")]
    [InlineData(RouteChangeStatus.Skipped, "skipped")]
    [InlineData(RouteChangeStatus.Aborted, "aborted")]
    public void ToWireStatus_MapsToLowercase(RouteChangeStatus status, string expected)
    {
        Assert.Equal(expected, JsonOutputFormatter.ToWireStatus(status));
    }
}
