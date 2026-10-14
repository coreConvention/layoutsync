using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LayoutSync.Configuration;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Exceptions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// End-to-end tests for issue #54: the <c>[check]</c> line that ends every batch sync, driven through
/// the real <see cref="DocumentSyncService.SyncAllAsync"/> against <see cref="InMemoryRavenDbService"/>.
///
/// Each per-type total used to count every result in the batch, so failed writes, unreadable files,
/// refused collisions, deleted orphans and dry-run previews were all reported as synced, and an
/// unreadable file, which has no type, as an entity.
///
/// The contract: the totals count only documents that were written or already matched the database.
/// Everything else has its own line below the summary. A dry run counts the writes it would make, and
/// says on the next line that nothing was written. The <c>[check]</c> line itself keeps its wording,
/// because w31rd's drift check (<c>scripts/check-layout-drift.mjs</c>) parses it from a dry run's log.
/// </summary>
public sealed class BatchSummaryTests : IDisposable
{
    private const string Layout = "dirt-life";

    private static readonly string Sections = DocumentType.Section.GetCollection();
    private static readonly string Menus = DocumentType.Menu.GetCollection();

    private readonly string _root;
    private readonly string _layoutsPath;
    private readonly CapturingLogger _log = new();

    public BatchSummaryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "layoutsync-tests-" + Guid.NewGuid().ToString("N"));
        _layoutsPath = Path.Combine(_root, "layouts");
        Directory.CreateDirectory(_layoutsPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private DocumentSyncService CreateSync(RavenDbService store) => new(
        _log,
        new LocalFileService(NullLogger<LocalFileService>.Instance),
        store,
        new RelativeDateResolver(NullLogger<RelativeDateResolver>.Instance),
        [],
        new SyncOptions(),
        new CommandLineArgs());

    private static JsonObject Section(string identifier, string variant = "default") => new()
    {
        ["layoutId"] = Layout,
        ["identifier"] = identifier,
        ["type"] = "ui-schema-section",
        ["active"] = true,
        ["data"] = new JsonObject { ["props"] = new JsonObject { ["variant"] = variant } },
    };

    private static JsonObject Menu(string identifier) => new()
    {
        ["layoutId"] = Layout,
        ["identifier"] = identifier,
        ["items"] = new JsonArray(),
    };

    private void WriteFile(string folder, string fileName, string text)
    {
        string directory = Path.Combine(_layoutsPath, Layout, folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), text);
    }

    private void WriteSection(JsonObject content, string? fileName = null) =>
        WriteFile("sections", fileName ?? content["identifier"]!.GetValue<string>() + ".json", content.ToJsonString());

    private const string DryRunLabel = "Dry run: nothing was written. The totals above are what a real run would sync.";

    /// <summary>The batch summary line as logged. There is one per batch, at Information.</summary>
    private string RawSummaryLine
    {
        get
        {
            (LogLevel level, string message) = Assert.Single(_log.Entries, entry => entry.Message.StartsWith("[check] "));
            Assert.Equal(LogLevel.Information, level);
            return message;
        }
    }

    /// <summary>The batch summary line with its duration replaced by <c>&lt;t&gt;</c>, so it can be compared exactly.</summary>
    private string SummaryLine => Regex.Replace(RawSummaryLine, @" in \d+[.,]\ds ", " in <t>s ");

    /// <summary>The line logged right after the summary line.</summary>
    private (LogLevel Level, string Message) LineAfterSummary =>
        _log.Entries[_log.Entries.FindIndex(entry => entry.Message.StartsWith("[check] ")) + 1];

    /// <summary>The store when every replace is refused because the document changed after it was compared (issue #36).</summary>
    private sealed class ConcurrentEditStore : InMemoryRavenDbService
    {
        public override Task<string?> ReplaceDocumentAsync(
            string documentId,
            string? expectedChangeVector,
            SyncDocument doc,
            JsonObject newContent,
            CancellationToken ct = default) =>
            Task.FromException<string?>(
                new ConcurrencyException($"Document {documentId} changed after it was compared."));
    }

    /// <summary>The store when every delete fails: <see cref="RavenDbService.DeleteDocumentAsync"/> has logged why and returns <c>false</c> (issue #37).</summary>
    private sealed class FailingDeleteStore : InMemoryRavenDbService
    {
        public override Task<bool> DeleteDocumentAsync(string documentId, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    // ── A healthy run ────────────────────────────────────────────────────────

    [Fact]
    public async Task HealthyRun_CountsCreatedAndUnchangedDocumentsByType()
    {
        using InMemoryRavenDbService store = new();
        store.Seed(Sections, "sections/footer", Section("footer"));
        WriteSection(Section("footer"));
        WriteSection(Section("hero"));
        WriteFile("menus", "main.json", Menu("main").ToJsonString());

        SyncBatchResult batch = await CreateSync(store).SyncAllAsync(_layoutsPath);

        Assert.Equal(0, batch.FailedCount);
        Assert.Equal("[check] 2 sections, 1 menus synced in <t>s (1 unchanged)", SummaryLine);
        Assert.DoesNotContain(_log.Entries, entry => entry.Message == DryRunLabel);
    }

    // ── What is not synced ───────────────────────────────────────────────────

    [Fact]
    public async Task FailedWrite_IsNotCountedAsSynced()
    {
        using ConcurrentEditStore store = new();
        store.Seed(Sections, "sections/footer", Section("footer", "old"));
        WriteSection(Section("footer", "new"));
        WriteSection(Section("hero"));

        SyncBatchResult batch = await CreateSync(store).SyncAllAsync(_layoutsPath);

        Assert.Equal(1, batch.FailedCount);
        Assert.Equal("[check] 1 sections synced in <t>s (0 unchanged)", SummaryLine);
        Assert.Contains((LogLevel.Warning, "1 sync operations failed"), _log.Entries);
    }

    [Fact]
    public async Task UnreadableFile_IsNotCountedAsSynced_NorAsAnEntity()
    {
        using InMemoryRavenDbService store = new();
        WriteSection(Section("hero"));
        WriteFile("sections", "broken.json", "{ \"identifier\": ");

        SyncBatchResult batch = await CreateSync(store).SyncAllAsync(_layoutsPath);

        Assert.Equal(1, batch.FailedCount);
        Assert.Equal("[check] 1 sections synced in <t>s (0 unchanged)", SummaryLine);
        Assert.Contains((LogLevel.Warning, "1 sync operations failed"), _log.Entries);
    }

    [Fact]
    public async Task DeletedOrphan_IsNotCountedAsSynced()
    {
        using InMemoryRavenDbService store = new();
        store.Seed(Sections, "sections/stale", Section("removed-section"));
        WriteSection(Section("hero"));

        SyncBatchResult batch = await CreateSync(store).SyncAllAsync(_layoutsPath, cleanOrphans: true);

        Assert.Equal(1, batch.OrphanDeletedCount);
        Assert.Equal("[check] 1 sections synced in <t>s (0 unchanged)", SummaryLine);
        Assert.Contains((LogLevel.Warning, "1 orphan(s) deleted"), _log.Entries);
    }

    [Fact]
    public async Task FailedOrphanDelete_IsNotCountedAsSynced()
    {
        using FailingDeleteStore store = new();
        store.Seed(Sections, "sections/stale", Section("removed-section"));
        WriteSection(Section("hero"));

        SyncBatchResult batch = await CreateSync(store).SyncAllAsync(_layoutsPath, cleanOrphans: true);

        Assert.Equal(1, batch.OrphanDeleteFailedCount);
        Assert.Equal("[check] 1 sections synced in <t>s (0 unchanged)", SummaryLine);
        Assert.Contains(_log.Entries, entry => entry.Message.StartsWith("1 orphan(s) could not be deleted"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedCollision_IsNotCountedAsSynced(bool dryRun)
    {
        // Two files declare the same section with different content, so neither is written; a dry
        // run must not count them as writes it would make either, since the real run refuses them too.
        using InMemoryRavenDbService store = new();
        WriteSection(Section("hero", "one"), "hero.json");
        WriteSection(Section("hero", "two"), "hero-copy.json");
        WriteSection(Section("footer"));

        SyncBatchResult batch = await CreateSync(store).SyncAllAsync(_layoutsPath, dryRun: dryRun);

        Assert.Equal(0, batch.FailedCount);
        Assert.Equal("[check] 1 sections synced in <t>s (0 unchanged)", SummaryLine);
    }

    // ── A dry run ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DryRun_CountsTheWritesItWouldMake_AndSaysNothingWasWritten()
    {
        using InMemoryRavenDbService store = new();
        store.Seed(Sections, "sections/footer", Section("footer", "old"));
        store.Seed(Menus, "menus/main", Menu("main"));
        WriteSection(Section("footer", "new"));
        WriteSection(Section("hero"));
        WriteFile("menus", "main.json", Menu("main").ToJsonString());

        await CreateSync(store).SyncAllAsync(_layoutsPath, dryRun: true);

        Assert.Equal("[check] 2 sections, 1 menus synced in <t>s (1 unchanged)", SummaryLine);
        Assert.Equal((LogLevel.Information, DryRunLabel), LineAfterSummary);
        Assert.Equal("old", store.DocumentsIn(Sections)["sections/footer"]["data"]!["props"]!["variant"]!.GetValue<string>());
        Assert.Single(store.DocumentsIn(Sections));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SummaryLine_KeepsTheGrammarDownstreamCiParses(bool dryRun)
    {
        // w31rd's scripts/check-layout-drift.mjs accepts a dry run as fully compared only when this
        // line matches its grammar, anchored at both ends; anything appended to it fails that check.
        using InMemoryRavenDbService store = new();
        store.Seed(Sections, "sections/footer", Section("footer"));
        WriteSection(Section("footer"));

        await CreateSync(store).SyncAllAsync(_layoutsPath, dryRun: dryRun);

        Assert.Matches(@"^\[check\] (?:\d+ [a-z ]+(?:, \d+ [a-z ]+)*|0 documents) synced in \d+\.\ds \(\d+ unchanged\)$", RawSummaryLine);
    }

    // ── Test plumbing ────────────────────────────────────────────────────────

    /// <summary>Captures every log line with its level, so a test can tell an error from a warning.</summary>
    private sealed class CapturingLogger : ILogger<DocumentSyncService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
