using System.Text.Json.Nodes;
using LayoutSync.Configuration;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Exceptions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Unit tests for <see cref="SyncFailureGate"/> — the pure helper that decides whether a
/// <c>--sync-once</c> run with failed documents fails the process, and what it prints (issue #36).
///
/// The contract:
/// <list type="bullet">
///   <item><description>Any failed document → exit 5, with or without <c>--strict</c>.</description></item>
///   <item><description>5 wins over <c>--strict</c>'s 2, because downstream CI reads 2 as "every write succeeded".</description></item>
///   <item><description>No failures → the strict decision stands unchanged (0 or 2).</description></item>
///   <item><description>The error line counts and names only the failed documents, capped so an outage that fails every document still prints one readable line.</description></item>
/// </list>
///
/// The end-to-end tests drive the real <see cref="DocumentSyncService.SyncAllAsync"/> (what the CLI
/// runs) against <see cref="InMemoryRavenDbService"/>, so they also pin which outcomes the sync
/// records as failures in the first place.
/// </summary>
public class SyncFailureGateTests : IDisposable
{
    private readonly string _root;
    private readonly string _layoutsPath;

    public SyncFailureGateTests()
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

    private static SyncDocument Doc(string relativePath) => new()
    {
        RelativePath = relativePath,
        FilePath = "/work/layouts/" + relativePath,
    };

    private static DocumentSyncService CreateSync(RavenDbService store) => new(
        NullLogger<DocumentSyncService>.Instance,
        new LocalFileService(NullLogger<LocalFileService>.Instance),
        store,
        new RelativeDateResolver(NullLogger<RelativeDateResolver>.Instance),
        [],
        new SyncOptions(),
        new CommandLineArgs());

    private static JsonObject Section(string identifier, string variant) => new()
    {
        ["identifier"] = identifier,
        ["type"] = "ui-schema-section",
        ["active"] = true,
        ["data"] = new JsonObject { ["props"] = new JsonObject { ["variant"] = variant } },
    };

