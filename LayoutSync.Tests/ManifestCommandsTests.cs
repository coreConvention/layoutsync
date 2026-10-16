using System.CommandLine;
using System.Text.Json.Nodes;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Drives the section subcommands and <c>remove-route</c> through System.CommandLine, the way
/// the CLI runs them, to pin what the service tests cannot see: flag-to-parameter mapping
/// (<c>--manifest-only</c> is inverted into "update the section file"), the <c>--json</c>
/// envelope on stdout, and exit codes. Kept in one class on purpose: the handlers reconfigure
/// Serilog's static logger and write to the process console, and xunit runs one class's tests
/// in sequence.
/// </summary>
public class ManifestCommandsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _layoutsPath;
    private readonly string _manifestPath;
    private readonly string _sectionPath;
    private const string LayoutId = "test-layout";

    public ManifestCommandsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"section-commands-tests-{Guid.NewGuid()}");
        _layoutsPath = Path.Combine(_tempRoot, "layouts");
        _manifestPath = Path.Combine(_layoutsPath, LayoutId, "manifests", "layout-manifest.json");
        _sectionPath = Path.Combine(_layoutsPath, LayoutId, "sections", "hero.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_manifestPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_sectionPath)!);
        File.WriteAllText(_manifestPath, Manifest);
        File.WriteAllText(_sectionPath, SectionFile("hero"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RenameSection_UpdatesTheSectionFileByDefault()
    {
        int exitCode = await RunAsync("rename-section", "hero", "banner");

        Assert.Equal(0, exitCode);
        Assert.Equal(SectionFile("banner"), File.ReadAllText(_sectionPath));
        Assert.Equal("banner", ReadManifest()["routeConfigs"]!["/"]!["structuralSection"]?.GetValue<string>());
    }

    [Fact]
    public async Task RenameSection_ManifestOnly_LeavesTheSectionFile()
    {
        int exitCode = await RunAsync("rename-section", "hero", "banner", "--manifest-only");

        Assert.Equal(0, exitCode);
        Assert.Equal(SectionFile("hero"), File.ReadAllText(_sectionPath));
        Assert.Equal("banner", ReadManifest()["routeConfigs"]!["/"]!["structuralSection"]?.GetValue<string>());
    }

    [Fact]
    public async Task RenameSection_Strict_ExitsTwoWhenTheChangeLeavesAWarning()
    {
        // --manifest-only leaves the file declaring the old name, which is a warning.
        int exitCode = await RunAsync("rename-section", "hero", "banner", "--manifest-only", "--strict");

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task RenameSection_DryRun_WritesNothing()
    {
        string manifestBefore = File.ReadAllText(_manifestPath);

        int exitCode = await RunAsync("rename-section", "hero", "banner", "--dry-run");

        Assert.Equal(0, exitCode);
        Assert.Equal(manifestBefore, File.ReadAllText(_manifestPath));
        Assert.Equal(SectionFile("hero"), File.ReadAllText(_sectionPath));
    }

    [Fact]
    public async Task RemoveSection_ExitsOneAndWritesNothingWhileReferenced()
    {
        string manifestBefore = File.ReadAllText(_manifestPath);

        int exitCode = await RunAsync("remove-section", "hero");

        Assert.Equal(1, exitCode);
        Assert.Equal(manifestBefore, File.ReadAllText(_manifestPath));
    }

    [Fact]
    public async Task RemoveSection_ForceStrict_RemovesTheEntryButExitsTwo()
    {
        int exitCode = await RunAsync("remove-section", "hero", "--force", "--strict");

        Assert.Equal(2, exitCode);
        Assert.Empty(ReadManifest()["entities"]!["sections"]!.AsArray());
    }

    [Fact]
    public async Task AddSection_MapsEveryOption()
    {
        int exitCode = await RunAsync(
            "add-section", "footer",
            "--type", "custom-type",
            "--file", "sections/site-footer.json",
            "--description", "Page footer");

        // The section file does not exist, which is only a warning without --strict.
        Assert.Equal(0, exitCode);
        JsonObject entry = ReadManifest()["entities"]!["sections"]![1]!.AsObject();
        Assert.Equal("footer", entry["identifier"]?.GetValue<string>());
        Assert.Equal("custom-type", entry["type"]?.GetValue<string>());
        Assert.Equal("sections/site-footer.json", entry["file"]?.GetValue<string>());
        Assert.Equal("Page footer", entry["description"]?.GetValue<string>());
    }

    [Fact]
    public async Task AddSection_ExitsOneForADuplicate()
    {
        int exitCode = await RunAsync("add-section", "hero");

        Assert.Equal(1, exitCode);
    }

    // ───── remove-route (issue #64) ─────

    [Fact]
    public async Task RemoveRoute_RemovesTheRoute()
    {
        int exitCode = await RunAsync("remove-route", "/");

        // The route's only section is now unused, which is only a warning without --strict.
        Assert.Equal(0, exitCode);
        Assert.Empty(ReadManifest()["routeConfigs"]!.AsObject());
        // The section stays declared.
        Assert.Single(ReadManifest()["entities"]!["sections"]!.AsArray());
    }

    [Fact]
    public async Task RemoveRoute_Strict_ExitsTwoWhenASectionIsLeftUnused()
    {
        int exitCode = await RunAsync("remove-route", "/", "--strict");

        Assert.Equal(2, exitCode);
        Assert.Empty(ReadManifest()["routeConfigs"]!.AsObject());
    }

    [Fact]
    public async Task RemoveRoute_ExitsOneAndWritesNothingForAMissingRoute()
    {
        string manifestBefore = File.ReadAllText(_manifestPath);

        int exitCode = await RunAsync("remove-route", "/nope");

        Assert.Equal(1, exitCode);
        Assert.Equal(manifestBefore, File.ReadAllText(_manifestPath));
    }

    [Fact]
    public async Task RemoveRoute_DryRunJson_PrintsTheEnvelopeAndWritesNothing()
    {
        string manifestBefore = File.ReadAllText(_manifestPath);
        StringWriter stdout = new();
        TextWriter original = Console.Out;
        Console.SetOut(stdout);
        int exitCode;
        try
        {
            exitCode = await RunAsync("remove-route", "/", "--dry-run", "--json");
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Equal(0, exitCode);
        Assert.Equal(manifestBefore, File.ReadAllText(_manifestPath));

        JsonObject envelope = JsonNode.Parse(stdout.ToString())!.AsObject();
        Assert.Equal("manifest remove-route", envelope["command"]?.GetValue<string>());
        Assert.Equal(_manifestPath, envelope["manifestPath"]?.GetValue<string>());
        Assert.True(envelope["dryRun"]?.GetValue<bool>());
        JsonObject change = Assert.Single(envelope["changes"]!.AsArray())!.AsObject();
        Assert.Equal("hero", change["before"]?["structuralSection"]?.GetValue<string>());
        Assert.Equal("/routeConfigs/~1", change["patch"]?[0]?["path"]?.GetValue<string>());
        Assert.Contains("'hero'", Assert.Single(envelope["warnings"]!.AsArray())!.GetValue<string>());
        Assert.Empty(envelope["errors"]!.AsArray());
    }

    /// <summary>
    /// Runs <c>layoutsync manifest &lt;args&gt;</c> against the temp layout. Always passes
    /// <c>--allow-cross-worktree-sync</c>: the test host's working directory may itself sit
    /// inside a worktree, and the temp layouts directory never does.
    /// </summary>
    private Task<int> RunAsync(params string[] args)
    {
        RootCommand root = new("LayoutSync test host");
        root.AddCommand(ManifestCommands.Build());
        return root.InvokeAsync(
        [
            "manifest", .. args,
            "--layout", LayoutId,
            "--layouts-path", _layoutsPath,
            "--allow-cross-worktree-sync",
        ]);
    }

    private JsonObject ReadManifest() => JsonNode.Parse(File.ReadAllText(_manifestPath))!.AsObject();

    private static string SectionFile(string identifier)
        => $$"""{ "identifier": "{{identifier}}", "type": "ui-schema-section" }""";

    private const string Manifest = """
        {
          "identifier": "test-layout",
          "entities": {
            "sections": [
              { "identifier": "hero", "type": "ui-schema-section", "file": "sections/hero.json" }
            ]
          },
          "routeConfigs": {
            "/": { "structuralSection": "hero", "patches": [] }
          }
        }
        """;
}
