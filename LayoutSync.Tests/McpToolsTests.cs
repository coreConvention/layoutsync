using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using LayoutSync.Mcp;
using LayoutSync.Mcp.Tools;
using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
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
    private readonly ManifestSectionRegistryService _registryService;
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
        _registryService = new ManifestSectionRegistryService(
            _fileService, NullLogger<ManifestSectionRegistryService>.Instance);
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

    // ───── target checkout: layoutsPath + manifestPath echo (issue #29) ─────
    //
    // Scenario under test: the server was launched in checkout A (_layoutsPath, the
    // default) while the session edits checkout B. Without layoutsPath every call
    // silently targeted A; these tests pin that B is reachable, that A stays untouched
    // when B is named, and that every response says which file it used.

    [Fact]
    public async Task ManifestSetRoute_WithLayoutsPath_WritesThatCheckoutAndLeavesDefaultUntouched()
    {
        WriteFixture();
        string otherLayouts = WriteFixtureInOtherCheckout();
        ManifestTools tools = new(_mutationService, _pathProvider);

        string output = await tools.ManifestSetRoute(
            layoutId: LayoutId,
            route: "/events",
            structuralSection: "full-width-layout",
            layoutsPath: Path.GetDirectoryName(otherLayouts)); // checkout-root form

        JsonObject envelope = JsonNode.Parse(output)!.AsObject();
        Assert.True(envelope["success"]?.GetValue<bool>());
        Assert.Equal(ManifestPathIn(otherLayouts), envelope["manifestPath"]?.GetValue<string>());
        Assert.Empty(envelope["warnings"]!.AsArray());
        Assert.Contains("full-width-layout", File.ReadAllText(ManifestPathIn(otherLayouts)));
        Assert.Equal(StandardFixture, File.ReadAllText(ManifestPathIn(_layoutsPath)));
    }

    [Fact]
    public async Task ManifestApplyBatch_WithLayoutsPath_WritesThatCheckoutAndLeavesDefaultUntouched()
    {
        WriteFixture();
        string otherLayouts = WriteFixtureInOtherCheckout();
        ManifestTools tools = new(_mutationService, _pathProvider);

        string output = await tools.ManifestApplyBatch(
            layoutId: LayoutId,
            patches: [new ManifestTools.BatchPatch(Route: "/events", MainSections: ["my-rsvps-list"])],
            layoutsPath: otherLayouts); // layouts-directory form

        JsonObject envelope = JsonNode.Parse(output)!.AsObject();
        Assert.True(envelope["success"]?.GetValue<bool>());
        Assert.Equal(ManifestPathIn(otherLayouts), envelope["manifestPath"]?.GetValue<string>());
        Assert.Empty(envelope["warnings"]!.AsArray());
        Assert.Contains("my-rsvps-list", File.ReadAllText(ManifestPathIn(otherLayouts)));
        Assert.Equal(StandardFixture, File.ReadAllText(ManifestPathIn(_layoutsPath)));
    }

    [Fact]
    public async Task ManifestSetRoute_WithoutLayoutsPath_EchoesDefaultAndWarnsBeforeWriting()
    {
        WriteFixture();
        ManifestTools tools = new(_mutationService, _pathProvider);

        string output = await tools.ManifestSetRoute(
            layoutId: LayoutId,
            route: "/events",
            structuralSection: "full-width-layout",
            dryRun: true);

        JsonObject envelope = JsonNode.Parse(output)!.AsObject();
        string defaultManifest = ManifestPathIn(_layoutsPath);
        Assert.Equal(defaultManifest, envelope["manifestPath"]?.GetValue<string>());
        string warning = Assert.Single(envelope["warnings"]!.AsArray())!.GetValue<string>();
        Assert.Contains("layoutsPath was not passed", warning);
        Assert.Contains($"targeted {defaultManifest}", warning);
    }

    [Fact]
    public async Task ManifestApplyBatch_WithoutLayoutsPath_RealWrite_WarningSaysTheFileWasWritten()
    {
        WriteFixture();
        ManifestTools tools = new(_mutationService, _pathProvider);

        string output = await tools.ManifestApplyBatch(
            layoutId: LayoutId,
            patches: [new ManifestTools.BatchPatch(Route: "/events", StructuralSection: "full-width-layout")]);

        JsonObject envelope = JsonNode.Parse(output)!.AsObject();
        string defaultManifest = ManifestPathIn(_layoutsPath);
        Assert.Equal(defaultManifest, envelope["manifestPath"]?.GetValue<string>());
        string warning = Assert.Single(envelope["warnings"]!.AsArray())!.GetValue<string>();
        Assert.Contains($"wrote to {defaultManifest}", warning);
    }

    [Fact]
    public async Task ManifestSetRoute_ManifestMissingInNamedCheckout_ReportsThePathItTried()
    {
        WriteFixture();
        string emptyLayouts = Path.Combine(_tempRoot, "checkout-without-layout", "layouts");
        Directory.CreateDirectory(emptyLayouts);
        ManifestTools tools = new(_mutationService, _pathProvider);

        string output = await tools.ManifestSetRoute(
            layoutId: LayoutId,
            route: "/events",
            structuralSection: "full-width-layout",
            layoutsPath: emptyLayouts);

        JsonObject envelope = JsonNode.Parse(output)!.AsObject();
        Assert.False(envelope["success"]?.GetValue<bool>());
        Assert.Equal(ManifestPathIn(emptyLayouts), envelope["manifestPath"]?.GetValue<string>());
        Assert.Equal(StandardFixture, File.ReadAllText(ManifestPathIn(_layoutsPath)));
    }

    [FactWhenFilePermissionsEnforced]
    public async Task ManifestSetRoute_WriteFailure_EnvelopeNamesPathAndWarningDoesNotClaimAWrite()
    {
        WriteFixture();
        string defaultManifest = ManifestPathIn(_layoutsPath);
        File.SetAttributes(defaultManifest, FileAttributes.ReadOnly);
        try
        {
            ManifestTools tools = new(_mutationService, _pathProvider);

            string output = await tools.ManifestSetRoute(
                layoutId: LayoutId, route: "/events", structuralSection: "full-width-layout");

            JsonObject envelope = JsonNode.Parse(output)!.AsObject();
            Assert.False(envelope["success"]?.GetValue<bool>());
            Assert.Equal(defaultManifest, envelope["manifestPath"]?.GetValue<string>());
            Assert.Contains($"Failed to write {defaultManifest}",
                Assert.Single(envelope["errors"]!.AsArray())!.GetValue<string>());
            Assert.Contains($"targeted {defaultManifest}",
                Assert.Single(envelope["warnings"]!.AsArray())!.GetValue<string>());
        }
        finally
        {
            File.SetAttributes(defaultManifest, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task MutationTools_RelativeLayoutsPath_AreRejectedBeforeAnyWrite()
    {
        WriteFixture();
        ManifestTools tools = new(_mutationService, _pathProvider);

        await Assert.ThrowsAsync<McpException>(() => tools.ManifestSetRoute(
            layoutId: LayoutId, route: "/events", structuralSection: "full-width-layout", layoutsPath: "layouts"));
        await Assert.ThrowsAsync<McpException>(() => tools.ManifestApplyBatch(
            layoutId: LayoutId,
            patches: [new ManifestTools.BatchPatch(Route: "/events", StructuralSection: "full-width-layout")],
            layoutsPath: "layouts"));

        Assert.Equal(StandardFixture, File.ReadAllText(ManifestPathIn(_layoutsPath)));
    }

    [Fact]
    public async Task ManifestListRoutes_WithLayoutsPath_ReadsThatCheckout()
    {
        WriteFixture();
        string otherLayouts = WriteFixtureInOtherCheckout(
            StandardFixture.Replace("\"/events\"", "\"/only-in-other-worktree\""));
        ManifestReadTools tools = new(_fileService, _pathProvider);

        JsonObject fromDefault = JsonNode.Parse(await tools.ManifestListRoutes(LayoutId))!.AsObject();
        JsonObject fromOther = JsonNode.Parse(await tools.ManifestListRoutes(LayoutId, otherLayouts))!.AsObject();

        Assert.Equal(ManifestPathIn(_layoutsPath), fromDefault["manifestPath"]?.GetValue<string>());
        Assert.Equal("/events", fromDefault["routes"]![0]!["route"]?.GetValue<string>());
        Assert.Equal(ManifestPathIn(otherLayouts), fromOther["manifestPath"]?.GetValue<string>());
        Assert.Equal("/only-in-other-worktree", fromOther["routes"]![0]!["route"]?.GetValue<string>());
    }

    [Fact]
    public async Task ManifestListSections_WithLayoutsPath_EchoesThatCheckout()
    {
        WriteFixture();
        string otherLayouts = WriteFixtureInOtherCheckout();
        ManifestReadTools tools = new(_fileService, _pathProvider);

        JsonObject envelope = JsonNode.Parse(await tools.ManifestListSections(LayoutId, otherLayouts))!.AsObject();

        Assert.Equal(ManifestPathIn(otherLayouts), envelope["manifestPath"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("/events", true)]
    [InlineData("/nonexistent", false)]
    public async Task ManifestGetRoute_EchoesManifestPath_WhetherOrNotRouteIsFound(string route, bool expectedFound)
    {
        WriteFixture();
        ManifestReadTools tools = new(_fileService, _pathProvider);

        JsonObject envelope = JsonNode.Parse(await tools.ManifestGetRoute(LayoutId, route))!.AsObject();

        Assert.Equal(expectedFound, envelope["found"]?.GetValue<bool>());
        Assert.Equal(ManifestPathIn(_layoutsPath), envelope["manifestPath"]?.GetValue<string>());
    }

    [Fact]
    public async Task ReadTools_MissingManifestAtDefault_ErrorNamesPathAndHowToOverride()
    {
        // No fixture: the layout exists only in "some other checkout".
        ManifestReadTools tools = new(_fileService, _pathProvider);

        McpException ex = await Assert.ThrowsAsync<McpException>(() => tools.ManifestListRoutes(LayoutId));

        Assert.Contains(ManifestPathIn(_layoutsPath), ex.Message);
        Assert.Contains("layoutsPath", ex.Message);
    }

    [Fact]
    public async Task ReadTools_MissingManifestAtNamedCheckout_ErrorNamesPathWithoutDefaultHint()
    {
        string emptyLayouts = Path.Combine(_tempRoot, "checkout-without-layout", "layouts");
        Directory.CreateDirectory(emptyLayouts);
        ManifestReadTools tools = new(_fileService, _pathProvider);

        McpException ex = await Assert.ThrowsAsync<McpException>(
            () => tools.ManifestListSections(LayoutId, emptyLayouts));

        Assert.Contains(ManifestPathIn(emptyLayouts), ex.Message);
        Assert.DoesNotContain("server's default", ex.Message);
    }

    /// <summary>
    /// Guard for future tools (e.g. the config-block tools proposed in #21): every MCP
    /// manifest tool must accept an optional <c>layoutsPath</c> and say so in its
    /// description, or a session editing another worktree has no way to aim it.
    /// </summary>
    [Fact]
    public void EveryTool_AcceptsOptionalLayoutsPath_AndDescribesIt()
    {
        MethodInfo[] toolMethods = typeof(ManifestTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToArray();

        // 5 route/read tools (#29) + 3 section-registry tools (#30).
        Assert.Equal(8, toolMethods.Length);
        foreach (MethodInfo method in toolMethods)
        {
            ParameterInfo? parameter = method.GetParameters().SingleOrDefault(p => p.Name == "layoutsPath");
            Assert.True(parameter is not null, $"{method.Name} has no layoutsPath parameter.");
            Assert.Equal(typeof(string), parameter.ParameterType);
            Assert.True(parameter.HasDefaultValue && parameter.DefaultValue is null,
                $"{method.Name}: layoutsPath must be optional (default null).");

            string description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
            Assert.Contains("layoutsPath", description);
            Assert.Contains("manifestPath", description);
        }
    }

    // ───── section registry tools (issue #30) ─────

    [Fact]
    public async Task ManifestAddSection_ReturnsEnvelopeWithConventionalEntry()
    {
        WriteFixture();
        ManifestSectionTools tools = new(_registryService, _pathProvider);

        string output = await tools.ManifestAddSection(LayoutId, "new-section", dryRun: true, layoutsPath: _layoutsPath);
        JsonObject envelope = JsonNode.Parse(output)!.AsObject();

        Assert.Equal("manifest add-section", envelope["command"]?.GetValue<string>());
        Assert.Equal(ManifestPathIn(_layoutsPath), envelope["manifestPath"]?.GetValue<string>());
        Assert.True(envelope["success"]?.GetValue<bool>());
        Assert.Null(envelope["before"]);
        Assert.Equal("ui-schema-section", envelope["after"]?["type"]?.GetValue<string>());
        Assert.Equal("sections/new-section.json", envelope["after"]?["file"]?.GetValue<string>());
        // The fixture has no section files on disk, so the missing file is flagged.
        Assert.Contains("does not exist", Assert.Single(envelope["warnings"]!.AsArray())!.GetValue<string>());
    }

    [Fact]
    public async Task ManifestRenameSection_ReportsEveryRewrittenReference()
    {
        WriteFixture();
        ManifestSectionTools tools = new(_registryService, _pathProvider);

        string output = await tools.ManifestRenameSection(
            LayoutId, "sidebar-user-summary", "events-sidebar-summary",
            manifestOnly: true, dryRun: true, layoutsPath: _layoutsPath);
        JsonObject envelope = JsonNode.Parse(output)!.AsObject();

        Assert.Equal("manifest rename-section", envelope["command"]?.GetValue<string>());
        Assert.True(envelope["success"]?.GetValue<bool>());
        Assert.Equal("events-sidebar-summary", envelope["newIdentifier"]?.GetValue<string>());
        Assert.Equal(
            "/routeConfigs/~1events/patches/1/sectionIdentifiers/0",
            Assert.Single(envelope["references"]!.AsArray())?.GetValue<string>());
        // Registry entry + one reference.
        Assert.Equal(2, envelope["patch"]!.AsArray().Count);
        Assert.Single(envelope["filesChanged"]!.AsArray());
    }

    [Fact]
    public async Task ManifestRemoveSection_RefusesWhileReferenced()
    {
        WriteFixture();
        ManifestSectionTools tools = new(_registryService, _pathProvider);

        string output = await tools.ManifestRemoveSection(LayoutId, "sidebar-layout", layoutsPath: _layoutsPath);
        JsonObject envelope = JsonNode.Parse(output)!.AsObject();

        Assert.False(envelope["success"]?.GetValue<bool>());
        // Refusals name their target too.
        Assert.Equal(ManifestPathIn(_layoutsPath), envelope["manifestPath"]?.GetValue<string>());
        Assert.Null(envelope["patch"]);
        Assert.Equal(
            "/routeConfigs/~1events/structuralSection",
            Assert.Single(envelope["references"]!.AsArray())?.GetValue<string>());
        Assert.Single(envelope["errors"]!.AsArray());
        Assert.Empty(envelope["warnings"]!.AsArray());
    }

    [Fact]
    public async Task ManifestRenameSection_WithLayoutsPath_WritesThatCheckoutAndLeavesDefaultUntouched()
    {
        WriteFixture();
        string otherLayouts = WriteFixtureInOtherCheckout();
        ManifestSectionTools tools = new(_registryService, _pathProvider);

        string output = await tools.ManifestRenameSection(
            LayoutId, "sidebar-layout", "events-sidebar-layout",
            manifestOnly: true, layoutsPath: Path.GetDirectoryName(otherLayouts)); // checkout-root form

        JsonObject envelope = JsonNode.Parse(output)!.AsObject();
        Assert.True(envelope["success"]?.GetValue<bool>());
        Assert.Equal(ManifestPathIn(otherLayouts), envelope["manifestPath"]?.GetValue<string>());
        Assert.Contains("events-sidebar-layout", File.ReadAllText(ManifestPathIn(otherLayouts)));
        Assert.Equal(StandardFixture, File.ReadAllText(ManifestPathIn(_layoutsPath)));
    }

    [Theory]
    [InlineData(true, "targeted")]
    [InlineData(false, "wrote to")]
    public async Task SectionTools_WithoutLayoutsPath_WarnWhichDefaultFileTheyUsed(bool dryRun, string verb)
    {
        WriteFixture();
        ManifestSectionTools tools = new(_registryService, _pathProvider);

        string output = await tools.ManifestRemoveSection(LayoutId, "full-width-layout", dryRun: dryRun);

        JsonObject envelope = JsonNode.Parse(output)!.AsObject();
        string defaultManifest = ManifestPathIn(_layoutsPath);
        Assert.True(envelope["success"]?.GetValue<bool>());
        Assert.Equal(defaultManifest, envelope["manifestPath"]?.GetValue<string>());
        string warning = Assert.Single(envelope["warnings"]!.AsArray())!.GetValue<string>();
        Assert.Contains("layoutsPath was not passed", warning);
        Assert.Contains($"{verb} {defaultManifest}", warning);
    }

    [Fact]
    public async Task SectionTools_RelativeLayoutsPath_AreRejectedBeforeAnyWrite()
    {
        WriteFixture();
        ManifestSectionTools tools = new(_registryService, _pathProvider);

        await Assert.ThrowsAsync<McpException>(() => tools.ManifestAddSection(LayoutId, "x", layoutsPath: "layouts"));
        await Assert.ThrowsAsync<McpException>(() => tools.ManifestRenameSection(
            LayoutId, "sidebar-layout", "y", manifestOnly: true, layoutsPath: "layouts"));
        await Assert.ThrowsAsync<McpException>(() => tools.ManifestRemoveSection(
            LayoutId, "full-width-layout", layoutsPath: "layouts"));

        Assert.Equal(StandardFixture, File.ReadAllText(ManifestPathIn(_layoutsPath)));
    }

    private void WriteFixture()
    {
        string manifestDir = Path.Combine(_layoutsPath, LayoutId, "manifests");
        Directory.CreateDirectory(manifestDir);
        File.WriteAllText(
            Path.Combine(manifestDir, "layout-manifest.json"),
            StandardFixture);
    }

    /// <summary>
    /// Creates a second checkout ("worktree B") next to the default one and writes a
    /// manifest into it. Returns B's <c>layouts/</c> directory.
    /// </summary>
    private string WriteFixtureInOtherCheckout(string content = StandardFixture)
    {
        string otherLayouts = Path.Combine(_tempRoot, "other-worktree", "layouts");
        string manifestDir = Path.Combine(otherLayouts, LayoutId, "manifests");
        Directory.CreateDirectory(manifestDir);
        File.WriteAllText(Path.Combine(manifestDir, "layout-manifest.json"), content);
        return otherLayouts;
    }

    private static string ManifestPathIn(string layoutsPath)
        => Path.Combine(layoutsPath, LayoutId, "manifests", "layout-manifest.json");

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
