using System.Text.Json.Nodes;
using LayoutSync.Configuration;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// End-to-end tests for issue #37: an orphan that a real <c>--clean</c> fails to delete.
///
/// <see cref="RavenDbService.DeleteDocumentAsync"/> catches every exception, logs it, and returns
/// <c>false</c>. The orphan pass handled only <c>true</c>, so a failed delete added nothing to the
/// batch: it was missing from <see cref="SyncBatchResult.FailedCount"/>, and a
/// <c>--clean --sync-once</c> whose deletes all failed still exited 0.
///
/// The contract: a failed delete is a failed result, so <see cref="SyncFailureGate"/> exits 5; it is
/// not counted as deleted; the summary reports it; and the orphan is still stored. Only a real
/// <c>--clean</c> attempts a delete, so no other run can fail this way.
/// </summary>
public sealed class OrphanDeleteFailureTests : IDisposable
{
    private const string OrphanId = "docs/stale";
    private const string OrphanIdentifier = "removed-section";

    private static readonly string Sections = DocumentType.Section.GetCollection();

    private readonly string _root;
    private readonly string _layoutsPath;
    private readonly CapturingLogger _log = new();

    public OrphanDeleteFailureTests()
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

    private static JsonObject Section(string identifier) => new()
    {
        ["layoutId"] = "dirt-life",
        ["identifier"] = identifier,
        ["type"] = "ui-schema-section",
        ["active"] = true,
        ["data"] = new JsonObject { ["props"] = new JsonObject { ["variant"] = identifier } },
    };

