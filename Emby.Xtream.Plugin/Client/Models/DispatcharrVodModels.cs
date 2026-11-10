using System.Collections.Generic;
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
    /// <summary>One episode from <c>/api/vod/episodes/{id}/</c>; only the series it belongs to is read.</summary>
    public class DispatcharrEpisodeDetail
    {
        public int Id { get; set; }
        public string Uuid { get; set; }
        public DispatcharrIdRef Series { get; set; }
    }

    public class DispatcharrIdRef
    {
        public int Id { get; set; }
    }

    /// <summary>One episode from <c>/api/vod/series/{id}/episodes/</c>, with every provider that has it.</summary>
    public class DispatcharrSeriesEpisode
    {
        public int Id { get; set; }
        public string Uuid { get; set; }
        public List<DispatcharrEpisodeProvider> Providers { get; set; }
    }

    public class DispatcharrEpisodeProvider
    {
        public int Id { get; set; }

        /// <summary>The provider's own stream id. A text field in Dispatcharr, so read as a string.</summary>
        [JsonPropertyName("stream_id")]
        [JsonConverter(typeof(TolerantStringConverter))]
        public string StreamId { get; set; }

        [JsonPropertyName("m3u_account")]
        public DispatcharrAccountRef Account { get; set; }
    }

    public class DispatcharrAccountRef
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int? Priority { get; set; }
    }
}
