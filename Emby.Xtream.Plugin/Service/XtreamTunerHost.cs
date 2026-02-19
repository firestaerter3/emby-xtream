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
        private List<ChannelInfo> _cachedChannels;
        private DateTime _cacheTime = DateTime.MinValue;

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
            if (!int.TryParse(tunerChannelId, NumberStyles.None, CultureInfo.InvariantCulture, out int streamId))
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

                var title = LiveTvService.DecodeBase64(p.Title);
                var description = LiveTvService.DecodeBase64(p.Description);

                result.Add(new ProgramInfo
                {
                    Id = string.Format(CultureInfo.InvariantCulture, "xtream_epg_{0}_{1}", streamId, p.StartTimestamp),
                    ChannelId = tunerChannelId,
                    StartDate = DateTimeOffset.FromUnixTimeSeconds(p.StartTimestamp).UtcDateTime,
                    EndDate = DateTimeOffset.FromUnixTimeSeconds(p.StopTimestamp).UtcDateTime,
                    Name = string.IsNullOrEmpty(title) ? "Unknown" : title,
                    Overview = string.IsNullOrEmpty(description) ? null : description,
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
                Logger.Debug("Returning cached channel list ({0} channels)", _cachedChannels.Count);
                return _cachedChannels;
            }

            Logger.Info("Fetching channels from Xtream API");

            // Use LiveTvService for filtered channels (category filtering + overrides)
            var liveTvService = Plugin.Instance.LiveTvService;
            List<Client.Models.LiveStreamInfo> channels;
            try
            {
                channels = await liveTvService.GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn("LiveTvService channel fetch failed, falling back to direct API: {0}", ex.Message);
                channels = await FetchAllChannelsDirectAsync(config).ConfigureAwait(false);
            }

            // Fetch stream stats and UUID map from Dispatcharr in a single API call
            var newStats = new Dictionary<int, StreamStatsInfo>();
            if (config.EnableDispatcharr && !string.IsNullOrEmpty(config.DispatcharrUrl))
            {
                try
                {
                    _dispatcharrClient.Configure(config.DispatcharrUser, config.DispatcharrPass);
                    var (uuidMap, statsMap) = await _dispatcharrClient.GetChannelDataAsync(
                        config.DispatcharrUrl, cancellationToken).ConfigureAwait(false);
                    newStats = statsMap;
                    _channelUuidMap = uuidMap;
                }
                catch (Exception ex)
                {
                    Logger.Warn("Failed to fetch Dispatcharr channel data: {0}", ex.Message);
                }
            }

            int statsCount = newStats.Count;

            var result = channels.Select(channel =>
            {
                var cleanName = ChannelNameCleaner.CleanChannelName(
                    channel.Name,
                    config.ChannelRemoveTerms,
                    config.EnableChannelNameCleaning);

                var streamIdStr = channel.StreamId.ToString(CultureInfo.InvariantCulture);
                return new ChannelInfo
                {
                    Id = CreateEmbyChannelId(tuner, streamIdStr),
                    TunerChannelId = streamIdStr,
                    Name = cleanName,
                    Number = channel.Num.ToString(CultureInfo.InvariantCulture),
                    ImageUrl = string.IsNullOrEmpty(channel.StreamIcon) ? null : channel.StreamIcon,
                    ChannelType = ChannelType.TV,
                    TunerHostId = tuner.Id,
                };
            }).ToList();

            _streamStats = newStats;
            _cachedChannels = result;
            _cacheTime = DateTime.UtcNow;
            Logger.Info("Channel list cached with {0} channels ({1} with stream stats)",
                result.Count, statsCount);

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
            if (!TryParseStreamId(tunerChannel, out int streamId))
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

            var mediaSource = CreateMediaSourceInfo(streamId, streamUrl, stats, isDispatcharr);

            return new List<MediaSourceInfo> { mediaSource };
        }

        protected override async Task<ILiveStream> GetChannelStream(
            TunerHostInfo tuner, MediaBrowser.Controller.Entities.BaseItem dbChannel,
            ChannelInfo tunerChannel, string mediaSourceId,
            CancellationToken cancellationToken)
        {
            if (!TryParseStreamId(tunerChannel, out int streamId))
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

            var mediaSource = CreateMediaSourceInfo(streamId, streamUrl, stats, isDispatcharr);
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
            Logger.Info("Xtream tuner caches cleared");
        }

        /// <summary>
        /// Ensures Dispatcharr stats and UUID mappings are loaded. Called lazily
        /// on first playback if GetChannelsInternal hasn't run yet (e.g. after restart).
        /// </summary>
        private async Task EnsureStatsLoadedAsync(CancellationToken cancellationToken)
        {
            if (_streamStats.Count > 0 && _channelUuidMap.Count > 0)
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
                var (uuidMap, statsMap) = await _dispatcharrClient.GetChannelDataAsync(
                    config.DispatcharrUrl, cancellationToken).ConfigureAwait(false);
                if (statsMap.Count > 0) _streamStats = statsMap;
                if (uuidMap.Count > 0) _channelUuidMap = uuidMap;
                Logger.Info("Loaded {0} UUIDs and {1} stream stats from Dispatcharr on-demand",
                    uuidMap.Count, statsMap.Count);
            }
            catch (Exception ex)
            {
                Logger.Warn("On-demand Dispatcharr data fetch failed: {0}", ex.Message);
            }
        }

        private static bool TryParseStreamId(ChannelInfo tunerChannel, out int streamId)
        {
            streamId = 0;
            if (tunerChannel == null) return false;

            var id = tunerChannel.TunerChannelId ?? tunerChannel.Id;
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
            int streamId, string streamUrl, StreamStatsInfo stats, bool disableProbing = false)
        {
            var sourceId = "xtream_live_" + streamId.ToString(CultureInfo.InvariantCulture);
            bool hasStats = stats?.VideoCodec != null;

            // Disable probing for Dispatcharr proxy URLs: the probe opens a short-lived HTTP
            // connection that Dispatcharr interprets as a client, and when it closes after
            // analysis (~0.1s) Dispatcharr tears down the channel. The real playback connection
            // then hits the teardown and fails, causing a rapid retry storm.
            bool suppressProbing = disableProbing || hasStats;

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
                SupportsDirectStream = true,
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

                mediaStreams.Add(new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = -1,
                    Codec = !string.IsNullOrEmpty(stats.AudioCodec)
                        ? stats.AudioCodec.ToLowerInvariant()
                        : null,
                });

                mediaSource.MediaStreams = mediaStreams;

                Logger.Debug(
                    "Stream {0}: using stats - {1} {2}x{3} @{4}fps, audio {5}",
                    streamId, videoCodec, width, height,
                    stats.SourceFps, stats.AudioCodec ?? "unknown");
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
