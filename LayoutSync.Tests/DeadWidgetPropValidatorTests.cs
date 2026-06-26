using System.Text.Json.Nodes;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Unit tests for <see cref="DeadWidgetPropValidator"/>.
///
/// The validator is a pure, non-blocking detector. Each test drives a small wrapped section
/// document through <c>Validate</c> and asserts on the captured log output (a
/// <see cref="CapturingLogger"/> collects every <c>LogWarning</c> so we can inspect count + text).
/// The behaviour that matters most is TYPE-SCOPING: <c>defaultExpanded</c> is dead on
/// <c>floating-panel</c> but legitimate on <c>accordion</c>, so the accordion case must stay clean.
/// </summary>
public class DeadWidgetPropValidatorTests
{
    private static (DeadWidgetPropValidator validator, CapturingLogger logger) CreateValidator()
    {
        CapturingLogger logger = new();
        DeadWidgetPropValidator validator = new(logger);
        return (validator, logger);
    }

    /// <summary>Builds a wrapped section document with the given widget element under data.children[0].</summary>
    private static JsonObject Section(JsonObject element) =>
        new()
        {
            ["identifier"] = "some-section",
            ["type"] = "ui-schema-section",
            ["data"] = new JsonObject
            {
                ["type"] = "container",
                ["children"] = new JsonArray(element),
            },
        };

    // ── Case 1: defaultExpanded on a floating-panel → warn ────────────────────

    [Fact]
    public void Validate_DefaultExpandedOnFloatingPanel_EmitsWarning()
    {
        (DeadWidgetPropValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject content = Section(new JsonObject
        {
            ["type"] = "floating-panel",
            ["id"] = "actions-panel",
            ["props"] = new JsonObject
            {
                ["title"] = "Trail Report",
                ["display"] = "floating",
                ["defaultExpanded"] = true,
            },
        });

        validator.Validate(DocumentType.Section, "layouts/x/sections/trail-report-detail.json", content);

        Assert.Single(logger.Warnings);
        Assert.Contains("data.children[0].props.defaultExpanded", logger.Warnings[0]);
        Assert.Contains("floating-panel", logger.Warnings[0]);
        Assert.Contains("trail-report-detail.json", logger.Warnings[0]);
        Assert.Equal(1, validator.DeadPropWarningCount);
    }

    // ── Case 2: floating-panel WITHOUT the dead prop → no warn ────────────────

    [Fact]
    public void Validate_FloatingPanelWithoutDeadProp_DoesNotWarn()
    {
        (DeadWidgetPropValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject content = Section(new JsonObject
        {
            ["type"] = "floating-panel",
            ["props"] = new JsonObject { ["title"] = "Filters", ["display"] = "floating" },
        });

        validator.Validate(DocumentType.Section, "layouts/x/sections/filters.json", content);

        Assert.Empty(logger.Warnings);
        Assert.Equal(0, validator.DeadPropWarningCount);
    }

    // ── Case 3: defaultExpanded on an ACCORDION → no warn (type-scoping) ──────

    [Fact]
    public void Validate_DefaultExpandedOnAccordion_DoesNotWarn()
    {
        // defaultExpanded is a REAL prop on accordion (array form). The type-scoped rule must
        // not false-flag it. This is the guard that makes the validator safe to ship.
        (DeadWidgetPropValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject content = Section(new JsonObject
        {
            ["type"] = "accordion",
            ["props"] = new JsonObject { ["defaultExpanded"] = new JsonArray("about") },
        });

        validator.Validate(DocumentType.Section, "layouts/x/sections/group-detail-info.json", content);

        Assert.Empty(logger.Warnings);
        Assert.Equal(0, validator.DeadPropWarningCount);
    }

    // ── Case 4: non-section document types → no warn (type gate) ──────────────

    [Theory]
    [InlineData(DocumentType.Entity)]
    [InlineData(DocumentType.Layout)]
    [InlineData(DocumentType.Manifest)]
    [InlineData(DocumentType.Menu)]
    public void Validate_NonSectionDocumentTypes_DoesNotWarn(DocumentType documentType)
    {
        // Content that WOULD warn if this were a section — proves the document-type check is the gate.
        (DeadWidgetPropValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject content = Section(new JsonObject
        {
            ["type"] = "floating-panel",
            ["props"] = new JsonObject { ["defaultExpanded"] = true },
        });

        validator.Validate(documentType, "layouts/x/sections/some.json", content);

        Assert.Empty(logger.Warnings);
    }

    // ── Case 5: floating-panel nested deep in the tree → warn (recursion) ─────

    [Fact]
    public void Validate_DeeplyNestedFloatingPanel_EmitsWarning()
    {
        (DeadWidgetPropValidator validator, CapturingLogger logger) = CreateValidator();

        // data.children[0].children[1] is the offending panel.
        JsonObject content = new()
        {
            ["type"] = "ui-schema-section",
            ["data"] = new JsonObject
            {
                ["type"] = "container",
                ["children"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "container",
                        ["children"] = new JsonArray(
                            new JsonObject { ["type"] = "text" },
                            new JsonObject
                            {
                                ["type"] = "floating-panel",
                                ["props"] = new JsonObject { ["defaultExpanded"] = false },
                            }),
                    }),
            },
        };

        validator.Validate(DocumentType.Section, "layouts/x/sections/nested.json", content);

        Assert.Single(logger.Warnings);
        Assert.Contains("data.children[0].children[1].props.defaultExpanded", logger.Warnings[0]);
    }

    // ── Case 6: two offending panels in one file → ONE warn, both paths listed ─

    [Fact]
    public void Validate_TwoDeadPanelsInOneFile_WarnsOnceListingBoth()
    {
        (DeadWidgetPropValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject content = new()
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
                    },
                    new JsonObject
                    {
                        ["type"] = "floating-panel",
                        ["props"] = new JsonObject { ["defaultExpanded"] = false },
                    }),
            },
        };

        validator.Validate(DocumentType.Section, "layouts/x/sections/two.json", content);

        Assert.Single(logger.Warnings);
        Assert.Contains("data.children[0].props.defaultExpanded", logger.Warnings[0]);
        Assert.Contains("data.children[1].props.defaultExpanded", logger.Warnings[0]);
        // One file-level offense even though two props were flagged.
        Assert.Equal(1, validator.DeadPropWarningCount);
    }

