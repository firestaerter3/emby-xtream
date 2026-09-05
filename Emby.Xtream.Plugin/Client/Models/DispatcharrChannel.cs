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
    /// UUID, tvg-id, station-id, and stats maps are keyed by stream.StreamId
    /// (the Xtream provider's stream_id from the embedded stream objects), which is
    /// what Emby stores and uses for playback lookups. ch.Id is Dispatcharr's own
    /// internal channel ID and does not match the Xtream stream_id.
    /// </summary>
    public class DispatcharrChannelWithStreams
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("uuid")]
        public string Uuid { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("channel_number")]
        public double? ChannelNumber { get; set; }

        [JsonPropertyName("tvg_id")]
        public string TvgId { get; set; }

        [JsonPropertyName("tvc_guide_stationid")]
        public string TvcGuideStationId { get; set; }

        [JsonPropertyName("streams")]
        public List<DispatcharrChannel> Streams { get; set; } = new List<DispatcharrChannel>();

        // Which stream profile decides what leaves the proxy for this channel. Dispatcharr
        // resolves an override before the channel's own value, and exposes the result as
        // effective_stream_profile_id on newer serializers; older ones only carry
        // stream_profile_id. Prefer the effective value where present, fall back to the
        // plain one, and treat both being absent as "use the server default profile".
        [JsonPropertyName("stream_profile_id")]
        [JsonConverter(typeof(TolerantNullableIntConverter))]
        public int? StreamProfileId { get; set; }

        [JsonPropertyName("effective_stream_profile_id")]
        [JsonConverter(typeof(TolerantNullableIntConverter))]
        public int? EffectiveStreamProfileId { get; set; }

        /// <summary>Profile that actually governs this channel, or null to use the server default.</summary>
        public int? ResolvedStreamProfileId => EffectiveStreamProfileId ?? StreamProfileId;
    }

    /// <summary>
    /// Represents a Dispatcharr Channel Profile.
    /// The <see cref="Channels"/> list contains only Dispatcharr channel IDs where the membership is enabled.
    /// </summary>
    public class DispatcharrProfile
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("channels")]
        public List<int> Channels { get; set; } = new List<int>();
    }
}
