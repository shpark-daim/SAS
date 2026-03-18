using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace sas;
public class NanosecondsDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString()!;
        // 소수점 이하 9자리 → 7자리로 truncate
        var normalized = Regex.Replace(str, @"(\.\d{7})\d+", "$1");
        return DateTimeOffset.Parse(normalized, null, DateTimeStyles.RoundtripKind);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString("O"));
    }
}

public class DescriptionEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        foreach (var field in typeof(T).GetFields())
        {
            var attr = field.GetCustomAttribute<DescriptionAttribute>();
            if (attr?.Description == value)
                return (T)field.GetValue(null)!;
        }
        throw new JsonException($"Unknown value: {value}");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        var field = typeof(T).GetField(value.ToString());
        var attr = field?.GetCustomAttribute<DescriptionAttribute>();
        writer.WriteStringValue(attr?.Description ?? value.ToString());
    }
}