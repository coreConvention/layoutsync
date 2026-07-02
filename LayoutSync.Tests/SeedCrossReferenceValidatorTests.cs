using System.Text.Json.Nodes;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Unit tests for <see cref="SeedCrossReferenceValidator"/>.
///
/// The validator is a two-phase detector: <c>Inspect</c> accumulates per-file metadata
/// during the normal sync pass, and <c>FinalizeBatch</c> runs the cross-check at batch end,
/// emitting one <c>LogWarning</c> per offending referencer file. Tests exercise grammar
/// (NanoID shape, <c>ext:*</c> tagged refs, empty strings), cross-reference topology
/// (pinned / unpinned / dangling targets), and the integration-style fixture that mirrors
/// the Moab example from issue #300.
///
/// No tenant-literal identifiers or real-environment NanoIDs appear here — every value is a
/// synthetic grammar exemplar.
/// </summary>
public class SeedCrossReferenceValidatorTests
{
    // Synthetic 21-char NanoID-shaped values (grammar exemplars).
    private const string NanoIdEvent = "Abc123_-Def456Ghi789x";
    private const string NanoIdOther = "Xyz789_-UvwPqr456Lmn0";
    private const string NanoIdDangling = "Qrs456_-Tuv123Wxy789z";
    private const string NanoIdSelf = "Selfref_-Same111Same1";

    private static (SeedCrossReferenceValidator validator, CapturingLogger logger) CreateValidator()
    {
        CapturingLogger logger = new();
        SeedCrossReferenceValidator validator = new(logger);
        return (validator, logger);
    }

    private static JsonObject BuildSeed(string? pinnedId, JsonObject? indexes = null, JsonObject? data = null)
    {
        JsonObject content = new()
        {
            ["identifier"] = "some-entity",
            ["type"] = "test-entity",
            ["indexes"] = indexes ?? [],
            ["data"] = data ?? []
        };
        if (!string.IsNullOrEmpty(pinnedId))
        {
            content["@metadata"] = new JsonObject
            {
                ["@id"] = pinnedId,
                ["@collection"] = "entities"
            };
        }
        return content;
    }

    // ── Case 1: Pinned owner + referencer pointing at pinned id → no warn ────

