using System.Text.Json.Nodes;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

public class ManifestSectionValidatorTests
{
    [Fact]
    public void Validate_NoOffenses_WhenAllReferencesResolve()
    {
        ManifestSectionValidator validator = NewValidator();
        JsonObject manifest = NewManifestWithSections(
            "events-page-header", "event-filters-panel", "full-width-layout");
        List<RoutePatchInput> patches =
        [
            new(
                Route: "/events",
                StructuralSection: "full-width-layout",
                MainSections: ["events-page-header", "event-filters-panel"]),
        ];

        SectionValidationResult result = validator.Validate(manifest, patches);

        Assert.True(result.AllValid);
        Assert.Empty(result.ErrorsByRoute);
        Assert.Equal(0, validator.OffenseCount);
    }

    [Fact]
    public void Validate_FlagsTypo_WithSuggestions()
    {
        ManifestSectionValidator validator = NewValidator();
        JsonObject manifest = NewManifestWithSections(
            "events-page-header", "event-filters-panel", "full-width-layout");
        List<RoutePatchInput> patches =
        [
            new(
                Route: "/events",
                MainSections: ["event-fitlers-panel"]), // typo: fitlers vs filters
        ];

        SectionValidationResult result = validator.Validate(manifest, patches);

        Assert.False(result.AllValid);
        Assert.Single(result.ErrorsByRoute);
        IReadOnlyList<string> errors = result.ErrorsByRoute["/events"];
        Assert.Single(errors);
        Assert.Contains("event-fitlers-panel", errors[0]);
        Assert.Contains("Did you mean", errors[0]);
        Assert.Contains("event-filters-panel", errors[0]);
        Assert.Equal(1, validator.OffenseCount);
    }

    [Fact]
    public void Validate_FlagsUnknownStructuralSection()
    {
        ManifestSectionValidator validator = NewValidator();
        JsonObject manifest = NewManifestWithSections(
            "full-width-layout", "sidebar-layout");
        List<RoutePatchInput> patches =
        [
            new(
                Route: "/events",
                StructuralSection: "fullwidth-layout"), // missing hyphen
        ];

        SectionValidationResult result = validator.Validate(manifest, patches);

        Assert.False(result.AllValid);
        Assert.Equal(1, validator.OffenseCount);
        IReadOnlyList<string> errors = result.ErrorsByRoute["/events"];
        Assert.Contains("structural section", errors[0]);
        Assert.Contains("fullwidth-layout", errors[0]);
        Assert.Contains("full-width-layout", errors[0]);
    }

    [Fact]
    public void Validate_LevenshteinSuggestions_AreSortedByDistance()
    {
        // "event-foo" should rank closer to "event-bar" (distance 3) than to
        // "completely-different" (distance much higher), so the closer match comes first.
        IReadOnlyList<string> suggestions = ManifestSectionValidator.NearestMatches(
            needle: "event-foo",
            haystack: ["completely-different", "event-bar", "event-baz", "totally-unrelated"],
            max: 3);

        Assert.Equal(["event-bar", "event-baz"], suggestions);
    }

    [Fact]
    public void Validate_LevenshteinSuggestions_TieBreakAlphabetically()
    {
        // Both "event-bar" and "event-baz" are equidistant from "event-bao";
        // alphabetical tie-break puts bar before baz.
        IReadOnlyList<string> suggestions = ManifestSectionValidator.NearestMatches(
            needle: "event-bao",
            haystack: ["event-baz", "event-bar"],
            max: 2);

        Assert.Equal(["event-bar", "event-baz"], suggestions);
    }

    [Fact]
    public void Validate_OffenseCount_IncrementsPerRoute()
    {
        ManifestSectionValidator validator = NewValidator();
        JsonObject manifest = NewManifestWithSections("known-section");
        List<RoutePatchInput> patches =
        [
            new(Route: "/r1", MainSections: ["bogus-1"]),
            new(Route: "/r2", MainSections: ["bogus-2", "bogus-3"]), // 2 errors, 1 offense
            new(Route: "/r3", MainSections: ["known-section"]),       // valid — no offense
        ];

        SectionValidationResult result = validator.Validate(manifest, patches);

        Assert.False(result.AllValid);
        Assert.Equal(2, validator.OffenseCount); // /r1 and /r2, NOT /r3
        Assert.Equal(2, result.ErrorsByRoute.Count);
        Assert.Equal(2, result.ErrorsByRoute["/r2"].Count); // both bogus-2 and bogus-3
    }

    [Fact]
    public void Validate_HandlesManifestWithNoSections()
    {
        // Degenerate but possible: a brand-new manifest with no sections declared yet.
        // The validator should not crash; every reference is unresolved.
        ManifestSectionValidator validator = NewValidator();
        JsonObject manifest = new(); // no entities at all
        List<RoutePatchInput> patches =
        [
            new(Route: "/events", MainSections: ["any-section"]),
        ];

        SectionValidationResult result = validator.Validate(manifest, patches);

        Assert.False(result.AllValid);
        IReadOnlyList<string> errors = result.ErrorsByRoute["/events"];
        Assert.Single(errors);
        Assert.Contains("any-section", errors[0]);
    }

    [Fact]
    public void Validate_IsCaseSensitive()
    {
        // Section identifiers in this codebase are NanoIDs / kebab-case strings; case
        // matters. "Events-Page-Header" should NOT match "events-page-header".
        ManifestSectionValidator validator = NewValidator();
        JsonObject manifest = NewManifestWithSections("events-page-header");
        List<RoutePatchInput> patches =
        [
            new(Route: "/events", MainSections: ["Events-Page-Header"]),
        ];

        SectionValidationResult result = validator.Validate(manifest, patches);

        Assert.False(result.AllValid);
        IReadOnlyList<string> errors = result.ErrorsByRoute["/events"];
        Assert.Contains("Events-Page-Header", errors[0]);
    }

    [Fact]
    public void Reset_ClearsOffenseCount()
    {
        ManifestSectionValidator validator = NewValidator();
        JsonObject manifest = NewManifestWithSections("known");
        validator.Validate(manifest, [new("/r", MainSections: ["bogus"])]);
        Assert.Equal(1, validator.OffenseCount);

        validator.Reset();

        Assert.Equal(0, validator.OffenseCount);
    }

    [Fact]
    public void Validate_DoesNotMutateInputs()
    {
        ManifestSectionValidator validator = NewValidator();
        JsonObject manifest = NewManifestWithSections("known");
        string manifestBefore = manifest.ToJsonString();
        List<RoutePatchInput> patches = [new("/r", MainSections: ["bogus"])];

        validator.Validate(manifest, patches);

        Assert.Equal(manifestBefore, manifest.ToJsonString());
        // Patch input is a record, immutable by construction — no need to assert.
    }

    private static ManifestSectionValidator NewValidator()
        => new(NullLogger<ManifestSectionValidator>.Instance);

    private static JsonObject NewManifestWithSections(params string[] identifiers)
    {
        JsonArray sections = [];
        foreach (string id in identifiers)
        {
            sections.Add(new JsonObject
            {
                ["identifier"] = id,
                ["type"] = "ui-schema-section",
            });
        }
        return new JsonObject
        {
            ["entities"] = new JsonObject
            {
                ["sections"] = sections,
            },
        };
    }
}
