using System.Text.Json.Nodes;
using LayoutSync.Configuration;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// End-to-end tests for issue #28 (and #31): real seed files in a scratch layouts tree, synced by
/// the real <see cref="DocumentSyncService"/> against <see cref="InMemoryRavenDbService"/>.
///
/// The live incident (coreConvention/w31rd#2843): dirt-life and cream-pi both shipped a
/// <c>full-width-layout</c> section, each declaring its own layoutId. Sections were looked up by
/// identifier alone, so both files resolved to ONE document; whichever synced last took it over,
/// and the tenant that lost could no longer compose any page. Discovery order differs between
/// fresh checkouts, so the winner flipped from sync to sync.
/// </summary>
public sealed class LayoutScopedSyncTests : IDisposable
{
    private readonly string _root;
    private readonly string _layoutsPath;
    private readonly InMemoryRavenDbService _store = new();

    public LayoutScopedSyncTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "layoutsync-tests-" + Guid.NewGuid().ToString("N"));
        _layoutsPath = Path.Combine(_root, "layouts");
        Directory.CreateDirectory(_layoutsPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private DocumentSyncService CreateSync() => new(
        NullLogger<DocumentSyncService>.Instance,
        new LocalFileService(NullLogger<LocalFileService>.Instance),
        _store,
        new RelativeDateResolver(NullLogger<RelativeDateResolver>.Instance),
        [],
        new SyncOptions(),
        new CommandLineArgs());

    private void WriteSeed(string layout, string folder, string fileName, JsonObject content)
    {
        string directory = Path.Combine(_layoutsPath, layout, folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content.ToJsonString());
    }

    private void WritePlatformTheme(string fileName, JsonObject content)
    {
        // The platform catalogue is a `themes/` sibling of the layouts root.
        string directory = Path.Combine(_root, "themes");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content.ToJsonString());
    }

    /// <summary>A seed in the shape w31rd ships; <paramref name="variant"/> tells copies apart.</summary>
    private static JsonObject Seed(string identifier, string? layoutId, string variant, string type = "ui-schema-section")
    {
        JsonObject seed = new();
        if (layoutId != null)
            seed["layoutId"] = layoutId;
        seed["identifier"] = identifier;
        seed["type"] = type;
        seed["active"] = true;
        seed["data"] = new JsonObject { ["props"] = new JsonObject { ["variant"] = variant } };
        return seed;
    }

    private static string Variant(JsonObject document) => document["data"]!["props"]!["variant"]!.GetValue<string>();

    private static string? LayoutIdOf(JsonObject document) => document["layoutId"]?.GetValue<string>();

    // ── The three behaviors issue #28 asks for ─────────────────────────────────

    [Fact]
    public async Task TwoLayoutsSharingASectionIdentifier_SyncToTwoSeparateDocuments()
    {
        WriteSeed("dirt-life", "sections", "full-width-layout.json", Seed("full-width-layout", "dirt-life", "dirt-life-full-width"));
        WriteSeed("cream-pi", "sections", "full-width-layout.json", Seed("full-width-layout", "cream-pi", "cream-pi-full-width"));
        DocumentSyncService sync = CreateSync();

        await sync.SyncAllAsync(_layoutsPath);
        List<string> idsAfterFirstSync = [.. _store.DocumentsIn("sections").Keys.Order()];
        // The old lookup flip-flopped on EVERY sync, so one clean pass is not enough evidence.
        await sync.SyncAllAsync(_layoutsPath);

        IReadOnlyDictionary<string, JsonObject> sections = _store.DocumentsIn("sections");
        List<string> idsAfterSecondSync = [.. sections.Keys.Order()];
        Assert.Equal(2, sections.Count);
        Assert.Equal(idsAfterFirstSync, idsAfterSecondSync);
        Dictionary<string, string> variantByLayout = sections.Values.ToDictionary(doc => LayoutIdOf(doc)!, Variant);
        Assert.Equal("dirt-life-full-width", variantByLayout["dirt-life"]);
        Assert.Equal("cream-pi-full-width", variantByLayout["cream-pi"]);
        Assert.Equal(0, sync.DocumentCollisionCount);
        Assert.Equal(0, _store.DuplicateEntityIdentifierCount);
    }

    [Fact]
    public async Task SectionWithoutLayoutId_StillMatchesByIdentifier()
    {
        // A genuinely shared section — its file declares no layoutId — resolves to the existing
        // document by identifier and is replaced in place, exactly as before #28.
        _store.Seed("sections", "docs/shared", Seed("shared-footer", layoutId: null, "old"));
        WriteSeed("dirt-life", "sections", "shared-footer.json", Seed("shared-footer", layoutId: null, "new"));

        await CreateSync().SyncAllAsync(_layoutsPath);

        (string documentId, JsonObject document) = Assert.Single(_store.DocumentsIn("sections"));
        Assert.Equal("docs/shared", documentId);
        Assert.Equal("new", Variant(document));
        Assert.Null(LayoutIdOf(document));
    }

    [Fact]
    public async Task CollidingFiles_AreNotSynced_AndFailStrict()
    {
        // Two files declaring one document identity (here a copy that kept the identifier). Neither
        // is written — whichever order they are discovered in — and --strict fails the run.
        _store.Seed("sections", "docs/hero", Seed("hero", "dirt-life", "current"));
        WriteSeed("dirt-life", "sections", "hero.json", Seed("hero", "dirt-life", "hero-a"));
        WriteSeed("dirt-life", "sections", "hero-copy.json", Seed("hero", "dirt-life", "hero-b"));
        DocumentSyncService sync = CreateSync();

        SyncBatchResult batch = await sync.SyncAllAsync(_layoutsPath);

        (_, JsonObject stored) = Assert.Single(_store.DocumentsIn("sections"));
        Assert.Equal("current", Variant(stored));
        Assert.Equal(2, batch.Results.Count);
        Assert.All(batch.Results, result => Assert.StartsWith("Collision", result.ErrorMessage));
        Assert.Equal(1, sync.DocumentCollisionCount);

        IReadOnlyList<string> offenses = StrictModeGate.Offenses(
            _store.DuplicateEntityIdentifierCount, sync.DocumentCollisionCount, validators: []);
        Assert.Contains(offenses, offense => offense.Contains("document collision"));
        Assert.Equal(StrictModeGate.ExitCode, StrictModeGate.ExitCodeFor(offenses));
    }

    // ── Collision detection: CI-relevant details ───────────────────────────────

    [Fact]
    public async Task Collision_IsDetectedInDryRun_BeforeTheDocumentExists()
    {
        // w31rd's PR gate is a --strict --dry-run: a new colliding pair must fail it before merge,
        // even though no stored document exists yet for either file to resolve to.
        WriteSeed("dirt-life", "sections", "promo.json", Seed("promo", "dirt-life", "a"));
        WriteSeed("dirt-life", "sections", "promo-v2.json", Seed("promo", "dirt-life", "b"));
        DocumentSyncService sync = CreateSync();

        await sync.SyncAllAsync(_layoutsPath, dryRun: true);

        Assert.Equal(1, sync.DocumentCollisionCount);
        Assert.Empty(_store.DocumentsIn("sections"));
    }

    [Fact]
    public async Task CollidingFiles_UnderClean_DoNotOrphanTheDocumentTheyDeclare()
    {
        _store.Seed("sections", "docs/hero", Seed("hero", "dirt-life", "current"));
        WriteSeed("dirt-life", "sections", "hero.json", Seed("hero", "dirt-life", "hero-a"));
        WriteSeed("dirt-life", "sections", "hero-copy.json", Seed("hero", "dirt-life", "hero-b"));

        SyncBatchResult batch = await CreateSync().SyncAllAsync(_layoutsPath, cleanOrphans: true);

        Assert.Single(_store.DocumentsIn("sections"));
        Assert.Equal(0, batch.OrphanDeletedCount);
    }

    [Fact]
    public async Task CollidingFiles_UnderClean_KeepTheUnattributedDocumentTheyCouldAdopt()
    {
        // The refused pair never learns which document is its own; the legacy one they would have
        // adopted must survive --clean until the collision is fixed.
        _store.Seed("sections", "docs/legacy", Seed("hero", layoutId: null, "legacy"));
        WriteSeed("dirt-life", "sections", "hero.json", Seed("hero", "dirt-life", "hero-a"));
        WriteSeed("dirt-life", "sections", "hero-copy.json", Seed("hero", "dirt-life", "hero-b"));

        SyncBatchResult batch = await CreateSync().SyncAllAsync(_layoutsPath, cleanOrphans: true);

        Assert.Equal("docs/legacy", Assert.Single(_store.DocumentsIn("sections")).Key);
        Assert.Equal(0, batch.OrphanDeletedCount);
    }

    [Fact]
    public async Task IdenticalCopies_AreSyncedOnce_AndStillFailStrict()
    {
        // Byte-identical copies carry no conflict, so the document still gets the content — once —
        // but the duplication is still reported and --strict still fails.
        WriteSeed("dirt-life", "sections", "promo.json", Seed("promo", "dirt-life", "same"));
        WriteSeed("dirt-life", "sections", "promo-copy.json", Seed("promo", "dirt-life", "same"));
        DocumentSyncService sync = CreateSync();

        SyncBatchResult batch = await sync.SyncAllAsync(_layoutsPath);

        Assert.Equal("same", Variant(Assert.Single(_store.DocumentsIn("sections")).Value));
        Assert.Single(batch.Results, result => result.Action == SyncAction.Created);
        Assert.Single(batch.Results, result => result.ErrorMessage?.StartsWith("Collision: identical copy") == true);
        Assert.Equal(1, sync.DocumentCollisionCount);
        Assert.Equal(
            StrictModeGate.ExitCode,
            StrictModeGate.ExitCodeFor(StrictModeGate.Offenses(0, sync.DocumentCollisionCount, validators: [])));
    }

    // ── Migration: the first layout-scoped sync of an existing database ────────

    [Fact]
    public async Task FirstScopedSync_OfTheClobberedDatabase_GivesTheLosingLayoutItsOwnDocument()
    {
        // The shared database after the 2026-09-27 outage: one full-width-layout document holding
        // cream-pi's content and layoutId. cream-pi keeps it (same id); dirt-life gets its own. The
        // "fall back to identifier-only when exactly one document has the identifier" rule would
        // hand dirt-life cream-pi's document instead — re-opening the flip-flop.
        _store.Seed("sections", "docs/clobbered", Seed("full-width-layout", "cream-pi", "cream-pi-full-width"));
        WriteSeed("dirt-life", "sections", "full-width-layout.json", Seed("full-width-layout", "dirt-life", "dirt-life-full-width"));
        WriteSeed("cream-pi", "sections", "full-width-layout.json", Seed("full-width-layout", "cream-pi", "cream-pi-full-width"));

        await CreateSync().SyncAllAsync(_layoutsPath);

        IReadOnlyDictionary<string, JsonObject> sections = _store.DocumentsIn("sections");
        Assert.Equal(2, sections.Count);
        Assert.Equal("cream-pi", LayoutIdOf(sections["docs/clobbered"]));
        Assert.Equal("cream-pi-full-width", Variant(sections["docs/clobbered"]));
        JsonObject dirtLife = Assert.Single(sections, kvp => kvp.Key != "docs/clobbered").Value;
        Assert.Equal("dirt-life", LayoutIdOf(dirtLife));
        Assert.Equal("dirt-life-full-width", Variant(dirtLife));
        Assert.Equal(0, _store.DuplicateEntityIdentifierCount);
    }

    [Fact]
    public async Task FirstScopedSync_AdoptsAnUnattributedDocument_InsteadOfMintingADuplicate()
    {
        // Stored before its file declared a layoutId: re-attributed in place, not left beside a new one.
        _store.Seed("sections", "docs/legacy", Seed("hero", layoutId: null, "legacy"));
        WriteSeed("dirt-life", "sections", "hero.json", Seed("hero", "dirt-life", "hero"));

        await CreateSync().SyncAllAsync(_layoutsPath);

        (string documentId, JsonObject document) = Assert.Single(_store.DocumentsIn("sections"));
        Assert.Equal("docs/legacy", documentId);
        Assert.Equal("dirt-life", LayoutIdOf(document));
        Assert.Equal("hero", Variant(document));
    }

    [Fact]
    public async Task UnattributedDocument_StaysWithTheFileThatDeclaresNoLayoutId()
    {
        // A shared (unattributed) section and a tenant copy of the same identifier. The shared file
        // owns the unattributed document in any discovery order — the tenant copy never adopts it.
        _store.Seed("sections", "docs/shared", Seed("footer", layoutId: null, "shared"));
        WriteSeed("alpha", "sections", "footer.json", Seed("footer", "alpha", "alpha-footer"));
        WriteSeed("zulu", "sections", "footer.json", Seed("footer", layoutId: null, "shared-v2"));

        await CreateSync().SyncAllAsync(_layoutsPath);

        IReadOnlyDictionary<string, JsonObject> sections = _store.DocumentsIn("sections");
        Assert.Equal(2, sections.Count);
        Assert.Equal("shared-v2", Variant(sections["docs/shared"]));
        Assert.Null(LayoutIdOf(sections["docs/shared"]));
        JsonObject tenant = Assert.Single(sections, kvp => kvp.Key != "docs/shared").Value;
        Assert.Equal("alpha", LayoutIdOf(tenant));
    }

    [Fact]
    public async Task ScopedRun_NeverAdoptsADocumentOwnedByAnUnattributedFileInAnotherLayout()
    {
        // `--layout alpha` does not sync zulu, but zulu's unattributed footer.json still owns the
        // unattributed document: alpha's tenant copy gets its own instead of taking that one over.
        _store.Seed("sections", "docs/shared", Seed("footer", layoutId: null, "shared"));
        WriteSeed("alpha", "sections", "footer.json", Seed("footer", "alpha", "alpha-footer"));
        WriteSeed("zulu", "sections", "footer.json", Seed("footer", layoutId: null, "shared"));

        await CreateSync().SyncAllAsync(_layoutsPath, layout: "alpha");

        IReadOnlyDictionary<string, JsonObject> sections = _store.DocumentsIn("sections");
        Assert.Equal(2, sections.Count);
        Assert.Null(LayoutIdOf(sections["docs/shared"]));
        Assert.Equal("alpha", LayoutIdOf(Assert.Single(sections, kvp => kvp.Key != "docs/shared").Value));
    }

    [Fact]
    public async Task UnattributedFile_NeverCapturesAnotherLayoutsDocument()
    {
        _store.Seed("sections", "docs/dl", Seed("footer", "dirt-life", "dirt-life-footer"));
        WriteSeed("commons", "sections", "footer.json", Seed("footer", layoutId: null, "shared"));

        await CreateSync().SyncAllAsync(_layoutsPath);

        IReadOnlyDictionary<string, JsonObject> sections = _store.DocumentsIn("sections");
        Assert.Equal(2, sections.Count);
        Assert.Equal("dirt-life", LayoutIdOf(sections["docs/dl"]));
        Assert.Equal("dirt-life-footer", Variant(sections["docs/dl"]));
    }

    // ── The per-collection decisions (manifests, themes) ───────────────────────

    [Fact]
    public async Task Manifests_SameIdentifierFromTwoLayouts_AreRefusedNotSplit()
    {
        // A manifest's identifier is the tenant key, so a second layout reusing it (a copy that kept
        // the identifier but changed layoutId) is a conflict: refused, not stored as a second tenant.
        WriteSeed("dirt-life", "manifests", "layout-manifest.json", Seed("dirt-life", "dirt-life", "original", "layout-manifest"));
        WriteSeed("cream-pi", "manifests", "layout-manifest.json", Seed("dirt-life", "cream-pi", "copy", "layout-manifest"));
        DocumentSyncService sync = CreateSync();

        await sync.SyncAllAsync(_layoutsPath);

        Assert.Equal(1, sync.DocumentCollisionCount);
        Assert.Empty(_store.DocumentsIn("manifests"));
    }

    [Fact]
    public async Task PlatformTheme_NeverCapturesALayoutOverrideWithTheSameIdentifier()
    {
        // Layout overrides were scoped by #16; the platform catalogue (no layoutId) still matched by
        // identifier alone, so a catalogue theme could replace a layout's same-named override.
        WriteSeed("dirt-life", "themes", "trail-dust.json", Seed("trail-dust", layoutId: null, "override", "theme-definition"));
        WritePlatformTheme("trail-dust.json", Seed("trail-dust", layoutId: null, "catalogue", "theme-definition"));
        DocumentSyncService sync = CreateSync();

        await sync.SyncAllAsync(_layoutsPath);
        await sync.SyncAllAsync(_layoutsPath);

        IReadOnlyDictionary<string, JsonObject> themes = _store.DocumentsIn("theme-definitions");
        Assert.Equal(2, themes.Count);
        Assert.Equal("override", Variant(Assert.Single(themes.Values, doc => LayoutIdOf(doc) == "dirt-life")));
        Assert.Equal("catalogue", Variant(Assert.Single(themes.Values, doc => LayoutIdOf(doc) == null)));
        Assert.Equal(0, _store.DuplicateEntityIdentifierCount);
    }

    // ── Issue #31: --clean and sections that declare a layoutId ────────────────

    [Fact]
    public async Task Clean_KeepsDocumentsThatDeclareALayoutId_AndRemovesOnlyRealOrphans()
    {
        WriteSeed("dirt-life", "sections", "hero.json", Seed("hero", "dirt-life", "hero"));
        WriteSeed("dirt-life", "sections", "shared-footer.json", Seed("shared-footer", layoutId: null, "footer"));
        _store.Seed("sections", "docs/stale", Seed("removed-section", "dirt-life", "stale"));

        SyncBatchResult batch = await CreateSync().SyncAllAsync(_layoutsPath, cleanOrphans: true);

        List<string> remaining = [.. _store.DocumentsIn("sections").Values.Select(doc => doc["identifier"]!.GetValue<string>()).Order()];
        List<string> expected = ["hero", "shared-footer"];
        Assert.Equal(expected, remaining);
        Assert.Equal(1, batch.OrphanDeletedCount);
    }
}
