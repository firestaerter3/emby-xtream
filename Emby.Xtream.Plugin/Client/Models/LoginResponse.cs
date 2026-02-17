using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    public class LoginResponse
    {
        [JsonPropertyName("access")]
        public string Access { get; set; } = string.Empty;

        [JsonPropertyName("refresh")]
        public string Refresh { get; set; } = string.Empty;
    }
}
