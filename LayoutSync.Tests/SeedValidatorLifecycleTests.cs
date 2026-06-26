using System.Text.Json.Nodes;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Cross-validator contract tests for <see cref="ISeedValidator"/>. Every validator is exercised
/// *through the interface* (not its concrete type) to prove the uniform batch lifecycle holds:
/// <c>Reset</c> → <c>Inspect</c> (per file) → <c>FinalizeBatch</c>, with <c>WarningCount</c>
/// surfacing offenses and <c>Reset()</c> clearing them so counts never leak across batches — the
/// watch-mode count-leak the refactor closes (issue #7).
///
/// The assertion is phase-agnostic: the cross-reference validator only emits at
/// <c>FinalizeBatch</c>, the others at <c>Inspect</c>, but the full-lifecycle pass flags all three.
/// </summary>
public class SeedValidatorLifecycleTests
{
    // Synthetic 21-char NanoID-shaped grammar exemplar (not a real identity).
    private const string NanoId = "Abc123_-Def456Ghi789x";

    [Fact]
    public void Authorship_FollowsLifecycleContract()
    {
        ISeedValidator validator = new SeedAuthorshipValidator(NullLogger<SeedAuthorshipValidator>.Instance);

        // Raw NanoID in an identity-bearing index field → flagged at Inspect.
        JsonObject offending = new()
        {
            ["indexes"] = new JsonObject { ["ownerId"] = NanoId }
        };

        AssertLifecycleContract(validator, "authorship", DocumentType.Entity, "layouts/x/entities/a.json", offending);
    }

    [Fact]
    public void CrossReference_FollowsLifecycleContract()
    {
        ISeedValidator validator = new SeedCrossReferenceValidator(NullLogger<SeedCrossReferenceValidator>.Instance);

        // A lone unpinned referencer whose outbound NanoID ref has no owning seed in the batch is
        // dangling → flagged at FinalizeBatch (not Inspect). Proves the contract is phase-agnostic.
        JsonObject offending = new()
        {
            ["identifier"] = "ref",
            ["type"] = "test-entity",
            ["indexes"] = new JsonObject { ["eventId"] = NanoId },
            ["data"] = new JsonObject(),
        };

        AssertLifecycleContract(validator, "cross-reference", DocumentType.Entity, "layouts/x/entities/ref.json", offending);
    }

    [Fact]
    public void DeadWidgetProp_FollowsLifecycleContract()
    {
        ISeedValidator validator = new DeadWidgetPropValidator(NullLogger<DeadWidgetPropValidator>.Instance);

        // defaultExpanded on a floating-panel element is dead → flagged at Inspect.
        JsonObject offending = new()
        {
            ["type"] = "ui-schema-section",
            ["data"] = new JsonObject
            {
                ["type"] = "container",
                ["children"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "floating-panel",
                        ["props"] = new JsonObject { ["defaultExpanded"] = true },
                    }),
            },
        };

        AssertLifecycleContract(validator, "dead-widget-prop", DocumentType.Section, "layouts/x/sections/s.json", offending);
    }

    /// <summary>
    /// Drives a validator through the full <see cref="ISeedValidator"/> lifecycle via the interface
    /// only, proving: (1) <c>Name</c>/<c>StrictWarningDetail</c> are populated; (2) a fresh batch
    /// starts at zero; (3) the full <c>Reset</c>→<c>Inspect</c>→<c>FinalizeBatch</c> pass flags the
    /// offending document regardless of which phase emits; (4) <c>Reset()</c> clears the count so it
    /// never leaks into the next batch.
    /// </summary>
    private static void AssertLifecycleContract(
        ISeedValidator validator,
        string expectedName,
        DocumentType documentType,
        string relativePath,
        JsonObject offending)
    {
        Assert.Equal(expectedName, validator.Name);
        Assert.False(string.IsNullOrWhiteSpace(validator.StrictWarningDetail));

        // Batch 1 — full lifecycle over one offending file.
        validator.Reset();
        Assert.Equal(0, validator.WarningCount);

        validator.Inspect(documentType, relativePath, offending);
        validator.FinalizeBatch();
        Assert.True(validator.WarningCount > 0, $"{expectedName} should flag the offending document by batch end");

        // Batch 2 — Reset must clear batch-1 state (the watch-mode leak the refactor fixes).
        validator.Reset();
        Assert.Equal(0, validator.WarningCount);

        // A clean batch (nothing inspected) stays at zero through FinalizeBatch.
        validator.FinalizeBatch();
        Assert.Equal(0, validator.WarningCount);
    }
}
