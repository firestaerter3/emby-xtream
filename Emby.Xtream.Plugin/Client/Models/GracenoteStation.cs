namespace Emby.Xtream.Plugin.Client.Models
{
    /// <summary>
    /// Represents a station row from the Channel Identifiarr SQLite database.
    /// </summary>
    public class GracenoteStation
    {
        public string StationId { get; set; }
        public string Name { get; set; }
        public string CallSign { get; set; }
        public string Language { get; set; }
        public string LogoUri { get; set; }
    }

    /// <summary>
    /// A station enriched with lineup-specific data from the station_lineups table.
    /// </summary>
    public class GracenoteLineupStation : GracenoteStation
    {
        public string ChannelNumber { get; set; }
        public string AffiliateCallSign { get; set; }
        public string VideoType { get; set; }
    }

    /// <summary>
    /// Represents a lineup from the Channel Identifiarr SQLite database.
    /// </summary>
    public class GracenoteLineup
    {
        public string LineupId { get; set; }
        public string Name { get; set; }
        public string Location { get; set; }
        public string Type { get; set; }
    }

    /// <summary>
    /// Result of matching a channel name against the Gracenote database.
    /// </summary>
    public class GracenoteMatchResult
    {
        public string StationId { get; set; }
        public string StationName { get; set; }
        public string CallSign { get; set; }
        public string LogoUri { get; set; }
        public double Score { get; set; }
        public bool IsOverride { get; set; }
    }
}
