using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
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
        [JsonConverter(typeof(TolerantStringConverter))]
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
