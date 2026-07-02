using System.Text.Json.Nodes;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Unit tests for <see cref="SeedAuthorshipValidator"/>.
///
/// The validator is a pure, non-blocking policy nudge. Each test drives a small wrapped document
/// through <c>Inspect</c> and asserts on the captured log output (a <see cref="CapturingLogger"/>
/// collects every <c>LogWarning</c> so we can inspect count + message text). No tenant-literal
/// identifiers or provider OIDs appear in these tests — all sample NanoIDs and ext-refs are
/// synthetic grammar exemplars.
/// </summary>
public class SeedAuthorshipValidatorTests
{
    // Synthetic 21-char NanoID-shaped values (grammar exemplars, not real identities).
    private const string NanoIdA = "Abc123_-Def456Ghi789x";
    private const string NanoIdB = "Xyz789_-UvwPqr456Lmn0";
    private const string NanoIdC = "Qrs456_-Tuv123Wxy789z";

    // Synthetic ext-ref grammar exemplars.
    private const string ExtRefA = "ext:entra:provider-subject-a";
    private const string ExtRefB = "ext:entra:provider-subject-b";

    private static (SeedAuthorshipValidator validator, CapturingLogger logger) CreateValidator()
    {
        CapturingLogger logger = new();
        SeedAuthorshipValidator validator = new(logger);
        return (validator, logger);
    }

    // ── Case 1: NanoID in indexes.adminIds → warn ─────────────────────────────

