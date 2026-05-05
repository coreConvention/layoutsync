using System.Dynamic;
using Newtonsoft.Json;

namespace coreConvention.Core.Serialization.Converters.Newtonsoft;

/// <summary>
/// Newtonsoft.Json converter for ExpandoObject that ensures clean JSON output without $type metadata.
///
/// Problem: When ExpandoObject contains List&lt;object&gt; (created by System.Text.Json deserialization),
/// Newtonsoft.Json adds $type metadata even with TypeNameHandling.None because it sees polymorphic collections.
///
/// Solution: This converter explicitly writes ExpandoObject as plain JSON objects and arrays,
/// bypassing Newtonsoft's type inference that causes $type/$values wrapper objects.
///
/// Usage: Automatically registered by NewtonsoftJsonSerializer.
/// </summary>
public class ExpandoObjectNewtonsoftConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(ExpandoObject);
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value == null)
        {
            writer.WriteNull();
            return;
        }

        WriteValue(writer, value);
    }

    /// <summary>
    /// Recursively write any value as clean JSON without type metadata.
    /// This handles ExpandoObject, arrays/lists, and primitives.
    /// </summary>
    private static void WriteValue(JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull();
                break;

            case ExpandoObject expando:
                WriteExpandoObject(writer, expando);
                break;

            case IDictionary<string, object?> dict:
                WriteDictionary(writer, dict);
                break;

            case string str:
                // Handle string before IEnumerable (string implements IEnumerable<char>)
                writer.WriteValue(str);
                break;

            case IEnumerable<object?> enumerable:
                WriteArray(writer, enumerable);
                break;

            // Handle primitive types directly
            case bool b:
                writer.WriteValue(b);
                break;

            case int i:
                writer.WriteValue(i);
                break;

            case long l:
                writer.WriteValue(l);
                break;

            case double d:
                writer.WriteValue(d);
                break;

            case float f:
                writer.WriteValue(f);
                break;

            case decimal dec:
                writer.WriteValue(dec);
                break;

            case DateTime dt:
                writer.WriteValue(dt);
                break;

            case DateTimeOffset dto:
                writer.WriteValue(dto);
                break;

            case Guid g:
                writer.WriteValue(g);
                break;

            default:
                // For any other type, write as raw value
                // This handles remaining primitives and enums
                writer.WriteValue(value);
                break;
        }
    }

    private static void WriteExpandoObject(JsonWriter writer, ExpandoObject expando)
    {
        IDictionary<string, object?> dict = expando;
        WriteDictionary(writer, dict);
    }

    private static void WriteDictionary(JsonWriter writer, IDictionary<string, object?> dict)
    {
        writer.WriteStartObject();

        foreach (KeyValuePair<string, object?> kvp in dict)
        {
            writer.WritePropertyName(kvp.Key);
            WriteValue(writer, kvp.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteArray(JsonWriter writer, IEnumerable<object?> enumerable)
    {
        writer.WriteStartArray();

        foreach (object? item in enumerable)
        {
            WriteValue(writer, item);
        }

        writer.WriteEndArray();
    }

    public override object? ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer
    )
    {
        return ReadValue(reader);
    }

    private static object? ReadValue(JsonReader reader)
    {
        while (reader.TokenType == JsonToken.Comment)
        {
            if (!reader.Read())
            {
                throw new JsonSerializationException("Unexpected end when reading ExpandoObject.");
            }
        }

        return reader.TokenType switch
        {
            JsonToken.StartObject => ReadObjectOrWrappedArray(reader),
            JsonToken.StartArray => ReadArray(reader),
            JsonToken.Integer => reader.Value is long l ? l : Convert.ToInt64(reader.Value),
            JsonToken.Float => reader.Value is double d ? d : Convert.ToDouble(reader.Value),
            JsonToken.String => reader.Value?.ToString(),
            JsonToken.Boolean => Convert.ToBoolean(reader.Value),
            JsonToken.Null => null,
            JsonToken.Undefined => null,
            JsonToken.Date => reader.Value,
            JsonToken.Bytes => reader.Value,
            _ => throw new JsonSerializationException($"Unexpected token type: {reader.TokenType}")
        };
    }

    /// <summary>
    /// Reads an object that might be either a real object or a Newtonsoft wrapped array.
    /// Wrapped arrays look like: { "$type": "System.Collections.Generic.List`1...", "$values": [...] }
    /// If it's a wrapped array, return the array content. Otherwise return an ExpandoObject.
    /// </summary>
    private static object? ReadObjectOrWrappedArray(JsonReader reader)
    {
        ExpandoObject expando = new();
        IDictionary<string, object?> dict = expando;
        object? valuesContent = null;
        bool hasTypeMetadata = false;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonToken.PropertyName:
                    string propertyName = reader.Value?.ToString() ?? string.Empty;

                    if (!reader.Read())
                    {
                        throw new JsonSerializationException(
                            "Unexpected end when reading object."
                        );
                    }

                    // Handle Newtonsoft's array wrapper: { "$type": "...", "$values": [...] }
                    if (propertyName == "$type")
                    {
                        hasTypeMetadata = true;
                        ReadValue(reader); // Consume and discard the $type value
                        continue;
                    }

                    if (propertyName == "$values")
                    {
                        // This is a wrapped array - capture its content
                        valuesContent = ReadValue(reader);
                        continue;
                    }

                    // Skip other $ metadata properties ($id, $ref, etc.)
                    if (propertyName.StartsWith('$'))
                    {
                        ReadValue(reader);
                        continue;
                    }

                    dict[propertyName] = ReadValue(reader);
                    break;

                case JsonToken.Comment:
                    break;

                case JsonToken.EndObject:
                    // If this was a wrapped array ({ "$type": "...", "$values": [...] }),
                    // return the array content instead of an empty ExpandoObject
                    if (hasTypeMetadata && valuesContent != null && dict.Count == 0)
                    {
                        return valuesContent;
                    }
                    return expando;
            }
        }

        throw new JsonSerializationException("Unexpected end when reading object.");
    }

    private static List<object?> ReadArray(JsonReader reader)
    {
        List<object?> list = [];

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonToken.Comment:
                    break;

                case JsonToken.EndArray:
                    return list;

                default:
                    list.Add(ReadValue(reader));
                    break;
            }
        }

        throw new JsonSerializationException("Unexpected end when reading ExpandoObject array.");
    }
}
