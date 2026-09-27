using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

public class ManifestSectionRegistryServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _layoutsPath;
    private readonly ManifestSectionRegistryService _service;
    private const string LayoutId = "test-layout";

    public ManifestSectionRegistryServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"section-registry-tests-{Guid.NewGuid()}");
        _layoutsPath = Path.Combine(_tempRoot, "layouts");
        Directory.CreateDirectory(_layoutsPath);

        _service = new ManifestSectionRegistryService(
            new LocalFileService(NullLogger<LocalFileService>.Instance),
            NullLogger<ManifestSectionRegistryService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ───── add ─────

    [Fact]
    public async Task AddSection_AppendsEntryWithConventionalDefaults()
    {
        WriteStandardLayout();
        WriteSectionFile("sections/new-section.json", MinimalSectionFile("new-section"));
        string original = File.ReadAllText(ManifestPath());

        SectionMutationResult result = await _service.AddSectionAsync(
            _layoutsPath, LayoutId, new SectionEntryInput("new-section", Description: "A new section"), dryRun: false);

        Assert.True(result.Success);
        Assert.Empty(result.Warnings);
        Assert.Null(result.Before);
        Assert.Equal("ui-schema-section", result.After!["type"]?.GetValue<string>());
        Assert.Equal("sections/new-section.json", result.After["file"]?.GetValue<string>());
        Assert.Equal("A new section", result.After["description"]?.GetValue<string>());
        JsonObject op = Assert.Single(result.Patch!)!.AsObject();
        Assert.Equal("add", op["op"]?.GetValue<string>());
        Assert.Equal("/entities/sections/-", op["path"]?.GetValue<string>());
        Assert.Equal([ManifestPath()], result.FilesChanged);

        JsonArray registry = ReadManifest()["entities"]!["sections"]!.AsArray();
        Assert.Equal("new-section", registry[^1]!["identifier"]?.GetValue<string>());
        AssertPatchReproduces(original, result.Patch!, File.ReadAllText(ManifestPath()));
    }

    [Fact]
    public async Task AddSection_TreatsBlankOptionalValuesAsOmitted()
    {
        // MCP clients often send "" for an optional parameter they mean to leave out.
        WriteStandardLayout();
        WriteSectionFile("sections/new-section.json", MinimalSectionFile("new-section"));

        SectionMutationResult result = await _service.AddSectionAsync(
            _layoutsPath, LayoutId, new SectionEntryInput("new-section", Type: "", File: " ", Description: ""), dryRun: true);

        Assert.True(result.Success);
        Assert.Equal("ui-schema-section", result.After!["type"]?.GetValue<string>());
        Assert.Equal("sections/new-section.json", result.After["file"]?.GetValue<string>());
        Assert.False(result.After.ContainsKey("description"));
    }

    [Fact]
    public async Task AddSection_RejectsIdentifierAlreadyDeclared()
    {
        WriteStandardLayout();
        string original = File.ReadAllText(ManifestPath());

        SectionMutationResult result = await _service.AddSectionAsync(
            _layoutsPath, LayoutId, new SectionEntryInput("event-list"), dryRun: false);

        Assert.False(result.Success);
        Assert.Contains("already declared", Assert.Single(result.Errors));
        Assert.Null(result.Patch);
        Assert.Empty(result.FilesChanged);
        Assert.Equal(original, File.ReadAllText(ManifestPath()));
    }

    [Fact]
    public async Task AddSection_WarnsWhenSectionFileIsMissing()
    {
        WriteStandardLayout();

        SectionMutationResult result = await _service.AddSectionAsync(
            _layoutsPath, LayoutId, new SectionEntryInput("ghost-section"), dryRun: false);

        Assert.True(result.Success);
        Assert.Contains("does not exist", Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task AddSection_WarnsInsteadOfFailingWhenSectionFileCannotBeRead()
    {
        WriteStandardLayout();
        WriteSectionFile("sections/locked-section.json", MinimalSectionFile("locked-section"));
        string sectionPath = SectionPath("sections/locked-section.json");
        ManifestSectionRegistryService service = ServiceWith(new FaultyFileService(
            readFault: path => path == sectionPath ? new UnauthorizedAccessException("denied") : null));

        SectionMutationResult result = await service.AddSectionAsync(
            _layoutsPath, LayoutId, new SectionEntryInput("locked-section"), dryRun: false);

        Assert.True(result.Success);
        Assert.Contains("could not be read (denied)", Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task AddSection_WarnsWhenSectionFileDeclaresAnotherIdentifier()
    {
        WriteStandardLayout();
        WriteSectionFile("sections/typo.json", MinimalSectionFile("typo-section"));

        SectionMutationResult result = await _service.AddSectionAsync(
            _layoutsPath, LayoutId, new SectionEntryInput("typo"), dryRun: false);

        Assert.True(result.Success);
        Assert.Contains("declares identifier 'typo-section'", Assert.Single(result.Warnings));
    }

    [Theory]
    [InlineData("""{ "identifier": "test-layout" }""", "/entities")]
    [InlineData("""{ "identifier": "test-layout", "entities": {} }""", "/entities/sections")]
    public async Task AddSection_CreatesRegistryWhenManifestHasNone(string manifest, string expectedPath)
    {
        WriteManifest(manifest);
        WriteSectionFile("sections/first-section.json", MinimalSectionFile("first-section"));

        SectionMutationResult result = await _service.AddSectionAsync(
            _layoutsPath, LayoutId, new SectionEntryInput("first-section"), dryRun: false);

        Assert.True(result.Success);
        Assert.Equal(expectedPath, Assert.Single(result.Patch!)!["path"]?.GetValue<string>());
        Assert.Equal("first-section", ReadManifest()["entities"]!["sections"]![0]!["identifier"]?.GetValue<string>());
        AssertPatchReproduces(manifest, result.Patch!, File.ReadAllText(ManifestPath()));
    }

    [Fact]
    public async Task AddSection_RejectsFileOutsideLayoutDirectory()
    {
        WriteStandardLayout();

        SectionMutationResult result = await _service.AddSectionAsync(
            _layoutsPath, LayoutId, new SectionEntryInput("escapee", File: "../other-layout/sections/escapee.json"), dryRun: false);

        Assert.False(result.Success);
        Assert.Contains("outside", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task AddSection_ReportsDanglingReferencesItResolves()
    {
        WriteManifest("""
            {
              "identifier": "test-layout",
              "entities": { "sections": [] },
              "routing": { "notFoundSection": "missing-section" }
            }
            """);
        WriteSectionFile("sections/missing-section.json", MinimalSectionFile("missing-section"));

        SectionMutationResult result = await _service.AddSectionAsync(
            _layoutsPath, LayoutId, new SectionEntryInput("missing-section"), dryRun: false);

        Assert.True(result.Success);
        Assert.Equal(["/routing/notFoundSection"], result.References);
    }

    // ───── rename ─────

    [Fact]
    public async Task RenameSection_RewritesRegistryEntryAndEveryReference()
    {
        WriteStandardLayout();
        string original = File.ReadAllText(ManifestPath());

        SectionMutationResult result = await _service.RenameSectionAsync(
            _layoutsPath, LayoutId, "full-width-layout", "test-full-width-layout",
            updateSectionFile: true, dryRun: false);

        Assert.True(result.Success);
        Assert.Empty(result.Warnings);
        Assert.Equal(ManifestPath(), result.ManifestPath);
        Assert.Equal("full-width-layout", result.Before!["identifier"]?.GetValue<string>());
        Assert.Equal("test-full-width-layout", result.After!["identifier"]?.GetValue<string>());
        // The file keeps its name; only identifiers change.
        Assert.Equal("sections/full-width-layout.json", result.After["file"]?.GetValue<string>());

        string[] references =
        [
            "/routeConfigs/~1/structuralSection",
            "/routeConfigs/~1events~1list/structuralSection",
            "/onboarding/structuralSection",
        ];
        Assert.Equal(references, result.References);
        Assert.Equal(
            ["/entities/sections/0/identifier", .. references],
            result.Patch!.Select(op => op!["path"]!.GetValue<string>()));
        Assert.All(result.Patch!, op => Assert.Equal("replace", op!["op"]?.GetValue<string>()));

        // Nothing in the written manifest still names the old identifier.
        JsonObject written = ReadManifest();
        Assert.Empty(ManifestSectionRegistryService.FindMentions(written, "full-width-layout"));
        Assert.DoesNotContain("\"full-width-layout\"", File.ReadAllText(ManifestPath()));
        AssertPatchReproduces(original, result.Patch!, File.ReadAllText(ManifestPath()));
    }

    [Fact]
    public async Task RenameSection_RewritesArrayReferencesInsideAndOutsideRouteConfigs()
    {
        WriteStandardLayout();

        SectionMutationResult result = await _service.RenameSectionAsync(
            _layoutsPath, LayoutId, "events-page-header", "events-header",
            updateSectionFile: true, dryRun: false);

        Assert.True(result.Success);
        Assert.Equal(
            [
                "/routeConfigs/~1/patches/0/sectionIdentifiers/0",
                "/routeConfigs/~1events~1list/patches/0/sectionIdentifiers/0",
                "/onboarding/formSection",
                "/authoring/editableSections/0",
            ],
            result.References);

        // Array order survives: only the renamed element changes.
        JsonArray main = ReadManifest()["routeConfigs"]!["/events/list"]!["patches"]![0]!["sectionIdentifiers"]!.AsArray();
        Assert.Equal(["events-header", "event-list"], main.Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task RenameSection_UpdatesOnlyTheSectionFilesTopLevelIdentifier()
    {
        WriteStandardLayout();
        string sectionPath = SectionPath("sections/full-width-layout.json");

        SectionMutationResult result = await _service.RenameSectionAsync(
            _layoutsPath, LayoutId, "full-width-layout", "test-full-width-layout",
            updateSectionFile: true, dryRun: false);

        Assert.True(result.Success);
        Assert.Equal([ManifestPath(), sectionPath], result.FilesChanged);
        // Byte-for-byte: CRLF, the missing trailing newline, the characters a JSON round-trip
        // would escape, and the nested "identifier" all survive. Only the top-level value moved.
        Assert.Equal(
            Encoding.UTF8.GetBytes(StructuralSectionFile("test-full-width-layout")),
            File.ReadAllBytes(sectionPath));
    }

    [Fact]
    public async Task RenameSection_ManifestOnly_LeavesSectionFileAndWarns()
    {
        WriteStandardLayout();
        string sectionPath = SectionPath("sections/full-width-layout.json");
        byte[] originalSection = File.ReadAllBytes(sectionPath);

        SectionMutationResult result = await _service.RenameSectionAsync(
            _layoutsPath, LayoutId, "full-width-layout", "test-full-width-layout",
            updateSectionFile: false, dryRun: false);

        Assert.True(result.Success);
        Assert.Equal([ManifestPath()], result.FilesChanged);
        Assert.Contains("declares identifier 'full-width-layout'", Assert.Single(result.Warnings));
        Assert.Equal(originalSection, File.ReadAllBytes(sectionPath));
    }

    [Fact]
    public async Task RenameSection_DryRun_WritesNothing()
    {
        WriteStandardLayout();
        string sectionPath = SectionPath("sections/full-width-layout.json");
        byte[] originalManifest = File.ReadAllBytes(ManifestPath());
        byte[] originalSection = File.ReadAllBytes(sectionPath);

        SectionMutationResult result = await _service.RenameSectionAsync(
            _layoutsPath, LayoutId, "full-width-layout", "test-full-width-layout",
            updateSectionFile: true, dryRun: true);

        // The result still describes the full change...
        Assert.True(result.Success);
        Assert.Equal(4, result.Patch!.Count);
        Assert.Equal([ManifestPath(), sectionPath], result.FilesChanged);
        // ...but neither file moved.
        Assert.Equal(originalManifest, File.ReadAllBytes(ManifestPath()));
        Assert.Equal(originalSection, File.ReadAllBytes(sectionPath));
    }

    [Fact]
    public async Task RenameSection_RejectsUnknownIdentifierWithSuggestion()
    {
        WriteStandardLayout();

        SectionMutationResult result = await _service.RenameSectionAsync(
            _layoutsPath, LayoutId, "full-width-layuot", "whatever",
            updateSectionFile: true, dryRun: false);

        Assert.False(result.Success);
        Assert.Contains("Did you mean: full-width-layout", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task RenameSection_RejectsNewIdentifierAlreadyDeclared()
    {
        WriteStandardLayout();
        byte[] originalManifest = File.ReadAllBytes(ManifestPath());

        SectionMutationResult result = await _service.RenameSectionAsync(
            _layoutsPath, LayoutId, "full-width-layout", "sidebar-layout",
            updateSectionFile: true, dryRun: false);

        Assert.False(result.Success);
        Assert.Contains("already declared", Assert.Single(result.Errors));
        Assert.Equal(originalManifest, File.ReadAllBytes(ManifestPath()));
    }

    [Theory]
    [InlineData("", "new-id")]
    [InlineData("full-width-layout", "  ")]
    [InlineData("full-width-layout", " padded ")]
    [InlineData("full-width-layout", "full-width-layout")]
    public async Task RenameSection_RejectsInvalidArguments(string identifier, string newIdentifier)
    {
        WriteStandardLayout();

        SectionMutationResult result = await _service.RenameSectionAsync(
            _layoutsPath, LayoutId, identifier, newIdentifier,
            updateSectionFile: true, dryRun: false);

        Assert.False(result.Success);
        Assert.Single(result.Errors);
        // Even an input refusal names the file it would have targeted (issue #29).
        Assert.Equal(ManifestPath(), result.ManifestPath);
    }

    [Fact]
    public async Task RenameSection_LeavesNonSectionFieldsUntouchedAndWarns()
    {
        WriteManifest("""
            {
              "identifier": "test-layout",
              "entities": {
                "sections": [
                  { "identifier": "full-width-layout", "type": "ui-schema-section", "file": "sections/full-width-layout.json" }
                ]
              },
              "routeConfigs": { "/": { "structuralSection": "full-width-layout", "patches": [] } },
              "conventions": { "defaultLayout": "full-width-layout" }
            }
            """);
        WriteSectionFile("sections/full-width-layout.json", MinimalSectionFile("full-width-layout"));

        SectionMutationResult result = await _service.RenameSectionAsync(
            _layoutsPath, LayoutId, "full-width-layout", "renamed-layout",
            updateSectionFile: true, dryRun: false);

        Assert.True(result.Success);
        Assert.Equal(["/routeConfigs/~1/structuralSection"], result.References);
        Assert.Contains("/conventions/defaultLayout", Assert.Single(result.Warnings));
        Assert.Equal("full-width-layout", ReadManifest()["conventions"]!["defaultLayout"]?.GetValue<string>());
    }

    /// <summary>Section files a rename cannot update, with the reason it should give.</summary>
    public static TheoryData<string, string> UnupdatableSectionFiles => new()
    {
        { """{ "identifier": "something-else" }""", "declares identifier 'something-else'" },
        { """{ "identifier": "sidebar-layout", """, "is not valid JSON" },
        { """{ "type": "ui-schema-section" }""", "has no top-level \"identifier\"" },
    };

    [Theory]
    [MemberData(nameof(UnupdatableSectionFiles))]
    public async Task RenameSection_RefusesWhenTheSectionFileCannotBeUpdated(string content, string reason)
    {
        // The caller asked for both files; renaming only the manifest would break that promise.
        WriteStandardLayout();
        WriteSectionFile("sections/sidebar-layout.json", content);
        byte[] originalManifest = File.ReadAllBytes(ManifestPath());

        SectionMutationResult result = await _service.RenameSectionAsync(
            _layoutsPath, LayoutId, "sidebar-layout", "test-sidebar-layout",
            updateSectionFile: true, dryRun: false);

        Assert.False(result.Success);
        string error = Assert.Single(result.Errors);
        Assert.Contains(reason, error);
        Assert.Contains("manifest-only", error);
        Assert.Equal(originalManifest, File.ReadAllBytes(ManifestPath()));
        Assert.Equal(content, File.ReadAllText(SectionPath("sections/sidebar-layout.json")));
    }

    [Theory]
    [MemberData(nameof(UnupdatableSectionFiles))]
    public async Task RenameSection_ManifestOnly_ProceedsPastAnUnupdatableSectionFile(string content, string reason)
    {
        WriteStandardLayout();
        WriteSectionFile("sections/sidebar-layout.json", content);

        SectionMutationResult result = await _service.RenameSectionAsync(
            _layoutsPath, LayoutId, "sidebar-layout", "test-sidebar-layout",
            updateSectionFile: false, dryRun: false);

        Assert.True(result.Success);
        Assert.Equal([ManifestPath()], result.FilesChanged);
        Assert.Contains(reason, Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task RenameSection_RefusesWhenTheSectionFileCannotBeRead()
    {
        WriteStandardLayout();
        string sectionPath = SectionPath("sections/full-width-layout.json");
        byte[] originalManifest = File.ReadAllBytes(ManifestPath());
        ManifestSectionRegistryService service = ServiceWith(new FaultyFileService(
            readFault: path => path == sectionPath ? new IOException("in use") : null));

        SectionMutationResult result = await service.RenameSectionAsync(
            _layoutsPath, LayoutId, "full-width-layout", "test-full-width-layout",
            updateSectionFile: true, dryRun: false);

        Assert.False(result.Success);
        Assert.Contains("could not be read (in use)", Assert.Single(result.Errors));
        Assert.Equal(originalManifest, File.ReadAllBytes(ManifestPath()));
    }

    [Fact]
    public async Task RenameSection_ProceedsWithAWarningWhenTheSectionFileIsMissing()
    {
        // No file means nothing to keep in step, so the registry rename still goes ahead.
        WriteStandardLayout();
        File.Delete(SectionPath("sections/sidebar-layout.json"));

        SectionMutationResult result = await _service.RenameSectionAsync(
            _layoutsPath, LayoutId, "sidebar-layout", "test-sidebar-layout",
            updateSectionFile: true, dryRun: false);

        Assert.True(result.Success);
        Assert.Equal([ManifestPath()], result.FilesChanged);
        Assert.Contains("does not exist", Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task RenameAndRemove_RefuseAnIdentifierDeclaredTwice()
    {
        WriteManifest("""
            {
              "identifier": "test-layout",
              "entities": {
                "sections": [
                  { "identifier": "twin", "type": "ui-schema-section", "file": "sections/twin.json" },
                  { "identifier": "twin", "type": "ui-schema-section", "file": "sections/twin.json" }
                ]
              }
            }
            """);
        byte[] originalManifest = File.ReadAllBytes(ManifestPath());

        SectionMutationResult renamed = await _service.RenameSectionAsync(
            _layoutsPath, LayoutId, "twin", "single", updateSectionFile: false, dryRun: false);
        SectionMutationResult removed = await _service.RemoveSectionAsync(
            _layoutsPath, LayoutId, "twin", force: true, dryRun: false);

        Assert.Contains("declared 2 times", Assert.Single(renamed.Errors));
        Assert.Contains("declared 2 times", Assert.Single(removed.Errors));
        Assert.Equal(originalManifest, File.ReadAllBytes(ManifestPath()));
    }

    [Theory]
    [InlineData("""{ "identifier": "test-layout", "entities": [] }""", "\"entities\" is not a JSON object")]
    [InlineData("""{ "identifier": "test-layout", "entities": { "sections": {} } }""", "\"entities.sections\" is not a JSON array")]
    public async Task AllOperations_RefuseAMalformedRegistryInsteadOfOverwritingIt(string manifest, string reason)
    {
        WriteManifest(manifest);

        SectionMutationResult added = await _service.AddSectionAsync(
            _layoutsPath, LayoutId, new SectionEntryInput("hero"), dryRun: false);
        SectionMutationResult renamed = await _service.RenameSectionAsync(
            _layoutsPath, LayoutId, "hero", "banner", updateSectionFile: false, dryRun: false);
        SectionMutationResult removed = await _service.RemoveSectionAsync(
            _layoutsPath, LayoutId, "hero", force: true, dryRun: false);

        Assert.All([added, renamed, removed], result => Assert.Contains(reason, Assert.Single(result.Errors)));
        Assert.Equal(manifest, File.ReadAllText(ManifestPath()));
    }

    [Fact]
    public async Task RenameSection_RestoresManifestWhenSectionFileWriteFails()
    {
        WriteStandardLayout();
        string sectionPath = SectionPath("sections/full-width-layout.json");
        byte[] originalManifest = File.ReadAllBytes(ManifestPath());
        byte[] originalSection = File.ReadAllBytes(sectionPath);
        ManifestSectionRegistryService service = ServiceWith(new FaultyFileService(
            writeFault: path => path == sectionPath ? new IOException("locked") : null));

        SectionMutationResult result = await service.RenameSectionAsync(
            _layoutsPath, LayoutId, "full-width-layout", "test-full-width-layout",
            updateSectionFile: true, dryRun: false);

        Assert.False(result.Success);
        Assert.Contains("restored, so nothing was renamed", Assert.Single(result.Errors));
        Assert.Null(result.Patch);
        Assert.Empty(result.FilesChanged);
        Assert.Equal(originalManifest, File.ReadAllBytes(ManifestPath()));
        Assert.Equal(originalSection, File.ReadAllBytes(sectionPath));
    }

    [Fact]
    public async Task RenameSection_ReportsWhatIsLeftChangedWhenTheRestoreAlsoFails()
    {
        WriteStandardLayout();
        string sectionPath = SectionPath("sections/full-width-layout.json");
        byte[] originalSection = File.ReadAllBytes(sectionPath);
        // Every raw write fails: the section file, then the manifest restore.
        ManifestSectionRegistryService service = ServiceWith(new FaultyFileService(
            writeFault: _ => new IOException("disk full")));

        SectionMutationResult result = await service.RenameSectionAsync(
            _layoutsPath, LayoutId, "full-width-layout", "test-full-width-layout",
            updateSectionFile: true, dryRun: false);

        // Not a clean refusal: the envelope must say the manifest is still changed.
        Assert.False(result.Success);
        Assert.Contains("restoring layout-manifest.json failed too", Assert.Single(result.Errors));
        Assert.Equal([ManifestPath()], result.FilesChanged);
        Assert.NotNull(result.Patch);
        Assert.Equal(
            "test-full-width-layout",
            ReadManifest()["entities"]!["sections"]![0]!["identifier"]?.GetValue<string>());
        Assert.Equal(originalSection, File.ReadAllBytes(sectionPath));
    }

    [Fact]
    public async Task RenameSection_RefusesWhenTheManifestBackupCannotBeRead()
    {
        // The two-file write reads the manifest's bytes first so it can roll back; without
        // them it must not start writing.
        WriteStandardLayout();
        string sectionPath = SectionPath("sections/full-width-layout.json");
        byte[] originalManifest = File.ReadAllBytes(ManifestPath());
        byte[] originalSection = File.ReadAllBytes(sectionPath);
        ManifestSectionRegistryService service = ServiceWith(new FaultyFileService(
            readFault: path => path == ManifestPath() ? new IOException("in use") : null));

        SectionMutationResult result = await service.RenameSectionAsync(
            _layoutsPath, LayoutId, "full-width-layout", "test-full-width-layout",
            updateSectionFile: true, dryRun: false);

        Assert.False(result.Success);
        Assert.Contains($"Failed to read {ManifestPath()} before writing", Assert.Single(result.Errors));
        Assert.Empty(result.FilesChanged);
        Assert.Equal(originalManifest, File.ReadAllBytes(ManifestPath()));
        Assert.Equal(originalSection, File.ReadAllBytes(sectionPath));
    }

    [Fact]
    public async Task RenameSection_RestoresManifestThenRethrowsAnUnexpectedFailure()
    {
        WriteStandardLayout();
        string sectionPath = SectionPath("sections/full-width-layout.json");
        byte[] originalManifest = File.ReadAllBytes(ManifestPath());
        ManifestSectionRegistryService service = ServiceWith(new FaultyFileService(
            writeFault: path => path == sectionPath ? new InvalidOperationException("boom") : null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameSectionAsync(
            _layoutsPath, LayoutId, "full-width-layout", "test-full-width-layout",
            updateSectionFile: true, dryRun: false));

        Assert.Equal(originalManifest, File.ReadAllBytes(ManifestPath()));
    }

    [Fact]
    public async Task RenameSection_ManifestNotFound_ReturnsError()
    {
        SectionMutationResult result = await _service.RenameSectionAsync(
            _layoutsPath, LayoutId, "a", "b", updateSectionFile: true, dryRun: false);

        Assert.False(result.Success);
        Assert.Contains("not found", Assert.Single(result.Errors));
    }

    // ───── remove ─────

    [Fact]
    public async Task RemoveSection_RefusesWhileReferenced()
    {
        WriteStandardLayout();
        byte[] originalManifest = File.ReadAllBytes(ManifestPath());

        SectionMutationResult result = await _service.RemoveSectionAsync(
            _layoutsPath, LayoutId, "not-found-section", force: false, dryRun: false);

        Assert.False(result.Success);
        Assert.Contains("still referenced by 1 field", Assert.Single(result.Errors));
        Assert.Equal(["/routing/notFoundSection"], result.References);
        Assert.NotNull(result.Before);
        Assert.Null(result.Patch);
        Assert.Equal(originalManifest, File.ReadAllBytes(ManifestPath()));
    }

    [Fact]
    public async Task RemoveSection_Force_RemovesAndReportsDanglingReferences()
    {
        WriteStandardLayout();
        string original = File.ReadAllText(ManifestPath());

        SectionMutationResult result = await _service.RemoveSectionAsync(
            _layoutsPath, LayoutId, "event-list", force: true, dryRun: false);

        Assert.True(result.Success);
        string[] references =
        [
            "/routeConfigs/~1events~1list/patches/0/sectionIdentifiers/1",
            "/routeConfigs/~1profile/patches/0/sectionIdentifiers/0",
        ];
        Assert.Equal(references, result.References);
        Assert.Equal(2, result.Warnings.Count);
        Assert.All(references, pointer => Assert.Contains(result.Warnings, w => w.StartsWith(pointer)));
        Assert.Equal("/entities/sections/3", Assert.Single(result.Patch!)!["path"]?.GetValue<string>());
        Assert.DoesNotContain("event-list", ManifestSectionValidator.CollectDeclaredSectionIdentifiers(ReadManifest()));
        AssertPatchReproduces(original, result.Patch!, File.ReadAllText(ManifestPath()));
    }

    [Fact]
    public async Task RemoveSection_RemovesUnreferencedEntryAndKeepsItsFile()
    {
        WriteStandardLayout();

        SectionMutationResult result = await _service.RemoveSectionAsync(
            _layoutsPath, LayoutId, "unused-section", force: false, dryRun: false);

        Assert.True(result.Success);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.References);
        Assert.Null(result.After);
        Assert.Equal([ManifestPath()], result.FilesChanged);
        Assert.DoesNotContain("unused-section", ManifestSectionValidator.CollectDeclaredSectionIdentifiers(ReadManifest()));
        Assert.True(File.Exists(SectionPath("sections/unused-section.json")));
    }

    [FactWhenFilePermissionsEnforced]
    public async Task RemoveSection_ManifestWriteFailure_ReturnsAnErrorNamingTheFile()
    {
        // Same contract as set-route since #29: a failed write is a result that names the
        // file, not an exception that MCP clients would only see as a generic error.
        WriteStandardLayout();
        File.SetAttributes(ManifestPath(), FileAttributes.ReadOnly);
        try
        {
            SectionMutationResult result = await _service.RemoveSectionAsync(
                _layoutsPath, LayoutId, "unused-section", force: false, dryRun: false);

            Assert.False(result.Success);
            Assert.Contains($"Failed to write {ManifestPath()}", Assert.Single(result.Errors));
            Assert.Empty(result.FilesChanged);
            Assert.Null(result.Patch);
        }
        finally
        {
            File.SetAttributes(ManifestPath(), FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task RemoveSection_RejectsUnknownIdentifier()
    {
        WriteStandardLayout();

        SectionMutationResult result = await _service.RemoveSectionAsync(
            _layoutsPath, LayoutId, "no-such-section", force: true, dryRun: false);

        Assert.False(result.Success);
        Assert.Contains("not declared", Assert.Single(result.Errors));
    }

    // ───── helpers under test ─────

    [Theory]
    [InlineData("structuralSection", true)]
    [InlineData("sectionIdentifiers", true)]
    [InlineData("notFoundSection", true)]
    [InlineData("editableSections", true)]
    [InlineData("targetElementId", false)]
    [InlineData("identifier", false)]
    [InlineData("layoutId", false)]
    [InlineData(null, false)]
    public void IsSectionReferenceField_FollowsTheNamingConvention(string? propertyName, bool expected)
    {
        Assert.Equal(expected, ManifestSectionRegistryService.IsSectionReferenceField(propertyName));
    }

    [Fact]
    public void FindTopLevelIdentifier_SkipsNestedIdentifiersAndCountsTheBom()
    {
        byte[] json = [0xEF, 0xBB, 0xBF, .. """{"data":{"identifier":"inner"},"identifier":"outer"}"""u8];

        ManifestSectionRegistryService.IdentifierToken? token = ManifestSectionRegistryService.FindTopLevelIdentifier(json);

        Assert.NotNull(token);
        Assert.Equal("outer", token.Value);
        byte[] replaced = ManifestSectionRegistryService.ReplaceToken(json, token, "renamed");
        Assert.Equal(
            [0xEF, 0xBB, 0xBF, .. """{"data":{"identifier":"inner"},"identifier":"renamed"}"""u8],
            replaced);
    }

    [Theory]
    [InlineData("""{"data":{"identifier":"inner"}}""")]
    [InlineData("""{"identifier":42}""")]
    [InlineData("""["identifier"]""")]
    public void FindTopLevelIdentifier_ReturnsNullWithoutATopLevelStringIdentifier(string json)
    {
        Assert.Null(ManifestSectionRegistryService.FindTopLevelIdentifier(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("""{"identifier":"a",""")]
    [InlineData("""{"identifier":"a"} trailing""")]
    public void FindTopLevelIdentifier_RejectsMalformedJsonEvenAfterTheIdentifier(string json)
    {
        // Stopping at the identifier would splice a broken file instead of reporting it.
        Assert.ThrowsAny<JsonException>(
            () => ManifestSectionRegistryService.FindTopLevelIdentifier(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void ReplaceToken_ReplacesAnEscapedValueWhole()
    {
        // The raw token is longer than the decoded value (the JSON escape \/ decodes to /, and
        // some serializers, PHP's json_encode among them, write slashes that way), so the
        // splice must use the raw length.
        byte[] json = """{"identifier":"full\/width","type":"x"}"""u8.ToArray();

        ManifestSectionRegistryService.IdentifierToken token = ManifestSectionRegistryService.FindTopLevelIdentifier(json)!;
        byte[] replaced = ManifestSectionRegistryService.ReplaceToken(json, token, "renamed");

        Assert.Equal("full/width", token.Value);
        // Guards the fixture itself: 11 raw characters + 2 quotes, against 10 decoded.
        Assert.Equal(13, token.Length);
        Assert.Equal("""{"identifier":"renamed","type":"x"}""", Encoding.UTF8.GetString(replaced));
    }

    [Fact]
    public void ReplaceToken_WritesTheNewIdentifierLiterally()
    {
        // Issue #38: the manifest keeps these characters literal, so the spliced token must
        // too, or a rename would spell the identifier differently in the two files.
        byte[] json = """{"identifier":"old","type":"x"}"""u8.ToArray();

        ManifestSectionRegistryService.IdentifierToken token = ManifestSectionRegistryService.FindTopLevelIdentifier(json)!;
        byte[] replaced = ManifestSectionRegistryService.ReplaceToken(json, token, "café's+section");

        Assert.Equal("""{"identifier":"café's+section","type":"x"}""", Encoding.UTF8.GetString(replaced));
    }

    // ───── fixtures ─────

    private string ManifestPath()
        => Path.Combine(_layoutsPath, LayoutId, "manifests", "layout-manifest.json");

    private string SectionPath(string relativeFile)
        => Path.GetFullPath(Path.Combine(_layoutsPath, LayoutId, relativeFile));

    private JsonObject ReadManifest() => JsonNode.Parse(File.ReadAllText(ManifestPath()))!.AsObject();

    private void WriteManifest(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath())!);
        File.WriteAllText(ManifestPath(), content);
    }

    private void WriteSectionFile(string relativeFile, string content)
    {
        string path = SectionPath(relativeFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    /// Writes <see cref="StandardManifest"/> plus a section file for every declared section.
    /// full-width-layout's file is the realistic one (<see cref="StructuralSectionFile"/>).
    /// </summary>
    private void WriteStandardLayout()
    {
        WriteManifest(StandardManifest);
        foreach (string identifier in ManifestSectionValidator.CollectDeclaredSectionIdentifiers(ReadManifest()))
        {
            WriteSectionFile(
                $"sections/{identifier}.json",
                identifier == "full-width-layout" ? StructuralSectionFile(identifier) : MinimalSectionFile(identifier));
        }
    }

    private static string MinimalSectionFile(string identifier)
        => $$"""{ "identifier": "{{identifier}}", "type": "ui-schema-section" }""";

    /// <summary>
    /// A section file shaped like the real ones: CRLF line endings, no trailing newline,
    /// characters System.Text.Json would escape on a round-trip, and a nested
    /// <c>identifier</c> that a rename must not touch.
    /// </summary>
    private static string StructuralSectionFile(string identifier)
        => "{\r\n"
         + $"  \"identifier\": \"{identifier}\",\r\n"
         + "  \"type\": \"ui-schema-section\",\r\n"
         + "  \"layoutId\": \"test-layout\",\r\n"
         + "  \"data\": {\r\n"
         + "    \"identifier\": \"full-width-layout\",\r\n"
         + "    \"props\": { \"title\": \"Tom's <b>events</b> & more +1\" }\r\n"
         + "  }\r\n"
         + "}";

    private static ManifestSectionRegistryService ServiceWith(LocalFileService fileService)
        => new(fileService, NullLogger<ManifestSectionRegistryService>.Instance);

    /// <summary>
    /// A <see cref="LocalFileService"/> whose raw-byte reads and writes fail on demand, so the
    /// failure paths are tested the same way on every OS and as any user.
    /// </summary>
    private sealed class FaultyFileService(
        Func<string, Exception?>? readFault = null,
        Func<string, Exception?>? writeFault = null)
        : LocalFileService(NullLogger<LocalFileService>.Instance)
    {
        public override Task<byte[]> ReadAllBytesAsync(string filePath, CancellationToken ct = default)
            => readFault?.Invoke(filePath) is { } ex
                ? Task.FromException<byte[]>(ex)
                : base.ReadAllBytesAsync(filePath, ct);

        public override Task WriteAllBytesAsync(string filePath, byte[] content, CancellationToken ct = default)
            => writeFault?.Invoke(filePath) is { } ex
                ? Task.FromException(ex)
                : base.WriteAllBytesAsync(filePath, content, ct);
    }

    /// <summary>
    /// Applies <paramref name="patch"/> to <paramref name="originalJson"/> and asserts the
    /// result equals <paramref name="writtenJson"/> — i.e. the reported patch is exactly the
    /// change that was written, with paths valid from the manifest root.
    /// </summary>
    private static void AssertPatchReproduces(string originalJson, JsonArray patch, string writtenJson)
    {
        JsonNode document = JsonNode.Parse(originalJson)!;
        foreach (JsonNode? node in patch)
        {
            JsonObject op = node!.AsObject();
            string[] tokens = op["path"]!.GetValue<string>()
                .Split('/')
                .Skip(1)
                .Select(t => t.Replace("~1", "/").Replace("~0", "~"))
                .ToArray();
            JsonNode parent = document;
            foreach (string token in tokens[..^1])
                parent = parent is JsonArray array ? array[int.Parse(token)]! : parent[token]!;

            string last = tokens[^1];
            JsonNode? value = op["value"]?.DeepClone();
            switch (op["op"]!.GetValue<string>(), parent)
            {
                case ("add", JsonArray array) when last == "-": array.Add(value); break;
                case ("add", JsonObject obj): obj[last] = value; break;
                case ("replace", JsonArray array): array[int.Parse(last)] = value; break;
                case ("replace", JsonObject obj): obj[last] = value; break;
                case ("remove", JsonArray array): array.RemoveAt(int.Parse(last)); break;
                case ("remove", JsonObject obj): obj.Remove(last); break;
                default: throw new InvalidOperationException($"Unsupported op: {op.ToJsonString()}");
            }
        }

        Assert.True(
            JsonNode.DeepEquals(document, JsonNode.Parse(writtenJson)),
            $"Patch did not reproduce the written manifest.\nApplied: {document.ToJsonString()}\nWritten: {writtenJson}");
    }

    /// <summary>
    /// Seven declared sections, referenced from every field shape found in real manifests:
    /// routeConfigs structuralSection (including a route key that needs ~1 escaping), patch
    /// sectionIdentifiers, routing.notFoundSection, onboarding.structuralSection / formSection,
    /// and authoring.editableSections[]. unused-section is declared but never referenced.
    /// </summary>
    private const string StandardManifest = """
        {
          "identifier": "test-layout",
          "routing": {
            "defaultRoute": "/",
            "notFoundSection": "not-found-section"
          },
          "entities": {
            "sections": [
              { "identifier": "full-width-layout", "type": "ui-schema-section", "file": "sections/full-width-layout.json", "description": "Full-width structural section" },
              { "identifier": "sidebar-layout", "type": "ui-schema-section", "file": "sections/sidebar-layout.json", "description": "Sidebar structural section" },
              { "identifier": "events-page-header", "type": "ui-schema-section", "file": "sections/events-page-header.json", "description": "Events header" },
              { "identifier": "event-list", "type": "ui-schema-section", "file": "sections/event-list.json", "description": "Event list" },
              { "identifier": "sidebar-summary", "type": "ui-schema-section", "file": "sections/sidebar-summary.json", "description": "Sidebar summary" },
              { "identifier": "not-found-section", "type": "ui-schema-section", "file": "sections/not-found-section.json", "description": "404 page" },
              { "identifier": "unused-section", "type": "ui-schema-section", "file": "sections/unused-section.json", "description": "Declared, never referenced" }
            ]
          },
          "routeConfigs": {
            "/": {
              "structuralSection": "full-width-layout",
              "patches": [
                { "targetElementId": "main", "sectionIdentifiers": ["events-page-header"] }
              ]
            },
            "/events/list": {
              "structuralSection": "full-width-layout",
              "patches": [
                { "targetElementId": "main", "sectionIdentifiers": ["events-page-header", "event-list"] },
                { "targetElementId": "sidebar", "sectionIdentifiers": ["sidebar-summary"] }
              ]
            },
            "/profile": {
              "structuralSection": "sidebar-layout",
              "patches": [
                { "targetElementId": "main", "sectionIdentifiers": ["event-list"] }
              ]
            }
          },
          "onboarding": {
            "structuralSection": "full-width-layout",
            "formSection": "events-page-header"
          },
          "authoring": {
            "editableSections": ["events-page-header"]
          }
        }
        """;
}
