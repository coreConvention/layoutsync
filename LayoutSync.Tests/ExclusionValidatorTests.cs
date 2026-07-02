using LayoutSync.Services;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Tests for <see cref="ExclusionValidator"/> (issue #9): collections validate against the
/// <see cref="CollectionFolders"/> registry (closed set — tree presence is irrelevant),
/// layouts validate against the actual top-level directories (Ordinal), and typos get a
/// "Did you mean" suggestion. Any error exits the CLI with code 1 before a run starts.
/// </summary>
public class ExclusionValidatorTests : IDisposable
{
    private readonly string _root;
    private readonly string _layoutsPath;

    public ExclusionValidatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "layoutsync-tests-" + Guid.NewGuid().ToString("N"));
        _layoutsPath = Path.Combine(_root, "layouts");
        Directory.CreateDirectory(Path.Combine(_layoutsPath, "alpha"));
        Directory.CreateDirectory(Path.Combine(_layoutsPath, "__conformance__"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ── --exclude-collection ─────────────────────────────────────────────────

    [Fact]
    public void ValidCollections_NoErrors()
    {
        IReadOnlyList<string> errors = ExclusionValidator.Validate(
            _layoutsPath, ["entities", "identities"], []);

        Assert.Empty(errors);
    }

    [Fact]
    public void Collections_ValidateAgainstRegistryNotTree()
    {
        // The scratch tree has NO entities/ directory anywhere, yet "entities" is valid:
        // membership in the closed registry is the contract (tree presence would
        // false-reject on empty trees, --layout scoped runs, and the platform themes
        // sibling). See the ExclusionValidator class doc.
        IReadOnlyList<string> errors = ExclusionValidator.Validate(
            _layoutsPath, ["entities"], []);

        Assert.Empty(errors);
    }

    [Fact]
    public void TypoCollection_ErrorCarriesDidYouMeanSuggestion()
    {
        IReadOnlyList<string> errors = ExclusionValidator.Validate(
            _layoutsPath, ["entites"], []);

        string error = Assert.Single(errors);
        Assert.Contains("entites", error);
        Assert.Contains("Did you mean", error);
        Assert.Contains("entities", error);
    }

    [Fact]
    public void UnrecognizableCollection_ErrorWithoutSuggestion()
    {
        // "zzz" is beyond NearestMatches' distance cap for every registry name — the
        // error must still fire, just without a nonsense suggestion.
        IReadOnlyList<string> errors = ExclusionValidator.Validate(
            _layoutsPath, ["zzz"], []);

        string error = Assert.Single(errors);
        Assert.DoesNotContain("Did you mean", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyCollectionValue_IsRejected(string value)
    {
        // `--exclude-collection=` parses as an empty string under System.CommandLine.
        IReadOnlyList<string> errors = ExclusionValidator.Validate(
            _layoutsPath, [value], []);

        Assert.Single(errors);
    }

    // ── --exclude-layout ─────────────────────────────────────────────────────

    [Fact]
    public void ValidLayout_NoErrors()
    {
        IReadOnlyList<string> errors = ExclusionValidator.Validate(
            _layoutsPath, [], ["__conformance__"]);

        Assert.Empty(errors);
    }

    [Fact]
    public void TypoLayout_ErrorSuggestsActualDirectory()
    {
        IReadOnlyList<string> errors = ExclusionValidator.Validate(
            _layoutsPath, [], ["__conformence__"]);

        string error = Assert.Single(errors);
        Assert.Contains("Did you mean", error);
        Assert.Contains("__conformance__", error);
    }

    [Fact]
    public void LayoutCaseMismatch_IsAnError()
    {
        // Ordinal on purpose: Windows' case-insensitive filesystem would accept "Alpha"
        // via Directory.Exists probing, and Linux CI would then silently exclude nothing.
        // The suggestion points at the correctly-cased directory.
        IReadOnlyList<string> errors = ExclusionValidator.Validate(
            _layoutsPath, [], ["Alpha"]);

        string error = Assert.Single(errors);
        Assert.Contains("alpha", error);
    }

    [Fact]
    public void UnknownLayoutInEmptyTree_IsAnError()
    {
        string emptyLayouts = Path.Combine(_root, "empty-layouts");
        Directory.CreateDirectory(emptyLayouts);

        IReadOnlyList<string> errors = ExclusionValidator.Validate(
            emptyLayouts, [], ["anything"]);

        Assert.Single(errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyLayoutValue_IsRejected(string value)
    {
        IReadOnlyList<string> errors = ExclusionValidator.Validate(
            _layoutsPath, [], [value]);

        Assert.Single(errors);
    }

    // ── --layout interplay ───────────────────────────────────────────────────

    [Fact]
    public void ExcludingTheScopedLayout_IsAContradiction()
    {
        IReadOnlyList<string> errors = ExclusionValidator.Validate(
            _layoutsPath, [], ["alpha"], scopedLayout: "alpha");

        string error = Assert.Single(errors);
        Assert.Contains("contradicts", error);
    }

    [Fact]
    public void ExcludingADifferentLayoutUnderScope_IsAllowed()
    {
        // A no-op for discovery (the scoped branch never enumerates layout dirs) but not
        // an error — Program.cs logs an informational notice instead.
        IReadOnlyList<string> errors = ExclusionValidator.Validate(
            _layoutsPath, [], ["__conformance__"], scopedLayout: "alpha");

        Assert.Empty(errors);
    }

    [Fact]
    public void MultipleInvalidValues_OneErrorEach()
    {
        IReadOnlyList<string> errors = ExclusionValidator.Validate(
            _layoutsPath, ["entites", "zzz"], ["nope"]);

        Assert.Equal(3, errors.Count);
    }
}
