using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client;
using Emby.Xtream.Plugin.Client.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using STJ = System.Text.Json;

#pragma warning disable CS0612 // SupportsProbing and AnalyzeDurationMs are obsolete but still functional
namespace Emby.Xtream.Plugin.Service
{
    public class XtreamTunerHost : BaseTunerHost
    {
        internal const string TunerType = "xtream-tuner";

        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        private static readonly STJ.JsonSerializerOptions JsonOptions = new STJ.JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
        };

        private static volatile XtreamTunerHost _instance;

        private readonly DispatcharrClient _dispatcharrClient;
        private readonly IServerApplicationHost _applicationHost;

        private volatile Dictionary<int, StreamStatsInfo> _streamStats = new Dictionary<int, StreamStatsInfo>();
        private volatile Dictionary<int, string> _channelUuidMap = new Dictionary<int, string>();
        private volatile Dictionary<int, string> _tvgIdMap = new Dictionary<int, string>();
        private volatile Dictionary<int, string> _stationIdMap = new Dictionary<int, string>();
        private volatile Dictionary<int, double> _channelNumberMap = new Dictionary<int, double>();
        private volatile Dictionary<string, int> _tunerChannelIdToStreamId = new Dictionary<string, int>();
        private volatile bool _dispatcharrDataLoaded;
        private volatile HashSet<int> _allowedStreamIds;
        private List<ChannelInfo> _cachedChannels;
        private DateTime _cacheTime = DateTime.MinValue;

        public int CachedChannelCount => _cachedChannels?.Count ?? 0;

        public IReadOnlyDictionary<int, string> TvgIdMap => _tvgIdMap;
        public IReadOnlyDictionary<int, string> StationIdMap => _stationIdMap;

        public XtreamTunerHost(IServerApplicationHost applicationHost)
            : base(applicationHost)
        {
            _instance = this;
            _applicationHost = applicationHost;
            _dispatcharrClient = new DispatcharrClient(Logger);
        }

        public static XtreamTunerHost Instance => _instance;

        public IServerApplicationHost ApplicationHost => _applicationHost;

        public override string Name => "Xtream Tuner";
        public override string Type => TunerType;
        public override bool IsSupported => true;
        public override string SetupUrl => null;
        protected override bool UseTunerHostIdAsPrefix => false;

        public override TunerHostInfo GetDefaultConfiguration()
        {
            return new TunerHostInfo
            {
                Type = Type,
                TunerCount = 1
            };
        }

        public override bool SupportsGuideData(TunerHostInfo tuner)
        {
            return Plugin.Instance.Configuration.EpgSource != EpgSourceMode.Disabled;
        }

