using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    /// <summary>
    /// Reads a JSON value into a nullable <see cref="int"/> regardless of how the upstream
    /// provider typed it.
    ///
    /// This is the integer sibling of <see cref="TolerantStringConverter"/>. Xtream providers
    /// type the same field inconsistently: a nominally-integer field such as <c>category_id</c>
    /// may arrive as a bare number, a numeric string, an empty string, a non-numeric string,
    /// <c>null</c>, or even an array/object. The default <c>int?</c> deserializer throws
    /// <c>"The JSON value could not be converted to System.Nullable`1[System.Int32]"</c> on
    /// anything but a number (or, with <c>AllowReadingFromString</c>, a strictly-numeric string),
    /// which aborts the entire series/VOD parse and fails the whole sync.
    ///
    /// This converter coerces what it can to an <c>int</c> and treats everything else as
    /// <c>null</c>, so one malformed field can never break a sync. <c>null</c> is the correct
    /// degraded value: <c>category_id</c> is already optional and is only used for folder
    /// grouping, so a missing value simply falls back to the uncategorised bucket.
    /// </summary>
    internal sealed class TolerantNullableIntConverter : System.Text.Json.Serialization.JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    return reader.TryGetInt32(out var i)
                        ? i
                        : reader.TryGetDouble(out var d) ? (int?)(int)d : null;
                case JsonTokenType.String:
                    var s = reader.GetString();
                    return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl)
                            ? (int?)(int)dbl
                            : null;
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    // Some providers emit [] or {} where a scalar int is expected; skip it.
                    reader.Skip();
                    return null;
                default:
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                writer.WriteNumberValue(value.Value);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}
