using System.Text.Json.Nodes;
using LayoutSync.Configuration;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// End-to-end tests for issue #56: the line watch mode prints after it syncs a changed file. Driven
/// through the real <see cref="FileWatcherService.WatchAsync"/>, with a real file-system watcher over a
/// temp directory, and the real <see cref="DocumentSyncService"/> against <see cref="InMemoryRavenDbService"/>.
///
/// Under <c>--dry-run</c> the sync logs <c>Would CREATE</c> or <c>Would UPDATE</c> and returns a skipped
/// result, which the watcher used to confirm as <c>-&gt; Synced 'hero'</c>, as though it had been written.
/// The contract: a dry run says the file was not written, and a real run names what it did to the document.
///
/// Issue #59 is the same mislabel for a deleted file: a dry run announced <c>Deleting from DB</c>, then
/// logged <c>Would DELETE</c>. A dry run announces the deletion as one it would make; a real run is unchanged.
/// </summary>
public sealed class WatcherConfirmationTests : IDisposable
{
    private const string Layout = "dirt-life";
    private const string DryRunConfirmation = "-> Not written (dry run): 'hero'";

    private static readonly string Sections = DocumentType.Section.GetCollection();
    private static readonly TimeSpan LineTimeout = TimeSpan.FromSeconds(15);

    private readonly string _root;
    private readonly string _layoutsPath;
    private readonly string _sectionsPath;
    private readonly LogSink _log = new();

