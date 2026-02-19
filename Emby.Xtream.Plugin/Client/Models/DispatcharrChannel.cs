using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    /// <summary>
    /// Model for /api/channels/streams/ response items (stream stats).
    /// </summary>
    public class DispatcharrChannel
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("stream_id")]
        public int? StreamId { get; set; }

        [JsonPropertyName("stream_stats")]
        public StreamStatsInfo StreamStats { get; set; }
    }

    /// <summary>
    /// Channel object returned when fetching with include_streams=true.
    /// The stream_id field inside embedded stream sources is unreliable — for URL-based sources
    /// it contains the source's internal Dispatcharr ID rather than the Xtream provider stream_id.
    /// Both UUID and stats maps are therefore keyed by ch.Id, which is the value Dispatcharr's
    /// Xtream emulation always presents to Emby as the channel's stream_id.
    /// </summary>
    public class DispatcharrChannelWithStreams
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("uuid")]
        public string Uuid { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("streams")]
        public List<DispatcharrChannel> Streams { get; set; } = new List<DispatcharrChannel>();
    }
}
