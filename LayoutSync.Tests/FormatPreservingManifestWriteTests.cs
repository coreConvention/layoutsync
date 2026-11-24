using System.Text;
using System.Text.Json.Nodes;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Issue #70: every manifest operation changes only the bytes of the values it changes. Before,
/// each write re-serialized the whole manifest, so a block written on one line (a
/// <c>themes.set</c> array of one-line objects) was expanded by an operation that never touched it.
/// Each test runs a real operation on <see cref="Manifest"/>, which mixes one-line and expanded
/// blocks, and states the whole file it expects, byte for byte.
/// </summary>
public class FormatPreservingManifestWriteTests : IDisposable
{
    private const string LayoutId = "demo";

    /// <summary>
    /// One-line blocks (the theme set, most registry entries, the patch entry, the notification
    /// categories) beside expanded ones, plus literal characters that the default encoder escapes.
    /// </summary>
    private const string Manifest = """
        {
          "identifier": "demo",
          "description": "Demo — the BFF's <config> & a+b",
          "themes": {
            "set": [
              { "id": "light", "role": "light" },
              { "id": "dark", "role": "dark" }
            ]
          },
          "entities": {
            "sections": [
              { "identifier": "full-width-layout", "type": "ui-schema-section", "file": "sections/full-width-layout.json" },
              {
                "identifier": "events-page-header",
                "type": "ui-schema-section",
                "file": "sections/events-page-header.json"
              },
              { "identifier": "sidebar-user-summary", "type": "ui-schema-section", "file": "sections/sidebar-user-summary.json" }
            ]
          },
          "routeConfigs": {
            "/events": {
              "structuralSection": "full-width-layout",
              "patches": [
                { "targetElementId": "main", "sectionIdentifiers": ["events-page-header"] }
              ]
            }
          },
          "notifications": { "categories": [{ "id": "security", "consent": "transactional", "channels": ["email"] }] }
        }

        """;

    private readonly string _tempRoot;
    private readonly string _layoutsPath;
    private readonly string _manifestPath;
    private readonly ManifestMutationService _routes;
    private readonly ManifestSectionRegistryService _sections;

    public FormatPreservingManifestWriteTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"format-preserving-tests-{Guid.NewGuid()}");
        _layoutsPath = Path.Combine(_tempRoot, "layouts");
        string manifestDir = Path.Combine(_layoutsPath, LayoutId, "manifests");
        Directory.CreateDirectory(manifestDir);
        _manifestPath = Path.Combine(manifestDir, "layout-manifest.json");

        LocalFileService fileService = new(NullLogger<LocalFileService>.Instance);
        _routes = new ManifestMutationService(
            fileService,
            new ManifestSectionValidator(NullLogger<ManifestSectionValidator>.Instance),
            NullLogger<ManifestMutationService>.Instance);
        _sections = new ManifestSectionRegistryService(fileService, NullLogger<ManifestSectionRegistryService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ───── set-route / from-json (both are ApplyBatchAsync) ─────

    [Fact]
    public async Task SetRoute_NewRoute_IsAddedExpandedAndNothingElseMoves()
    {
        WriteManifest(Manifest);

        MutationResult result = await _routes.SetRouteAsync(
            _layoutsPath, LayoutId,
            new RoutePatchInput("/about", StructuralSection: "full-width-layout", MainSections: ["events-page-header"]),
            dryRun: false);

        Assert.True(result.Success);
        AssertManifest(Manifest.Replace(
            """
                  ]
                }
              },
              "notifications"
            """,
            """
                  ]
                },
                "/about": {
                  "structuralSection": "full-width-layout",
                  "patches": [
                    {
                      "targetElementId": "main",
                      "sectionIdentifiers": [
                        "events-page-header"
                      ]
                    }
                  ]
                }
              },
              "notifications"
            """));
    }

    [Fact]
    public async Task SetRoute_ChangedSlot_ChangesOnlyThatArrayInsideItsOneLineEntry()
    {
        WriteManifest(Manifest);

        MutationResult result = await _routes.ApplyBatchAsync(
            _layoutsPath, LayoutId,
            [new RoutePatchInput("/events", MainSections: ["events-page-header", "sidebar-user-summary"])],
            BatchErrorMode.Abort,
            dryRun: false);

        Assert.True(result.Success);
        AssertManifest(Manifest.Replace(
            """["events-page-header"] }""",
            """["events-page-header", "sidebar-user-summary"] }"""));
    }

    [Fact]
    public async Task SetRoute_NewSlot_IsAddedOnOneLineLikeTheEntryBeforeIt()
    {
        WriteManifest(Manifest);

        MutationResult result = await _routes.SetRouteAsync(
            _layoutsPath, LayoutId,
            new RoutePatchInput("/events", SidebarSections: ["sidebar-user-summary"]),
            dryRun: false);

        Assert.True(result.Success);
        AssertManifest(Manifest.Replace(
            """
                    { "targetElementId": "main", "sectionIdentifiers": ["events-page-header"] }
            """,
            """
                    { "targetElementId": "main", "sectionIdentifiers": ["events-page-header"] },
                    { "targetElementId": "sidebar", "sectionIdentifiers": ["sidebar-user-summary"] }
            """));
    }

    // ───── remove-route ─────

    [Fact]
    public async Task RemoveRoute_RemovesOnlyTheRoute()
    {
        WriteManifest(Manifest);

        MutationResult result = await _routes.RemoveRouteAsync(_layoutsPath, LayoutId, "/events", dryRun: false);

        Assert.True(result.Success);
        AssertManifest(Manifest.Replace(
            """
              "routeConfigs": {
                "/events": {
                  "structuralSection": "full-width-layout",
                  "patches": [
                    { "targetElementId": "main", "sectionIdentifiers": ["events-page-header"] }
                  ]
                }
              },
            """,
            """
              "routeConfigs": {},
            """));
    }

    // ───── add-section / rename-section / remove-section ─────

    [Fact]
    public async Task AddSection_AppendsAOneLineEntryAfterAOneLineEntry()
    {
        WriteManifest(Manifest);

        SectionMutationResult result = await _sections.AddSectionAsync(
            _layoutsPath, LayoutId, new SectionEntryInput("about-page", Description: "About — us"), dryRun: false);

        Assert.True(result.Success);
        AssertManifest(Manifest.Replace(
            """
                  { "identifier": "sidebar-user-summary", "type": "ui-schema-section", "file": "sections/sidebar-user-summary.json" }
            """,
            """
                  { "identifier": "sidebar-user-summary", "type": "ui-schema-section", "file": "sections/sidebar-user-summary.json" },
                  { "identifier": "about-page", "type": "ui-schema-section", "file": "sections/about-page.json", "description": "About — us" }
            """));
    }

    [Fact]
    public async Task AddSection_IntoAnEmptyEntitiesObject_ExpandsTheNewRegistry()
    {
        WriteManifest("""
            {
              "identifier": "demo",
              "themes": { "set": [{ "id": "light", "role": "light" }] },
              "entities": {}
            }

            """);

        SectionMutationResult result = await _sections.AddSectionAsync(
            _layoutsPath, LayoutId, new SectionEntryInput("home"), dryRun: false);

        Assert.True(result.Success);
        AssertManifest("""
            {
              "identifier": "demo",
              "themes": { "set": [{ "id": "light", "role": "light" }] },
              "entities": {
                "sections": [
                  {
                    "identifier": "home",
                    "type": "ui-schema-section",
                    "file": "sections/home.json"
                  }
                ]
              }
            }

            """);
    }

    [Fact]
    public async Task RenameSection_ChangesOnlyTheIdentifierAndItsReferences()
    {
        WriteManifest(Manifest);

        SectionMutationResult result = await _sections.RenameSectionAsync(
            _layoutsPath, LayoutId, "events-page-header", "page-header", updateSectionFile: false, dryRun: false);

        Assert.True(result.Success);
        AssertManifest(Manifest
            .Replace("""
                "identifier": "events-page-header",
            """, """
                "identifier": "page-header",
            """)
            .Replace("""["events-page-header"]""", """["page-header"]"""));
    }

    [Fact]
    public async Task RemoveSection_RemovesOnlyTheEntry()
    {
        WriteManifest(Manifest);

        SectionMutationResult result = await _sections.RemoveSectionAsync(
            _layoutsPath, LayoutId, "sidebar-user-summary", force: false, dryRun: false);

        Assert.True(result.Success);
        AssertManifest(Manifest.Replace(
            """
                  },
                  { "identifier": "sidebar-user-summary", "type": "ui-schema-section", "file": "sections/sidebar-user-summary.json" }
            """,
            """
                  }
            """));
    }

    // ───── line endings, BOM, refusal ─────

    [Fact]
    public async Task CrlfAndBom_AreKeptAndNewLinesUseCrlf()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        File.WriteAllBytes(_manifestPath, [.. bom, .. Encoding.UTF8.GetBytes(Crlf(Manifest))]);

        MutationResult result = await _routes.SetRouteAsync(
            _layoutsPath, LayoutId,
            new RoutePatchInput("/events", SidebarSections: ["sidebar-user-summary"]),
            dryRun: false);
        SectionMutationResult added = await _sections.AddSectionAsync(
            _layoutsPath, LayoutId, new SectionEntryInput("about-page"), dryRun: false);

        Assert.True(result.Success);
        Assert.True(added.Success);
        string expected = Manifest
            .Replace(
                """
                        { "targetElementId": "main", "sectionIdentifiers": ["events-page-header"] }
                """,
                """
                        { "targetElementId": "main", "sectionIdentifiers": ["events-page-header"] },
                        { "targetElementId": "sidebar", "sectionIdentifiers": ["sidebar-user-summary"] }
                """)
            .Replace(
                """
                      { "identifier": "sidebar-user-summary", "type": "ui-schema-section", "file": "sections/sidebar-user-summary.json" }
                """,
                """
                      { "identifier": "sidebar-user-summary", "type": "ui-schema-section", "file": "sections/sidebar-user-summary.json" },
                      { "identifier": "about-page", "type": "ui-schema-section", "file": "sections/about-page.json" }
                """);
        Assert.Equal([.. bom, .. Encoding.UTF8.GetBytes(Crlf(expected))], File.ReadAllBytes(_manifestPath));
    }

    [Fact]
    public async Task CrlfManifest_NewExpandedBlockUsesCrlf()
    {
        WriteManifest(Crlf(Manifest));

        MutationResult result = await _routes.SetRouteAsync(
            _layoutsPath, LayoutId,
            new RoutePatchInput("/about", StructuralSection: "full-width-layout"),
            dryRun: false);

        Assert.True(result.Success);
        string written = File.ReadAllText(_manifestPath);
        Assert.Contains(Crlf("""
                "/about": {
                  "structuralSection": "full-width-layout",
                  "patches": []
                }
            """), written, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", written.Replace("\r\n", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_UnchangedDocument_ReturnsTheSameBytes()
    {
        byte[] original = Encoding.UTF8.GetBytes(Manifest);

        byte[]? rewritten = FormatPreservingJsonWriter.Rewrite(original, JsonNode.Parse(Manifest)!);

        Assert.Equal(original, rewritten);
    }

    [Theory]
    [InlineData("{ \"a\": 1 } trailing")]
    [InlineData("{ \"a\": 1, \"a\": 2 }")]
    [InlineData("not json")]
    [InlineData("")]
    public void Rewrite_ReturnsNullWhenTheOriginalIsNotOneJsonValue(string original)
    {
        Assert.Null(FormatPreservingJsonWriter.Rewrite(Encoding.UTF8.GetBytes(original), new JsonObject { ["a"] = 3 }));
    }

    [Fact]
    public void Rewrite_EditedRemovedAndAddedArrayElements_KeepTheirNeighboursBytes()
    {
        const string original = """
            {
              "list": [
                { "id": "a" },
                {
                  "id": "b",
                  "tags": ["x"]
                },
                { "id": "c" }
              ]
            }
            """;
        JsonNode updated = JsonNode.Parse("""{ "list": [ { "id": "a" }, { "id": "b", "tags": ["x", "y"] }, { "id": "d" } ] }""")!;

        byte[]? rewritten = FormatPreservingJsonWriter.Rewrite(Encoding.UTF8.GetBytes(original), updated);

        Assert.NotNull(rewritten);
        Assert.Equal(
            """
            {
              "list": [
                { "id": "a" },
                {
                  "id": "b",
                  "tags": ["x", "y"]
                },
                { "id": "d" }
              ]
            }
            """,
            Encoding.UTF8.GetString(rewritten));
    }

    [Fact]
    public async Task WriteManifestAsync_WhenTheFileIsNoLongerJson_RefusesAndWritesNothing()
    {
        LocalFileService fileService = new(NullLogger<LocalFileService>.Instance);
        File.WriteAllText(_manifestPath, "{ broken");

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => fileService.WriteManifestAsync(_manifestPath, new JsonObject { ["a"] = 1 }, new JsonObject()));

        Assert.Contains("Nothing was written", error.Message, StringComparison.Ordinal);
        Assert.Equal("{ broken", File.ReadAllText(_manifestPath));
    }

    private void WriteManifest(string text) => File.WriteAllBytes(_manifestPath, Encoding.UTF8.GetBytes(text));

    private void AssertManifest(string expected) => Assert.Equal(expected, File.ReadAllText(_manifestPath));

    /// <summary>Raw string literals take the source file's line endings; tests state them explicitly.</summary>
    private static string Crlf(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");
}