    /// <summary>Syncs one local section beside one stored section that has no file: a single orphan.</summary>
    private Task<SyncBatchResult> SyncWithOneOrphanAsync(InMemoryRavenDbService store, bool dryRun, bool clean)
    {
        string directory = Path.Combine(_layoutsPath, "dirt-life", "sections");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "hero.json"), Section("hero").ToJsonString());
        store.Seed(Sections, OrphanId, Section(OrphanIdentifier));

        return CreateSync(store).SyncAllAsync(_layoutsPath, dryRun: dryRun, cleanOrphans: clean);
    }

    /// <summary>
    /// The store as it behaves when a delete fails: <see cref="RavenDbService.DeleteDocumentAsync"/>
    /// has already logged the exception and returns <c>false</c>. Fails only the listed ids, or every
    /// delete when none are listed, and counts every delete it is asked for.
    /// </summary>
    private sealed class FailingDeleteStore(params string[] failingIds) : InMemoryRavenDbService
    {
        public int DeleteCalls { get; private set; }

        public override Task<bool> DeleteDocumentAsync(string documentId, CancellationToken ct = default)
        {
            DeleteCalls++;
            return failingIds.Length == 0 || failingIds.Contains(documentId)
                ? Task.FromResult(false)
                : base.DeleteDocumentAsync(documentId, ct);
        }
    }

    // ── A failed delete ──────────────────────────────────────────────────────

    [Fact]
    public async Task CleanWhoseDeleteFails_RecordsAFailure_AndTheRunExitsFive()
    {
        using FailingDeleteStore store = new();

        SyncBatchResult batch = await SyncWithOneOrphanAsync(store, dryRun: false, clean: true);

        Assert.Equal(1, store.DeleteCalls);
        SyncResult failed = Assert.Single(batch.Results, r => !r.Success);
        Assert.Equal(SyncAction.OrphanDeleted, failed.Action);
        Assert.Equal(OrphanIdentifier, failed.Document.Identifier);
        Assert.Equal(DocumentType.Section, failed.Document.DocumentType);
        Assert.Equal("Delete operation returned false", failed.ErrorMessage);
        Assert.Equal(1, batch.FailedCount);
        Assert.Equal(0, batch.OrphanDeletedCount);
        Assert.Equal(1, batch.OrphanDeleteFailedCount);
        Assert.Equal(SyncFailureGate.ExitCode, SyncFailureGate.ExitCodeFor(batch.FailedCount, strictExitCode: 0));
        Assert.Contains($": {OrphanIdentifier}.", SyncFailureGate.Describe(batch));
        Assert.True(store.DocumentsIn(Sections).ContainsKey(OrphanId));
    }

    [Fact]
    public async Task CleanWhoseDeleteFails_LogsTheOrphan_AndSummarizesTheFailure()
    {
        using FailingDeleteStore store = new();

        await SyncWithOneOrphanAsync(store, dryRun: false, clean: true);

        (LogLevel level, string message) = Assert.Single(_log.Entries, entry => entry.Message.Contains($"({OrphanId})"));
        Assert.Equal(LogLevel.Error, level);
        Assert.Equal($"Failed to delete orphan: {OrphanIdentifier} ({OrphanId}) from {Sections}", message);

        (LogLevel summaryLevel, string _) = Assert.Single(
            _log.Entries, entry => entry.Message.StartsWith("1 orphan(s) could not be deleted and are still in the database"));
        Assert.Equal(LogLevel.Warning, summaryLevel);

        // Neither a delete that did not happen nor advice to pass the --clean that was passed.
        Assert.DoesNotContain(_log.Entries, entry => entry.Message.Contains("orphan(s) deleted"));
        Assert.DoesNotContain(_log.Entries, entry => entry.Message.Contains("Use --clean"));
    }

    [Fact]
    public async Task CleanWithOneFailedAndOneSuccessfulDelete_CountsAndSummarizesEachSeparately()
    {
        const string DeletableId = "docs/also-stale";
        using FailingDeleteStore store = new(OrphanId);
        store.Seed(Sections, DeletableId, Section("another-removed-section"));

        SyncBatchResult batch = await SyncWithOneOrphanAsync(store, dryRun: false, clean: true);

        Assert.Equal(1, batch.OrphanDeletedCount);
        Assert.Equal(1, batch.OrphanDeleteFailedCount);
        Assert.Equal(1, batch.FailedCount);
        Assert.Equal(SyncFailureGate.ExitCode, SyncFailureGate.ExitCodeFor(batch.FailedCount, strictExitCode: 0));
        Assert.True(store.DocumentsIn(Sections).ContainsKey(OrphanId));
        Assert.False(store.DocumentsIn(Sections).ContainsKey(DeletableId));
        Assert.Single(_log.Entries, entry => entry.Message == "1 orphan(s) deleted");
        Assert.Single(_log.Entries, entry => entry.Message.StartsWith("1 orphan(s) could not be deleted"));
    }

    // ── No false positives ───────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false)] // no --clean: orphans are reported, never deleted
    [InlineData(true, false)]  // --dry-run
    [InlineData(true, true)]   // --dry-run --clean: previews the delete, never attempts it
    public async Task RunsThatDoNotDelete_NeverRecordADeleteFailure(bool dryRun, bool clean)
    {
        // A store whose every delete fails turns any attempted delete into a failure, so this pins
        // that only a real --clean attempts one. w31rd CI does not pass --clean; a false positive
        // here would turn its runs red.
        using FailingDeleteStore store = new();

        SyncBatchResult batch = await SyncWithOneOrphanAsync(store, dryRun, clean);

        // The orphan is found, but no delete is attempted and no delete result is recorded.
        Assert.Equal(OrphanId, Assert.Single(batch.OrphansDetected[Sections]).Key);
        Assert.Equal(0, store.DeleteCalls);
        Assert.DoesNotContain(batch.Results, r => r.Action == SyncAction.OrphanDeleted);
        Assert.Equal(0, batch.FailedCount);
        Assert.Equal(0, batch.OrphanDeleteFailedCount);
        Assert.Equal(0, SyncFailureGate.ExitCodeFor(batch.FailedCount, strictExitCode: 0));
        Assert.True(store.DocumentsIn(Sections).ContainsKey(OrphanId));
    }

    [Fact]
    public async Task CleanWhoseDeleteSucceeds_IsUnchanged()
    {
        using InMemoryRavenDbService store = new();

        SyncBatchResult batch = await SyncWithOneOrphanAsync(store, dryRun: false, clean: true);

        SyncResult deleted = Assert.Single(batch.Results, r => r.Action == SyncAction.OrphanDeleted);
        Assert.True(deleted.Success);
        Assert.Equal(OrphanId, deleted.RavenDocumentId);
        Assert.Equal(1, batch.OrphanDeletedCount);
        Assert.Equal(0, batch.OrphanDeleteFailedCount);
        Assert.Equal(0, batch.FailedCount);
        Assert.False(store.DocumentsIn(Sections).ContainsKey(OrphanId));
        Assert.Single(_log.Entries, entry => entry.Message == "1 orphan(s) deleted");
        Assert.DoesNotContain(_log.Entries, entry => entry.Message.Contains("could not be deleted"));
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