    public WatcherConfirmationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "layoutsync-tests-" + Guid.NewGuid().ToString("N"));
        _layoutsPath = Path.Combine(_root, "layouts");
        // Created before the watcher starts, so the only event a test raises is its file's write.
        _sectionsPath = Path.Combine(_layoutsPath, Layout, "sections");
        Directory.CreateDirectory(_sectionsPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static JsonObject Section(string variant) => new()
    {
        ["layoutId"] = Layout,
        ["identifier"] = "hero",
        ["type"] = "ui-schema-section",
        ["active"] = true,
        ["data"] = new JsonObject { ["props"] = new JsonObject { ["variant"] = variant } },
    };

    private static string StoredVariant(InMemoryRavenDbService store) =>
        Assert.Single(store.DocumentsIn(Sections)).Value["data"]!["props"]!["variant"]!.GetValue<string>();

    private string HeroFile => Path.Combine(_sectionsPath, "hero.json");

    private void WriteHero(JsonObject section) => File.WriteAllText(HeroFile, section.ToJsonString());

    /// <summary>Writes <c>sections/hero.json</c> while watching, and returns the watcher's first confirmation line.</summary>
    private Task<(LogLevel Level, string Message)> WatchOneChangeAsync(
        InMemoryRavenDbService store, bool dryRun, JsonObject section) =>
        WatchAsync(store, dryRun, () => WriteHero(section), awaited: "-> ");

    /// <summary>Deletes <c>sections/hero.json</c> while watching, and returns the first line that starts with <paramref name="awaited"/>.</summary>
    private Task<(LogLevel Level, string Message)> WatchOneDeletionAsync(
        InMemoryRavenDbService store, bool dryRun, string awaited) =>
        WatchAsync(store, dryRun, () => File.Delete(HeroFile), awaited);

    /// <summary>
    /// Syncs the layouts once and starts watching, as the CLI does: the initial sync tells the watcher which
    /// document each file is, so a deletion targets that document. Then makes <paramref name="change"/> and
    /// returns the first line that starts with <paramref name="awaited"/>. The watcher is stopped before this
    /// returns, whether or not the line arrived.
    /// </summary>
    private async Task<(LogLevel Level, string Message)> WatchAsync(
        InMemoryRavenDbService store, bool dryRun, Action change, string awaited)
    {
        LocalFileService files = new(NullLogger<LocalFileService>.Instance);
        DocumentSyncService sync = new(
            _log.For<DocumentSyncService>(),
            files,
            store,
            new RelativeDateResolver(NullLogger<RelativeDateResolver>.Instance),
            [],
            new SyncOptions(),
            new CommandLineArgs());
        FileWatcherService watcher = new(_log.For<FileWatcherService>(), sync, files, new SyncOptions(), new CommandLineArgs());

        SyncBatchResult initial = await sync.SyncAllAsync(_layoutsPath, dryRun: dryRun);
        using CancellationTokenSource cts = new();
        // WatchAsync arms the file-system watcher before its first await, so the change below is seen.
        Task watching = watcher.WatchAsync(_layoutsPath, dryRun: dryRun, initialSyncResult: initial, ct: cts.Token);
        try
        {
            change();
            return await FirstLineAsync(awaited);
        }
        finally
        {
            cts.Cancel();
            await watching;
        }
    }

    /// <summary>The first line that starts with <paramref name="awaited"/>. The watcher logs from a timer thread after the debounce.</summary>
    private async Task<(LogLevel Level, string Message)> FirstLineAsync(string awaited)
    {
        DateTime deadline = DateTime.UtcNow + LineTimeout;
        while (DateTime.UtcNow < deadline)
        {
            List<(LogLevel Level, string Message)> entries = _log.Snapshot();
            int index = entries.FindIndex(entry => entry.Message.StartsWith(awaited));
            if (index >= 0)
            {
                return entries[index];
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"No line starting with \"{awaited}\" within {LineTimeout.TotalSeconds}s. Log:{Environment.NewLine}"
            + string.Join(Environment.NewLine, _log.Snapshot().Select(entry => $"{entry.Level}: {entry.Message}")));
    }

    /// <summary>The line logged right before <paramref name="entry"/>.</summary>
    private string LineBefore((LogLevel Level, string Message) entry)
    {
        List<(LogLevel Level, string Message)> entries = _log.Snapshot();
        return entries[entries.IndexOf(entry) - 1].Message;
    }

    // ── A dry run ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DryRun_NewFile_IsConfirmedAsNotWritten()
    {
        using InMemoryRavenDbService store = new();

        (LogLevel Level, string Message) confirmation = await WatchOneChangeAsync(store, dryRun: true, Section("new"));

        Assert.Equal((LogLevel.Information, DryRunConfirmation), confirmation);
        Assert.StartsWith("Would CREATE: ", LineBefore(confirmation));
        Assert.Empty(store.DocumentsIn(Sections));
    }

    [Fact]
    public async Task DryRun_ChangedFile_IsConfirmedAsNotWritten()
    {
        // The incident: "Would UPDATE: dirt-life/sections/hero.json" followed by "-> Synced 'hero'".
        using InMemoryRavenDbService store = new();
        store.Seed(Sections, "sections/hero", Section("old"));

        (LogLevel Level, string Message) confirmation = await WatchOneChangeAsync(store, dryRun: true, Section("new"));

        Assert.Equal((LogLevel.Information, DryRunConfirmation), confirmation);
        Assert.StartsWith("Would UPDATE: ", LineBefore(confirmation));
        Assert.Equal("old", StoredVariant(store));
    }

    // ── A real run ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "-> Created 'hero'")]
    [InlineData("old", "-> Replaced 'hero'")]
    [InlineData("new", "-> Unchanged 'hero'")]
    public async Task RealRun_IsConfirmedWithWhatItDidToTheDocument(string? storedVariant, string expected)
    {
        using InMemoryRavenDbService store = new();
        if (storedVariant != null)
        {
            store.Seed(Sections, "sections/hero", Section(storedVariant));
        }

        (LogLevel Level, string Message) confirmation = await WatchOneChangeAsync(store, dryRun: false, Section("new"));

        Assert.Equal((LogLevel.Information, expected), confirmation);
        Assert.Equal("new", StoredVariant(store));
    }

    // ── A deleted file ───────────────────────────────────────────────────────

    private static readonly string HeroRelativePath = Path.Combine(Layout, "sections", "hero.json");

    [Fact]
    public async Task DryRun_DeletedFile_IsAnnouncedAsAPreview()
    {
        // Issue #59: "Deleting from DB: dirt-life/sections/hero.json (hero)" followed by "Would DELETE: hero".
        using InMemoryRavenDbService store = new();
        store.Seed(Sections, "sections/hero", Section("new"));
        WriteHero(Section("new"));

        (LogLevel Level, string Message) preview = await WatchOneDeletionAsync(store, dryRun: true, awaited: "Would DELETE: ");

        string header = $"Would delete from DB: {HeroRelativePath} (hero)";
        Assert.Equal(header, LineBefore(preview));
        Assert.Contains((LogLevel.Information, header), _log.Snapshot());
        Assert.DoesNotContain(_log.Snapshot(), entry => entry.Message.StartsWith("Deleting from DB"));
        Assert.Equal("new", StoredVariant(store));
    }

    [Fact]
    public async Task RealRun_DeletedFile_IsAnnouncedAndDeleted()
    {
        using InMemoryRavenDbService store = new();
        store.Seed(Sections, "sections/hero", Section("new"));
        WriteHero(Section("new"));

        (LogLevel Level, string Message) summary = await WatchOneDeletionAsync(store, dryRun: false, awaited: "Deletion batch: ");

        Assert.Equal((LogLevel.Information, "Deletion batch: 1 deleted, 0 failed"), summary);
        Assert.Contains((LogLevel.Information, $"Deleting from DB: {HeroRelativePath} (hero)"), _log.Snapshot());
        Assert.Empty(store.DocumentsIn(Sections));
    }

    // ── Test plumbing ────────────────────────────────────────────────────────

    /// <summary>
    /// One log shared by the watcher and the sync service, in the order lines were written, so a test can
    /// read a confirmation beside the sync line before it. Locked, because the watcher logs from a timer thread.
    /// </summary>
    private sealed class LogSink
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public ILogger<T> For<T>() => new CapturingLogger<T>(this);

        public List<(LogLevel Level, string Message)> Snapshot()
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }

        private void Add(LogLevel level, string message)
        {
            lock (_entries)
            {
                _entries.Add((level, message));
            }
        }

        private sealed class CapturingLogger<T>(LogSink sink) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
                => NullLogger.Instance.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => sink.Add(logLevel, formatter(state, exception));
        }
    }
}
