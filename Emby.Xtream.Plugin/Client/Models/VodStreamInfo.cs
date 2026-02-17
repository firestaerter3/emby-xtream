using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    public class VodStreamInfo
    {
        [JsonPropertyName("stream_id")]
        public int StreamId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("stream_icon")]
        public string StreamIcon { get; set; } = string.Empty;

        [JsonPropertyName("rating")]
        public string Rating { get; set; } = string.Empty;

        [JsonPropertyName("category_id")]
        public int? CategoryId { get; set; }

        [JsonPropertyName("container_extension")]
        public string ContainerExtension { get; set; } = "mp4";

        [JsonPropertyName("added")]
        public long Added { get; set; }

        [JsonPropertyName("tmdb_id")]
        public string TmdbId { get; set; } = string.Empty;
    }
}
