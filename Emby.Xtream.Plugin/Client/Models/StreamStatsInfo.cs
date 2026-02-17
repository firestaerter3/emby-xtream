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
        public int? Bitrate { get; set; }
    }
}
