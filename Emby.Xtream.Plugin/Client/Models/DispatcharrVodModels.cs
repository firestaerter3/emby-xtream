using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    public class DispatcharrVodMovieDetail
    {
        public int Id { get; set; }
        public string Uuid { get; set; }
    }

    public class DispatcharrVodProvider
    {
        public int Id { get; set; }

        [JsonPropertyName("stream_id")]
        public int StreamId { get; set; }

        public string Name { get; set; }
    }
}
