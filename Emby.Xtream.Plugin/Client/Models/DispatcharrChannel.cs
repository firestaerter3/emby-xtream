using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    /// <summary>
    /// Wrapper for paginated Dispatcharr API responses.
    /// </summary>
    public class PaginatedResponse<T>
    {
        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("results")]
        public List<T> Results { get; set; } = new List<T>();
    }

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
    /// Model for /api/channels/channels/ response items (channel info with UUID).
    /// The Id field matches the Xtream emulation stream_id used by Emby.
    /// The Streams array lists underlying stream source IDs from /api/channels/streams/.
    /// </summary>
    public class DispatcharrChannelInfo
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("uuid")]
        public string Uuid { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("streams")]
        public List<int> Streams { get; set; } = new List<int>();
    }
}
