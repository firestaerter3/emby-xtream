using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    public class SeriesInfo
    {
        [JsonPropertyName("series_id")]
        public int SeriesId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("cover")]
        public string Cover { get; set; } = string.Empty;

        [JsonPropertyName("plot")]
        public string Plot { get; set; } = string.Empty;

        [JsonPropertyName("cast")]
        public string Cast { get; set; } = string.Empty;

        [JsonPropertyName("director")]
        public string Director { get; set; } = string.Empty;

        [JsonPropertyName("genre")]
        public string Genre { get; set; } = string.Empty;

        [JsonPropertyName("releaseDate")]
        public string ReleaseDate { get; set; } = string.Empty;

        [JsonPropertyName("rating")]
        public string Rating { get; set; } = string.Empty;

        [JsonPropertyName("category_id")]
        public int? CategoryId { get; set; }

        [JsonPropertyName("category_name")]
        public string CategoryName { get; set; } = string.Empty;

        [JsonPropertyName("last_modified")]
        public string LastModified { get; set; } = string.Empty;

        [JsonPropertyName("tmdb")]
        public string TmdbId { get; set; } = string.Empty;
    }
}
