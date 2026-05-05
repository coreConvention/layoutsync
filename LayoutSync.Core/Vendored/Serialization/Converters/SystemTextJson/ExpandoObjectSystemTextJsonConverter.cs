using System.Dynamic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace coreConvention.Core.Serialization.Converters.SystemTextJson;

/// <summary>
/// Custom JsonConverter for ExpandoObject to support System.Text.Json serialization.
///
/// Problem: System.Text.Json does not natively support ExpandoObject - it serializes correctly
/// but deserializes nested objects as JsonElement instead of ExpandoObject.
///
/// Solution: This converter ensures proper round-trip serialization/deserialization
/// by converting nested objects to ExpandoObject during deserialization.
///
/// Usage: Automatically registered by SystemTextJsonSerializer.
///
/// Related: JsonElementNormalizer performs the same logical conversion (JsonElement → CLR types)
/// using the in-memory JsonElement DOM API. This converter uses Utf8JsonReader (streaming ref struct)
/// for STJ deserialization pipeline integration. Both must exist — they serve different entry points
/// but ensure the same guarantee: zero JsonElement values in the output ExpandoObject tree.
/// </summary>
public class ExpandoObjectSystemTextJsonConverter : JsonConverter<ExpandoObject>
{
    public override ExpandoObject? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected StartObject token");
        }

        return ReadObject(ref reader);
    }

    public override void Write(
        Utf8JsonWriter writer,
        ExpandoObject value,
        JsonSerializerOptions options
    )
    {
        // System.Text.Json handles ExpandoObject serialization correctly via IDictionary<string, object>
        // We just need to serialize it as a JSON object
        JsonSerializer.Serialize(writer, (IDictionary<string, object?>)value, options);
    }

    /// <summary>
    /// Recursively read JSON object and convert to ExpandoObject.
    /// Nested objects are also converted to ExpandoObject (not JsonElement).
    /// </summary>
    private static ExpandoObject ReadObject(ref Utf8JsonReader reader)
    {
        ExpandoObject expando = new();
        IDictionary<string, object?> dictionary = expando;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return expando;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected PropertyName token");
            }

            string propertyName = reader.GetString()
                ?? throw new JsonException("Property name cannot be null");

            reader.Read();
            object? value = ReadValue(ref reader);
            dictionary[propertyName] = value;
        }

        throw new JsonException("Unexpected end of JSON");
    }

    /// <summary>
    /// Read a JSON value and convert to appropriate .NET type.
    /// Objects become ExpandoObject, arrays become List&lt;object&gt;, primitives stay as-is.
    /// </summary>
    private static object? ReadValue(ref Utf8JsonReader reader)
    {
        return reader.TokenType switch
        {
            JsonTokenType.StartObject => ReadObject(ref reader),
            JsonTokenType.StartArray => ReadArray(ref reader),
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when reader.TryGetInt32(out int intValue) => intValue,
            JsonTokenType.Number when reader.TryGetInt64(out long longValue) => longValue,
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Unexpected token type: {reader.TokenType}")
        };
    }

    /// <summary>
    /// Read JSON array and convert to List&lt;object&gt;.
    /// </summary>
    private static List<object?> ReadArray(ref Utf8JsonReader reader)
    {
        List<object?> list = [];

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return list;
            }

            object? value = ReadValue(ref reader);
            list.Add(value);
        }

        throw new JsonException("Unexpected end of JSON array");
    }
}
