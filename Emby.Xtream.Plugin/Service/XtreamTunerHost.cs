using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client;
using Emby.Xtream.Plugin.Client.Models;
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
        private volatile Dictionary<string, int> _tunerChannelIdToStreamId = new Dictionary<string, int>();
        private volatile bool _dispatcharrDataLoaded;
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
            return Plugin.Instance.Configuration.EnableEpg;
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
                return new List<ProgramInfo>();
            }

            var startUnix = startDateUtc.ToUnixTimeSeconds();
            var endUnix = endDateUtc.ToUnixTimeSeconds();

            var result = new List<ProgramInfo>();
            foreach (var p in programs)
            {
                if (p.StopTimestamp <= startUnix || p.StartTimestamp >= endUnix)
                {
                    continue;
                }

                var title = p.IsPlainText ? p.Title : LiveTvService.DecodeBase64(p.Title);
                var description = p.IsPlainText ? p.Description : LiveTvService.DecodeBase64(p.Description);

                result.Add(new ProgramInfo
                {
                    Id = string.Format(CultureInfo.InvariantCulture, "xtream_epg_{0}_{1}", streamId, p.StartTimestamp),
                    ChannelId = tunerChannelId,
                    StartDate = DateTimeOffset.FromUnixTimeSeconds(p.StartTimestamp).UtcDateTime,
                    EndDate = DateTimeOffset.FromUnixTimeSeconds(p.StopTimestamp).UtcDateTime,
                    Name = string.IsNullOrEmpty(title) ? "Unknown" : title,
                    Overview = string.IsNullOrEmpty(description) ? null : description,
                    IsLive = p.IsLive,
                    IsRepeat = p.IsPreviouslyShown,
                    IsPremiere = p.IsNew || p.IsPremiere,
                    ImageUrl = string.IsNullOrEmpty(p.ImageUrl) ? null : p.ImageUrl,
                });
            }

            Logger.Debug("GetProgramsInternal: returning {0} programs for channel {1}", result.Count, streamId);
            return result;
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
                catch (Exception ex)
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
                    var (uuidMap, statsMap, tvgIdMap, stationIdMap) = await _dispatcharrClient.GetChannelDataAsync(
                        config.DispatcharrUrl, cancellationToken).ConfigureAwait(false);
                    newStats = statsMap;
                    _channelUuidMap = uuidMap;
                    _tvgIdMap = tvgIdMap;
                    _stationIdMap = stationIdMap;
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

                return new ChannelInfo
                {
                    Id = CreateEmbyChannelId(tuner, streamIdStr),
                    TunerChannelId = tunerChannelId,
                    Name = cleanName,
                    Number = channel.Num.ToString(CultureInfo.InvariantCulture),
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

            return result;
        }

        private static async Task<List<Client.Models.LiveStreamInfo>> FetchAllChannelsDirectAsync(PluginConfiguration config)
        {
            using (var httpClient = new HttpClient())
            {
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}&action=get_live_streams",
                    config.BaseUrl, config.Username, config.Password);

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

            await EnsureStatsLoadedAsync(cancellationToken).ConfigureAwait(false);

            var config = Plugin.Instance.Configuration;
            var (streamUrl, isDispatcharr) = BuildStreamUrl(config, streamId);
            if (streamUrl == null)
            {
                return new List<MediaSourceInfo>();
            }

            _streamStats.TryGetValue(streamId, out var stats);

            var mediaSource = CreateMediaSourceInfo(streamId, streamUrl, stats, isDispatcharr, config.ForceAudioTranscode);

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

            await EnsureStatsLoadedAsync(cancellationToken).ConfigureAwait(false);

            var config = Plugin.Instance.Configuration;
            var (streamUrl, isDispatcharr) = BuildStreamUrl(config, streamId);
            if (streamUrl == null)
            {
                throw new System.IO.FileNotFoundException(
                    string.Format("Channel {0}: Dispatcharr proxy unavailable and fallback disabled", streamId));
            }
            _streamStats.TryGetValue(streamId, out var stats);

            var mediaSource = CreateMediaSourceInfo(streamId, streamUrl, stats, isDispatcharr, config.ForceAudioTranscode);
            var httpClient = new HttpClient();
            ILiveStream liveStream = new XtreamLiveStream(mediaSource, tuner.Id, httpClient);

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
            _tunerChannelIdToStreamId = new Dictionary<string, int>();
            _dispatcharrDataLoaded = false;
            Logger.Info("Xtream tuner caches cleared");
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
                var (uuidMap, statsMap, tvgIdMap, stationIdMap) = await _dispatcharrClient.GetChannelDataAsync(
                    config.DispatcharrUrl, cancellationToken).ConfigureAwait(false);
                if (statsMap.Count > 0) _streamStats = statsMap;
                if (uuidMap.Count > 0) _channelUuidMap = uuidMap;
                if (tvgIdMap.Count > 0) _tvgIdMap = tvgIdMap;
                if (stationIdMap.Count > 0) _stationIdMap = stationIdMap;
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
            bool disableProbing = false, bool forceAudioTranscode = false)
        {
            var sourceId = "xtream_live_" + streamId.ToString(CultureInfo.InvariantCulture);
            bool hasStats = stats?.VideoCodec != null;

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
            };

            if (hasStats)
            {
                var mediaStreams = new List<MediaStream>();

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
                if (stats.SourceFps.HasValue)
                {
                    videoStream.RealFrameRate = (float)stats.SourceFps.Value;
                    videoStream.AverageFrameRate = (float)stats.SourceFps.Value;
                }
                if (stats.Bitrate.HasValue) videoStream.BitRate = (int)(stats.Bitrate.Value * 1000);

                mediaStreams.Add(videoStream);

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

                mediaStreams.Add(new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = audioCodecLower,
                    Channels = audioChannels,
                    ChannelLayout = channelLayout,
                });

                mediaSource.MediaStreams = mediaStreams;

                Logger.Debug(
                    "Stream {0}: using stats - {1} {2}x{3} @{4}fps, audio {5} {6}ch{7}",
                    streamId, videoCodec, width, height,
                    stats.SourceFps, audioCodecLower ?? "unknown",
                    audioChannels.HasValue ? audioChannels.Value.ToString(CultureInfo.InvariantCulture) : "?",
                    suppressDirectStream ? " [force transcode]" : string.Empty);
            }
            else
            {
                // No stats — provide defaults so hardware decoding can still be attempted.
                mediaSource.MediaStreams = new List<MediaStream>
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Index = 0,
                        IsInterlaced = false,
                        PixelFormat = "yuv420p",
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Index = 1,
                    },
                };
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