        protected override async Task<List<ProgramInfo>> GetProgramsInternal(
            TunerHostInfo tuner, string tunerChannelId,
            DateTimeOffset startDateUtc, DateTimeOffset endDateUtc,
            CancellationToken cancellationToken)
        {
            int streamId;
            if (_tunerChannelIdToStreamId.TryGetValue(tunerChannelId, out streamId))
            {
                // Translated station ID → stream ID via mapping
            }
            else if (!int.TryParse(tunerChannelId, NumberStyles.None, CultureInfo.InvariantCulture, out streamId))
            {
                Logger.Warn("GetProgramsInternal: cannot parse tunerChannelId '{0}'", tunerChannelId);
                return new List<ProgramInfo>();
            }

            // If this channel has a Gracenote station ID, fetch programs directly from the
            // listings provider rather than returning Xtream EPG. This gives the channel
            // rich Gracenote metadata (artwork, descriptions, genres) without relying on
            // Emby's auto-mapper — which would incorrectly match other channels too.
            var stationMap = _stationIdMap;
            if (stationMap != null && stationMap.TryGetValue(streamId, out var stationId)
                && !string.IsNullOrEmpty(stationId)
                && Plugin.Instance.Configuration.DeferEpgToGuideData)
            {
                try
                {
                    var gracenotePrograms = await FetchGracenotePrograms(
                        stationId, tunerChannelId, startDateUtc, endDateUtc, cancellationToken)
                        .ConfigureAwait(false);

                    if (gracenotePrograms != null && gracenotePrograms.Count > 0)
                    {
                        Logger.Debug("GetProgramsInternal: stream {0} using {1} Gracenote programs (station {2})",
                            streamId, gracenotePrograms.Count, stationId);
                        return gracenotePrograms;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("GetProgramsInternal: Gracenote fetch failed for stream {0} (station {1}): {2}",
                        streamId, stationId, ex.Message);
                }

                Logger.Debug("GetProgramsInternal: stream {0} has station ID {1} but no Gracenote data, falling back to Xtream EPG",
                    streamId, stationId);
            }

            var liveTvService = Plugin.Instance.LiveTvService;
            List<Client.Models.EpgProgram> programs;
            try
            {
                programs = await liveTvService.FetchEpgForChannelCachedAsync(streamId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn("GetProgramsInternal: failed to fetch EPG for stream {0}: {1}", streamId, ex.Message);
                programs = new List<EpgProgram>();
            }

            var startUnix = startDateUtc.ToUnixTimeSeconds();
            var endUnix = endDateUtc.ToUnixTimeSeconds();

            const long MinTimestamp = 946684800L;   // 2000-01-01
            const long MaxTimestamp = 4102444800L;  // 2100-01-01

            var result = new List<ProgramInfo>();
            foreach (var p in programs)
            {
                if (p.StopTimestamp <= startUnix || p.StartTimestamp >= endUnix)
                {
                    continue;
                }

                if (p.StartTimestamp < MinTimestamp || p.StartTimestamp > MaxTimestamp
                    || p.StopTimestamp < MinTimestamp || p.StopTimestamp > MaxTimestamp)
                {
                    Logger.Debug("GetProgramsInternal: skipping program with out-of-range timestamps " +
                        "(start={0}, stop={1}) on channel {2}", p.StartTimestamp, p.StopTimestamp, streamId);
                    continue;
                }

                // Skip zero-duration or reversed programs — Emby's GetProgram throws when
                // EndDate <= StartDate, which causes the entire channel to be rejected.
                if (p.StopTimestamp <= p.StartTimestamp)
                {
                    Logger.Warn("GetProgramsInternal: skipping zero-duration or reversed program " +
                        "(start={0}, stop={1}, title='{2}') on channel {3}",
                        p.StartTimestamp, p.StopTimestamp,
                        p.IsPlainText ? (p.Title ?? string.Empty) : "(base64)", streamId);
                    continue;
                }

                var title = p.IsPlainText ? p.Title : LiveTvService.DecodeBase64(p.Title);
                var description = p.IsPlainText ? p.Description : LiveTvService.DecodeBase64(p.Description);
                try
                {
                    result.Add(BuildProgramInfo(p, streamId, tunerChannelId, title, description));
                }
                catch (Exception ex)
                {
                    Logger.Warn("GetProgramsInternal: skipping program on channel {0} " +
                        "(start={1}, stop={2}, title='{3}'): {4}",
                        streamId, p.StartTimestamp, p.StopTimestamp,
                        p.IsPlainText ? p.Title : "(base64)", ex.Message);
                }
            }

            // No EPG data — return a dummy entry spanning the requested window so the channel
            // row stays visible and clickable in the guide (matches M3U tuner behaviour).
            if (result.Count == 0)
            {
                var channelName = _cachedChannels?.Find(c => c.TunerChannelId == tunerChannelId)?.Name;
                if (!string.IsNullOrEmpty(channelName))
                {
                    result.Add(new ProgramInfo
                    {
                        Id = string.Format(CultureInfo.InvariantCulture, "xtream_dummy_{0}_{1}", streamId, startDateUtc.ToUnixTimeSeconds()),
                        ChannelId = tunerChannelId,
                        StartDate = startDateUtc.UtcDateTime,
                        EndDate = endDateUtc.UtcDateTime,
                        Name = channelName,
                        Genres = new List<string>(),
                    });
                    Logger.Debug("GetProgramsInternal: no EPG for channel {0}, returning dummy entry", streamId);
                }
            }

            if (result.Count > 0 && result.Count <= 15)
            {
                // Low program count — log first entry to help diagnose EPG quality issues.
                var first = result[0];
                Logger.Debug("GetProgramsInternal: channel {0} first program: start={1:u}, end={2:u}, name='{3}'",
                    streamId, first.StartDate, first.EndDate, first.Name);
            }

            Logger.Debug("GetProgramsInternal: returning {0} programs for channel {1}", result.Count, streamId);
            return result;
        }

        /// <summary>
        /// Converts a single <see cref="EpgProgram"/> into a <see cref="ProgramInfo"/> ready for
        /// Emby. Extracted as an internal static so it can be unit-tested without Emby DI.
        /// </summary>
        internal static ProgramInfo BuildProgramInfo(
            EpgProgram p, int streamId, string tunerChannelId,
            string title, string description)
        {
            var cats = p.Categories;
            var isMovie = cats != null && cats.Exists(c =>
                c.IndexOf("movie", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                c.IndexOf("film", System.StringComparison.OrdinalIgnoreCase) >= 0);
            var isSports = cats != null && cats.Exists(c =>
                c.IndexOf("sport", System.StringComparison.OrdinalIgnoreCase) >= 0);
            var isSeries = !isMovie && !isSports;

            return new ProgramInfo
            {
                Id = string.Format(CultureInfo.InvariantCulture, "xtream_epg_{0}_{1}", streamId, p.StartTimestamp),
                ChannelId = tunerChannelId,
                StartDate = DateTimeOffset.FromUnixTimeSeconds(p.StartTimestamp).UtcDateTime,
                EndDate = DateTimeOffset.FromUnixTimeSeconds(p.StopTimestamp).UtcDateTime,
                Name = string.IsNullOrEmpty(title) ? "Unknown" : title,
                Overview = string.IsNullOrEmpty(description) ? null : description,
                EpisodeTitle = string.IsNullOrEmpty(p.SubTitle) ? null : p.SubTitle,
                IsLive = p.IsLive,
                IsRepeat = p.IsPreviouslyShown,
                IsPremiere = p.IsNew || p.IsPremiere,
                ImageUrl = IsValidHttpUrl(p.ImageUrl) ? p.ImageUrl : null,
                Genres = cats ?? new List<string>(),
                IsSports = isSports,
                IsNews = cats != null && cats.Exists(c =>
                    c.IndexOf("news", System.StringComparison.OrdinalIgnoreCase) >= 0),
                IsMovie = isMovie,
                IsKids = cats != null && cats.Exists(c =>
                    c.IndexOf("children", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    c.IndexOf("kids", System.StringComparison.OrdinalIgnoreCase) >= 0),
                IsSeries = isSeries,
                SeriesId = isSeries && !string.IsNullOrEmpty(title) ? title.ToLowerInvariant() : null,
            };
        }

        protected override async Task<List<ChannelInfo>> GetChannelsInternal(
            TunerHostInfo tuner, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;

            if (!config.EnableLiveTv)
            {
                return new List<ChannelInfo>();
            }

            // Return cached channels if available and not expired
            if (_cachedChannels != null && DateTime.UtcNow - _cacheTime < CacheDuration)
            {
                // Emby mutates ChannelInfo objects after receiving them, clearing ListingsChannelId.
                // Re-apply from _stationIdMap on every cache hit so the field is always correct.
                foreach (var ch in _cachedChannels)
                {
                    if (config.EnableDispatcharr
                        && _tunerChannelIdToStreamId.TryGetValue(ch.TunerChannelId, out var streamIdForLookup)
                        && _stationIdMap.TryGetValue(streamIdForLookup, out var stId)
                        && !string.IsNullOrEmpty(stId))
                        ch.ListingsChannelId = stId;
                    else
                        ch.ListingsChannelId = null;
                }
                var cachedGracenote = _cachedChannels.Count(c => c.ListingsChannelId != null);
                Logger.Debug("Returning cached channel list ({0} channels, {1} with Gracenote station ID)",
                    _cachedChannels.Count, cachedGracenote);
                return _cachedChannels;
            }

            Logger.Info("Fetching channels from Xtream API");

            var liveTvService = Plugin.Instance.LiveTvService;
            var newStats = new Dictionary<int, StreamStatsInfo>();

            // All three fetches run concurrently; each handles its own errors internally.
            async Task<List<Client.Models.LiveStreamInfo>> channelsFetch()
            {
                try
                {
                    return await liveTvService.GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is TaskCanceledException) && !(ex is OperationCanceledException))
                {
                    Logger.Warn("LiveTvService channel fetch failed, falling back to direct API: {0}", ex.Message);
                    return await FetchAllChannelsDirectAsync(config).ConfigureAwait(false);
                }
            }

            async Task<Dictionary<int, string>> categoriesFetch()
            {
                try
                {
                    var cats = await liveTvService.GetLiveCategoriesAsync(cancellationToken).ConfigureAwait(false);
                    Logger.Debug("Fetched {0} live categories for guide chips", cats.Count);
                    return cats.ToDictionary(c => c.CategoryId, c => c.CategoryName);
                }
                catch (Exception ex)
                {
                    Logger.Warn("Failed to fetch live categories for guide chips: {0}", ex.Message);
                    return new Dictionary<int, string>();
                }
            }

            async Task dispatcharrFetch()
            {
                if (!config.EnableDispatcharr || string.IsNullOrEmpty(config.DispatcharrUrl))
                    return;
                try
                {
                    _dispatcharrClient.Configure(config.DispatcharrUser, config.DispatcharrPass);

                    // Profile filtering: build the set of allowed Dispatcharr channel IDs.
                    HashSet<int> enabledChannelIds = null;
                    if (config.SelectedDispatcharrProfileIds != null && config.SelectedDispatcharrProfileIds.Length > 0)
                    {
                        var profiles = await _dispatcharrClient.GetProfilesAsync(
                            config.DispatcharrUrl, cancellationToken).ConfigureAwait(false);
                        enabledChannelIds = new HashSet<int>();
                        foreach (var profile in profiles)
                        {
                            if (Array.IndexOf(config.SelectedDispatcharrProfileIds, profile.Id) >= 0)
                            {
                                foreach (var chId in profile.Channels)
                                    enabledChannelIds.Add(chId);
                            }
                        }
                        Logger.Info("Profile filter active: {0} profile(s), {1} enabled Dispatcharr channel IDs",
                            config.SelectedDispatcharrProfileIds.Length, enabledChannelIds.Count);
                    }

                    var (uuidMap, statsMap, tvgIdMap, stationIdMap, allowedStreamIds, channelNumberMap) =
                        await _dispatcharrClient.GetChannelDataAsync(
                            config.DispatcharrUrl, cancellationToken, enabledChannelIds).ConfigureAwait(false);
                    newStats = statsMap;
                    _channelUuidMap = uuidMap;
                    _tvgIdMap = tvgIdMap;
                    _stationIdMap = stationIdMap;
                    _channelNumberMap = channelNumberMap;
                    _allowedStreamIds = allowedStreamIds;
                    _dispatcharrDataLoaded = true;
                }
                catch (Exception ex)
                {
                    Logger.Warn("Failed to fetch Dispatcharr channel data: {0}", ex.Message);
                }
            }

            var channelsTask = channelsFetch();
            var categoriesTask = categoriesFetch();
            var dispatcharrTask = dispatcharrFetch();

            await Task.WhenAll(channelsTask, categoriesTask, dispatcharrTask).ConfigureAwait(false);

            var channels = channelsTask.Result;
            var categoryMap = categoriesTask.Result;
            int statsCount = newStats.Count;

            // Profile filtering: if a profile filter is active, restrict the Xtream channel list
            // to only those channels whose stream ID is in the allowed set.
            var allowedIds = _allowedStreamIds;
            if (allowedIds != null)
            {
                var before = channels.Count;
                channels = channels.Where(c => allowedIds.Contains(c.StreamId)).ToList();
                Logger.Info("Profile filter applied: {0} → {1} channels ({2} excluded)",
                    before, channels.Count, before - channels.Count);
            }

            var usedStationIds = new HashSet<string>(StringComparer.Ordinal);
            var newTunerChannelIdToStreamId = new Dictionary<string, int>();

            var result = channels.Select(channel =>
            {
                var cleanName = ChannelNameCleaner.CleanChannelName(
                    channel.Name,
                    config.ChannelRemoveTerms,
                    config.EnableChannelNameCleaning);

                var streamIdStr = channel.StreamId.ToString(CultureInfo.InvariantCulture);

                string[] tags = null;
                if (channel.CategoryId.HasValue
                    && categoryMap.TryGetValue(channel.CategoryId.Value, out var groupTitle)
                    && !string.IsNullOrEmpty(groupTitle))
                {
                    tags = new[] { groupTitle };
                }

                // Determine TunerChannelId: use Gracenote station ID for guide matching
                // when available, otherwise use stream ID. Emby's matching waterfall uses
                // TunerChannelId (not ListingsChannelId) to correlate channels with guide data.
                string tunerChannelId = streamIdStr;
                string listingsChannelId = null;
                if (config.EnableDispatcharr
                    && _stationIdMap.TryGetValue(channel.StreamId, out var stationId)
                    && !string.IsNullOrEmpty(stationId))
                {
                    if (usedStationIds.Add(stationId))
                    {
                        tunerChannelId = stationId;
                        listingsChannelId = stationId;
                        Logger.Debug("Stream {0} ({1}): TunerChannelId = {2} (Gracenote station ID)",
                            channel.StreamId, cleanName, stationId);
                    }
                    else
                    {
                        Logger.Warn("Stream {0} ({1}): duplicate station ID {2}, falling back to stream ID as TunerChannelId",
                            channel.StreamId, cleanName, stationId);
                    }
                }

                newTunerChannelIdToStreamId[tunerChannelId] = channel.StreamId;

                string channelNumber;
                if (config.EnableDispatcharr
                    && _channelNumberMap.TryGetValue(channel.StreamId, out var dispatcharrNum))
                {
                    // Use Dispatcharr's real channel_number (supports decimals like 2.1).
                    // Format as integer when there is no fractional part (e.g. 5.0 → "5").
                    channelNumber = dispatcharrNum == Math.Floor(dispatcharrNum)
                        ? ((int)dispatcharrNum).ToString(CultureInfo.InvariantCulture)
                        : dispatcharrNum.ToString("G", CultureInfo.InvariantCulture);
                }
                else
                {
                    channelNumber = channel.Num.ToString(CultureInfo.InvariantCulture);
                }

                string callSign = null;
                if (config.EnableDispatcharr
                    && _tvgIdMap.TryGetValue(channel.StreamId, out var tvgId)
                    && !string.IsNullOrEmpty(tvgId))
                {
                    callSign = tvgId;
                }

                return new ChannelInfo
                {
                    Id = CreateEmbyChannelId(tuner, streamIdStr),
                    TunerChannelId = tunerChannelId,
                    Name = cleanName,
                    Number = channelNumber,
                    CallSign = callSign,
                    ImageUrl = string.IsNullOrEmpty(channel.StreamIcon) ? null : channel.StreamIcon,
                    ChannelType = ChannelType.TV,
                    TunerHostId = tuner.Id,
                    Tags = tags,
                    ListingsChannelId = listingsChannelId,
                };
            }).ToList();

            _streamStats = newStats;
            _tunerChannelIdToStreamId = newTunerChannelIdToStreamId;
            _cachedChannels = result;
            _cacheTime = DateTime.UtcNow;
            var gracenoteCount = result.Count(c => c.ListingsChannelId != null);
            Logger.Info("Channel list cached with {0} channels ({1} with stream stats, {2} with Gracenote station ID)",
                result.Count, statsCount, gracenoteCount);

            if (config.DeferEpgToGuideData && gracenoteCount > 0)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        var updated = DetachListingProviders();
                        if (updated > 0)
                            Logger.Info("Auto-detached Xtream tuner from {0} listing provider(s) — Gracenote EPG will be fetched by the tuner directly", updated);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("Auto detach listing providers failed: {0}", ex.Message);
                    }
                });
            }

