using System.Dynamic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace coreConvention.Core.Serialization.Converters.Newtonsoft;

/// <summary>
/// Newtonsoft.Json converter for ExpandoObject that preserves reference elements ($id, $ref).
///
/// This converter is intended for use when PreserveReferencesHandling = PreserveReferencesHandling.All/Object
/// in the JsonSerializerSettings. It maintains type references when JSON is deserialized into an ExpandoObject
/// that was serialized using a strongly typed model containing reference loops.
///
/// Reference: https://stackoverflow.com/a/23461179/1426342
///
/// Note: This converter is for specialized scenarios where reference preservation is required.
/// For standard ExpandoObject serialization without reference handling, use ExpandoObjectNewtonsoftConverter.
/// </summary>
public class ObjectReferenceExpandoObjectConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(ExpandoObject);
    }

    public override bool CanRead => true;
    public override bool CanWrite => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        throw new NotSupportedException(
            "ObjectReferenceExpandoObjectConverter is read-only. Use ExpandoObjectNewtonsoftConverter for writing."
        );
    }

    public override object ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer
    )
    {
        return ReadValue(serializer, reader);
    }

    private object ReadValue(JsonSerializer serializer, JsonReader reader)
    {
        while (reader.TokenType == JsonToken.Comment)
        {
            if (!reader.Read())
            {
                throw CreateJsonException(reader, "Unexpected end when reading ExpandoObject.");
            }
        }

        return reader.TokenType switch
        {
            JsonToken.StartObject => ReadObject(serializer, reader),
            JsonToken.StartArray => ReadList(serializer, reader),
            _ when IsPrimitiveToken(reader.TokenType) && reader.Value != null => reader.Value,
            _ => throw CreateJsonException(reader, "Unexpected token when converting ExpandoObject")
        };
    }

    private object ReadList(JsonSerializer serializer, JsonReader reader)
    {
        List<object> list = [];

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonToken.Comment:
                    break;

                case JsonToken.EndArray:
                    return list;

                default:
                    object value = ReadValue(serializer, reader);
                    list.Add(value);
                    break;
            }
        }

        throw CreateJsonException(reader, "Unexpected end when reading ExpandoObject.");
    }

    private object ReadObject(JsonSerializer serializer, JsonReader reader)
    {
        IDictionary<string, object?>? expandoObject = null;
        object? referenceObject = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonToken.PropertyName:
                    string propertyName = reader.Value?.ToString() ?? string.Empty;

                    if (!reader.Read())
                    {
                        throw new JsonSerializationException(
                            "Unexpected end when reading ExpandoObject."
                        );
                    }

                    object value = ReadValue(serializer, reader);

                    if (propertyName == "$ref")
                    {
                        string? id = value == null
                            ? null
                            : Convert.ToString(value, CultureInfo.InvariantCulture);
                        referenceObject = serializer.ReferenceResolver?.ResolveReference(
                            serializer,
                            id ?? string.Empty
                        );
                    }
                    else if (propertyName == "$id")
                    {
                        string? id = value == null
                            ? null
                            : Convert.ToString(value, CultureInfo.InvariantCulture);
                        serializer.ReferenceResolver?.AddReference(
                            serializer,
                            id ?? string.Empty,
                            expandoObject ??= new ExpandoObject()
                        );
                    }
                    else
                    {
                        (expandoObject ??= new ExpandoObject())[propertyName] = value;
                    }
                    break;

                case JsonToken.Comment:
                    break;

                case JsonToken.EndObject:
                    if (referenceObject != null && expandoObject != null)
                    {
                        throw CreateJsonException(
                            reader,
                            "ExpandoObject contained both $ref and real data"
                        );
                    }
                    return referenceObject ?? expandoObject ?? new ExpandoObject();
            }
        }

        throw CreateJsonException(reader, "Unexpected end when reading ExpandoObject.");
    }

    private static bool IsPrimitiveToken(JsonToken token)
    {
        return token switch
        {
            JsonToken.Integer => true,
            JsonToken.Float => true,
            JsonToken.String => true,
            JsonToken.Boolean => true,
            JsonToken.Undefined => true,
            JsonToken.Null => true,
            JsonToken.Date => true,
            JsonToken.Bytes => true,
            _ => false
        };
    }

    private static JsonSerializationException CreateJsonException(
        JsonReader reader,
        string format,
        params object[] args
    )
    {
        string? path = reader?.Path;
        string? message = string.Format(CultureInfo.InvariantCulture, format, args);

        if (!message.EndsWith(Environment.NewLine, StringComparison.Ordinal))
        {
            message = message.Trim();
            if (!message.EndsWith('.'))
            {
                message += ".";
            }
            message += " ";
        }

        message += string.Format(CultureInfo.InvariantCulture, "Path '{0}'", path);

        if (reader is IJsonLineInfo lineInfo && lineInfo.HasLineInfo())
        {
            message += string.Format(
                CultureInfo.InvariantCulture,
                ", line {0}, position {1}",
                lineInfo.LineNumber,
                lineInfo.LinePosition
            );
        }

        message += ".";

        return new JsonSerializationException(message);
    }
}
