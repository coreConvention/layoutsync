using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Newtonsoft.Json;

namespace coreConvention.Core.Serialization.Converters.Newtonsoft;

/// <summary>
/// Newtonsoft.Json converter that serializes enum values using their [Display] attribute name.
///
/// Example:
/// <code>
/// public enum Status
/// {
///     [Display(Name = "In Progress")]
///     InProgress,
///
///     [Display(Name = "Completed")]
///     Done
/// }
///
/// // Serializes as: "In Progress" instead of "InProgress"
/// </code>
///
/// Note: This converter only supports writing (serialization).
/// Reading (deserialization) is not implemented.
/// </summary>
public class DisplayEnumConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        return objectType.IsEnum;
    }

    public override bool CanRead => false;
    public override bool CanWrite => true;

    public override object ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer
    )
    {
        throw new NotSupportedException(
            "DisplayEnumConverter only supports serialization, not deserialization. " +
            "Use StringEnumConverter for two-way enum conversion."
        );
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value == null)
        {
            writer.WriteNull();
            return;
        }

        Enum enumValue = (Enum)value;
        MemberInfo[] memberInfo = enumValue.GetType().GetMember(enumValue.ToString());

        if (memberInfo.Length > 0)
        {
            DisplayAttribute? displayAttribute = memberInfo[0].GetCustomAttribute<DisplayAttribute>();

            if (displayAttribute != null && !string.IsNullOrEmpty(displayAttribute.Name))
            {
                writer.WriteValue(displayAttribute.Name);
                return;
            }
        }

        // Fallback to enum value name
        writer.WriteValue(value.ToString());
    }
}
