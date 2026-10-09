using System.Globalization;
using System.Text.Json.Nodes;
using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Unit tests for <see cref="RelativeDateResolver"/>.
/// All tests use fixed reference dates so assertions are deterministic: a whole-second one for the
/// arithmetic, and one with sub-millisecond ticks for the output format (issue #48).
/// </summary>
public class RelativeDateResolverTests
{
    // Fixed reference: 2026-04-23 12:00:00 UTC
    private static readonly DateTime Reference = new(2026, 4, 23, 12, 0, 0, DateTimeKind.Utc);

    // 2026-10-16 19:10:51.1234567 UTC. The ticks below the millisecond are the ones .NET's "o"
    // format used to write and a browser's toISOString() never can (issue #48).
    private static readonly DateTime SubMillisecondReference =
        new DateTime(2026, 10, 16, 19, 10, 51, 123, DateTimeKind.Utc).AddTicks(4567);

    // The only shape toISOString() produces for years 0-9999 (DateTime's whole range).
    private const string MillisecondIsoPattern = @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$";

    private static RelativeDateResolver CreateResolver()
        => new(NullLogger<RelativeDateResolver>.Instance);

    // ── IsRelativeDate ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("+3d", true)]
    [InlineData("+2w", true)]
    [InlineData("+1m", true)]
    [InlineData("-5d", true)]
    [InlineData("-3w", true)]
    [InlineData("-2m", true)]
    [InlineData("7d",  true)]  // sign optional — treated as positive
    [InlineData("NOW", true)]  // case-insensitive
    [InlineData("now", true)]
    [InlineData("2026-04-26T09:00:00Z", false)] // ISO timestamp — not relative
    [InlineData("2025-01-01T00:00:00.0000000Z", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("+3y", false)] // 'y' is not a supported unit
    [InlineData("tomorrow", false)]
    [InlineData("today", false)]
    public void IsRelativeDate_RecognizesPatternCorrectly(string value, bool expected)
    {
        Assert.Equal(expected, RelativeDateResolver.IsRelativeDate(value));
    }

    // ── IsDateFieldName ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("date", true)]
    [InlineData("startDate", true)]
    [InlineData("endDate", true)]
    [InlineData("startTime", true)]
    [InlineData("endTime", true)]
    [InlineData("eventDate", true)]
    [InlineData("scheduledDate", true)]
    [InlineData("publishedDate", true)]
    [InlineData("registrationDeadline", true)]
    [InlineData("title", false)]
    [InlineData("description", false)]
    [InlineData("location", false)]
    [InlineData("maxParticipants", false)]
    public void IsDateFieldName_RecognizesKnownFields(string fieldName, bool expected)
    {
        Assert.Equal(expected, RelativeDateResolver.IsDateFieldName(fieldName));
    }

    // ── Resolve (single expression) ───────────────────────────────────────────

    [Fact]
    public void Resolve_Now_ReturnsReferenceInstant()
    {
        RelativeDateResolver resolver = CreateResolver();
        string? result = resolver.Resolve("now", Reference);
        Assert.NotNull(result);
        // Should round-trip back to the same instant
        DateTime parsed = DateTime.Parse(result, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(Reference, parsed);
    }

    [Fact]
    public void Resolve_PositiveDays_AddsCorrectly()
    {
        RelativeDateResolver resolver = CreateResolver();
        string? result = resolver.Resolve("+3d", Reference);
        Assert.NotNull(result);
        DateTime parsed = DateTime.Parse(result, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(Reference.AddDays(3), parsed);
    }

    [Fact]
    public void Resolve_NegativeDays_SubtractsCorrectly()
    {
        RelativeDateResolver resolver = CreateResolver();
        string? result = resolver.Resolve("-5d", Reference);
        Assert.NotNull(result);
        DateTime parsed = DateTime.Parse(result, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(Reference.AddDays(-5), parsed);
    }

    [Fact]
    public void Resolve_Weeks_EquivalentToSevenDayMultiple()
    {
        RelativeDateResolver resolver = CreateResolver();
        string? result = resolver.Resolve("+2w", Reference);
        Assert.NotNull(result);
        DateTime parsed = DateTime.Parse(result, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(Reference.AddDays(14), parsed);
    }

    [Fact]
    public void Resolve_Months_UsesCalendarMonth()
    {
        RelativeDateResolver resolver = CreateResolver();
        // 2026-04-23 + 1 month = 2026-05-23
        string? result = resolver.Resolve("+1m", Reference);
        Assert.NotNull(result);
        DateTime parsed = DateTime.Parse(result, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc), parsed);
    }

    [Fact]
    public void Resolve_MonthBoundary_HandlesMonthEnd()
    {
        // 2026-01-31 + 1 month = 2026-02-28 (February, non-leap year)
        DateTime monthEnd = new(2026, 1, 31, 12, 0, 0, DateTimeKind.Utc);
        RelativeDateResolver resolver = CreateResolver();
        string? result = resolver.Resolve("+1m", monthEnd);
        Assert.NotNull(result);
        DateTime parsed = DateTime.Parse(result, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(new DateTime(2026, 2, 28, 12, 0, 0, DateTimeKind.Utc), parsed);
    }

    [Fact]
    public void Resolve_NegativeMonths_SubtractsCorrectly()
    {
        RelativeDateResolver resolver = CreateResolver();
        // 2026-04-23 - 2 months = 2026-02-23
        string? result = resolver.Resolve("-2m", Reference);
        Assert.NotNull(result);
        DateTime parsed = DateTime.Parse(result, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(new DateTime(2026, 2, 23, 12, 0, 0, DateTimeKind.Utc), parsed);
    }

    [Fact]
    public void Resolve_ImplicitPositiveSign_TreatedAsPositive()
    {
        RelativeDateResolver resolver = CreateResolver();
        // "7d" without sign should equal "+7d"
        string? withSign = resolver.Resolve("+7d", Reference);
        string? withoutSign = resolver.Resolve("7d", Reference);
        Assert.Equal(withSign, withoutSign);
    }

    [Fact]
    public void Resolve_CaseInsensitiveUnit_Works()
    {
        RelativeDateResolver resolver = CreateResolver();
        string? lower = resolver.Resolve("+3d", Reference);
        string? upper = resolver.Resolve("+3D", Reference);
        Assert.Equal(lower, upper);
    }

    [Fact]
    public void Resolve_NonRelativeExpression_ReturnsNull()
    {
        RelativeDateResolver resolver = CreateResolver();
        Assert.Null(resolver.Resolve("2026-04-26T09:00:00Z", Reference));
        Assert.Null(resolver.Resolve("", Reference));
        Assert.Null(resolver.Resolve("tomorrow", Reference));
    }

    // ── Output format (issue #48) ─────────────────────────────────────────────

    [Theory]
    [InlineData("now", "2026-10-16T19:10:51.123Z")]
    [InlineData("+3d", "2026-10-19T19:10:51.123Z")]
    [InlineData("-5d", "2026-10-11T19:10:51.123Z")]
    [InlineData("+2w", "2026-10-30T19:10:51.123Z")]
    [InlineData("+1m", "2026-11-16T19:10:51.123Z")]
    [InlineData("-2m", "2026-08-16T19:10:51.123Z")]
    public void Resolve_WritesMillisecondIsoUtc(string expression, string expected)
    {
        // Exactly the text toISOString() gives for the same instant: three fractional digits, 'Z'.
        Assert.Equal(expected, CreateResolver().Resolve(expression, SubMillisecondReference));
    }

    [Fact]
    public void Resolve_WholeSecondReference_StillWritesThreeFractionalDigits()
    {
        Assert.Equal("2026-04-23T12:00:00.000Z", CreateResolver().Resolve("now", Reference));
    }

    [Fact]
    public void Resolve_TruncatesSubMillisecondTicks_NeverRoundsUp()
    {
        // 23:59:59.9999999 on the last day of the year: rounding would carry into the next year.
        DateTime lastTick = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_999_999);
        RelativeDateResolver resolver = CreateResolver();

        Assert.Equal("2026-12-31T23:59:59.999Z", resolver.Resolve("now", lastTick));
        Assert.Equal("2027-01-01T23:59:59.999Z", resolver.Resolve("+1d", lastTick));
    }

    [Fact]
    public void Resolve_LocalKindReference_IsConvertedToUtcBeforeTheOffsetIsAdded()
    {
        DateTime local = SubMillisecondReference.ToLocalTime();
        RelativeDateResolver resolver = CreateResolver();

        Assert.Equal("2026-10-16T19:10:51.123Z", resolver.Resolve("now", local));
        // Three weeks crosses the end of daylight saving time in zones that observe it (EU 25 Oct,
        // US 1 Nov 2026). Adding in local time would move the UTC result by the DST change, so the
        // output would depend on the machine's zone.
        Assert.Equal("2026-11-06T19:10:51.123Z", resolver.Resolve("+3w", local));
    }

    [Fact]
    public void Resolve_UnspecifiedKindReference_IsTakenAsUtc()
    {
        // The parameter is documented as UTC. ToUniversalTime() would treat Unspecified as local
        // and shift it by the machine's offset.
        DateTime unspecified = DateTime.SpecifyKind(SubMillisecondReference, DateTimeKind.Unspecified);
        Assert.Equal("2026-10-16T19:10:51.123Z", CreateResolver().Resolve("now", unspecified));
    }

    [Fact]
    public void Resolve_IgnoresCurrentCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            // th-TH formats dates in the Thai Buddhist calendar, where 2026 is 2569.
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            Assert.Equal("2026-10-16T19:10:51.123Z", CreateResolver().Resolve("now", SubMillisecondReference));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("now")]
    [InlineData("+1d")]
    public void Resolve_DefaultReference_WritesMillisecondIsoUtc(string expression)
    {
        Assert.Matches(MillisecondIsoPattern, CreateResolver().Resolve(expression));
    }

    // ── ResolveInDocument ─────────────────────────────────────────────────────

    [Fact]
    public void ResolveInDocument_ResolvesTopLevelDateFields()
    {
        RelativeDateResolver resolver = CreateResolver();
        JsonObject doc = new()
        {
            ["date"] = "+3d",
            ["title"] = "Test Event"
        };

        resolver.ResolveInDocument(doc, Reference);

        // "date" should be resolved; "title" should be untouched
        string? resolvedDate = doc["date"]?.GetValue<string>();
        Assert.NotNull(resolvedDate);
        Assert.False(RelativeDateResolver.IsRelativeDate(resolvedDate), "date should now be ISO, not relative");

        string? title = doc["title"]?.GetValue<string>();
        Assert.Equal("Test Event", title);
    }

    [Fact]
    public void ResolveInDocument_ResolvesNestedDateFields()
    {
        RelativeDateResolver resolver = CreateResolver();
        JsonObject doc = new()
        {
            ["data"] = new JsonObject
            {
                ["startDate"] = "+7d",
                ["endDate"] = "+9d",
                ["title"] = "Nested Event"
            }
        };

        resolver.ResolveInDocument(doc, Reference);

        JsonObject data = doc["data"]!.AsObject();
        string? startDate = data["startDate"]?.GetValue<string>();
        string? endDate = data["endDate"]?.GetValue<string>();

        Assert.NotNull(startDate);
        Assert.NotNull(endDate);
        Assert.False(RelativeDateResolver.IsRelativeDate(startDate));
        Assert.False(RelativeDateResolver.IsRelativeDate(endDate));
        Assert.Equal("Nested Event", data["title"]?.GetValue<string>());

        // End should be 2 days after start
        DateTime start = DateTime.Parse(startDate, null, System.Globalization.DateTimeStyles.RoundtripKind);
        DateTime end = DateTime.Parse(endDate, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(2, (int)(end - start).TotalDays);
    }

    [Fact]
    public void ResolveInDocument_LeavesIsoTimestampsUnchanged()
    {
        RelativeDateResolver resolver = CreateResolver();
        const string existingIso = "2026-05-15T09:00:00.0000000Z";
        JsonObject doc = new()
        {
            ["date"] = existingIso
        };

        resolver.ResolveInDocument(doc, Reference);

        // ISO strings are not relative — they must be left verbatim
        Assert.Equal(existingIso, doc["date"]?.GetValue<string>());
    }

    [Fact]
    public void ResolveInDocument_NonDateFieldsAreIgnored()
    {
        RelativeDateResolver resolver = CreateResolver();
        JsonObject doc = new()
        {
            // "description" is not a recognized date field
            ["description"] = "+3d",
            ["title"] = "+1w"
        };

        resolver.ResolveInDocument(doc, Reference);

        // Values should be unchanged because the field names are not date fields
        Assert.Equal("+3d", doc["description"]?.GetValue<string>());
        Assert.Equal("+1w", doc["title"]?.GetValue<string>());
    }

    [Fact]
    public void ResolveInDocument_AllDatesAnchoredToSameInstant()
    {
        // When multiple date fields are resolved in one document, all should be
        // calculated from the same reference (syncInstant), not from incrementing wall clock.
        RelativeDateResolver resolver = CreateResolver();
        JsonObject doc = new()
        {
            ["startDate"] = "+0d",
            ["endDate"] = "+3d"
        };

        resolver.ResolveInDocument(doc, Reference);

        DateTime start = DateTime.Parse(
            doc["startDate"]!.GetValue<string>(),
            null, System.Globalization.DateTimeStyles.RoundtripKind);
        DateTime end = DateTime.Parse(
            doc["endDate"]!.GetValue<string>(),
            null, System.Globalization.DateTimeStyles.RoundtripKind);

        Assert.Equal(Reference, start);
        Assert.Equal(Reference.AddDays(3), end);
    }

    [Fact]
    public void ResolveInDocument_HandlesArrayOfObjects()
    {
        RelativeDateResolver resolver = CreateResolver();
        JsonObject doc = new()
        {
            ["sessions"] = new JsonArray(
                new JsonObject { ["startDate"] = "+1d", ["name"] = "Session A" },
                new JsonObject { ["startDate"] = "+2d", ["name"] = "Session B" }
            )
        };

        resolver.ResolveInDocument(doc, Reference);

        JsonArray sessions = doc["sessions"]!.AsArray();
        foreach (JsonNode? session in sessions)
        {
            JsonObject item = session!.AsObject();
            string? startDate = item["startDate"]?.GetValue<string>();
            Assert.NotNull(startDate);
            Assert.False(RelativeDateResolver.IsRelativeDate(startDate),
                $"Expected resolved ISO but got: {startDate}");
        }
    }

    [Fact]
    public void ResolveInDocument_WritesMillisecondIsoUtcAtEveryDepth()
    {
        JsonObject doc = new()
        {
            ["date"] = "now",
            ["data"] = new JsonObject { ["startDate"] = "+1d" },
            ["sessions"] = new JsonArray(new JsonObject { ["endDate"] = "-1w" }),
        };

        CreateResolver().ResolveInDocument(doc, SubMillisecondReference);

        Assert.Equal("2026-10-16T19:10:51.123Z", doc["date"]!.GetValue<string>());
        Assert.Equal("2026-10-17T19:10:51.123Z", doc["data"]!["startDate"]!.GetValue<string>());
        Assert.Equal("2026-10-09T19:10:51.123Z", doc["sessions"]![0]!["endDate"]!.GetValue<string>());
    }

    [Fact]
    public void ResolveInDocument_DefaultReference_WritesMillisecondIsoUtc()
    {
        JsonObject doc = new() { ["date"] = "+3d" };

        CreateResolver().ResolveInDocument(doc);

        Assert.Matches(MillisecondIsoPattern, doc["date"]!.GetValue<string>());
    }

    [Fact]
    public void ResolveInDocument_EmptyDocumentDoesNotThrow()
    {
        RelativeDateResolver resolver = CreateResolver();
        JsonObject doc = new();
        // Should complete without exception
        resolver.ResolveInDocument(doc, Reference);
        Assert.Empty(doc);
    }

    [Fact]
    public void ResolveInDocument_NullStringValueDoesNotThrow()
    {
        RelativeDateResolver resolver = CreateResolver();
        JsonObject doc = new()
        {
            ["date"] = JsonValue.Create<string?>(null)
        };
        // Null value in a date field should be safely skipped
        resolver.ResolveInDocument(doc, Reference);
        Assert.True(doc.ContainsKey("date")); // key still present, value unchanged
    }
}
