using System.Text.Json;
using System.Text.Json.Serialization;
using global::Newtonsoft.Json.Linq;

namespace coreConvention.Core.Serialization.Converters.SystemTextJson;

/// <summary>
/// JsonConverterFactory that bridges Newtonsoft.Json JToken types to System.Text.Json serialization.
///
/// Problem: RavenDB uses Newtonsoft.Json internally for document deserialization. For Dictionary&lt;string, object&gt;
/// properties (like Entity.Indexes), arrays are deserialized as JArray and primitives as JValue.
/// When the Azure Functions response serializer (System.Text.Json) encounters these JToken types,
/// it sees JToken's IEnumerable&lt;JToken&gt; interface and serializes each node as an array of its children.
/// JValue (leaf node, zero children) becomes [] instead of the actual value.
///
/// Solution: This converter intercepts JToken serialization and writes the correct JSON by converting
/// the JToken to its JSON string representation, then writing it through System.Text.Json.
///
/// Affects: Entity.Indexes (Dictionary&lt;string, object&gt;) — the only property path where
/// Newtonsoft JToken types survive into STJ serialization.
/// Does NOT affect: Entity.Data (ExpandoObject) — handled by ExpandoObjectSystemTextJsonConverter.
/// </summary>
public class JTokenSystemTextJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeof(JToken).IsAssignableFrom(typeToConvert);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        return new JTokenConverter();
    }

    private sealed class JTokenConverter : JsonConverter<JToken>
    {
        public override JToken Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // JToken deserialization from STJ is not needed — RavenDB handles loading via Newtonsoft
            throw new NotSupportedException(
                "Reading JToken from System.Text.Json is not supported. " +
                "JToken types are only expected during serialization of RavenDB-loaded entities."
            );
        }

        public override void Write(Utf8JsonWriter writer, JToken value, JsonSerializerOptions options)
        {
            // Convert JToken to JSON string via Newtonsoft, then parse with STJ and write.
            // This bridges the Newtonsoft → STJ gap correctly for all JToken types:
            // JValue("hello") → "hello", JArray["a","b"] → ["a","b"], JObject → {...}
            string json = value.ToString(global::Newtonsoft.Json.Formatting.None);
            using JsonDocument doc = JsonDocument.Parse(json);
            doc.RootElement.WriteTo(writer);
        }
    }
}