    private string WriteFile(string layout, string fileName, string text)
    {
        string directory = Path.Combine(_layoutsPath, layout, "sections");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>
    /// The store as it behaves when another writer changed a document after the sync compared it:
    /// the change-vector-guarded replace is refused (issue #24), and <see cref="RavenDbService"/>
    /// rethrows the <see cref="ConcurrencyException"/> rather than retrying it.
    /// </summary>
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

    // ── Exit code ────────────────────────────────────────────────────────────

    [Fact]
    public void ExitCode_IsFive()
    {
        // Published contract: w31rd's layout-sync workflows document 0 success, 1 config/IO error,
        // 2 --strict offense, 3 remote-target refusal and 4 worktree mismatch. A failed sync must
        // not be mistaken for any of them.
        Assert.Equal(5, SyncFailureGate.ExitCode);
    }

    [Theory]
    [InlineData(0)] // no --strict, or --strict with nothing flagged
    [InlineData(2)] // --strict offenses
    public void ExitCodeFor_NoFailures_KeepsTheStrictDecision(int strictExitCode)
    {
        Assert.Equal(strictExitCode, SyncFailureGate.ExitCodeFor(failedCount: 0, strictExitCode));
    }

    [Theory]
    [InlineData(1, 0)]   // a failure without --strict still fails the run
    [InlineData(1, 2)]   // a failure AND --strict offenses: 5 wins, since 2 would claim every write succeeded
    [InlineData(250, 2)] // an outage that failed every document
    public void ExitCodeFor_AnyFailure_ReturnsFive_EvenOverStrictOffenses(int failedCount, int strictExitCode)
    {
        Assert.Equal(SyncFailureGate.ExitCode, SyncFailureGate.ExitCodeFor(failedCount, strictExitCode));
    }

    // ── Error line ───────────────────────────────────────────────────────────

    [Fact]
    public void Describe_NoFailures_ReturnsNull()
    {
        SyncBatchResult batch = new();
        batch.Results.Add(SyncResult.Succeeded(Doc("dirt-life/sections/home.json"), SyncAction.Created));
        batch.Results.Add(SyncResult.Skipped(Doc("dirt-life/menus/main.json"), "Dry run: Would UPDATE"));

        Assert.Null(SyncFailureGate.Describe(batch));
    }

    [Fact]
    public void Describe_CountsAndNamesOnlyTheFailedDocuments()
    {
        SyncBatchResult batch = new();
        batch.Results.Add(SyncResult.Succeeded(Doc("dirt-life/sections/home.json"), SyncAction.Created));
        batch.Results.Add(SyncResult.Failed(Doc("dirt-life/sections/hero.json"), SyncAction.Created, "Database unavailable"));
        batch.Results.Add(SyncResult.Failed(Doc("cream-pi/menus/main.json"), SyncAction.Replaced, "Document changed after it was compared"));

        string? line = SyncFailureGate.Describe(batch);

        Assert.Equal(
            "2 document(s) failed to sync, so the run exits 5: dirt-life/sections/hero.json, cream-pi/menus/main.json. See the errors above for each cause.",
            line);
    }

    [Fact]
    public void Describe_UnreadableFile_IsNamedByItsAbsolutePath()
    {
        // The sync records a file it could not read with only its FilePath: nothing was parsed,
        // so there is no RelativePath or identifier. The line must still name the file.
        SyncBatchResult batch = new();
        batch.Results.Add(SyncResult.Failed(
            new SyncDocument { FilePath = "/work/layouts/dirt-life/sections/broken.json" },
            SyncAction.Skipped,
            "Failed to read file"));

        Assert.Contains(": /work/layouts/dirt-life/sections/broken.json.", SyncFailureGate.Describe(batch));
    }

    [Fact]
    public void Describe_FailureWithoutAPath_IsNamedByItsIdentifier()
    {
        // A failed delete carries only the identifier (DeleteTrackedDocumentAsync).
        SyncBatchResult batch = new();
        batch.Results.Add(SyncResult.Failed(
            new SyncDocument { Identifier = "home-hero", DocumentType = DocumentType.Section },
            SyncAction.Deleted,
            "Delete operation returned false"));

        Assert.Contains(": home-hero.", SyncFailureGate.Describe(batch));
    }

    [Fact]
    public void Describe_ManyFailures_NamesTheFirstTenAndCountsTheRest()
    {
        SyncBatchResult batch = new();
        int total = SyncFailureGate.MaxNamedFailures + 2;
        for (int i = 1; i <= total; i++)
        {
            batch.Results.Add(SyncResult.Failed(Doc($"dirt-life/sections/s{i:00}.json"), SyncAction.Created, "Database unavailable"));
        }

        string? line = SyncFailureGate.Describe(batch);

        Assert.StartsWith($"{total} document(s) failed to sync", line);
        Assert.Contains($"s{SyncFailureGate.MaxNamedFailures:00}.json and 2 more.", line);
        Assert.DoesNotContain($"s{SyncFailureGate.MaxNamedFailures + 1:00}.json", line);
    }

    // ── End to end: what the sync records as failed ──────────────────────────

    [Fact]
    public async Task SyncAllAsync_BrokenJsonInADryRun_FailsTheRunWithFive()
    {
        // The PR dry-run check depends on this: broken JSON must come back FAILED, not skipped,
        // or it never reaches FailedCount and the run still exits 0.
        WriteFile("dirt-life", "hero.json", Section("hero", "file").ToJsonString());
        string broken = WriteFile("dirt-life", "broken.json", "{ \"identifier\": \"broken\", ");
        using InMemoryRavenDbService store = new();

        SyncBatchResult batch = await CreateSync(store).SyncAllAsync(_layoutsPath, dryRun: true);

        Assert.Equal(1, batch.FailedCount);
        Assert.Equal(SyncFailureGate.ExitCode, SyncFailureGate.ExitCodeFor(batch.FailedCount, strictExitCode: 0));
        Assert.Contains(broken, SyncFailureGate.Describe(batch));
    }

    [Fact]
    public async Task SyncAllAsync_ReplaceRefusedAfterAConcurrentEdit_FailsTheRunWithFive()
    {
        // The stored document differs from the file, so the sync replaces it in place, and the
        // replace is refused because another writer changed the document after the comparison.
        // Before issue #36 the run still exited 0.
        WriteFile("dirt-life", "hero.json", Section("hero", "file").ToJsonString());
        using ConcurrentEditStore store = new();
        store.Seed(DocumentType.Section.GetCollection(), "doc-hero", Section("hero", "stored"));

        SyncBatchResult batch = await CreateSync(store).SyncAllAsync(_layoutsPath);

        SyncResult refused = Assert.Single(batch.Results, r => !r.Success);
        Assert.Equal(SyncAction.Replaced, refused.Action);
        Assert.IsType<ConcurrencyException>(refused.Exception);
        Assert.Equal(SyncFailureGate.ExitCode, SyncFailureGate.ExitCodeFor(batch.FailedCount, strictExitCode: 2));
        Assert.Contains("dirt-life/sections/hero.json", SyncFailureGate.Describe(batch));
    }

    [Fact]
    public async Task SyncAllAsync_HealthyRuns_RecordNoFailures()
    {
        // No false positives: a create, then an unchanged re-sync, then a dry run all leave the
        // strict decision alone. A false positive here would turn a healthy CI run red.
        WriteFile("dirt-life", "hero.json", Section("hero", "file").ToJsonString());
        using InMemoryRavenDbService store = new();
        DocumentSyncService sync = CreateSync(store);

        SyncBatchResult created = await sync.SyncAllAsync(_layoutsPath);
        SyncBatchResult unchanged = await sync.SyncAllAsync(_layoutsPath);
        SyncBatchResult dryRun = await sync.SyncAllAsync(_layoutsPath, dryRun: true);

        Assert.Equal(SyncAction.Created, Assert.Single(created.Results).Action);
        Assert.Equal(SyncAction.Unchanged, Assert.Single(unchanged.Results).Action);
        SyncBatchResult[] batches = [created, unchanged, dryRun];
        Assert.All(batches, batch =>
        {
            Assert.Equal(0, batch.FailedCount);
            Assert.Null(SyncFailureGate.Describe(batch));
        });
    }
}
