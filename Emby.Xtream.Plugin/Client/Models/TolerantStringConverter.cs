using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    /// <summary>
    /// Reads a JSON value into a string regardless of how the upstream provider typed it.
    ///
    /// Xtream providers are wildly inconsistent: a field documented as a string (e.g.
    /// <c>releasedate</c>, <c>rating</c>, <c>tmdb</c>) may arrive as a bare number, a boolean,
    /// <c>null</c>, or even an empty array/object. The default <see cref="string"/> deserializer
    /// throws <c>"The JSON value could not be converted to System.String"</c> on any of these,
    /// which aborts the entire series/VOD parse and fails the whole sync.
    ///
    /// This converter coerces scalars to their string form and quietly discards structured
    /// values, so one malformed field can never break a sync.
    /// </summary>
    internal sealed class TolerantStringConverter : System.Text.Json.Serialization.JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();
                case JsonTokenType.Number:
                    return reader.TryGetInt64(out var l)
                        ? l.ToString(CultureInfo.InvariantCulture)
                        : reader.GetDouble().ToString(CultureInfo.InvariantCulture);
                case JsonTokenType.True:
                    return "true";
                case JsonTokenType.False:
                    return "false";
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    // Some providers emit [] or {} where a scalar string is expected; skip it.
                    reader.Skip();
                    return null;
                default:
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }
}
