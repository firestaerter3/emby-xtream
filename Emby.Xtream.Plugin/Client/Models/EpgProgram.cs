using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    public class EpgProgram
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("epg_id")]
        public string EpgId { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("lang")]
        public string Language { get; set; } = string.Empty;

        [JsonPropertyName("start")]
        public string Start { get; set; } = string.Empty;

        [JsonPropertyName("end")]
        public string End { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("channel_id")]
        public string ChannelId { get; set; } = string.Empty;

        [JsonPropertyName("start_timestamp")]
        public long StartTimestamp { get; set; }

        [JsonPropertyName("stop_timestamp")]
        public long StopTimestamp { get; set; }

        [JsonPropertyName("has_archive")]
        public int HasArchive { get; set; }

        // Not from JSON — populated by XMLTV parser only
        [JsonIgnore] public bool IsLive { get; set; }
        [JsonIgnore] public bool IsNew { get; set; }
        [JsonIgnore] public bool IsPreviouslyShown { get; set; }
        [JsonIgnore] public bool IsPremiere { get; set; }
        [JsonIgnore] public bool IsPlainText { get; set; }
        [JsonIgnore] public string ImageUrl { get; set; }
    }
}
