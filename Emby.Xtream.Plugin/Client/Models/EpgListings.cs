using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    public class EpgListings
    {
        [JsonPropertyName("epg_listings")]
        public List<EpgProgram> Listings { get; set; } = new List<EpgProgram>();
    }
}
