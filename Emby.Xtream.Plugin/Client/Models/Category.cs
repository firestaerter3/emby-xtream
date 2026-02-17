using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    public class Category
    {
        [JsonPropertyName("category_id")]
        public int CategoryId { get; set; }

        [JsonPropertyName("category_name")]
        public string CategoryName { get; set; } = string.Empty;

        [JsonPropertyName("parent_id")]
        public int ParentId { get; set; }
    }
}
