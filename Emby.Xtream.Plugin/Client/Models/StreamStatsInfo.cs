using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    /// <summary>
    /// Reads a JSON value that may be a string ("5.1", "stereo") or a bare number (2, 6)
    /// and always surfaces it as a string. Dispatcharr versions differ in which they emit.
    /// </summary>
    internal sealed class StringOrNumberAsStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
                return reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString();
            if (reader.TokenType == JsonTokenType.String)
                return reader.GetString();
            if (reader.TokenType == JsonTokenType.Null)
                return null;
            throw new JsonException($"Unexpected token {reader.TokenType} for string field.");
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }

    public class StreamStatsInfo
    {
        [JsonPropertyName("resolution")]
        public string Resolution { get; set; }

        [JsonPropertyName("video_codec")]
        public string VideoCodec { get; set; }

        [JsonPropertyName("audio_codec")]
        public string AudioCodec { get; set; }

        [JsonPropertyName("source_fps")]
        public double? SourceFps { get; set; }

        [JsonPropertyName("ffmpeg_output_bitrate")]
        public double? Bitrate { get; set; }

        [JsonPropertyName("audio_channels")]
        [JsonConverter(typeof(StringOrNumberAsStringConverter))]
        public string AudioChannels { get; set; }

        [JsonPropertyName("audio_bitrate")]
        public double? AudioBitrate { get; set; }

        [JsonPropertyName("sample_rate")]
        public int? SampleRate { get; set; }

        [JsonPropertyName("video_profile")]
        public string VideoProfile { get; set; }

        [JsonPropertyName("video_level")]
        public int? VideoLevel { get; set; }

        [JsonPropertyName("video_bit_depth")]
        public int? VideoBitDepth { get; set; }

        [JsonPropertyName("video_ref_frames")]
        public int? VideoRefFrames { get; set; }

        [JsonPropertyName("audio_language")]
        public string AudioLanguage { get; set; }
    }
}
