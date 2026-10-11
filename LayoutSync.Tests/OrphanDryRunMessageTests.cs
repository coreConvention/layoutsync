using System.Text.Json.Nodes;
using LayoutSync.Configuration;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// End-to-end tests for issue #50: what a sync says about an orphan, and whether it deletes it,
/// for each combination of <c>--dry-run</c> and <c>--clean</c>.
///
/// The incident: a <c>--dry-run</c> without <c>--clean</c> printed <c>Would delete orphan</c> for
/// two documents that the same command without <c>--dry-run</c> left in place. A dry-run is read to
/// decide whether the real run is safe, so it must describe what that run does: only
/// <c>--clean</c> deletes, and only a dry-run with <c>--clean</c> may say it would.
/// </summary>
public sealed class OrphanDryRunMessageTests : IDisposable
{
    private const string OrphanId = "docs/stale";

    private readonly string _root;
    private readonly string _layoutsPath;
    private readonly InMemoryRavenDbService _store = new();
    private readonly CapturingLogger _log = new();

    public OrphanDryRunMessageTests()
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

    private DocumentSyncService CreateSync() => new(
        _log,
        new LocalFileService(NullLogger<LocalFileService>.Instance),
        _store,
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

    [Theory]
    [InlineData(false, false, LogLevel.Debug, "Orphan detected (not deleted without --clean):", false)]
    [InlineData(true, false, LogLevel.Information, "Orphan detected (not deleted without --clean):", false)]
    [InlineData(true, true, LogLevel.Warning, "Would delete orphan:", false)]
    [InlineData(false, true, LogLevel.Warning, "Deleted orphan:", true)]
    public async Task Orphan_IsReportedAsTheRealRunHandlesIt_AndDeletedOnlyByARealClean(
        bool dryRun, bool clean, LogLevel expectedLevel, string expectedMessage, bool expectDeleted)
    {
        string directory = Path.Combine(_layoutsPath, "dirt-life", "sections");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "hero.json"), Section("hero").ToJsonString());
        _store.Seed("sections", OrphanId, Section("removed-section"));

        SyncBatchResult batch = await CreateSync().SyncAllAsync(_layoutsPath, dryRun: dryRun, cleanOrphans: clean);

        (LogLevel level, string message) = Assert.Single(_log.Entries, entry => entry.Message.Contains($"({OrphanId})"));
        Assert.Equal(expectedLevel, level);
        Assert.StartsWith(expectedMessage, message);
        Assert.Equal(expectDeleted, !_store.DocumentsIn("sections").ContainsKey(OrphanId));
        Assert.Equal(expectDeleted ? 1 : 0, batch.OrphanDeletedCount);
        Assert.Equal(OrphanId, Assert.Single(batch.OrphansDetected["sections"]).Key);
    }

    /// <summary>Captures every log line with its level, so a test can tell Debug from Information.</summary>
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
