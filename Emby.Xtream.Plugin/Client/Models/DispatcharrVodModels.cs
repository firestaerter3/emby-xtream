using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    public class DispatcharrVodMovieDetail
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("uuid")]
        public string Uuid { get; set; }
    }

    public class DispatcharrVodProvider
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("stream_id")]
        public int StreamId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }
    }
}