            return result;
        }

        private static async Task<List<Client.Models.LiveStreamInfo>> FetchAllChannelsDirectAsync(PluginConfiguration config)
        {
            using (var httpClient = Plugin.CreateHttpClient(30))
            {
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}&action=get_live_streams",
                    config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty));

                var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                return STJ.JsonSerializer.Deserialize<List<Client.Models.LiveStreamInfo>>(json, JsonOptions)
                    ?? new List<Client.Models.LiveStreamInfo>();
            }
        }

        protected override async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(
            TunerHostInfo tuner, MediaBrowser.Controller.Entities.BaseItem dbChannel,
            ChannelInfo tunerChannel, CancellationToken cancellationToken)
        {
            if (!TryResolveStreamId(tunerChannel, out int streamId))
            {
                return new List<MediaSourceInfo>();
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            await EnsureStatsLoadedAsync(cancellationToken).ConfigureAwait(false);
            Logger.Info("[stream-timing] ch={0} EnsureStats={1}ms", tunerChannel?.Name, sw.ElapsedMilliseconds);
            sw.Restart();

            var config = Plugin.Instance.Configuration;
            var (streamUrl, isDispatcharr) = BuildStreamUrl(config, streamId);
            Logger.Info("[stream-timing] ch={0} BuildUrl={1}ms isDispatcharr={2}", tunerChannel?.Name, sw.ElapsedMilliseconds, isDispatcharr);
            sw.Restart();

            if (streamUrl == null)
            {
                return new List<MediaSourceInfo>();
            }

            _streamStats.TryGetValue(streamId, out var stats);

            var mediaSource = CreateMediaSourceInfo(streamId, streamUrl, stats, isDispatcharr, config.ForceAudioTranscode, config.HttpUserAgent);
            Logger.Info("[stream-timing] ch={0} CreateMediaSource={1}ms hasStats={2}", tunerChannel?.Name, sw.ElapsedMilliseconds, stats != null);

            return new List<MediaSourceInfo> { mediaSource };
        }

        protected override async Task<ILiveStream> GetChannelStream(
            TunerHostInfo tuner, MediaBrowser.Controller.Entities.BaseItem dbChannel,
            ChannelInfo tunerChannel, string mediaSourceId,
            CancellationToken cancellationToken)
        {
            if (!TryResolveStreamId(tunerChannel, out int streamId))
            {
                throw new System.IO.FileNotFoundException(
                    string.Format("Channel {0} not found in Xtream tuner", tunerChannel?.Id));
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            await EnsureStatsLoadedAsync(cancellationToken).ConfigureAwait(false);
            Logger.Info("[stream-timing] ch={0} EnsureStats={1}ms", tunerChannel?.Name, sw.ElapsedMilliseconds);
            sw.Restart();

            var config = Plugin.Instance.Configuration;
            var (streamUrl, isDispatcharr) = BuildStreamUrl(config, streamId);
            if (streamUrl == null)
            {
                throw new System.IO.FileNotFoundException(
                    string.Format("Channel {0}: Dispatcharr proxy unavailable and fallback disabled", streamId));
            }
            _streamStats.TryGetValue(streamId, out var stats);
            Logger.Info("[stream-timing] ch={0} BuildUrl={1}ms isDispatcharr={2}", tunerChannel?.Name, sw.ElapsedMilliseconds, isDispatcharr);
            sw.Restart();

            var mediaSource = CreateMediaSourceInfo(streamId, streamUrl, stats, isDispatcharr, config.ForceAudioTranscode, config.HttpUserAgent);
            Logger.Info("[stream-timing] ch={0} CreateMediaSource={1}ms hasStats={2}", tunerChannel?.Name, sw.ElapsedMilliseconds, stats != null);

            var httpClient = Plugin.CreateHttpClient();
            ILiveStream liveStream = new XtreamLiveStream(mediaSource, tuner.Id, httpClient, Logger);

            Logger.Info("Opening live stream for channel {0} (stream {1})",
                tunerChannel?.Name ?? tunerChannel?.Id, streamId);

            return liveStream;
        }

        public new void ClearCaches()
        {
            _cachedChannels = null;
            _cacheTime = DateTime.MinValue;
            _streamStats = new Dictionary<int, StreamStatsInfo>();
            _channelUuidMap = new Dictionary<int, string>();
            _tvgIdMap = new Dictionary<int, string>();
            _stationIdMap = new Dictionary<int, string>();
            _channelNumberMap = new Dictionary<int, double>();
            _tunerChannelIdToStreamId = new Dictionary<string, int>();
            _allowedStreamIds = null;
            _dispatcharrDataLoaded = false;
            Logger.Info("Xtream tuner caches cleared");
        }

        /// <summary>
        /// Removes the Xtream tuner from all listing providers' enabled-tuner lists so
        /// that Emby's auto-mapper does not assign (wrong) Gracenote entries to our
        /// channels. Instead, <see cref="GetProgramsInternal"/> fetches Gracenote programs
        /// directly for channels that have a station ID and returns Xtream EPG for the rest.
        /// </summary>
        /// <returns>Number of listing providers updated.</returns>
        public int DetachListingProviders()
        {
            IConfigurationManager configManager;
            try
            {
                configManager = _applicationHost.Resolve<IConfigurationManager>();
            }
            catch (Exception ex)
            {
                Logger.Warn("DetachListingProviders: failed to resolve IConfigurationManager: {0}", ex.Message);
                return 0;
            }

            LiveTvOptions liveTvOptions;
            try
            {
                liveTvOptions = configManager.GetConfiguration("livetv") as LiveTvOptions;
            }
            catch (Exception ex)
            {
                Logger.Warn("DetachListingProviders: failed to read LiveTvOptions: {0}", ex.Message);
                return 0;
            }

            if (liveTvOptions?.ListingProviders == null || liveTvOptions.ListingProviders.Length == 0)
            {
                Logger.Info("DetachListingProviders: no listing providers configured");
                return 0;
            }

            var xtreamTunerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (liveTvOptions.TunerHosts != null)
            {
                foreach (var th in liveTvOptions.TunerHosts)
                {
                    if (string.Equals(th.Type, TunerType, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(th.Id))
                    {
                        xtreamTunerIds.Add(th.Id);
                    }
                }
            }

            if (xtreamTunerIds.Count == 0)
            {
                Logger.Info("DetachListingProviders: no Xtream tuner hosts found in config");
                return 0;
            }

            var allTunerIds = liveTvOptions.TunerHosts?
                .Where(t => !string.IsNullOrEmpty(t.Id))
                .Select(t => t.Id)
                .ToArray() ?? Array.Empty<string>();

            int updated = 0;
            bool configChanged = false;

            foreach (var provider in liveTvOptions.ListingProviders)
            {
                if (string.IsNullOrEmpty(provider.Id))
                    continue;

                bool coversXtream;
                if (provider.EnableAllTuners)
                {
                    coversXtream = true;
                }
                else
                {
                    coversXtream = provider.EnabledTuners != null
                        && provider.EnabledTuners.Any(id => xtreamTunerIds.Contains(id));
                }

                if (!coversXtream)
                    continue;

                if (provider.EnableAllTuners)
                {
                    provider.EnableAllTuners = false;
                    provider.EnabledTuners = allTunerIds
                        .Where(id => !xtreamTunerIds.Contains(id))
                        .ToArray();
                }
                else
                {
                    provider.EnabledTuners = provider.EnabledTuners
                        .Where(id => !xtreamTunerIds.Contains(id))
                        .ToArray();
                }

                Logger.Info("DetachListingProviders: removed Xtream tuner from provider '{0}' (id={1}), remaining tuners: [{2}]",
                    provider.Name ?? provider.ListingsId, provider.Id,
                    string.Join(", ", provider.EnabledTuners));
                updated++;
                configChanged = true;
            }

            if (configChanged)
            {
                try
                {
                    configManager.SaveConfiguration("livetv", liveTvOptions);
                    Logger.Info("DetachListingProviders: saved config, {0} providers updated", updated);
                }
                catch (Exception ex)
                {
                    Logger.Warn("DetachListingProviders: failed to save config: {0}", ex.Message);
                    return 0;
                }
            }
            else
            {
                Logger.Info("DetachListingProviders: Xtream tuner already detached from all providers");
            }

            return updated;
        }

        /// <summary>
        /// Fetches Gracenote programs from the first listing provider that returns data
        /// for the given station ID. Returns null if no provider has programs.
        /// </summary>
        private async Task<List<ProgramInfo>> FetchGracenotePrograms(
            string stationId, string tunerChannelId,
            DateTimeOffset startDateUtc, DateTimeOffset endDateUtc,
            CancellationToken cancellationToken)
        {
            ILiveTvManager liveTvManager;
            IConfigurationManager configManager;
            try
            {
                liveTvManager = _applicationHost.Resolve<ILiveTvManager>();
                configManager = _applicationHost.Resolve<IConfigurationManager>();
            }
            catch (Exception ex)
            {
                Logger.Warn("FetchGracenotePrograms: failed to resolve services: {0}", ex.Message);
                return null;
            }

            LiveTvOptions liveTvOptions;
            try
            {
                liveTvOptions = configManager.GetConfiguration("livetv") as LiveTvOptions;
            }
            catch
            {
                return null;
            }

            if (liveTvOptions?.ListingProviders == null)
                return null;

            var providers = liveTvManager.ListingProviders;
            if (providers == null || providers.Length == 0)
                return null;

            foreach (var listingsProvider in providers)
            {
                var info = liveTvOptions.ListingProviders
                    .FirstOrDefault(p => string.Equals(p.Type, listingsProvider.Type, StringComparison.OrdinalIgnoreCase)
                                         && !string.IsNullOrEmpty(p.Id));
                if (info == null)
                    continue;

                try
                {
                    var programs = await listingsProvider.GetProgramsAsync(
                        info, stationId, startDateUtc, endDateUtc, cancellationToken)
                        .ConfigureAwait(false);

                    if (programs != null && programs.Count > 0)
                    {
                        var result = new List<ProgramInfo>(programs.Count);
                        foreach (var p in programs)
                        {
                            p.ChannelId = tunerChannelId;
                            result.Add(p);
                        }
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug("FetchGracenotePrograms: provider '{0}' returned no data for station {1}: {2}",
                        info.Name ?? info.ListingsId, stationId, ex.Message);
                }
            }

            return null;
        }

        /// <summary>
        /// Ensures Dispatcharr stats and UUID mappings are loaded. Called lazily
        /// on first playback if GetChannelsInternal hasn't run yet (e.g. after restart),
        /// and from LiveTvService before generating M3U output so that tvg-id and
        /// tvc-guide-stationid attributes are available even when Emby hasn't polled
        /// the tuner for channels yet (e.g. immediately after a cache refresh).
        /// Uses a flag rather than checking map counts so that a legitimately empty
        /// stats map (all URL-based sources with no stats) doesn't cause a redundant
        /// Dispatcharr API round-trip on every playback request.
        /// </summary>
        internal async Task EnsureStatsLoadedAsync(CancellationToken cancellationToken)
        {
            if (_dispatcharrDataLoaded)
            {
                return;
            }

            var config = Plugin.Instance.Configuration;
            if (!config.EnableDispatcharr || string.IsNullOrEmpty(config.DispatcharrUrl))
            {
                return;
            }

            Logger.Info("Dispatcharr data missing at playback time, fetching on-demand");
            _dispatcharrClient.Configure(config.DispatcharrUser, config.DispatcharrPass);

            try
            {
                // Profile filtering on-demand: re-compute the enabled channel set if profiles are selected.
                HashSet<int> enabledChannelIds = null;
                if (config.SelectedDispatcharrProfileIds != null && config.SelectedDispatcharrProfileIds.Length > 0)
                {
                    var profiles = await _dispatcharrClient.GetProfilesAsync(
                        config.DispatcharrUrl, cancellationToken).ConfigureAwait(false);
                    enabledChannelIds = new HashSet<int>();
                    foreach (var profile in profiles)
                    {
                        if (Array.IndexOf(config.SelectedDispatcharrProfileIds, profile.Id) >= 0)
                        {
                            foreach (var chId in profile.Channels)
                                enabledChannelIds.Add(chId);
                        }
                    }
                }

                var (uuidMap, statsMap, tvgIdMap, stationIdMap, allowedStreamIds, channelNumberMap) =
                    await _dispatcharrClient.GetChannelDataAsync(
                        config.DispatcharrUrl, cancellationToken, enabledChannelIds).ConfigureAwait(false);
                if (statsMap.Count > 0) _streamStats = statsMap;
                if (uuidMap.Count > 0) _channelUuidMap = uuidMap;
                if (tvgIdMap.Count > 0) _tvgIdMap = tvgIdMap;
                if (stationIdMap.Count > 0) _stationIdMap = stationIdMap;
                if (channelNumberMap.Count > 0) _channelNumberMap = channelNumberMap;
                _allowedStreamIds = allowedStreamIds;
                _dispatcharrDataLoaded = true;
                Logger.Info("Loaded {0} UUIDs and {1} stream stats from Dispatcharr on-demand",
                    uuidMap.Count, statsMap.Count);
            }
            catch (Exception ex)
            {
                Logger.Warn("On-demand Dispatcharr data fetch failed: {0}", ex.Message);
            }
        }

        private bool TryResolveStreamId(ChannelInfo tunerChannel, out int streamId)
        {
            streamId = 0;
            if (tunerChannel == null) return false;

            var id = tunerChannel.TunerChannelId ?? tunerChannel.Id;

            // Check authoritative mapping first (handles station ID → stream ID translation)
            if (_tunerChannelIdToStreamId.TryGetValue(id, out streamId))
                return true;

            // Fallback: parse directly (before channel list is loaded)
            return int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out streamId);
        }

        private (string Url, bool IsDispatcharr) BuildStreamUrl(PluginConfiguration config, int streamId)
        {
            // When Dispatcharr is enabled and we have a UUID for this channel,
            // use the proxy stream URL instead of the Xtream-style URL.
            if (config.EnableDispatcharr && !string.IsNullOrEmpty(config.DispatcharrUrl))
            {
                if (_channelUuidMap.TryGetValue(streamId, out var uuid))
                {
                    var proxyUrl = string.Format(CultureInfo.InvariantCulture,
                        "{0}/proxy/ts/stream/{1}",
                        config.DispatcharrUrl.TrimEnd('/'), uuid);
                    Logger.Debug("Stream {0}: using Dispatcharr proxy URL (uuid={1})", streamId, uuid);
                    return (proxyUrl, true);
                }

                if (!config.DispatcharrFallbackToXtream)
                {
                    Logger.Warn("Stream {0}: no Dispatcharr UUID and fallback disabled, skipping", streamId);
                    return (null, false);
                }

                Logger.Debug("Stream {0}: no Dispatcharr UUID, falling back to direct Xtream URL", streamId);
            }

            var extension = string.Equals(config.LiveTvOutputFormat, "ts", StringComparison.OrdinalIgnoreCase)
                ? "ts" : "m3u8";
            return (string.Format(CultureInfo.InvariantCulture,
                "{0}/live/{1}/{2}/{3}.{4}",
                config.BaseUrl, config.Username, config.Password, streamId, extension), false);
        }

        private MediaSourceInfo CreateMediaSourceInfo(
            int streamId, string streamUrl, StreamStatsInfo stats,
            bool disableProbing = false, bool forceAudioTranscode = false,
            string userAgent = null)
        {
            var sourceId = "xtream_live_" + streamId.ToString(CultureInfo.InvariantCulture);

            // Audio-only channel: Dispatcharr stats are present but no video_codec exists.
            // The normal hasStats gate (VideoCodec != null) would fall through to the dummy
            // H.264 fallback, which causes Emby to expect a video stream that isn't there.
            bool isAudioOnly = stats != null
                && stats.VideoCodec == null
                && !string.IsNullOrEmpty(stats.AudioCodec);

            bool hasStats = stats?.VideoCodec != null || isAudioOnly;

            // Disable probing for Dispatcharr proxy URLs: the probe opens a short-lived HTTP
            // connection that Dispatcharr interprets as a client, and when it closes after
            // analysis (~0.1s) Dispatcharr tears down the channel. The real playback connection
            // then hits the teardown and fails, causing a rapid retry storm.
            bool suppressProbing = disableProbing || hasStats;

            var audioCodecLower = hasStats && !string.IsNullOrEmpty(stats.AudioCodec)
                ? stats.AudioCodec.ToLowerInvariant() : null;

            // When ForceAudioTranscode is enabled, disable direct-stream so Emby transcodes
            // audio (→ AAC on iOS/Apple TV). This fixes silent AC3 playback on Apple devices.
            // If stats are available and confirm a non-AC3 codec, direct-stream is safe and
            // kept enabled. Without stats we can't verify the codec, so we also force
            // transcoding — the user has opted in and accepted that trade-off.
            bool suppressDirectStream = forceAudioTranscode &&
                (!hasStats || audioCodecLower == "ac3" || audioCodecLower == "eac3" || audioCodecLower == "mp2");

            var mediaSource = new MediaSourceInfo
            {
                Id = sourceId,
                Path = streamUrl,
                Protocol = MediaProtocol.Http,
                Container = "mpegts",
                SupportsProbing = !suppressProbing,
                IsRemote = true,
                IsInfiniteStream = true,
                SupportsDirectPlay = false,
                SupportsDirectStream = !suppressDirectStream,
                SupportsTranscoding = true,
                AnalyzeDurationMs = suppressProbing ? 0 : (int?)500,
                RequiresOpening = true,
                RequiresClosing = true,
                WallClockStart = DateTime.UtcNow,
            };

            if (!string.IsNullOrEmpty(userAgent))
            {
                mediaSource.RequiredHttpHeaders = new Dictionary<string, string>
                {
                    ["User-Agent"] = userAgent
                };
            }

            if (hasStats)
            {
                var mediaStreams = new List<MediaStream>();

                if (!isAudioOnly)
                {
                    // Parse resolution (e.g. "1920x1080")
                    int width = 0, height = 0;
                    if (!string.IsNullOrEmpty(stats.Resolution))
                    {
                        var parts = stats.Resolution.Split('x');
                        if (parts.Length == 2)
                        {
                            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width);
                            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height);
                        }
                    }

                    var videoCodec = MapVideoCodec(stats.VideoCodec);

                    var videoStream = new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Index = 0,
                        Codec = videoCodec,
                        IsInterlaced = false,
                        PixelFormat = "yuv420p",
                    };

                    if (width > 0) videoStream.Width = width;
                    if (height > 0) videoStream.Height = height;
                    videoStream.DisplayTitle = height > 0
                        ? $"{height}p {videoCodec.ToUpperInvariant()}"
                        : videoCodec.ToUpperInvariant();
                    if (stats.SourceFps.HasValue)
                    {
                        videoStream.RealFrameRate = (float)stats.SourceFps.Value;
                        videoStream.AverageFrameRate = (float)stats.SourceFps.Value;
                    }
                    if (stats.Bitrate.HasValue) videoStream.BitRate = (int)(stats.Bitrate.Value * 1000);
                    if (!string.IsNullOrEmpty(stats.VideoProfile))
                        videoStream.Profile = stats.VideoProfile;
                    if (stats.VideoLevel.HasValue)
                        videoStream.Level = (double)stats.VideoLevel.Value;
                    if (stats.VideoBitDepth.HasValue)
                        videoStream.BitDepth = stats.VideoBitDepth.Value;
                    if (stats.VideoRefFrames.HasValue)
                        videoStream.RefFrames = stats.VideoRefFrames.Value;

                    mediaStreams.Add(videoStream);
                }

                // Prefer the audio_channels field from stream_stats when present (Dispatcharr
                // 0.19.0+ includes it as e.g. "5.1", "2.0", "stereo").  Fall back to
                // codec-based broadcast defaults when the field is absent.
                int? audioChannels = null;
                string channelLayout = null;
                if (!string.IsNullOrEmpty(stats.AudioChannels))
                {
                    audioChannels = ParseAudioChannelCount(stats.AudioChannels);
                    channelLayout = stats.AudioChannels.Contains(".")
                        ? stats.AudioChannels  // e.g. "5.1", "7.1"
                        : stats.AudioChannels; // e.g. "stereo", "mono"
                }
                else if (audioCodecLower == "ac3" || audioCodecLower == "eac3")
                {
                    audioChannels = 6;
                    channelLayout = "5.1(side)";
                }
                else if (audioCodecLower == "mp2" || audioCodecLower == "mp1")
                {
                    audioChannels = 2;
                    channelLayout = "stereo";
                }

                var audioStream = new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = isAudioOnly ? 0 : 1,
                    Codec = audioCodecLower ?? "aac",
                    Channels = audioChannels,
                    ChannelLayout = channelLayout,
                    SampleRate = stats.SampleRate,
                };
                // For audio-only channels set the bitrate so Emby doesn't assume its
                // 40 Mbps live-TV default and force a transcode at low quality settings.
                if (isAudioOnly)
                {
                    if (stats.AudioBitrate.HasValue)
                        audioStream.BitRate = (int)(stats.AudioBitrate.Value * 1000);
                    else if (stats.Bitrate.HasValue)
                        audioStream.BitRate = (int)(stats.Bitrate.Value * 1000);
                }
                else
                {
                    if (stats.AudioBitrate.HasValue)
                        audioStream.BitRate = (int)(stats.AudioBitrate.Value * 1000);
                    else if (audioCodecLower == "ac3") audioStream.BitRate = 384000;
                    else if (audioCodecLower == "eac3") audioStream.BitRate = 640000;
                    else if (audioCodecLower == "aac") audioStream.BitRate = 128000;
                    else if (audioCodecLower == "mp2" || audioCodecLower == "mp1") audioStream.BitRate = 256000;
                }

                if (!string.IsNullOrEmpty(stats.AudioLanguage))
                    audioStream.Language = stats.AudioLanguage;

                audioStream.DisplayTitle = channelLayout != null
                    ? $"{(audioCodecLower ?? "aac").ToUpperInvariant()} {channelLayout}"
                    : (audioCodecLower ?? "aac").ToUpperInvariant();

                mediaStreams.Add(audioStream);

                mediaSource.MediaStreams = mediaStreams;
                mediaSource.DefaultAudioStreamIndex = isAudioOnly ? 0 : 1;

                if (isAudioOnly)
                {
                    Logger.Debug(
                        "Stream {0}: audio-only - {1} {2}ch{3}",
                        streamId, audioCodecLower ?? "unknown",
                        audioChannels.HasValue ? audioChannels.Value.ToString(CultureInfo.InvariantCulture) : "?",
                        suppressDirectStream ? " [force transcode]" : string.Empty);
                }
                else
                {
                    int width = 0, height = 0;
                    if (!string.IsNullOrEmpty(stats.Resolution))
                    {
                        var parts = stats.Resolution.Split('x');
                        if (parts.Length == 2)
                        {
                            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width);
                            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height);
                        }
                    }
                    Logger.Debug(
                        "Stream {0}: using stats - {1} {2}x{3} @{4}fps, audio {5} {6}ch{7}",
                        streamId, stats.VideoCodec, width, height,
                        stats.SourceFps, audioCodecLower ?? "unknown",
                        audioChannels.HasValue ? audioChannels.Value.ToString(CultureInfo.InvariantCulture) : "?",
                        suppressDirectStream ? " [force transcode]" : string.Empty);
                }
            }
            else
            {
                // No stats — provide defaults so hardware decoding can still be attempted.
                // Codec must be non-null: Emby's RecordingRequiresEncoding accesses it
                // directly and throws NullReferenceException when it is null.  H.264/AAC
                // are the most common IPTV codecs and serve as safe fallbacks.
                mediaSource.MediaStreams = new List<MediaStream>
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Index = 0,
                        Codec = "h264",
                        IsInterlaced = false,
                        PixelFormat = "yuv420p",
                        DisplayTitle = "H264",
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Index = 1,
                        Codec = "aac",
                        DisplayTitle = "AAC",
                    },
                };
                mediaSource.DefaultAudioStreamIndex = 1;
                Logger.Debug("Stream {0}: no stats available, will probe", streamId);
            }

            return mediaSource;
        }

        /// <summary>
        /// Parses an ffmpeg-style audio channel layout string ("5.1", "7.1", "stereo",
        /// "mono", "2.0") into a channel count.  Returns null for unrecognised values.
        /// </summary>
        internal static int? ParseAudioChannelCount(string layout)
        {
            if (string.IsNullOrEmpty(layout)) return null;
            var lower = layout.ToLowerInvariant().Trim();
            if (lower == "mono") return 1;
            if (lower == "stereo") return 2;
            // "X.Y" format: total = X + Y  (e.g. "5.1" → 6, "7.1" → 8, "2.0" → 2)
            var dot = lower.IndexOf('.');
            if (dot > 0 &&
                int.TryParse(lower.Substring(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out int main) &&
                int.TryParse(lower.Substring(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int lfe))
            {
                return main + lfe;
            }
            if (int.TryParse(lower, NumberStyles.None, CultureInfo.InvariantCulture, out int plain))
                return plain;
            return null;
        }

        private static bool IsValidHttpUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static string MapVideoCodec(string dispatcharrCodec)
        {
            var upper = dispatcharrCodec.ToUpperInvariant();
            if (upper == "H264" || upper == "AVC") return "h264";
            if (upper == "HEVC" || upper == "H265") return "hevc";
            if (upper == "MPEG2VIDEO") return "mpeg2video";
            return dispatcharrCodec.ToLowerInvariant();
        }
    }
}
