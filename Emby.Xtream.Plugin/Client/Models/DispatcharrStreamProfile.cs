using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    /// <summary>
    /// A Dispatcharr stream profile (<c>/api/core/streamprofiles/</c>).
    /// <para>
    /// <see cref="Command"/> is the executable Dispatcharr runs (<c>ffmpeg</c>, <c>streamlink</c>,
    /// a script, or the built-in redirect/proxy profiles) and <see cref="Parameters"/> holds its
    /// argument string with <c>{userAgent}</c>, <c>{streamUrl}</c> and <c>{channelId}</c>
    /// placeholders. Together they decide what codec leaves the proxy, which is the only
    /// authoritative answer to "what will Emby actually receive" short of probing the stream.
    /// </para>
    /// </summary>
    public class DispatcharrStreamProfile
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("command")]
        public string Command { get; set; }

        [JsonPropertyName("parameters")]
        public string Parameters { get; set; }

        [JsonPropertyName("is_active")]
        public bool? IsActive { get; set; }

        [JsonPropertyName("locked")]
        public bool? Locked { get; set; }
    }
}
