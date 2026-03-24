using System;
using MediaBrowser.Model.Plugins;

namespace Emby.Xtream.Plugin
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // Xtream connection
        public string BaseUrl { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string HttpUserAgent { get; set; } = string.Empty;

        // Live TV
        public bool EnableLiveTv { get; set; } = true;
        public string LiveTvOutputFormat { get; set; } = "ts";

        // EPG / Guide Data
        public EpgSourceMode EpgSource { get; set; } = EpgSourceMode.XtreamServer;
        public string CustomEpgUrl { get; set; } = string.Empty;
        public bool DeferEpgToGuideData { get; set; } = true;

        // Back-compat: migrate EnableEpg (bool) → EpgSource on first load
        [Obsolete("Use EpgSource instead")] public bool EnableEpg { get; set; } = true;
        public int EpgCacheMinutes { get; set; } = 30;
        public int EpgDaysToFetch { get; set; } = 2;
        public int M3UCacheMinutes { get; set; } = 15;

        // Category filtering
        public int[] SelectedLiveCategoryIds { get; set; } = new int[0];
        public bool IncludeAdultChannels { get; set; }

        // Channel name cleaning
        public string ChannelRemoveTerms { get; set; } = string.Empty;
        public bool EnableChannelNameCleaning { get; set; } = true;

        // Dispatcharr
        public bool EnableDispatcharr { get; set; }
        public string DispatcharrUrl { get; set; } = string.Empty;
        public string DispatcharrUser { get; set; } = string.Empty;
        public string DispatcharrPass { get; set; } = string.Empty;
        public bool DispatcharrFallbackToXtream { get; set; } = true;
        public bool ForceAudioTranscode { get; set; }
        public int[] SelectedDispatcharrProfileIds { get; set; } = new int[0];
        public string CachedDispatcharrProfiles { get; set; } = string.Empty;

        // Gracenote DB (Channel Identifiarr)
        public string GracenoteDbPath { get; set; } = string.Empty;
        public string SelectedLineupId { get; set; } = string.Empty;
        public double GracenoteMatchThreshold { get; set; } = 0.8;
        public string GracenoteStationOverrides { get; set; } = string.Empty;

        // VOD Movies
        public bool SyncMovies { get; set; }
        public string StrmLibraryPath { get; set; } = "/config/xtream";
        public int[] SelectedVodCategoryIds { get; set; } = new int[0];
        public string MovieFolderMode { get; set; } = "single";
        public string MovieFolderMappings { get; set; } = string.Empty;

        // Series / TV Shows
        public bool SyncSeries { get; set; }
        public int[] SelectedSeriesCategoryIds { get; set; } = new int[0];
        public string SeriesFolderMode { get; set; } = "single";
        public string SeriesFolderMappings { get; set; } = string.Empty;

        // Content name cleaning
        public bool EnableContentNameCleaning { get; set; }
        public string ContentRemoveTerms { get; set; } = string.Empty;

        // TMDB folder naming
        public bool EnableTmdbFolderNaming { get; set; }
        public bool EnableTmdbFallbackLookup { get; set; }

        // Series metadata matching
        public bool EnableSeriesIdFolderNaming { get; set; }
        public bool EnableSeriesMetadataLookup { get; set; }
        public string TvdbFolderIdOverrides { get; set; } = string.Empty;

        // NFO sidecar files
        public bool EnableNfoFiles { get; set; }

        // Cached categories (JSON arrays, populated on refresh)
        public string CachedVodCategories { get; set; } = string.Empty;
        public string CachedSeriesCategories { get; set; } = string.Empty;
        public string CachedLiveCategories { get; set; } = string.Empty;

        // Update tracking
        public string LastInstalledVersion { get; set; } = string.Empty;
        public bool UseBetaChannel { get; set; }

        // Sync settings
        public bool SmartSkipExisting { get; set; } = true;
        public int SyncParallelism { get; set; } = 3;
        public bool CleanupOrphans { get; set; }

        /// <summary>Fraction of existing STRMs that can be deleted in one cleanup pass. 0 = disabled.</summary>
        public double OrphanSafetyThreshold { get; set; } = 0.20;

        // Auto-sync schedule
        public bool   AutoSyncEnabled       { get; set; } = false;
        public string AutoSyncMode          { get; set; } = "interval"; // "interval" | "daily"
        public int    AutoSyncIntervalHours { get; set; } = 24;
        public string AutoSyncDailyTime     { get; set; } = "03:00";    // HH:mm server local time

        // Sync state (persisted across restarts)
        public string LastChannelListHash { get; set; } = string.Empty;
        public long LastMovieSyncTimestamp { get; set; }
        public long LastSeriesSyncTimestamp { get; set; }
        public int StrmNamingVersion { get; set; }  // default 0; bumped when naming logic changes to force re-sync
        public string SyncHistoryJson { get; set; } = string.Empty;

        /// <summary>
        /// JSON dictionary mapping series_id → SHA256 hash of episode URLs.
        /// Used to skip per-episode file I/O when the episode list hasn't changed.
        /// </summary>
        public string SeriesEpisodeHashesJson { get; set; } = string.Empty;
    }

    public enum EpgSourceMode
    {
        XtreamServer = 0,
        CustomUrl    = 1,
        Disabled     = 2,
    }
}