    [Fact]
    public void Validate_PinnedOwnerAndReferencer_NoWarning()
    {
        (SeedCrossReferenceValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject owner = BuildSeed(pinnedId: NanoIdEvent);
        JsonObject referencer = BuildSeed(
            pinnedId: null,
            indexes: new JsonObject { ["eventId"] = NanoIdEvent });

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/owner.json", owner);
        validator.Inspect(DocumentType.Entity, "layouts/x/entities/ref.json", referencer);
        int warnings = validator.FinalizeBatch();

        Assert.Equal(0, warnings);
        Assert.Empty(logger.Warnings);
    }

    // ── Case 2: Dangling reference (no owning seed in batch) → warn ──────────

    [Fact]
    public void Validate_DanglingReference_EmitsWarning()
    {
        (SeedCrossReferenceValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject referencer = BuildSeed(
            pinnedId: null,
            indexes: new JsonObject { ["eventId"] = NanoIdDangling });

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/ref.json", referencer);
        int warnings = validator.FinalizeBatch();

        Assert.Equal(1, warnings);
        Assert.Single(logger.Warnings);
        Assert.Contains("ref.json", logger.Warnings[0]);
        Assert.Contains("indexes.eventId", logger.Warnings[0]);
        Assert.Contains(NanoIdDangling, logger.Warnings[0]);
        Assert.Contains("<no matching seed>", logger.Warnings[0]);
    }

    // ── Case 3: Unpinned owner exists → referencer warns (owner unstable) ────

    [Fact]
    public void Validate_UnpinnedOwnerExists_EmitsWarningWithOwnerPath()
    {
        (SeedCrossReferenceValidator validator, CapturingLogger logger) = CreateValidator();

        // Owner seed without @metadata.@id, but a declared id via top-level "id" field
        // would NOT be picked up (declaredId is sourced from @metadata.@id only).
        // To simulate "unpinned owner exists with live NanoID", we construct an owner seed
        // that has no @metadata.@id — its DeclaredId will be null, so from the cross-check's
        // perspective it does not claim any NanoID at all, making the reference dangling.
        //
        // To exercise the *unpinned-owner* branch specifically, we need an owner whose
        // DeclaredId is non-null AND Pinned is false — which is structurally impossible in
        // the current model, because RecordSeed only reads DeclaredId from @metadata.@id,
        // and if that is present Pinned becomes true. The "unpinned owner" branch therefore
        // captures the edge case where someone later adds an id-source lookup path. Today
        // it is unreachable; we still test the dangling-via-unpinned-owner case for
        // protection against regressions when another id source is introduced.

        // Register an "unpinned owner" by directly giving it the id via reflection is too
        // invasive. Instead, demonstrate that an unpinned owner does not satisfy a
        // reference — the referencer must warn.
        JsonObject unpinnedOwner = BuildSeed(pinnedId: null);
        JsonObject referencer = BuildSeed(
            pinnedId: null,
            indexes: new JsonObject { ["eventId"] = NanoIdOther });

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/unpinned-owner.json", unpinnedOwner);
        validator.Inspect(DocumentType.Entity, "layouts/x/entities/ref.json", referencer);
        int warnings = validator.FinalizeBatch();

        // The reference points at NanoIdOther which no seed claims → dangling.
        Assert.Equal(1, warnings);
        Assert.Single(logger.Warnings);
        Assert.Contains("ref.json", logger.Warnings[0]);
        Assert.Contains("indexes.eventId", logger.Warnings[0]);
        Assert.Contains("<no matching seed>", logger.Warnings[0]);
    }

    // ── Case 4: Reference inside data.* with a dangling id → warn with path ──

    [Fact]
    public void Validate_DanglingReferenceInData_EmitsWarningWithDataPath()
    {
        (SeedCrossReferenceValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject owner = BuildSeed(pinnedId: NanoIdEvent);
        JsonObject referencer = BuildSeed(
            pinnedId: null,
            data: new JsonObject { ["linkedEventId"] = NanoIdDangling });

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/owner.json", owner);
        validator.Inspect(DocumentType.Entity, "layouts/x/entities/ref.json", referencer);
        int warnings = validator.FinalizeBatch();

        Assert.Equal(1, warnings);
        Assert.Single(logger.Warnings);
        Assert.Contains("data.linkedEventId", logger.Warnings[0]);
        Assert.Contains(NanoIdDangling, logger.Warnings[0]);
    }

    // ── Case 5: Self-reference (own @metadata.@id in own data/indexes) → no warn ─

    [Fact]
    public void Validate_SelfReferenceToOwnMetadataId_DoesNotWarn()
    {
        (SeedCrossReferenceValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject selfRef = BuildSeed(
            pinnedId: NanoIdSelf,
            indexes: new JsonObject { ["rootId"] = NanoIdSelf },
            data: new JsonObject { ["canonicalId"] = NanoIdSelf });

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/self.json", selfRef);
        int warnings = validator.FinalizeBatch();

        Assert.Equal(0, warnings);
        Assert.Empty(logger.Warnings);
    }

    // ── Case 6: ext:entra:* values → no warn ──────────────────────────────────

    [Fact]
    public void Validate_ExtRefsInReferenceFields_DoesNotWarn()
    {
        (SeedCrossReferenceValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject referencer = BuildSeed(
            pinnedId: null,
            indexes: new JsonObject
            {
                ["ownerId"] = "ext:entra:provider-subject-a",
                ["attendeeIds"] = new JsonArray("ext:entra:provider-subject-b", "ext:google:sub-c")
            },
            data: new JsonObject { ["createdBy"] = "ext:entra:provider-subject-a" });

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/ref.json", referencer);
        int warnings = validator.FinalizeBatch();

        Assert.Equal(0, warnings);
        Assert.Empty(logger.Warnings);
    }

    // ── Case 7: Mixed array (some valid pinned refs + some dangling) → one warn, only dangling listed ─

    [Fact]
    public void Validate_MixedArrayReferences_WarnsOnceListingOnlyOffenders()
    {
        (SeedCrossReferenceValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject owner = BuildSeed(pinnedId: NanoIdEvent);
        JsonObject referencer = BuildSeed(
            pinnedId: null,
            data: new JsonObject
            {
                ["relatedEventIds"] = new JsonArray(NanoIdEvent, NanoIdDangling, NanoIdOther)
            });

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/owner.json", owner);
        validator.Inspect(DocumentType.Entity, "layouts/x/entities/ref.json", referencer);
        int warnings = validator.FinalizeBatch();

        Assert.Equal(1, warnings);
        Assert.Single(logger.Warnings);
        string message = logger.Warnings[0];
        // The pinned-owner reference is NOT flagged.
        Assert.DoesNotContain($"data.relatedEventIds[0]={NanoIdEvent}", message);
        // The two dangling references ARE flagged with specific paths.
        Assert.Contains($"data.relatedEventIds[1]={NanoIdDangling}", message);
        Assert.Contains($"data.relatedEventIds[2]={NanoIdOther}", message);
    }

    // ── Case 8: Non-entity document types → no warn ───────────────────────────

    [Theory]
    [InlineData(DocumentType.Section)]
    [InlineData(DocumentType.Layout)]
    [InlineData(DocumentType.Identity)]
    [InlineData(DocumentType.Menu)]
    [InlineData(DocumentType.Modal)]
    [InlineData(DocumentType.Manifest)]
    [InlineData(DocumentType.Tag)]
    [InlineData(DocumentType.Workflow)]
    [InlineData(DocumentType.WritePolicy)]
    [InlineData(DocumentType.ReadPolicy)]
    [InlineData(DocumentType.EntityConfig)]
    public void Validate_NonEntityDocumentTypes_DoesNotWarn(DocumentType documentType)
    {
        (SeedCrossReferenceValidator validator, CapturingLogger logger) = CreateValidator();

        // A dangling reference that WOULD warn if this were classified as an Entity.
        JsonObject content = BuildSeed(
            pinnedId: null,
            indexes: new JsonObject { ["eventId"] = NanoIdDangling });

        validator.Inspect(documentType, "layouts/x/sections/some.json", content);
        int warnings = validator.FinalizeBatch();

        Assert.Equal(0, warnings);
        Assert.Empty(logger.Warnings);
    }

    // ── Case 9: Moab-style integration fixture ────────────────────────────────

    [Fact]
    public void Validate_MoabStyleFixture_AllReferencersPointAtPinnedOwner_NoWarning()
    {
        (SeedCrossReferenceValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject eventSeed = BuildSeed(pinnedId: NanoIdEvent);

        JsonObject chatMsg1 = BuildSeed(
            pinnedId: null,
            indexes: new JsonObject { ["eventId"] = NanoIdEvent });
        JsonObject chatMsg2 = BuildSeed(
            pinnedId: null,
            indexes: new JsonObject { ["eventId"] = NanoIdEvent });
        JsonObject chatSummary = BuildSeed(
            pinnedId: null,
            indexes: new JsonObject { ["eventId"] = NanoIdEvent });

        validator.Inspect(DocumentType.Entity, "layouts/tenant/entities/evt-weekend.json", eventSeed);
        validator.Inspect(DocumentType.Entity, "layouts/tenant/entities/ecm-one.json", chatMsg1);
        validator.Inspect(DocumentType.Entity, "layouts/tenant/entities/ecm-two.json", chatMsg2);
        validator.Inspect(DocumentType.Entity, "layouts/tenant/entities/ecs-summary.json", chatSummary);
        int warnings = validator.FinalizeBatch();

        Assert.Equal(0, warnings);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void Validate_MoabStyleFixture_OwnerUnpinned_AllReferencersBreak()
    {
        (SeedCrossReferenceValidator validator, CapturingLogger logger) = CreateValidator();

        // Same fixture as above, except the event seed is NOT pinned. All three referencers
        // should each produce one warning because their target is dangling (the owner's
        // live NanoID is not visible to the cross-check since DeclaredId comes from
        // @metadata.@id only).
        JsonObject eventSeed = BuildSeed(pinnedId: null);

        JsonObject chatMsg1 = BuildSeed(
            pinnedId: null,
            indexes: new JsonObject { ["eventId"] = NanoIdEvent });
        JsonObject chatMsg2 = BuildSeed(
            pinnedId: null,
            indexes: new JsonObject { ["eventId"] = NanoIdEvent });
        JsonObject chatSummary = BuildSeed(
            pinnedId: null,
            indexes: new JsonObject { ["eventId"] = NanoIdEvent });

        validator.Inspect(DocumentType.Entity, "layouts/tenant/entities/evt-weekend.json", eventSeed);
        validator.Inspect(DocumentType.Entity, "layouts/tenant/entities/ecm-one.json", chatMsg1);
        validator.Inspect(DocumentType.Entity, "layouts/tenant/entities/ecm-two.json", chatMsg2);
        validator.Inspect(DocumentType.Entity, "layouts/tenant/entities/ecs-summary.json", chatSummary);
        int warnings = validator.FinalizeBatch();

        Assert.Equal(3, warnings);
        Assert.Equal(3, logger.Warnings.Count);
        Assert.All(logger.Warnings, w => Assert.Contains(NanoIdEvent, w));
        Assert.All(logger.Warnings, w => Assert.Contains("<no matching seed>", w));
        // The event seed itself has no outbound references, so it should not appear as
        // a referencer in any warning.
        Assert.DoesNotContain(logger.Warnings, w => w.Contains("evt-weekend.json"));
    }

    // ── Case 10: Empty strings and non-NanoID scalars → not considered references ─

    [Fact]
    public void Validate_EmptyStringsAndNonNanoIdScalars_NotFlagged()
    {
        (SeedCrossReferenceValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject referencer = BuildSeed(
            pinnedId: null,
            indexes: new JsonObject
            {
                ["hostId"] = "",
                ["shortCode"] = "abc",
                ["slug"] = "moab-weekend"
            },
            data: new JsonObject
            {
                ["title"] = "Some long descriptive title that contains spaces and punctuation.",
                ["count"] = 15
            });

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/ref.json", referencer);
        int warnings = validator.FinalizeBatch();

        Assert.Equal(0, warnings);
        Assert.Empty(logger.Warnings);
    }

    // ── Case 11: Reset clears accumulated state between batches ───────────────

    [Fact]
    public void Reset_ClearsAccumulatedSeedsAndWarningCount()
    {
        (SeedCrossReferenceValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject dangling = BuildSeed(
            pinnedId: null,
            indexes: new JsonObject { ["eventId"] = NanoIdDangling });

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/ref.json", dangling);
        int first = validator.FinalizeBatch();
        Assert.Equal(1, first);

        validator.Reset();

        // After reset, the second batch contains a benign (pinned owner + referencer) pair
        // and should produce no warnings regardless of what was seen before.
        (SeedCrossReferenceValidator _, CapturingLogger _) = (validator, logger);
        JsonObject owner = BuildSeed(pinnedId: NanoIdEvent);
        JsonObject benign = BuildSeed(
            pinnedId: null,
            indexes: new JsonObject { ["eventId"] = NanoIdEvent });

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/owner.json", owner);
        validator.Inspect(DocumentType.Entity, "layouts/x/entities/ref.json", benign);
        int second = validator.FinalizeBatch();

        Assert.Equal(0, second);
        Assert.Equal(0, validator.WarningCount);
    }

    // ── Test plumbing ─────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="ILogger{T}"/> that captures formatted warning messages into a list so
    /// tests can assert both on count and text. Non-warning levels are ignored.
    /// </summary>
    private sealed class CapturingLogger : ILogger<SeedCrossReferenceValidator>
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