    // ── Case 7: missing / non-object data → no warn, no crash ─────────────────

    [Fact]
    public void Validate_MissingData_DoesNotWarn()
    {
        (DeadWidgetPropValidator validator, CapturingLogger logger) = CreateValidator();

        JsonObject content = new() { ["identifier"] = "empty", ["type"] = "ui-schema-section" };

        validator.Validate(DocumentType.Section, "layouts/x/sections/empty.json", content);

        Assert.Empty(logger.Warnings);
    }

    // ── Counter semantics: feeds --strict exit ────────────────────────────────

    [Fact]
    public void DeadPropWarningCount_StartsAtZero()
    {
        (DeadWidgetPropValidator validator, _) = CreateValidator();
        Assert.Equal(0, validator.DeadPropWarningCount);
    }

    [Fact]
    public void DeadPropWarningCount_AccumulatesAcrossFiles()
    {
        (DeadWidgetPropValidator validator, _) = CreateValidator();

        JsonObject first = Section(new JsonObject
        {
            ["type"] = "floating-panel",
            ["props"] = new JsonObject { ["defaultExpanded"] = true },
        });
        JsonObject second = Section(new JsonObject
        {
            ["type"] = "floating-panel",
            ["props"] = new JsonObject { ["defaultExpanded"] = false },
        });

        validator.Validate(DocumentType.Section, "layouts/x/sections/a.json", first);
        validator.Validate(DocumentType.Section, "layouts/x/sections/b.json", second);

        Assert.Equal(2, validator.DeadPropWarningCount);
    }

    [Fact]
    public void Reset_ZeroesTheCounter()
    {
        (DeadWidgetPropValidator validator, _) = CreateValidator();

        JsonObject content = Section(new JsonObject
        {
            ["type"] = "floating-panel",
            ["props"] = new JsonObject { ["defaultExpanded"] = true },
        });
        validator.Validate(DocumentType.Section, "layouts/x/sections/a.json", content);
        Assert.Equal(1, validator.DeadPropWarningCount);

        validator.Reset();
        Assert.Equal(0, validator.DeadPropWarningCount);
    }

    // ── Test plumbing ─────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="ILogger{T}"/> that captures formatted warning messages so tests can assert
    /// on count and text. Mirrors the harness used by <c>SeedAuthorshipValidatorTests</c>.
    /// </summary>
    private sealed class CapturingLogger : ILogger<DeadWidgetPropValidator>
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