    [Fact]
    public void Validate_NanoIdInIndexAdminIds_EmitsWarning()
    {
        (SeedAuthorshipValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject content = new()
        {
            ["indexes"] = new JsonObject
            {
                ["adminIds"] = new JsonArray(NanoIdA)
            }
        };

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/grp-a.json", content);

        Assert.Single(logger.Warnings);
        Assert.Contains("indexes.adminIds[0]", logger.Warnings[0]);
        Assert.Contains("grp-a.json", logger.Warnings[0]);
    }

    // ── Case 2: ext:entra:* in indexes.adminIds → no warn ─────────────────────

    [Fact]
    public void Validate_ExtRefInIndexAdminIds_DoesNotWarn()
    {
        (SeedAuthorshipValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject content = new()
        {
            ["indexes"] = new JsonObject
            {
                ["adminIds"] = new JsonArray(ExtRefA, ExtRefB)
            }
        };

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/grp-a.json", content);

        Assert.Empty(logger.Warnings);
    }

    // ── Case 3: Mixed array (NanoID + ext) → warn once, listing only NanoIDs ──

    [Fact]
    public void Validate_MixedArray_WarnsOnceListingOnlyNanoIds()
    {
        (SeedAuthorshipValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject content = new()
        {
            ["indexes"] = new JsonObject
            {
                ["adminIds"] = new JsonArray(ExtRefA, NanoIdA, ExtRefB, NanoIdB)
            }
        };

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/grp-a.json", content);

        Assert.Single(logger.Warnings);
        string message = logger.Warnings[0];
        // The NanoID positions (index 1 and 3) should be flagged.
        Assert.Contains("indexes.adminIds[1]", message);
        Assert.Contains("indexes.adminIds[3]", message);
        // The ext-ref positions (index 0 and 2) must NOT appear.
        Assert.DoesNotContain("indexes.adminIds[0]", message);
        Assert.DoesNotContain("indexes.adminIds[2]", message);
    }

    // ── Case 4: Empty array / missing field → no warn ─────────────────────────

    [Fact]
    public void Validate_EmptyArrayOrMissingField_DoesNotWarn()
    {
        (SeedAuthorshipValidator validator, CapturingLogger logger) = CreateValidator();

        // Empty array + empty string + missing fields entirely.
        JsonObject content = new()
        {
            ["indexes"] = new JsonObject
            {
                ["adminIds"] = new JsonArray(),
                ["ownerId"] = ""
            },
            ["data"] = new JsonObject
            {
                ["title"] = "Some entity with no identity refs"
            }
        };

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/plain.json", content);

        Assert.Empty(logger.Warnings);
    }

    // ── Case 5: Non-entity document types → no warn ───────────────────────────

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
        (SeedAuthorshipValidator validator, CapturingLogger logger) = CreateValidator();

        // Content that WOULD warn if this were an entity — proves the type check is the gate.
        JsonObject content = new()
        {
            ["indexes"] = new JsonObject
            {
                ["adminIds"] = new JsonArray(NanoIdA),
                ["ownerId"] = NanoIdB
            }
        };

        validator.Inspect(documentType, "layouts/x/sections/some.json", content);

        Assert.Empty(logger.Warnings);
    }

    // ── Case 6: Nested data.members[i].identityId → warn listing array index ──

    [Fact]
    public void Validate_NestedMembersIdentityId_WarnsWithArrayIndex()
    {
        (SeedAuthorshipValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject content = new()
        {
            ["data"] = new JsonObject
            {
                ["members"] = new JsonArray(
                    new JsonObject { ["identityId"] = ExtRefA,  ["role"] = "admin" },
                    new JsonObject { ["identityId"] = NanoIdA,  ["role"] = "member" },
                    new JsonObject { ["identityId"] = NanoIdB,  ["role"] = "member" }
                )
            }
        };

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/grp-family.json", content);

        Assert.Single(logger.Warnings);
        string message = logger.Warnings[0];
        Assert.Contains("data.members[1].identityId", message);
        Assert.Contains("data.members[2].identityId", message);
        Assert.DoesNotContain("data.members[0].identityId", message);
    }

    // ── Extra: aggregation across index + data scopes, single warning line ────

    [Fact]
    public void Validate_OffendersAcrossIndexesAndData_AggregatesIntoSingleWarning()
    {
        (SeedAuthorshipValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject content = new()
        {
            ["indexes"] = new JsonObject
            {
                ["adminIds"] = new JsonArray(NanoIdA),
                ["memberIds"] = new JsonArray(NanoIdB),
                ["ownerId"] = NanoIdC
            },
            ["data"] = new JsonObject
            {
                ["organizerId"] = NanoIdA,
                ["members"] = new JsonArray(
                    new JsonObject { ["identityId"] = NanoIdB }
                )
            }
        };

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/grp.json", content);

        Assert.Single(logger.Warnings);
        string message = logger.Warnings[0];
        Assert.Contains("indexes.adminIds[0]", message);
        Assert.Contains("indexes.memberIds[0]", message);
        Assert.Contains("indexes.ownerId", message);
        Assert.Contains("data.organizerId", message);
        Assert.Contains("data.members[0].identityId", message);
    }

    // ── Extra: scalar data.ownerId (not an array) is flagged at the field path ─

    [Fact]
    public void Validate_ScalarDataOwnerId_WarnsWithFieldPath()
    {
        (SeedAuthorshipValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject content = new()
        {
            ["data"] = new JsonObject
            {
                ["ownerId"] = NanoIdA
            }
        };

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/thing.json", content);

        Assert.Single(logger.Warnings);
        Assert.Contains("data.ownerId", logger.Warnings[0]);
        Assert.DoesNotContain("data.ownerId[", logger.Warnings[0]);
    }

    // ── Extra: non-NanoID-shaped scalars (too short, wrong chars) are ignored ──

    [Theory]
    [InlineData("abc")]                          // too short
    [InlineData("not a nanoid value at all")]    // spaces (invalid chars)
    [InlineData("has/slashes/in/it")]            // invalid chars
    public void Validate_NonNanoIdShapedValues_DoesNotWarn(string value)
    {
        (SeedAuthorshipValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject content = new()
        {
            ["indexes"] = new JsonObject
            {
                ["ownerId"] = value
            }
        };

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/thing.json", content);

        Assert.Empty(logger.Warnings);
    }

    // ── Counter: WarningCount feeds --strict exit ──────────────────

    [Fact]
    public void WarningCount_StartsAtZero()
    {
        // Guards the --strict wiring: a fresh validator must report zero offenses so a
        // clean sync never trips the strict gate.
        (SeedAuthorshipValidator validator, _) = CreateValidator();

        Assert.Equal(0, validator.WarningCount);
    }

    [Fact]
    public void WarningCount_IncrementsOncePerOffendingFile()
    {
        // One WARN line is emitted per file (not per offending field). The counter must
        // mirror that so operators see consistent numbers between logs and strict exit.
        (SeedAuthorshipValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject content = new()
        {
            ["indexes"] = new JsonObject
            {
                // Two offending fields in the same file — still one file-level offense.
                ["adminIds"] = new JsonArray(NanoIdA),
                ["ownerId"] = NanoIdB,
            }
        };

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/a.json", content);

        Assert.Single(logger.Warnings);
        Assert.Equal(1, validator.WarningCount);
    }

    [Fact]
    public void WarningCount_AccumulatesAcrossFiles()
    {
        // Multiple offending files bump the counter cumulatively — feeds the --strict
        // summary at process exit ("--strict: N seed file(s) with raw-NanoID ...").
        (SeedAuthorshipValidator validator, _) = CreateValidator();

        JsonObject first = new()
        {
            ["indexes"] = new JsonObject { ["ownerId"] = NanoIdA }
        };
        JsonObject second = new()
        {
            ["indexes"] = new JsonObject { ["ownerId"] = NanoIdB }
        };

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/a.json", first);
        validator.Inspect(DocumentType.Entity, "layouts/x/entities/b.json", second);

        Assert.Equal(2, validator.WarningCount);
    }

    [Fact]
    public void WarningCount_DoesNotIncrementForCleanFiles()
    {
        // ext:*-only and non-entity files must not increment the counter, otherwise the
        // strict gate would fire against well-formed seeds.
        (SeedAuthorshipValidator validator, _) = CreateValidator();

        JsonObject cleanEntity = new()
        {
            ["indexes"] = new JsonObject { ["ownerId"] = ExtRefA }
        };
        JsonObject nonEntity = new()
        {
            ["indexes"] = new JsonObject { ["ownerId"] = NanoIdA }
        };

        validator.Inspect(DocumentType.Entity, "layouts/x/entities/clean.json", cleanEntity);
        // Section documents are skipped entirely.
        validator.Inspect(DocumentType.Section, "layouts/x/sections/s.json", nonEntity);

        Assert.Equal(0, validator.WarningCount);
    }

    [Fact]
    public void Reset_ZeroesTheCounter()
    {
        // Before this validator implemented ISeedValidator it had NO Reset(), so WarningCount
        // leaked across watch-mode batches (repeated SyncAllAsync in one process). The uniform
        // lifecycle now resets it at batch start — this guards that fix. See issue #7.
        (SeedAuthorshipValidator validator, _) = CreateValidator();

        JsonObject content = new()
        {
            ["indexes"] = new JsonObject { ["ownerId"] = NanoIdA }
        };
        validator.Inspect(DocumentType.Entity, "layouts/x/entities/a.json", content);
        Assert.Equal(1, validator.WarningCount);

        validator.Reset();
        Assert.Equal(0, validator.WarningCount);
    }

    // ── Test plumbing ─────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="ILogger{T}"/> that captures formatted warning messages into a list so
    /// tests can assert both on count and text. Non-warning levels are ignored.
    /// </summary>
    private sealed class CapturingLogger : ILogger<SeedAuthorshipValidator>
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
