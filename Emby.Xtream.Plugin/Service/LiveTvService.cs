using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client.Models;
using MediaBrowser.Model.Logging;
using STJ = System.Text.Json;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Service for generating M3U playlists and XMLTV EPG files for Live TV.
    /// </summary>
    public class LiveTvService : IDisposable
    {
        private static readonly STJ.JsonSerializerOptions JsonOptions = new STJ.JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
        };

        private readonly ILogger _logger;
        private readonly SemaphoreSlim _m3uLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _epgLock = new SemaphoreSlim(1, 1);
        private readonly object _perChannelEpgLock = new object();

        private Dictionary<int, (List<EpgProgram> Programs, DateTime CacheTime)> _perChannelEpgCache
            = new Dictionary<int, (List<EpgProgram>, DateTime)>();

        private string _cachedM3U;
        private string _cachedCatchupM3U;
        private string _cachedEpgXml;
        private DateTime _m3uCacheTime = DateTime.MinValue;
        private DateTime _catchupCacheTime = DateTime.MinValue;
        private DateTime _epgCacheTime = DateTime.MinValue;
        private bool _disposed;

        public LiveTvService(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Gets the M3U playlist for Live TV channels.
        /// </summary>
        public async Task<string> GetM3UPlaylistAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;

            await _m3uLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cachedM3U != null && DateTime.UtcNow - _m3uCacheTime < TimeSpan.FromMinutes(config.M3UCacheMinutes))
                {
                    _logger.Debug("Returning cached M3U playlist");
                    return _cachedM3U;
                }

                _logger.Info("Generating M3U playlist");
                var channelsTask = GetFilteredChannelsAsync(cancellationToken);
                var categoriesTask = GetLiveCategoriesAsync(cancellationToken);
                Dictionary<int, string> categoryMap;
                try
                {
                    await Task.WhenAll(channelsTask, categoriesTask).ConfigureAwait(false);
                    categoryMap = categoriesTask.Result.ToDictionary(c => c.CategoryId, c => c.CategoryName);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Failed to fetch live categories for M3U group-title; categories will be omitted: {0}", ex.Message);
                    await channelsTask.ConfigureAwait(false);
                    categoryMap = new Dictionary<int, string>();
                }

                var channels = channelsTask.Result;
                var m3u = GenerateM3U(channels, config, categoryMap, catchupOnly: false);

                _cachedM3U = m3u;
                _m3uCacheTime = DateTime.UtcNow;

                return m3u;
            }
            finally
            {
                _m3uLock.Release();
            }
        }

        /// <summary>
        /// Gets the M3U playlist for catch-up enabled channels only.
        /// </summary>
        public async Task<string> GetCatchupM3UPlaylistAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;

            await _m3uLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cachedCatchupM3U != null && DateTime.UtcNow - _catchupCacheTime < TimeSpan.FromMinutes(config.M3UCacheMinutes))
                {
                    _logger.Debug("Returning cached Catchup M3U playlist");
                    return _cachedCatchupM3U;
                }

                _logger.Info("Generating Catchup M3U playlist");
                var channelsTask = GetFilteredChannelsAsync(cancellationToken);
                var categoriesTask = GetLiveCategoriesAsync(cancellationToken);
                Dictionary<int, string> categoryMap;
                try
                {
                    await Task.WhenAll(channelsTask, categoriesTask).ConfigureAwait(false);
                    categoryMap = categoriesTask.Result.ToDictionary(c => c.CategoryId, c => c.CategoryName);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Failed to fetch live categories for catchup M3U group-title; categories will be omitted: {0}", ex.Message);
                    await channelsTask.ConfigureAwait(false);
                    categoryMap = new Dictionary<int, string>();
                }

                var channels = channelsTask.Result;
                var m3u = GenerateM3U(channels, config, categoryMap, catchupOnly: true);

                _cachedCatchupM3U = m3u;
                _catchupCacheTime = DateTime.UtcNow;

                return m3u;
            }
            finally
            {
                _m3uLock.Release();
            }
        }

        /// <summary>
        /// Gets the XMLTV EPG for Live TV channels.
        /// </summary>
        public async Task<string> GetXmltvEpgAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;

            await _epgLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cachedEpgXml != null && DateTime.UtcNow - _epgCacheTime < TimeSpan.FromMinutes(config.EpgCacheMinutes))
                {
                    _logger.Debug("Returning cached XMLTV EPG");
                    return _cachedEpgXml;
                }

                _logger.Info("Generating XMLTV EPG");
                var channels = await GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
                var epgXml = await GenerateXmltvAsync(channels, config, cancellationToken).ConfigureAwait(false);

                _cachedEpgXml = epgXml;
                _epgCacheTime = DateTime.UtcNow;

                return epgXml;
            }
            finally
            {
                _epgLock.Release();
            }
        }

        /// <summary>
        /// Gets the live TV categories from the Xtream API.
        /// </summary>
        public async Task<List<Category>> GetLiveCategoriesAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_live_categories",
                config.BaseUrl, config.Username, config.Password);

            using (var httpClient = new HttpClient())
            {
                var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                var categories = STJ.JsonSerializer.Deserialize<List<Category>>(json, JsonOptions)
                    ?? new List<Category>();
                return categories.OrderBy(c => c.CategoryName).ToList();
            }
        }

        /// <summary>
        /// Invalidates the M3U and EPG caches.
        /// </summary>
        public void InvalidateCache()
        {
            _cachedM3U = null;
            _cachedCatchupM3U = null;
            _cachedEpgXml = null;
            _m3uCacheTime = DateTime.MinValue;
            _catchupCacheTime = DateTime.MinValue;
            _epgCacheTime = DateTime.MinValue;
            lock (_perChannelEpgLock)
            {
                _perChannelEpgCache = new Dictionary<int, (List<EpgProgram>, DateTime)>();
            }
            _logger.Info("Live TV cache invalidated");
        }

        /// <summary>
        /// Gets filtered channels from the Xtream API, applying category filters,
        /// adult filtering, and channel overrides.
        /// </summary>
        internal async Task<List<LiveStreamInfo>> GetFilteredChannelsAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;

            List<LiveStreamInfo> allChannels;

            if (config.SelectedLiveCategoryIds != null && config.SelectedLiveCategoryIds.Length > 0)
            {
                allChannels = new List<LiveStreamInfo>();
                var semaphore = new SemaphoreSlim(5);
                var tasks = config.SelectedLiveCategoryIds.Select(async categoryId =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        return await FetchChannelsByCategoryAsync(categoryId, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var result in results)
                {
                    allChannels.AddRange(result);
                }

                // Remove duplicates by StreamId
                allChannels = allChannels.GroupBy(c => c.StreamId).Select(g => g.First()).ToList();
            }
            else
            {
                allChannels = await FetchAllChannelsAsync(cancellationToken).ConfigureAwait(false);
            }

            // Filter adult channels
            if (!config.IncludeAdultChannels)
            {
                allChannels = allChannels.Where(c => !c.IsAdultChannel).ToList();
            }

            // Channel hash: detect changes and log accordingly
            var newHash = StrmSyncService.ComputeChannelListHash(allChannels);
            if (newHash != config.LastChannelListHash)
            {
                _logger.Info("Channel list changed (hash {0} → {1}), invalidating cache",
                    string.IsNullOrEmpty(config.LastChannelListHash) ? "(none)" : config.LastChannelListHash.Substring(0, 8),
                    newHash.Substring(0, 8));
                config.LastChannelListHash = newHash;
                Plugin.Instance.SaveConfiguration();
            }
            else
            {
                _logger.Debug("Channel list unchanged (hash {0})", newHash.Substring(0, 8));
            }

            _logger.Info("Fetched {0} Live TV channels", allChannels.Count);
            return allChannels;
        }

        private async Task<List<LiveStreamInfo>> FetchAllChannelsAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_live_streams",
                config.BaseUrl, config.Username, config.Password);

            using (var httpClient = new HttpClient())
            {
                var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                return STJ.JsonSerializer.Deserialize<List<LiveStreamInfo>>(json, JsonOptions)
                    ?? new List<LiveStreamInfo>();
            }
        }

        private async Task<List<LiveStreamInfo>> FetchChannelsByCategoryAsync(int categoryId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_live_streams&category_id={3}",
                config.BaseUrl, config.Username, config.Password, categoryId);

            using (var httpClient = new HttpClient())
            {
                try
                {
                    var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                    return STJ.JsonSerializer.Deserialize<List<LiveStreamInfo>>(json, JsonOptions)
                        ?? new List<LiveStreamInfo>();
                }
                catch (Exception ex)
                {
                    _logger.Warn("Failed to fetch channels for category {0}: {1}", categoryId, ex.Message);
                    return new List<LiveStreamInfo>();
                }
            }
        }

        private static string GenerateM3U(List<LiveStreamInfo> channels, PluginConfiguration config, Dictionary<int, string> categoryNames, bool catchupOnly)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#EXTM3U");

            var filteredChannels = catchupOnly
                ? channels.Where(c => c.HasTvArchive && c.TvArchiveDuration > 0).ToList()
                : channels;

            foreach (var channel in filteredChannels.OrderBy(c => c.Num))
            {
                var cleanName = ChannelNameCleaner.CleanChannelName(
                    channel.Name,
                    config.ChannelRemoveTerms,
                    config.EnableChannelNameCleaning);

                var epgId = !string.IsNullOrEmpty(channel.EpgChannelId)
                    ? channel.EpgChannelId
                    : channel.StreamId.ToString(CultureInfo.InvariantCulture);

                var extinf = new StringBuilder();
                extinf.Append("#EXTINF:-1");
                extinf.AppendFormat(CultureInfo.InvariantCulture, " tvg-id=\"{0}\"", EscapeAttribute(epgId));
                extinf.AppendFormat(CultureInfo.InvariantCulture, " tvg-name=\"{0}\"", EscapeAttribute(cleanName));
                extinf.AppendFormat(CultureInfo.InvariantCulture, " tvg-chno=\"{0}\"", channel.Num);

                if (!string.IsNullOrEmpty(channel.StreamIcon))
                {
                    extinf.AppendFormat(CultureInfo.InvariantCulture, " tvg-logo=\"{0}\"", EscapeAttribute(channel.StreamIcon));
                }

                if (channel.CategoryId.HasValue
                    && categoryNames.TryGetValue(channel.CategoryId.Value, out var groupTitle)
                    && !string.IsNullOrEmpty(groupTitle))
                {
                    extinf.AppendFormat(CultureInfo.InvariantCulture, " group-title=\"{0}\"", EscapeAttribute(groupTitle));
                }

                // Add catch-up attributes if enabled and channel supports it
                if (config.EnableCatchup && channel.HasTvArchive && channel.TvArchiveDuration > 0)
                {
                    var catchupDays = Math.Min(config.CatchupDays, channel.TvArchiveDuration);
                    extinf.Append(" catchup=\"default\"");
                    extinf.AppendFormat(CultureInfo.InvariantCulture, " catchup-days=\"{0}\"", catchupDays);

                    var catchupSource = BuildCatchupUrl(config, channel);
                    extinf.AppendFormat(CultureInfo.InvariantCulture, " catchup-source=\"{0}\"", EscapeAttribute(catchupSource));
                }

                extinf.AppendFormat(CultureInfo.InvariantCulture, ",{0}", cleanName);

                sb.AppendLine(extinf.ToString());
                sb.AppendLine(BuildStreamUrl(config, channel));
            }

            return sb.ToString();
        }

        internal static string BuildStreamUrl(PluginConfiguration config, LiveStreamInfo channel)
        {
            var extension = string.Equals(config.LiveTvOutputFormat, "ts", StringComparison.OrdinalIgnoreCase) ? "ts" : "m3u8";
            return string.Format(CultureInfo.InvariantCulture,
                "{0}/live/{1}/{2}/{3}.{4}",
                config.BaseUrl, config.Username, config.Password, channel.StreamId, extension);
        }

        private static string BuildCatchupUrl(PluginConfiguration config, LiveStreamInfo channel)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0}/timeshift/{1}/{2}/{{duration}}/{{start}}/{3}.ts",
                config.BaseUrl, config.Username, config.Password, channel.StreamId);
        }

        private async Task<string> GenerateXmltvAsync(List<LiveStreamInfo> channels, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<tv generator-info-name=\"Emby Xtream Tuner\">");

            // Channel definitions
            foreach (var channel in channels.OrderBy(c => c.Num))
            {
                var cleanName = ChannelNameCleaner.CleanChannelName(
                    channel.Name,
                    config.ChannelRemoveTerms,
                    config.EnableChannelNameCleaning);

                var channelId = !string.IsNullOrEmpty(channel.EpgChannelId)
                    ? channel.EpgChannelId
                    : channel.StreamId.ToString(CultureInfo.InvariantCulture);

                sb.AppendFormat(CultureInfo.InvariantCulture, "  <channel id=\"{0}\">\n", EscapeXml(channelId));
                sb.AppendFormat(CultureInfo.InvariantCulture, "    <display-name>{0}</display-name>\n", EscapeXml(cleanName));
                if (!string.IsNullOrEmpty(channel.StreamIcon))
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, "    <icon src=\"{0}\" />\n", EscapeXml(channel.StreamIcon));
                }
                sb.AppendLine("  </channel>");
            }

            // Fetch EPG data if enabled
            if (config.EnableEpg)
            {
                var epgData = await FetchEpgDataAsync(channels, config, cancellationToken).ConfigureAwait(false);

                foreach (var program in epgData.OrderBy(p => p.StartTimestamp))
                {
                    var startStr = FormatXmltvTime(program.StartTimestamp);
                    var stopStr = FormatXmltvTime(program.StopTimestamp);
                    var channelId = !string.IsNullOrEmpty(program.ChannelId)
                        ? program.ChannelId
                        : program.EpgId;

                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "  <programme start=\"{0}\" stop=\"{1}\" channel=\"{2}\">\n",
                        startStr, stopStr, EscapeXml(channelId));
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "    <title>{0}</title>\n", EscapeXml(DecodeBase64(program.Title)));
                    var desc = DecodeBase64(program.Description);
                    if (!string.IsNullOrEmpty(desc))
                    {
                        sb.AppendFormat(CultureInfo.InvariantCulture,
                            "    <desc>{0}</desc>\n", EscapeXml(desc));
                    }
                    sb.AppendLine("  </programme>");
                }
            }

            sb.AppendLine("</tv>");
            return sb.ToString();
        }

        private async Task<List<EpgProgram>> FetchEpgDataAsync(
            List<LiveStreamInfo> channels,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var allPrograms = new List<EpgProgram>();
            var semaphore = new SemaphoreSlim(5);

            var now = DateTimeOffset.UtcNow;
            var endTime = now.AddDays(config.EpgDaysToFetch);

            var tasks = channels.Select(async channel =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var epgListings = await FetchEpgForChannelAsync(channel.StreamId, cancellationToken).ConfigureAwait(false);

                    if (epgListings == null || epgListings.Listings == null)
                    {
                        return new List<EpgProgram>();
                    }

                    var channelId = !string.IsNullOrEmpty(channel.EpgChannelId)
                        ? channel.EpgChannelId
                        : channel.StreamId.ToString(CultureInfo.InvariantCulture);

                    foreach (var program in epgListings.Listings)
                    {
                        if (string.IsNullOrEmpty(program.ChannelId))
                        {
                            program.ChannelId = channelId;
                        }
                    }

                    var nowUnix = now.ToUnixTimeSeconds();
                    var endUnix = endTime.ToUnixTimeSeconds();
                    return epgListings.Listings
                        .Where(p => p.StopTimestamp > nowUnix && p.StartTimestamp < endUnix)
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger.Debug("Failed to fetch EPG for channel {0}: {1}", channel.StreamId, ex.Message);
                    return new List<EpgProgram>();
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var result in results)
            {
                allPrograms.AddRange(result);
            }

            _logger.Info("Fetched {0} EPG programs for {1} channels", allPrograms.Count, channels.Count);
            return allPrograms;
        }

        /// <summary>
        /// Fetches EPG data for a single channel, with per-channel caching.
        /// Used by the tuner host to serve guide data directly.
        /// </summary>
        internal async Task<List<EpgProgram>> FetchEpgForChannelCachedAsync(int streamId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            var cacheTtl = TimeSpan.FromMinutes(config.EpgCacheMinutes);

            lock (_perChannelEpgLock)
            {
                if (_perChannelEpgCache.TryGetValue(streamId, out var entry)
                    && DateTime.UtcNow - entry.CacheTime < cacheTtl)
                {
                    return entry.Programs;
                }
            }

            var epgListings = await FetchEpgForChannelAsync(streamId, cancellationToken).ConfigureAwait(false);
            var programs = epgListings?.Listings ?? new List<EpgProgram>();

            lock (_perChannelEpgLock)
            {
                _perChannelEpgCache[streamId] = (programs, DateTime.UtcNow);
            }

            return programs;
        }

        private async Task<EpgListings> FetchEpgForChannelAsync(int streamId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_simple_data_table&stream_id={3}",
                config.BaseUrl, config.Username, config.Password, streamId);

            using (var httpClient = new HttpClient())
            {
                var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                return STJ.JsonSerializer.Deserialize<EpgListings>(json, JsonOptions);
            }
        }

        private static string FormatXmltvTime(long unixTimestamp)
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).UtcDateTime;
            return dt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";
        }

        private static string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        internal static string DecodeBase64(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch (FormatException)
            {
                return value;
            }
        }

        private static string EscapeAttribute(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\"", "&quot;")
                .Replace("&", "&amp;");
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _m3uLock.Dispose();
                    _epgLock.Dispose();
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
        }
    }
}
