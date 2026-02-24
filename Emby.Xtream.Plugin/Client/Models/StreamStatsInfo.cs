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
        public string AudioChannels { get; set; }

        [JsonPropertyName("audio_bitrate")]
        public double? AudioBitrate { get; set; }

        [JsonPropertyName("sample_rate")]
        public int? SampleRate { get; set; }
    }
}
