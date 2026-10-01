using System.Text.Json.Nodes;
using LayoutSync.Configuration;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// End-to-end tests for issue #46: real seed files in a scratch layouts tree, synced by the real
/// <see cref="DocumentSyncService"/> against <see cref="InMemoryRavenDbService"/>.
///
/// The live incident (coreConvention/w31rd#2990): a section that already existed in the database
/// had <c>@metadata.@id</c> added to its file so a modal could reference it by that id. The sync
/// found the stored document by identifier and kept updating it under its old id, so no document
/// ever had the pinned id, the modal rendered blank, and no log line said why.
///
/// The contract: a pin that is not the stored document's id is reported once per file and fails
/// <c>--strict</c>. The document is NOT re-keyed.
/// </summary>
public sealed class PinnedIdMismatchTests : IDisposable
{
    private const string PinnedId = "editProfileFormSec0001";
    private const string StoredId = "Vq3xT8mKp2LrN9wZ5yHcB";
    private const string SectionPath = "dirt-life/sections/edit-profile-form.json";

    private readonly string _root;
    private readonly string _layoutsPath;
    private readonly InMemoryRavenDbService _store = new();
    private readonly CapturingLogger _log = new();

    public PinnedIdMismatchTests()
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

    /// <summary><c>--preserve-ids</c> is on, as in every w31rd sync: it is what makes a pin apply at all.</summary>
    private DocumentSyncService CreateSync() => new(
        _log,
        new LocalFileService(NullLogger<LocalFileService>.Instance),
        _store,
        new RelativeDateResolver(NullLogger<RelativeDateResolver>.Instance),
        [],
        new SyncOptions(),
        new CommandLineArgs { PreserveIds = true });

