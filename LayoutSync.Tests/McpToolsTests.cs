using System.Text.Json.Nodes;
using LayoutSync.Mcp;
using LayoutSync.Mcp.Tools;
using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Validates the MCP tool surface end-to-end: each tool method takes its declared
/// inputs, calls into <see cref="ManifestMutationService"/>, and emits the expected
/// JSON shape. The MCP SDK's transport / dispatch is exercised by the SDK's own
/// tests; this suite focuses on what we wrote — the input mapping and output
/// formatting.
/// </summary>
public class McpToolsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _layoutsPath;
    private readonly LayoutsPathProvider _pathProvider;
    private readonly LocalFileService _fileService;
    private readonly ManifestSectionValidator _validator;
    private readonly ManifestMutationService _mutationService;
    private const string LayoutId = "test-layout";

    public McpToolsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"mcp-tools-tests-{Guid.NewGuid()}");
        _layoutsPath = Path.Combine(_tempRoot, "layouts");
        Directory.CreateDirectory(_layoutsPath);

        _pathProvider = new LayoutsPathProvider(_layoutsPath);
        _fileService = new LocalFileService(NullLogger<LocalFileService>.Instance);
        _validator = new ManifestSectionValidator(NullLogger<ManifestSectionValidator>.Instance);
        _mutationService = new ManifestMutationService(
            _fileService, _validator, NullLogger<ManifestMutationService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ManifestSetRoute_ReturnsValidJsonEnvelope()
    {
        WriteFixture();
        ManifestTools tools = new(_mutationService, _pathProvider);

        string output = await tools.ManifestSetRoute(
            layoutId: LayoutId,
            route: "/events",
            structuralSection: "full-width-layout",
            dryRun: true);

        JsonObject envelope = JsonNode.Parse(output)!.AsObject();
        Assert.Equal("manifest set-route", envelope["command"]?.GetValue<string>());
        Assert.Equal(LayoutId, envelope["layoutId"]?.GetValue<string>());
        Assert.True(envelope["dryRun"]?.GetValue<bool>());
        Assert.True(envelope["success"]?.GetValue<bool>());
        Assert.Single(envelope["changes"]!.AsArray());
    }

    [Fact]
    public async Task ManifestSetRoute_TranslatesRemovePatchArray()
    {
        WriteFixture();
        ManifestTools tools = new(_mutationService, _pathProvider);

        string output = await tools.ManifestSetRoute(
            layoutId: LayoutId,
            route: "/events",
            removePatch: ["sidebar"],
            dryRun: true);

        JsonObject envelope = JsonNode.Parse(output)!.AsObject();
        JsonObject change = envelope["changes"]!.AsArray()[0]!.AsObject();
        // Sidebar should be removed in the after-state
        JsonArray? patches = change["after"]?["patches"]?.AsArray();
        Assert.NotNull(patches);
        bool hasSidebar = patches.Any(p =>
            p is JsonObject po
            && po["targetElementId"]?.GetValue<string>() == "sidebar");
        Assert.False(hasSidebar);
    }

    [Fact]
    public async Task ManifestApplyBatch_RespectsSkipMode()
    {
        WriteFixture();
        ManifestTools tools = new(_mutationService, _pathProvider);

        string output = await tools.ManifestApplyBatch(
            layoutId: LayoutId,
            patches:
            [
                new ManifestTools.BatchPatch(
                    Route: "/events",
                    StructuralSection: "full-width-layout"),
                new ManifestTools.BatchPatch(
                    Route: "/new-route",
                    MainSections: ["bogus-section-typo"]),
            ],
            onError: "skip",
            dryRun: true);

        JsonObject envelope = JsonNode.Parse(output)!.AsObject();
        Assert.True(envelope["success"]?.GetValue<bool>());
        JsonArray changes = envelope["changes"]!.AsArray();
        Assert.Equal(2, changes.Count);
        // Valid patch applied
        Assert.Equal("applied",
            changes.First(c => c!["route"]?.GetValue<string>() == "/events")!["status"]?.GetValue<string>());
        // Invalid patch skipped (not aborted, since mode=skip)
        Assert.Equal("skipped",
            changes.First(c => c!["route"]?.GetValue<string>() == "/new-route")!["status"]?.GetValue<string>());
    }

    [Fact]
    public async Task ManifestListRoutes_ReturnsAllDeclaredRoutes()
    {
        WriteFixture();
        ManifestReadTools tools = new(_fileService, _pathProvider);

        string output = await tools.ManifestListRoutes(LayoutId);
        JsonObject envelope = JsonNode.Parse(output)!.AsObject();

        Assert.Equal(LayoutId, envelope["layoutId"]?.GetValue<string>());
        JsonArray routes = envelope["routes"]!.AsArray();
        Assert.Single(routes);
        JsonObject route = routes[0]!.AsObject();
        Assert.Equal("/events", route["route"]?.GetValue<string>());
        Assert.Equal("sidebar-layout", route["structuralSection"]?.GetValue<string>());
        JsonArray slots = route["slots"]!.AsArray();
        Assert.Equal(2, slots.Count);
        Assert.Contains(slots, s => s?.GetValue<string>() == "main");
        Assert.Contains(slots, s => s?.GetValue<string>() == "sidebar");
    }

    [Fact]
    public async Task ManifestGetRoute_ReturnsFullConfigWhenPresent()
    {
        WriteFixture();
        ManifestReadTools tools = new(_fileService, _pathProvider);

        string output = await tools.ManifestGetRoute(LayoutId, "/events");
        JsonObject envelope = JsonNode.Parse(output)!.AsObject();

        Assert.True(envelope["found"]?.GetValue<bool>());
        Assert.NotNull(envelope["config"]);
        Assert.Equal("sidebar-layout",
            envelope["config"]?["structuralSection"]?.GetValue<string>());
    }

    [Fact]
    public async Task ManifestGetRoute_ReturnsFoundFalseWhenAbsent()
    {
        WriteFixture();
        ManifestReadTools tools = new(_fileService, _pathProvider);

        string output = await tools.ManifestGetRoute(LayoutId, "/nonexistent");
        JsonObject envelope = JsonNode.Parse(output)!.AsObject();

        Assert.False(envelope["found"]?.GetValue<bool>());
        Assert.Null(envelope["config"]);
    }

    [Fact]
    public async Task ManifestListSections_ReturnsAllDeclaredSections()
    {
        WriteFixture();
        ManifestReadTools tools = new(_fileService, _pathProvider);

        string output = await tools.ManifestListSections(LayoutId);
        JsonObject envelope = JsonNode.Parse(output)!.AsObject();

        JsonArray sections = envelope["sections"]!.AsArray();
        Assert.Equal(6, sections.Count);
        // Each item has at minimum identifier + type
        foreach (JsonNode? section in sections)
        {
            Assert.NotNull(section?["identifier"]);
            Assert.NotNull(section?["type"]);
        }
    }

    private void WriteFixture()
    {
        string manifestDir = Path.Combine(_layoutsPath, LayoutId, "manifests");
        Directory.CreateDirectory(manifestDir);
        File.WriteAllText(
            Path.Combine(manifestDir, "layout-manifest.json"),
            StandardFixture);
    }

    private const string StandardFixture = """
        {
          "identifier": "test-layout",
          "entities": {
            "sections": [
              { "identifier": "full-width-layout", "type": "ui-schema-section" },
              { "identifier": "sidebar-layout", "type": "ui-schema-section" },
              { "identifier": "events-page-header", "type": "ui-schema-section" },
              { "identifier": "event-filters-panel", "type": "ui-schema-section" },
              { "identifier": "my-rsvps-list", "type": "ui-schema-section" },
              { "identifier": "sidebar-user-summary", "type": "ui-schema-section" }
            ]
          },
          "routeConfigs": {
            "/events": {
              "structuralSection": "sidebar-layout",
              "patches": [
                {
                  "targetElementId": "main",
                  "sectionIdentifiers": ["events-page-header", "event-filters-panel"]
                },
                {
                  "targetElementId": "sidebar",
                  "sectionIdentifiers": ["sidebar-user-summary"]
                }
              ]
            }
          }
        }
        """;
}
