using System.Text.Json.Nodes;
using LayoutSync.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Unit tests for <see cref="RavenDbService.ResolveEntityLookup"/> — the pure helper
/// that decides how <c>FindDocumentAsync</c> handles zero / one / many entity documents
/// sharing the same Identifier.
///
/// Detection is report-only: LayoutSync never auto-deletes entity duplicates because
/// entity orphan cleanup is intentionally disabled (user-data protection).
/// When &gt;1 match is found, the helper still returns the first document so sync
/// can proceed, but emits a <c>LogWarning</c> per duplicate document id so the ghosts
/// become auditable. Operators opt into hard-failing via <c>--strict</c>, which consults
/// <see cref="RavenDbService.DuplicateEntityIdentifierCount"/> at shutdown.
///
/// See issue #299 and memory <c>01KPPC6T2SX4NWD8BTW51CGF8V</c>.
/// </summary>
public class DuplicateEntityDetectionTests
{
    // ── No match ──────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveEntityLookup_NoDocuments_ReturnsNullTupleAndNoDuplicate()
    {
        CapturingLogger logger = new();

        RavenDbService.DuplicateEntityLookupResult result = RavenDbService.ResolveEntityLookup(
            collection: "entities",
            lookupValue: "event-123",
            documents: [],
            logger: logger
        );

        Assert.Null(result.DocumentId);
        Assert.Null(result.Document);
        Assert.False(result.IsDuplicate);
        Assert.Empty(logger.WarningEntries);
    }

    // ── Single match (today's happy path) ────────────────────────────────────

    [Fact]
    public void ResolveEntityLookup_SingleDocument_ReturnsThatDocumentAndNoWarning()
    {
        CapturingLogger logger = new();
        JsonObject json = new() { ["identifier"] = "event-123" };

        RavenDbService.DuplicateEntityLookupResult result = RavenDbService.ResolveEntityLookup(
            collection: "entities",
            lookupValue: "event-123",
            documents: [("abc123", json)],
            logger: logger
        );

        Assert.Equal("abc123", result.DocumentId);
        Assert.Same(json, result.Document);
        Assert.False(result.IsDuplicate);

        // No WARN lines for single-match — only a Debug trace, which we don't capture.
        Assert.Empty(logger.WarningEntries);
    }

    // ── Multi-match (duplicate path) ─────────────────────────────────────────

    [Fact]
    public void ResolveEntityLookup_MultipleDocuments_ReturnsFirstAndFlagsDuplicate()
    {
        CapturingLogger logger = new();
        JsonObject first = new() { ["identifier"] = "trail-42" };
        JsonObject second = new() { ["identifier"] = "trail-42" };
        JsonObject third = new() { ["identifier"] = "trail-42" };

        RavenDbService.DuplicateEntityLookupResult result = RavenDbService.ResolveEntityLookup(
            collection: "entities",
            lookupValue: "trail-42",
            documents: [("doc-1", first), ("doc-2", second), ("doc-3", third)],
            logger: logger
        );

        // First doc wins so sync is non-destructive against the current behaviour.
        Assert.Equal("doc-1", result.DocumentId);
        Assert.Same(first, result.Document);
        Assert.True(result.IsDuplicate);
    }

    [Fact]
    public void ResolveEntityLookup_MultipleDocuments_EmitsWarningPerDuplicate()
    {
        CapturingLogger logger = new();
        JsonObject first = new() { ["identifier"] = "trail-42" };
        JsonObject second = new() { ["identifier"] = "trail-42" };

        RavenDbService.ResolveEntityLookup(
            collection: "entities",
            lookupValue: "trail-42",
            documents: [("doc-1", first), ("doc-2", second)],
            logger: logger
        );

        // Expect 1 header WARN + 1 WARN per duplicate doc id = 3 total.
        Assert.Equal(3, logger.WarningEntries.Count);

        // Header line mentions the identifier and the count.
        string header = logger.WarningEntries[0];
        Assert.Contains("trail-42", header);
        Assert.Contains("2", header);
        Assert.Contains("entities", header);

        // Per-doc lines include every document id so operators can copy them out of logs.
        string joined = string.Join("\n", logger.WarningEntries);
        Assert.Contains("doc-1", joined);
        Assert.Contains("doc-2", joined);
    }

    [Fact]
    public void ResolveEntityLookup_MultipleDocuments_WarningMentionsCollection()
    {
        // Collection name must be surfaced so operators know where to purge the ghost.
        CapturingLogger logger = new();

        RavenDbService.ResolveEntityLookup(
            collection: "entities",
            lookupValue: "event-1",
            documents: [("doc-1", new JsonObject()), ("doc-2", new JsonObject())],
            logger: logger
        );

        Assert.All(
            logger.WarningEntries,
            entry => Assert.Contains("entities", entry)
        );
    }

    [Fact]
    public void ResolveEntityLookup_SingleDocument_DoesNotFlagDuplicate()
    {
        CapturingLogger logger = new();

        RavenDbService.DuplicateEntityLookupResult result = RavenDbService.ResolveEntityLookup(
            collection: "entities",
            lookupValue: "solo-1",
            documents: [("only-doc", new JsonObject())],
            logger: logger
        );

        Assert.False(result.IsDuplicate);
        Assert.Empty(logger.WarningEntries);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="ILogger"/> that records the formatted message for every WARN-level
    /// entry. Debug/Info/Error are ignored because the duplicate-detection path only asserts
    /// on warnings (the operator-facing signal).
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> WarningEntries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullLogger.Instance.BeginScope(state)!;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                WarningEntries.Add(formatter(state, exception));
            }
        }
    }
}