    private string WriteSeed(string folder, string fileName, JsonObject content)
    {
        string directory = Path.Combine(_layoutsPath, "dirt-life", folder);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content.ToJsonString());
        return path;
    }

    private string WriteSection(JsonObject content) => WriteSeed("sections", "edit-profile-form.json", content);

    /// <summary>The section in the shape w31rd ships; <paramref name="pinnedId"/> adds the <c>@metadata.@id</c> pin.</summary>
    private static JsonObject Section(string variant, string? pinnedId = null)
    {
        JsonObject section = new()
        {
            ["layoutId"] = "dirt-life",
            ["identifier"] = "edit-profile-form",
            ["type"] = "ui-schema-section",
            ["active"] = true,
        };
        if (pinnedId != null)
            section["@metadata"] = new JsonObject { ["@id"] = pinnedId, ["@collection"] = "sections" };
        section["data"] = new JsonObject { ["props"] = new JsonObject { ["variant"] = variant } };
        return section;
    }

    private static string Variant(JsonObject document) => document["data"]!["props"]!["variant"]!.GetValue<string>();

    private List<string> MismatchWarnings => [.. _log.Warnings.Where(warning => warning.StartsWith("Pinned id mismatch:"))];

    private IReadOnlyList<string> StrictOffenses(DocumentSyncService sync) => StrictModeGate.Offenses(
        _store.DuplicateEntityIdentifierCount, sync.DocumentCollisionCount, sync.PinnedIdMismatchCount, validators: []);

    // ── The behaviors issue #46 asks for ───────────────────────────────────────

    [Fact]
    public async Task PinDifferentFromTheStoredId_WarnsOnce_AndFailsStrict()
    {
        _store.Seed("sections", StoredId, Section("old"));
        WriteSection(Section("new", PinnedId));
        DocumentSyncService sync = CreateSync();

        await sync.SyncAllAsync(_layoutsPath);

        string warning = Assert.Single(MismatchWarnings);
        Assert.Contains(SectionPath, warning);
        Assert.Contains($"'{PinnedId}'", warning);
        Assert.Contains($"'{StoredId}'", warning);
        Assert.Contains("only applies when the document is created", warning);
        Assert.Equal(1, sync.PinnedIdMismatchCount);

        IReadOnlyList<string> offenses = StrictOffenses(sync);
        Assert.Contains(offenses, offense => offense.Contains("pinned id mismatch"));
        Assert.Equal(StrictModeGate.ExitCode, StrictModeGate.ExitCodeFor(offenses));

        // Detect and warn only: the document is still updated in place under the id it had.
        (string documentId, JsonObject document) = Assert.Single(_store.DocumentsIn("sections"));
        Assert.Equal(StoredId, documentId);
        Assert.Equal("new", Variant(document));
    }

    [Fact]
    public async Task PinEqualToTheStoredId_IsNotReported()
    {
        _store.Seed("sections", PinnedId, Section("old"));
        WriteSection(Section("new", PinnedId));
        DocumentSyncService sync = CreateSync();

        await sync.SyncAllAsync(_layoutsPath);

        Assert.Empty(MismatchWarnings);
        Assert.Equal(0, sync.PinnedIdMismatchCount);
        Assert.Empty(StrictOffenses(sync));
        Assert.Equal("new", Variant(_store.DocumentsIn("sections")[PinnedId]));
    }

    [Fact]
    public async Task FileWithoutAPin_IsNotReported()
    {
        _store.Seed("sections", StoredId, Section("old"));
        WriteSection(Section("new"));
        DocumentSyncService sync = CreateSync();

        await sync.SyncAllAsync(_layoutsPath);

        Assert.Empty(MismatchWarnings);
        Assert.Equal(0, sync.PinnedIdMismatchCount);
        Assert.Empty(StrictOffenses(sync));
    }

    [Fact]
    public async Task DryRun_ReportsTheMismatch_WithoutWriting()
    {
        // w31rd's PR gate is a --strict --dry-run: it has to fail on the pull request that adds the pin.
        _store.Seed("sections", StoredId, Section("old"));
        WriteSection(Section("new", PinnedId));
        DocumentSyncService sync = CreateSync();

        await sync.SyncAllAsync(_layoutsPath, dryRun: true);

        Assert.Contains(SectionPath, Assert.Single(MismatchWarnings));
        Assert.Equal(1, sync.PinnedIdMismatchCount);
        Assert.Equal(StrictModeGate.ExitCode, StrictModeGate.ExitCodeFor(StrictOffenses(sync)));
        Assert.Equal("old", Variant(Assert.Single(_store.DocumentsIn("sections")).Value));
    }

    // ── Where the check sits in the sync path ──────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpToDateDocument_IsStillReported(bool dryRun)
    {
        // The steady state of the w31rd incident: nothing but the pin differs, so every sync said
        // "No changes" (@metadata is not compared) and returned before any write.
        _store.Seed("sections", StoredId, Section("same"));
        WriteSection(Section("same", PinnedId));
        DocumentSyncService sync = CreateSync();

        SyncBatchResult batch = await sync.SyncAllAsync(_layoutsPath, dryRun: dryRun);

        Assert.Equal(1, batch.UnchangedCount);
        Assert.Single(MismatchWarnings);
        Assert.Equal(1, sync.PinnedIdMismatchCount);
    }

    [Fact]
    public async Task SingleFileSync_ReportsTheMismatch()
    {
        // Watch mode syncs one file at a time, outside any batch.
        _store.Seed("sections", StoredId, Section("old"));
        string path = WriteSection(Section("new", PinnedId));
        DocumentSyncService sync = CreateSync();

        await sync.SyncFileAsync(path, _layoutsPath);

        Assert.Contains(SectionPath, Assert.Single(MismatchWarnings));
        Assert.Equal(1, sync.PinnedIdMismatchCount);
    }

    [Fact]
    public async Task Count_ResetsForEachBatch()
    {
        _store.Seed("sections", StoredId, Section("old"));
        WriteSection(Section("new", PinnedId));
        DocumentSyncService sync = CreateSync();
        await sync.SyncAllAsync(_layoutsPath);
        Assert.Equal(1, sync.PinnedIdMismatchCount);

        // One of the two fixes the warning names: point the pin at the stored id.
        WriteSection(Section("new", StoredId));
        await sync.SyncAllAsync(_layoutsPath);

        Assert.Equal(0, sync.PinnedIdMismatchCount);
        Assert.Single(MismatchWarnings);
    }

    // ── What is not a mismatch ─────────────────────────────────────────────────

    [Fact]
    public async Task NewDocument_IsCreatedUnderItsPin_AndNeverReported()
    {
        WriteSection(Section("new", PinnedId));
        DocumentSyncService sync = CreateSync();

        await sync.SyncAllAsync(_layoutsPath);
        await sync.SyncAllAsync(_layoutsPath);

        Assert.Equal(PinnedId, Assert.Single(_store.DocumentsIn("sections")).Key);
        Assert.Empty(MismatchWarnings);
        Assert.Equal(0, sync.PinnedIdMismatchCount);
    }

    [Fact]
    public async Task PinDifferingOnlyInCase_IsNotReported()
    {
        // RavenDB document ids are case-insensitive: both spellings name the same document.
        _store.Seed("sections", PinnedId.ToUpperInvariant(), Section("old"));
        WriteSection(Section("new", PinnedId));
        DocumentSyncService sync = CreateSync();

        await sync.SyncAllAsync(_layoutsPath);

        Assert.Empty(MismatchWarnings);
        Assert.Equal(0, sync.PinnedIdMismatchCount);
    }

    [Fact]
    public async Task TopLevelId_IsNotAPin()
    {
        // A top-level "id" is a content field; only @metadata.@id names the document id.
        JsonObject section = Section("new");
        section["id"] = PinnedId;
        _store.Seed("sections", StoredId, Section("old"));
        WriteSection(section);
        DocumentSyncService sync = CreateSync();

        await sync.SyncAllAsync(_layoutsPath);

        Assert.Empty(MismatchWarnings);
        Assert.Equal(0, sync.PinnedIdMismatchCount);
    }

    [Fact]
    public async Task Identity_IsNeverReported()
    {
        // Identities are looked up BY their @metadata.@id, so the stored id cannot differ from it.
        const string identityId = "seedIdentityPinned001";
        JsonObject Identity(string displayName) => new()
        {
            ["@metadata"] = new JsonObject { ["@id"] = identityId, ["@collection"] = "identities" },
            ["type"] = "identity",
            ["data"] = new JsonObject { ["displayName"] = displayName },
        };
        _store.Seed("identities", identityId, Identity("old"));
        WriteSeed("identities", "seed-identity.json", Identity("new"));
        DocumentSyncService sync = CreateSync();

        await sync.SyncAllAsync(_layoutsPath);

        (string documentId, JsonObject document) = Assert.Single(_store.DocumentsIn("identities"));
        Assert.Equal(identityId, documentId);
        Assert.Equal("new", document["data"]!["displayName"]!.GetValue<string>());
        Assert.Empty(MismatchWarnings);
        Assert.Equal(0, sync.PinnedIdMismatchCount);
    }

    // ── Test plumbing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="ILogger{T}"/> that captures formatted warning messages so tests can assert
    /// on count and text. Mirrors the harness used by <c>SeedAuthorshipValidatorTests</c>.
    /// </summary>
    private sealed class CapturingLogger : ILogger<DocumentSyncService>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
