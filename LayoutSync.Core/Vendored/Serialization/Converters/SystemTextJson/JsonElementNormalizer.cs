using System.Dynamic;
using System.Text.Json;

namespace coreConvention.Core.Serialization.Converters.SystemTextJson;

/// <summary>
/// Centralized utility for converting System.Text.Json JsonElement values to pure CLR types
/// (ExpandoObject, List&lt;object?&gt;, string, int, long, double, bool, null).
///
/// WHY THIS EXISTS:
/// Azure Durable Functions uses System.Text.Json for activity I/O serialization.
/// RavenDB uses Newtonsoft.Json for document storage. When an activity receives input
/// from an orchestrator, object? fields arrive as JsonElement. Calling
/// JsonSerializer.Deserialize&lt;ExpandoObject&gt;() leaves nested values as JsonElement,
/// which Newtonsoft.Json cannot serialize — causing runtime crashes on RavenDB writes.
/// This utility recursively converts all JsonElement values to pure CLR types,
/// ensuring zero JsonElement instances remain in the output.
///
/// RELATIONSHIP TO ExpandoObjectSystemTextJsonConverter:
/// That converter uses Utf8JsonReader (streaming ref struct) during STJ deserialization.
/// This normalizer uses JsonElement (in-memory DOM) for post-deserialization cleanup.
/// They solve the same logical problem but operate on different input types and cannot
/// share code without unnecessary round-trip overhead.
/// See: ExpandoObjectSystemTextJsonConverter.ReadValue() for the streaming equivalent.
/// </summary>
public static class JsonElementNormalizer
{
    /// <summary>
    /// Recursively convert a JsonElement into pure CLR types suitable for ExpandoObject storage.
    /// Objects become ExpandoObject, arrays become List&lt;object?&gt;,
    /// primitives become string/int/long/double/bool/null.
    ///
    /// Guarantees: zero JsonElement values remain in the output tree.
    /// </summary>
    public static object? Normalize(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => NormalizeToExpandoObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(Normalize).ToList(),
            JsonValueKind.String => element.GetString(),
            // 3-tier number handling matches ExpandoObjectSystemTextJsonConverter.ReadValue()
            JsonValueKind.Number when element.TryGetInt32(out int intVal) => intVal,
            JsonValueKind.Number when element.TryGetInt64(out long longVal) => longVal,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null // Undefined — treat as null
        };
    }

    /// <summary>
    /// Convert a JsonElement of Object kind to an ExpandoObject with recursively normalized property values.
    /// Ensures no JsonElement values leak into the ExpandoObject tree.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown if element is not JsonValueKind.Object.</exception>
    public static ExpandoObject NormalizeToExpandoObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                $"Expected JsonValueKind.Object but got {element.ValueKind}",
                nameof(element));
        }

        ExpandoObject expando = new();
        IDictionary<string, object?> dict = expando;

        foreach (JsonProperty prop in element.EnumerateObject())
        {
            dict[prop.Name] = Normalize(prop.Value);
        }

        return expando;
    }

    /// <summary>
    /// Convenience method for the common activity input pattern:
    /// convert an object? (which may be null, ExpandoObject, or JsonElement) to a non-null ExpandoObject.
    ///
    /// Behavior:
    /// - null → new ExpandoObject() (safe default for entity creation)
    /// - ExpandoObject → passthrough (already clean CLR types from RavenDB loading)
    /// - JsonElement → recursive normalization via NormalizeToExpandoObject()
    /// - anything else → round-trip through SerializeToElement then normalize
    ///
    /// Used by SaveDataActivity and similar activities that need guaranteed ExpandoObject output.
    /// For nullable output (e.g., SendMessageActivity), use a null check before calling this method.
    /// </summary>
    public static ExpandoObject NormalizeObjectToExpando(object? value)
    {
        return value switch
        {
            null => new ExpandoObject(),
            ExpandoObject expando => expando,
            JsonElement je => NormalizeToExpandoObject(je),
            // Fallback: round-trip through JsonElement for unknown types
            _ => NormalizeToExpandoObject(JsonSerializer.SerializeToElement(value))
        };
    }
}
