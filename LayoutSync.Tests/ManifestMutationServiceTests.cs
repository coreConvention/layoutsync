using System.Text.Json.Nodes;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

public class ManifestMutationServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _layoutsPath;
    private readonly ManifestMutationService _service;
    private const string LayoutId = "test-layout";

    public ManifestMutationServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"mutation-tests-{Guid.NewGuid()}");
        _layoutsPath = Path.Combine(_tempRoot, "layouts");
        Directory.CreateDirectory(_layoutsPath);

        LocalFileService fileService = new(NullLogger<LocalFileService>.Instance);
        ManifestSectionValidator validator = new(NullLogger<ManifestSectionValidator>.Instance);
        _service = new ManifestMutationService(
            fileService,
            validator,
            NullLogger<ManifestMutationService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SetRouteAsync_AddsNewRoute_WhenRouteAbsent()
    {
        WriteFixture(StandardFixture);
        RoutePatchInput patch = new(
            Route: "/new-route",
            StructuralSection: "full-width-layout",
            MainSections: ["events-page-header"]);

        MutationResult result = await _service.SetRouteAsync(
            _layoutsPath, LayoutId, patch, dryRun: false);

        Assert.True(result.Success);
        Assert.Single(result.Changes);
        RouteChange change = result.Changes[0];
        Assert.Equal(RouteChangeStatus.Applied, change.Status);
        Assert.Null(change.Before);
        Assert.NotNull(change.After);
        Assert.Equal("full-width-layout", change.After["structuralSection"]?.GetValue<string>());
        Assert.NotNull(change.Patch);
        // New route → single 'add' op at root
        Assert.Single(change.Patch);
        Assert.Equal("add", change.Patch[0]!["op"]?.GetValue<string>());
    }

    [Fact]
    public async Task SetRouteAsync_UpdatesStructuralSection_OnExistingRoute()
    {
        WriteFixture(StandardFixture);
        RoutePatchInput patch = new(
            Route: "/events",
            StructuralSection: "full-width-layout");

        MutationResult result = await _service.SetRouteAsync(
            _layoutsPath, LayoutId, patch, dryRun: false);

        Assert.True(result.Success);
        RouteChange change = Assert.Single(result.Changes);
        Assert.Equal(RouteChangeStatus.Applied, change.Status);
        Assert.NotNull(change.After);
        Assert.Equal("full-width-layout", change.After["structuralSection"]?.GetValue<string>());
        // patches[] preserved — only structuralSection changed
        JsonArray? patches = change.After["patches"]?.AsArray();
        Assert.NotNull(patches);
        Assert.Equal(2, patches.Count);
        // Diff should contain exactly one replace op for /structuralSection
        Assert.NotNull(change.Patch);
        Assert.Single(change.Patch);
        Assert.Equal("replace", change.Patch[0]!["op"]?.GetValue<string>());
        Assert.Equal("/structuralSection", change.Patch[0]!["path"]?.GetValue<string>());
    }

    [Fact]
    public async Task SetRouteAsync_ReplacesMainSections()
    {
        WriteFixture(StandardFixture);
        RoutePatchInput patch = new(
            Route: "/events",
            MainSections: ["my-rsvps-list"]);

        MutationResult result = await _service.SetRouteAsync(
            _layoutsPath, LayoutId, patch, dryRun: false);

        Assert.True(result.Success);
        RouteChange change = Assert.Single(result.Changes);
        JsonArray? mainSections = FindSlotSections(change.After!, "main");
        Assert.NotNull(mainSections);
        Assert.Single(mainSections);
        Assert.Equal("my-rsvps-list", mainSections[0]?.GetValue<string>());
    }

    [Fact]
    public async Task SetRouteAsync_RemovesPatchSlot()
    {
        WriteFixture(StandardFixture);
        RoutePatchInput patch = new(
            Route: "/events",
            RemoveSidebar: true);

        MutationResult result = await _service.SetRouteAsync(
            _layoutsPath, LayoutId, patch, dryRun: false);

        Assert.True(result.Success);
        RouteChange change = Assert.Single(result.Changes);
        // Sidebar slot is gone, main remains
        Assert.Null(FindSlotSections(change.After!, "sidebar"));
        Assert.NotNull(FindSlotSections(change.After!, "main"));
    }

    [Fact]
    public async Task SetRouteAsync_DryRun_DoesNotWriteFile()
    {
        WriteFixture(StandardFixture);
        string manifestPath = ManifestPath();
        string before = File.ReadAllText(manifestPath);

        RoutePatchInput patch = new(
            Route: "/events",
            StructuralSection: "full-width-layout");

        MutationResult result = await _service.SetRouteAsync(
            _layoutsPath, LayoutId, patch, dryRun: true);

        // The result reports the change as Applied — dry-run still computes the diff
        Assert.True(result.Success);
        Assert.Equal(RouteChangeStatus.Applied, result.Changes[0].Status);
        // …but the file on disk is byte-identical to what it was before
        string after = File.ReadAllText(manifestPath);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task SetRouteAsync_RejectsConflictingPatchAndRemove()
    {
        WriteFixture(StandardFixture);
        RoutePatchInput patch = new(
            Route: "/events",
            MainSections: ["events-page-header"],
            RemoveMain: true);

        MutationResult result = await _service.SetRouteAsync(
            _layoutsPath, LayoutId, patch, dryRun: false);

        Assert.False(result.Success);
        Assert.Empty(result.Changes);
        Assert.Single(result.Errors);
        Assert.Contains("mutually exclusive", result.Errors[0]);
    }

    [Fact]
    public async Task ApplyBatchAsync_Abort_AtomicOnValidationFailure()
    {
        WriteFixture(StandardFixture);
        string manifestPath = ManifestPath();
        string before = File.ReadAllText(manifestPath);

        // Two patches: one valid, one with a typo. Abort mode should reject both.
        List<RoutePatchInput> patches =
        [
            new(Route: "/events", StructuralSection: "full-width-layout"),       // valid
            new(Route: "/new-route", MainSections: ["bogus-section-typo"]),      // invalid
        ];

        MutationResult result = await _service.ApplyBatchAsync(
            _layoutsPath, LayoutId, patches, BatchErrorMode.Abort, dryRun: false);

        Assert.False(result.Success);
        Assert.Equal(2, result.Changes.Count);
        Assert.All(result.Changes, c => Assert.Equal(RouteChangeStatus.Aborted, c.Status));
        // Error attached to the route that actually failed
        Assert.Contains("bogus-section-typo",
            result.Changes.Single(c => c.Route == "/new-route").Error);
        // File on disk is unchanged
        Assert.Equal(before, File.ReadAllText(manifestPath));
    }

    [Fact]
    public async Task ApplyBatchAsync_Skip_AppliesValidAndSkipsInvalid()
    {
        WriteFixture(StandardFixture);
        List<RoutePatchInput> patches =
        [
            new(Route: "/events", StructuralSection: "full-width-layout"),       // valid
            new(Route: "/new-route", MainSections: ["bogus-section-typo"]),      // invalid
        ];

        MutationResult result = await _service.ApplyBatchAsync(
            _layoutsPath, LayoutId, patches, BatchErrorMode.Skip, dryRun: false);

        Assert.True(result.Success); // not aborted under Skip
        Assert.Equal(2, result.Changes.Count);
        RouteChange validChange = result.Changes.Single(c => c.Route == "/events");
        Assert.Equal(RouteChangeStatus.Applied, validChange.Status);
        RouteChange invalidChange = result.Changes.Single(c => c.Route == "/new-route");
        Assert.Equal(RouteChangeStatus.Skipped, invalidChange.Status);
        Assert.Contains("bogus-section-typo", invalidChange.Error);

        // The valid patch persisted to disk
        string content = File.ReadAllText(ManifestPath());
        Assert.Contains("\"structuralSection\": \"full-width-layout\"", content);
    }

    [Fact]
    public async Task ApplyBatchAsync_TwoPatchesOnSameRoute_Compose()
    {
        WriteFixture(StandardFixture);
        // First patch sets structuralSection; second patch updates main sections.
        // Both should end up applied to the same route.
        List<RoutePatchInput> patches =
        [
            new(Route: "/events", StructuralSection: "full-width-layout"),
            new(Route: "/events", MainSections: ["my-rsvps-list"]),
        ];

        MutationResult result = await _service.ApplyBatchAsync(
            _layoutsPath, LayoutId, patches, BatchErrorMode.Abort, dryRun: false);

        Assert.True(result.Success);
        // Both changes recorded
        Assert.Equal(2, result.Changes.Count);
        Assert.All(result.Changes, c => Assert.Equal(RouteChangeStatus.Applied, c.Status));
        // Final manifest state has both mutations
        string content = File.ReadAllText(ManifestPath());
        Assert.Contains("\"structuralSection\": \"full-width-layout\"", content);
        Assert.Contains("\"my-rsvps-list\"", content);
    }

    [Fact]
    public async Task ApplyBatchAsync_ManifestNotFound_ReturnsTopLevelError()
    {
        // No fixture written. Directory exists but the manifest file does not.
        string manifestDir = Path.Combine(_layoutsPath, LayoutId, "manifests");
        Directory.CreateDirectory(manifestDir);

        RoutePatchInput patch = new(Route: "/r", StructuralSection: "x");

        MutationResult result = await _service.SetRouteAsync(
            _layoutsPath, LayoutId, patch, dryRun: false);

        Assert.False(result.Success);
        Assert.Single(result.Errors);
        Assert.Contains("not found", result.Errors[0]);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public async Task SetRouteAsync_RemovedSlot_ProducesReplacePatchesOp()
    {
        WriteFixture(StandardFixture);
        RoutePatchInput patch = new(Route: "/events", RemoveSidebar: true);

        MutationResult result = await _service.SetRouteAsync(
            _layoutsPath, LayoutId, patch, dryRun: false);

        Assert.True(result.Success);
        RouteChange change = result.Changes[0];
        Assert.NotNull(change.Patch);
        // Only the patches array changed; structuralSection didn't.
        Assert.Single(change.Patch);
        JsonObject op = change.Patch[0]!.AsObject();
        Assert.Equal("replace", op["op"]?.GetValue<string>());
        Assert.Equal("/patches", op["path"]?.GetValue<string>());
    }

    [Fact]
    public void ComputeRfc6902Diff_RouteKeyWithSlash_Escapes()
    {
        // Sanity-check the JSON Pointer escaping for route keys (which contain /).
        // The diff is computed on the route entry directly, so the route's own slashes
        // never appear in the path, but this tests the helper's escape logic on a key
        // that does contain /.
        JsonObject before = new() { ["a/b"] = "old" };
        JsonObject after = new() { ["a/b"] = "new" };

        JsonArray ops = ManifestMutationService.ComputeRfc6902Diff(before, after);

        Assert.Single(ops);
        Assert.Equal("/a~1b", ops[0]!["path"]?.GetValue<string>());
    }

    // ───── helpers ─────

    private string ManifestPath()
        => Path.Combine(_layoutsPath, LayoutId, "manifests", "layout-manifest.json");

    private void WriteFixture(string content)
    {
        string manifestDir = Path.Combine(_layoutsPath, LayoutId, "manifests");
        Directory.CreateDirectory(manifestDir);
        File.WriteAllText(ManifestPath(), content);
    }

    /// <summary>
    /// Helper: walks <c>routeConfig.patches[]</c> looking for the entry where
    /// <c>targetElementId == slot</c>, and returns its <c>sectionIdentifiers</c> array
    /// (or null if absent).
    /// </summary>
    private static JsonArray? FindSlotSections(JsonObject routeConfig, string slot)
    {
        if (routeConfig["patches"] is not JsonArray patches) return null;
        foreach (JsonNode? n in patches)
        {
            if (n is JsonObject entry
                && entry["targetElementId"]?.GetValue<string>() == slot)
            {
                return entry["sectionIdentifiers"]?.AsArray();
            }
        }
        return null;
    }

    /// <summary>
    /// A complete fixture manifest with declared sections and one existing route
    /// (<c>/events</c>) that has both main and sidebar slots filled. Re-used across
    /// most tests as the starting state.
    /// </summary>
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
