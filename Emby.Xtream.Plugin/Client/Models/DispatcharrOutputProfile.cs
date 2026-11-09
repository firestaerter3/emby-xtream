using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    /// <summary>
    /// A Dispatcharr output profile (<c>/api/core/outputprofiles/</c>, Dispatcharr 0.28+): an
    /// ffmpeg step Dispatcharr runs on a channel's stream before handing it to a client that asks
    /// for it with <c>?output_profile={id}</c>.
    /// </summary>
    public class DispatcharrOutputProfile
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("is_active")]
        public bool? IsActive { get; set; }
    }

    /// <summary>What happened when the plugin looked for, or created, its output profile.</summary>
    public enum OutputProfileOutcome
    {
        /// <summary>A profile with the name exists and is active.</summary>
        Found,

        /// <summary>No profile had the name, so the plugin created it.</summary>
        Created,

        /// <summary>A profile with the name exists but is switched off in Dispatcharr.</summary>
        Inactive,

        /// <summary>The server has no output profiles (older than Dispatcharr 0.28).</summary>
        Unsupported,

        /// <summary>The account may read profiles but not create one.</summary>
        Forbidden,

        /// <summary>Anything else: network error, unreadable answer, unexpected status.</summary>
        Failed,
    }

    public sealed class OutputProfileResult
    {
        public OutputProfileResult(OutputProfileOutcome outcome, int? id = null, string detail = null)
        {
            Outcome = outcome;
            Id = id;
            Detail = detail;
        }

        public OutputProfileOutcome Outcome { get; }

        /// <summary>The profile id to request, or null when there is none to use.</summary>
        public int? Id { get; }

        /// <summary>Status code or error message, for the log.</summary>
        public string Detail { get; }
    }
}
