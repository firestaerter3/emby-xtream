using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client.Models;
using MediaBrowser.Model.Logging;
using STJ = System.Text.Json;

namespace Emby.Xtream.Plugin.Service
{
    public class SyncProgress
    {
        public string Phase = string.Empty;
        public int Total;
        public int Completed;
        public int Skipped;
        public int Failed;
        public int Added;
        public int Deleted;
        public bool IsRunning;

        /// <summary>Set when sync exits early (e.g. invalid folder configuration).</summary>
        public string AbortReason = string.Empty;
    }

    public class SyncHistoryEntry
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public bool Success { get; set; }
        public int MoviesTotal { get; set; }
        public int MoviesCompleted { get; set; }
        public int MoviesAdded { get; set; }
        public int MoviesSkipped { get; set; }
        public int MoviesFailed { get; set; }
        public int MoviesDeleted { get; set; }
        public int SeriesTotal { get; set; }
        public int SeriesCompleted { get; set; }
        public int SeriesAdded { get; set; }
        public int SeriesSkipped { get; set; }
        public int SeriesFailed { get; set; }
        public int SeriesDeleted { get; set; }
        public int EpisodeTotal { get; set; }
        public int EpisodeAdded { get; set; }
        public int EpisodeSkipped { get; set; }
        public int EpisodeFailed { get; set; }
        public int EpisodeDeleted { get; set; }
        public bool WasMovieSync { get; set; }
        public bool WasSeriesSync { get; set; }
        public List<string> AddedMovieTitles { get; set; } = new List<string>();
        public List<string> AddedSeriesTitles { get; set; } = new List<string>();
    }

    public class FailedSyncItem
    {
        public string ItemType { get; set; }   // "Movie" | "Series"
        public int StreamId { get; set; }
        public string Name { get; set; }
        public int? CategoryId { get; set; }
        public string TmdbId { get; set; }
        public string ContainerExtension { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime FailedAt { get; set; } = DateTime.UtcNow;
    }

    public class StrmSyncService
    {
        private static readonly STJ.JsonSerializerOptions JsonOptions = new STJ.JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
            // Providers sometimes send string fields (e.g. info.releasedate, rating, tmdb) as
            // numbers, booleans, null, or empty arrays, and integer fields (e.g. category_id) as
            // empty/non-numeric strings or arrays. Coerce them instead of failing the sync.
            Converters =
            {
                new Client.Models.TolerantStringConverter(),
                new Client.Models.TolerantNullableIntConverter(),
            },
        };

        private static readonly Regex InvalidFileCharsRegex = new Regex(
            @"[<>:""/\\|?*\x00-\x1F]",
            RegexOptions.Compiled);

        private static readonly Regex YearInTitleRegex = new Regex(
            @"\((\d{4})\)\s*$",
            RegexOptions.Compiled);

        private static readonly Regex FolderIdSuffixRegex = new Regex(
            @" \[(?:tmdbid|tvdbid)=\d+\]$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly int MaxHistoryEntries = 10;
        private static readonly HttpClient SharedHttpClient = new HttpClient(
            new XtreamRateLimitHandler { InnerHandler = new HttpClientHandler() })
        { Timeout = TimeSpan.FromSeconds(30) };

        // Increment when naming logic changes so existing installs force a full re-sync on next run.
        internal const int CurrentStrmNamingVersion = 1;

        private static void ApplyUserAgentToSharedClient()
        {
            var ua = Plugin.InstanceOrNull?.Configuration?.HttpUserAgent;
            SharedHttpClient.DefaultRequestHeaders.Remove("User-Agent");
            if (!string.IsNullOrEmpty(ua))
                SharedHttpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);
        }

        private readonly ILogger _logger;
        private readonly TmdbLookupService _tmdbLookupService;
        private readonly HttpClient _httpClient;
        private List<SyncHistoryEntry> _syncHistory;
        private readonly object _historyLock = new object();
        private readonly List<FailedSyncItem> _failedItems = new List<FailedSyncItem>();
        private readonly object _failedItemsLock = new object();

        private SyncProgress _movieProgress = new SyncProgress();
        private SyncProgress _seriesProgress = new SyncProgress();
        private SyncProgress _episodeProgress = new SyncProgress();

        private static void ReportTaskProgress(SyncProgress syncProgress, IProgress<double> taskProgress)
        {
            if (taskProgress == null) return;
            var total = Volatile.Read(ref syncProgress.Total);
            if (total <= 0) return;
            var completed = Volatile.Read(ref syncProgress.Completed);
            var pct = Math.Min(100.0, (double)completed / total * 100.0);
            taskProgress.Report(pct);
        }

        public StrmSyncService(ILogger logger, HttpClient httpClient = null)
        {
            _logger = logger;
            _tmdbLookupService = new TmdbLookupService(logger);
            _httpClient = httpClient ?? SharedHttpClient;
        }

        /// <summary>
        /// Computes a stable hash of a series' episode URLs for change detection.
        /// The hash covers episode ID + extension for each episode, sorted by season+episode
        /// to be order-independent of the JSON layout.
        /// </summary>
        internal static string ComputeSeriesEpisodeHash(Dictionary<string, List<EpisodeInfo>> episodes)
        {
            var sb = new StringBuilder();
            foreach (var seasonEntry in episodes.OrderBy(e => e.Key))
            {
                foreach (var ep in seasonEntry.Value.OrderBy(e => e.Season).ThenBy(e => e.EpisodeNum))
                {
                    sb.Append(ep.Id);
                    sb.Append('.');
                    sb.Append(ep.ContainerExtension ?? "mp4");
                    sb.Append('|');
                }
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        /// <summary>
        /// Computes a stable hash of the channel list for change detection.
        /// </summary>
        internal static string ComputeChannelListHash(List<LiveStreamInfo> channels)
        {
            var sorted = channels.OrderBy(c => c.StreamId);
            var sb = new StringBuilder();
            foreach (var c in sorted)
            {
                sb.Append(c.StreamId);
                sb.Append(':');
                sb.Append(c.Name ?? string.Empty);
                sb.Append(':');
                sb.Append(c.EpgChannelId ?? string.Empty);
                sb.Append(':');
                sb.Append(c.CategoryId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                sb.Append('|');
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        internal static Dictionary<string, string> DeserializeEpisodeHashes(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, string>();
            try
            {
                return STJ.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }

        internal static string SerializeEpisodeHashes(ConcurrentDictionary<string, string> hashes)
        {
            if (hashes == null || hashes.IsEmpty)
                return string.Empty;
            return STJ.JsonSerializer.Serialize(hashes);
        }

        public SyncProgress MovieProgress => _movieProgress;
        public SyncProgress SeriesProgress => _seriesProgress;

        public IReadOnlyList<FailedSyncItem> FailedItems
        {
            get { lock (_failedItemsLock) { return _failedItems.ToList(); } }
        }

        // Lazy-loaded from PluginConfiguration.SyncHistoryJson so history survives restarts.
        // Must be called inside _historyLock.
        private List<SyncHistoryEntry> GetOrLoadHistory()
        {
            if (_syncHistory != null) return _syncHistory;

            _syncHistory = new List<SyncHistoryEntry>();
            try
            {
                var json = Plugin.InstanceOrNull?.Configuration?.SyncHistoryJson;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var loaded = STJ.JsonSerializer.Deserialize<List<SyncHistoryEntry>>(json, JsonOptions);
                    if (loaded != null) _syncHistory.AddRange(loaded);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to load sync history from config: {0}", ex.Message);
            }

            return _syncHistory;
        }

        public List<SyncHistoryEntry> GetSyncHistory()
        {
            lock (_historyLock)
            {
                return new List<SyncHistoryEntry>(GetOrLoadHistory());
            }
        }

        /// <summary>
        /// Checks whether the stored STRM naming version is current. If not, resets sync timestamps
        /// so the next run performs a full re-sync and regenerates files with corrected names.
        /// Returns true when a version upgrade was applied (timestamps were reset), false otherwise.
        /// </summary>
        internal bool CheckAndUpgradeNamingVersion(PluginConfiguration config, Action saveConfig)
        {
            if (config.StrmNamingVersion >= CurrentStrmNamingVersion)
                return false;

            _logger.Info("STRM naming version upgraded ({0} → {1}); resetting sync timestamps for full re-sync",
                config.StrmNamingVersion, CurrentStrmNamingVersion);

            config.StrmNamingVersion = CurrentStrmNamingVersion;
            config.LastMovieSyncTimestamp = 0;
            config.LastSeriesSyncTimestamp = 0;
            config.SeriesEpisodeHashesJson = string.Empty;
            saveConfig?.Invoke();
            return true;
        }

        public async Task SyncMoviesAsync(PluginConfiguration config, CancellationToken cancellationToken, Action saveConfig = null, IProgress<double> taskProgress = null)
        {
            ApplyUserAgentToSharedClient();
            CheckAndUpgradeNamingVersion(config, saveConfig);
            _movieProgress = new SyncProgress { IsRunning = true, Phase = "Starting movie sync" };
            lock (_failedItemsLock) { _failedItems.Clear(); }
            var movieSyncStart = DateTime.UtcNow;
            var movieSyncSuccess = true;
            var addedMovieTitles = new List<string>();

            try
            {
                EnsureStrmLibraryPath(config.StrmLibraryPath);

                var folderMappings = FolderMappingParser.Parse(config.MovieFolderMappings);
                if (string.Equals(config.MovieFolderMode, "custom", StringComparison.OrdinalIgnoreCase) &&
                    folderMappings.Count == 0)
                {
                    movieSyncSuccess = false;
                    _movieProgress.AbortReason =
                        "Multiple Folders mode is on but no categories are assigned to any folder. " +
                        "Click + Add Folder, name it, use Refresh Categories, tick the VOD categories for that folder, then save plugin settings. " +
                        "Or switch back to Single Folder to use the flat category list.";
                    _movieProgress.Phase = "Configuration needed";
                    _logger.Warn("Movie sync aborted: {0}", _movieProgress.AbortReason);
                    return;
                }

                var categoryNames = new Dictionary<int, string>();

                // Fetch category names if needed for folder organization
                if (!string.Equals(config.MovieFolderMode, "single", StringComparison.OrdinalIgnoreCase))
                {
                    _movieProgress.Phase = "Fetching VOD categories";
                    var categories = await FetchCategoriesAsync("get_vod_categories", config, cancellationToken).ConfigureAwait(false);
                    foreach (var cat in categories)
                    {
                        categoryNames[cat.CategoryId] = cat.CategoryName;
                    }
                }

                // Fetch streams for selected categories
                _movieProgress.Phase = "Fetching VOD streams";
                var vodFetch = await FetchVodStreamsAsync(config.SelectedVodCategoryIds, config, cancellationToken).ConfigureAwait(false);
                var fetchedStreams = vodFetch.Items;

                if (vodFetch.HadFailures)
                {
                    _logger.Warn(
                        "{0} of {1} VOD categories failed to answer — orphan cleanup will be skipped this run to avoid deleting files for the categories that did not report",
                        vodFetch.FailedCategoryCount, vodFetch.RequestedCategoryCount);
                }

                // Per-item exclusions (issue #57): split the catalogue before anything else reads it.
                // The excluded half is kept so its on-disk folders can be removed below.
                var excludedVodSet = ContentExclusionFilter.BuildSet(config.ExcludedVodStreamIds);
                var excludedMovies = new List<Tuple<string, int?>>();
                var allStreams = fetchedStreams;
                if (excludedVodSet.Count > 0)
                {
                    allStreams = new List<VodStreamInfo>();
                    foreach (var s in fetchedStreams)
                    {
                        if (ContentExclusionFilter.IsExcluded(excludedVodSet, s.StreamId))
                        {
                            var excludedName = config.EnableContentNameCleaning
                                ? ContentNameCleaner.CleanContentName(s.Name, config.ContentRemoveTerms)
                                : s.Name;
                            excludedMovies.Add(Tuple.Create(excludedName, s.CategoryId));
                        }
                        else
                        {
                            allStreams.Add(s);
                        }
                    }

                    _logger.Info("Per-item exclusions: skipping {0} of {1} movies",
                        excludedMovies.Count, fetchedStreams.Count);
                }

                // Delta sync: split into new (not yet synced) and existing
                var lastMovieTs = config.LastMovieSyncTimestamp;
                var newStreams = lastMovieTs > 0
                    ? allStreams.Where(m => m.Added > lastMovieTs).ToList()
                    : allStreams;
                var existingStreams = lastMovieTs > 0
                    ? allStreams.Where(m => m.Added <= lastMovieTs).ToList()
                    : new List<VodStreamInfo>();

                _logger.Info("Delta movie sync: {0} new, {1} existing (since timestamp {2})",
                    newStreams.Count, existingStreams.Count, lastMovieTs);

                _movieProgress.Total = allStreams.Count;
                _movieProgress.Phase = "Writing STRM files";

                // Log TMDB statistics
                if (config.EnableTmdbFolderNaming)
                {
                    var withTmdb = allStreams.Count(m => IsValidTmdbId(m.TmdbId));
                    var without = allStreams.Count - withTmdb;
                    var pct = allStreams.Count > 0 ? (int)(100.0 * withTmdb / allStreams.Count) : 0;
                    _logger.Info("TMDB IDs available: {0}/{1} movies ({2}%){3}",
                        withTmdb, allStreams.Count, pct,
                        config.EnableTmdbFallbackLookup
                            ? string.Format(CultureInfo.InvariantCulture, " — TMDB fallback lookup enabled for {0} movies without IDs", without)
                            : string.Empty);
                }

                _logger.Info("Starting movie STRM sync for {0} streams", allStreams.Count);

                var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var semaphore = new SemaphoreSlim(config.SyncParallelism);

                // Shared Dispatcharr VOD client — only queried per-movie, after smart-skip
                Emby.Xtream.Plugin.Client.DispatcharrClient dispatcharrVodClient = null;
                if (config.EnableDispatcharr && !string.IsNullOrEmpty(config.DispatcharrUrl))
                {
                    dispatcharrVodClient = new Emby.Xtream.Plugin.Client.DispatcharrClient(_logger);
                    dispatcharrVodClient.Configure(config.DispatcharrUser, config.DispatcharrPass);
                }

                var tasks = allStreams.Select(async movie =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var cleanedName = config.EnableContentNameCleaning
                            ? ContentNameCleaner.CleanContentName(movie.Name, config.ContentRemoveTerms)
                            : movie.Name;
                        var movieName = SanitizeFileName(cleanedName);
                        if (string.IsNullOrWhiteSpace(movieName))
                        {
                            Interlocked.Increment(ref _movieProgress.Failed);
                            return;
                        }

                        // Determine TMDB ID for folder naming
                        string tmdbId = null;
                        if (config.EnableTmdbFolderNaming)
                        {
                            if (IsValidTmdbId(movie.TmdbId))
                            {
                                tmdbId = movie.TmdbId.Trim();
                            }
                            else if (config.EnableTmdbFallbackLookup)
                            {
                                var yearMatch2 = YearInTitleRegex.Match(cleanedName);
                                int? yearForLookup = null;
                                if (yearMatch2.Success)
                                {
                                    int y;
                                    if (int.TryParse(yearMatch2.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out y))
                                    {
                                        yearForLookup = y;
                                    }
                                }

                                try
                                {
                                    tmdbId = await _tmdbLookupService.LookupTmdbIdAsync(cleanedName, yearForLookup, cancellationToken).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _logger.Debug("TMDB fallback error for '{0}': {1}", cleanedName, ex.Message);
                                }
                            }
                        }

                        var folderName = BuildMovieFolderName(cleanedName, tmdbId);
                        if (string.IsNullOrWhiteSpace(folderName))
                        {
                            Interlocked.Increment(ref _movieProgress.Failed);
                            return;
                        }

                        var subFolder = BuildContentFolderPath(
                            config.MovieFolderMode, movie.CategoryId, categoryNames, folderMappings, "Movies");

                        if (subFolder == null)
                        {
                            Interlocked.Increment(ref _movieProgress.Skipped);
                            Interlocked.Increment(ref _movieProgress.Completed);
                            ReportTaskProgress(_movieProgress, taskProgress);
                            return;
                        }

                        var movieDir = Path.Combine(config.StrmLibraryPath, subFolder, folderName);
                        var strmPath = Path.Combine(movieDir, folderName + ".strm");

                        // Smart skip: if file already exists AND the movie is not new (delta), skip
                        var isNewMovie = lastMovieTs == 0 || movie.Added > lastMovieTs;
                        if (!isNewMovie && config.SmartSkipExisting && File.Exists(strmPath))
                        {
                            lock (writtenPaths)
                            {
                                writtenPaths.Add(strmPath);
                            }
                            Interlocked.Increment(ref _movieProgress.Skipped);
                            Interlocked.Increment(ref _movieProgress.Completed);
                            ReportTaskProgress(_movieProgress, taskProgress);
                            return;
                        }

                        var ext = !string.IsNullOrEmpty(movie.ContainerExtension)
                            ? movie.ContainerExtension
                            : "mp4";

                        var streamUrl = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}/movie/{1}/{2}/{3}.{4}",
                            config.BaseUrl, config.Username, config.Password, movie.StreamId, ext);

                        // Build list of STRM entries (multi-version via Dispatcharr, or single)
                        var strmEntries = new List<Tuple<string, string>>();

                        if (dispatcharrVodClient != null)
                        {
                            try
                            {
                                var vodDetail = await dispatcharrVodClient.GetVodMovieDetailAsync(
                                    config.DispatcharrUrl, movie.StreamId, cancellationToken).ConfigureAwait(false);
                                if (vodDetail != null && !string.IsNullOrEmpty(vodDetail.Uuid))
                                {
                                    var providers = await dispatcharrVodClient.GetVodMovieProvidersAsync(
                                        config.DispatcharrUrl, movie.StreamId, cancellationToken).ConfigureAwait(false);
                                    if (providers.Count > 1)
                                    {
                                        for (int vi = 0; vi < providers.Count; vi++)
                                        {
                                            var suffix = vi == 0 ? string.Empty
                                                : string.Format(CultureInfo.InvariantCulture, " - Version {0}", vi + 1);
                                            var providerUrl = string.Format(
                                                CultureInfo.InvariantCulture,
                                                "{0}/proxy/vod/movie/{1}?stream_id={2}",
                                                config.DispatcharrUrl, vodDetail.Uuid, providers[vi].StreamId);
                                            strmEntries.Add(Tuple.Create(
                                                Path.Combine(movieDir, folderName + suffix + ".strm"),
                                                providerUrl));
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Debug("Dispatcharr VOD lookup failed for '{0}': {1}", movie.Name, ex.Message);
                            }
                        }

                        if (strmEntries.Count == 0)
                            strmEntries.Add(Tuple.Create(strmPath, streamUrl));

                        Directory.CreateDirectory(movieDir);
                        var isAnyNewFile = false;
                        foreach (var entry in strmEntries)
                        {
                            var itemPath = entry.Item1;
                            var itemUrl = entry.Item2;
                            var fileExists = File.Exists(itemPath);

                            // Skip write if file content is already up to date (avoids Emby library re-scan)
                            if (!fileExists || File.ReadAllText(itemPath) != itemUrl)
                            {
                                File.WriteAllText(itemPath, itemUrl);
                                if (!fileExists)
                                {
                                    Interlocked.Increment(ref _movieProgress.Added);
                                    isAnyNewFile = true;
                                }
                            }
                            lock (writtenPaths) { writtenPaths.Add(itemPath); }
                        }
                        if (isAnyNewFile)
                        {
                            lock (addedMovieTitles)
                            {
                                if (addedMovieTitles.Count < 20) addedMovieTitles.Add(cleanedName);
                            }
                        }

                        if (config.EnableNfoFiles)
                        {
                            var nfoPath = Path.Combine(movieDir, folderName + ".nfo");
                            var yearMatch = YearInTitleRegex.Match(cleanedName);
                            int? nfoYear = null;
                            if (yearMatch.Success)
                            {
                                int y;
                                if (int.TryParse(yearMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out y))
                                    nfoYear = y;
                            }
                            try { NfoWriter.WriteMovieNfo(nfoPath, cleanedName, tmdbId, nfoYear); }
                            catch (Exception ex) { _logger.Debug("NFO write failed for '{0}': {1}", movie.Name, ex.Message); }
                        }

                        Interlocked.Increment(ref _movieProgress.Completed);
                        ReportTaskProgress(_movieProgress, taskProgress);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Failed to write STRM for movie '{0}': [{1}] {2}", movie.Name, ex.GetType().Name, ex.Message);
                        lock (_failedItemsLock)
                        {
                            _failedItems.Add(new FailedSyncItem
                            {
                                ItemType = "Movie",
                                StreamId = movie.StreamId,
                                Name = movie.Name,
                                CategoryId = movie.CategoryId,
                                TmdbId = movie.TmdbId,
                                ContainerExtension = movie.ContainerExtension,
                                ErrorMessage = ex.Message
                            });
                        }
                        Interlocked.Increment(ref _movieProgress.Failed);
                        Interlocked.Increment(ref _movieProgress.Completed);
                        ReportTaskProgress(_movieProgress, taskProgress);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);

                // Remove folders for explicitly excluded movies. Deliberately before orphan
                // cleanup and independent of it — see RemoveExcludedContent remarks.
                // Note both passes accumulate into Deleted, which therefore counts folders
                // (exclusions) and files (orphans) together. The dashboard shows one number.
                if (excludedMovies.Count > 0)
                {
                    _movieProgress.Phase = "Removing excluded movies";
                    _movieProgress.Deleted += RemoveExcludedContent(
                        config, excludedMovies, config.MovieFolderMode, categoryNames, folderMappings, "Movies");
                }

                // Cleanup orphans. Skipped when any category failed to answer: those titles are
                // missing from writtenPaths through no fault of their own, so deleting what is
                // "orphaned" would delete a working category's library.
                if (config.CleanupOrphans && _movieProgress.Failed == 0 && !vodFetch.HadFailures)
                {
                    _movieProgress.Phase = "Cleaning up orphaned files";
                    var moviesRoot = Path.Combine(config.StrmLibraryPath, "Movies");
                    _movieProgress.Deleted += CleanupOrphans(moviesRoot, writtenPaths, config.OrphanSafetyThreshold, config);
                }

                // Persist the highest Added timestamp seen so next sync can delta from here.
                // Computed over the UNFILTERED catalogue: if the newest movie happens to be
                // excluded, the watermark must still advance past it or every later sync
                // re-processes everything after it.
                //
                // A partial fetch still advances the watermark. fetchedStreams holds only the
                // categories that answered, so this moves past what was actually processed;
                // freezing it because some other category 502'd would make every later sync
                // re-process the categories that succeeded.
                if (fetchedStreams.Count > 0)
                {
                    var maxAdded = fetchedStreams.Max(m => m.Added);
                    if (maxAdded > config.LastMovieSyncTimestamp)
                    {
                        config.LastMovieSyncTimestamp = maxAdded;
                        saveConfig?.Invoke();
                    }
                }

                _logger.Info("Movie STRM sync completed: {0} written, {1} skipped, {2} failed",
                    _movieProgress.Completed - _movieProgress.Skipped, _movieProgress.Skipped, _movieProgress.Failed);
            }
            catch (Exception ex)
            {
                _logger.Error("Movie sync failed: {0}", ex.Message);
                _movieProgress.Phase = "Failed: " + ex.Message;
                movieSyncSuccess = false;
                throw;
            }
            finally
            {
                _movieProgress.IsRunning = false;
                if (string.IsNullOrEmpty(_movieProgress.AbortReason))
                {
                    _movieProgress.Phase = "Complete";
                }

                AddHistoryEntry(new SyncHistoryEntry
                {
                    StartTime = movieSyncStart,
                    EndTime = DateTime.UtcNow,
                    Success = movieSyncSuccess,
                    WasMovieSync = true,
                    MoviesTotal = _movieProgress.Total,
                    MoviesCompleted = _movieProgress.Completed,
                    MoviesAdded = _movieProgress.Added,
                    MoviesSkipped = _movieProgress.Skipped,
                    MoviesFailed = _movieProgress.Failed,
                    MoviesDeleted = _movieProgress.Deleted,
                    AddedMovieTitles = addedMovieTitles,
                });
            }
        }

        public async Task SyncSeriesAsync(PluginConfiguration config, CancellationToken cancellationToken, Action saveConfig = null, IProgress<double> taskProgress = null)
        {
            ApplyUserAgentToSharedClient();
            CheckAndUpgradeNamingVersion(config, saveConfig);
            _seriesProgress = new SyncProgress { IsRunning = true, Phase = "Starting series sync" };
            _episodeProgress = new SyncProgress { IsRunning = true };
            lock (_failedItemsLock) { _failedItems.RemoveAll(i => i.ItemType == "Series"); }
            var seriesSyncStart = DateTime.UtcNow;
            var seriesSyncSuccess = true;
            var addedSeriesTitles = new List<string>();

            try
            {
                EnsureStrmLibraryPath(config.StrmLibraryPath);

                var folderMappings = FolderMappingParser.Parse(config.SeriesFolderMappings);
                if (string.Equals(config.SeriesFolderMode, "custom", StringComparison.OrdinalIgnoreCase) &&
                    folderMappings.Count == 0)
                {
                    seriesSyncSuccess = false;
                    _seriesProgress.AbortReason =
                        "Multiple Folders mode is on but no categories are assigned to any folder. " +
                        "Click + Add Folder, name it, use Refresh Categories, tick the series categories for that folder, then save plugin settings. " +
                        "Or switch back to Single Folder to use the flat category list.";
                    _seriesProgress.Phase = "Configuration needed";
                    _logger.Warn("Series sync aborted: {0}", _seriesProgress.AbortReason);
                    return;
                }

                var categoryNames = new Dictionary<int, string>();

                if (!string.Equals(config.SeriesFolderMode, "single", StringComparison.OrdinalIgnoreCase))
                {
                    _seriesProgress.Phase = "Fetching series categories";
                    var categories = await FetchSeriesCategoriesWithFallbackAsync(config, cancellationToken).ConfigureAwait(false);
                    foreach (var cat in categories)
                    {
                        categoryNames[cat.CategoryId] = cat.CategoryName;
                    }
                }

                // Parse TVDb overrides once before the loop
                var tvdbOverrides = config.EnableSeriesIdFolderNaming
                    ? ParseTvdbOverrides(config.TvdbFolderIdOverrides)
                    : null;

                _seriesProgress.Phase = "Fetching series list";
                var seriesFetch = await FetchSeriesListAsync(config.SelectedSeriesCategoryIds, config, cancellationToken).ConfigureAwait(false);
                var fetchedSeries = seriesFetch.Items;

                if (seriesFetch.HadFailures)
                {
                    _logger.Warn(
                        "{0} of {1} series categories failed to answer — orphan cleanup will be skipped this run to avoid deleting files for the categories that did not report",
                        seriesFetch.FailedCategoryCount, seriesFetch.RequestedCategoryCount);
                }

                // Per-item exclusions (issue #57) — see the matching block in SyncMoviesAsync.
                var excludedSeriesSet = ContentExclusionFilter.BuildSet(config.ExcludedSeriesIds);
                var excludedSeriesItems = new List<Tuple<string, int?>>();
                var excludedSeriesRaw = new List<SeriesInfo>();
                var allSeries = fetchedSeries;
                if (excludedSeriesSet.Count > 0)
                {
                    allSeries = new List<SeriesInfo>();
                    foreach (var s in fetchedSeries)
                    {
                        if (ContentExclusionFilter.IsExcluded(excludedSeriesSet, s.SeriesId))
                        {
                            var excludedName = config.EnableContentNameCleaning
                                ? ContentNameCleaner.CleanContentName(s.Name, config.ContentRemoveTerms)
                                : s.Name;
                            excludedSeriesItems.Add(Tuple.Create(excludedName, s.CategoryId));
                            excludedSeriesRaw.Add(s);
                        }
                        else
                        {
                            allSeries.Add(s);
                        }
                    }

                    _logger.Info("Per-item exclusions: skipping {0} of {1} series",
                        excludedSeriesItems.Count, fetchedSeries.Count);
                }

                // Delta sync: split into changed and unchanged using LastModified timestamp
                var lastSeriesTs = config.LastSeriesSyncTimestamp;
                long maxSeriesTs = lastSeriesTs;

                // Auto-reset delta state when folder naming flags change (stale folder paths would break pre-fetch skip)
                if (config.EnableSeriesIdFolderNaming != config.LastKnownEnableSeriesIdFolderNaming
                    || config.EnableSeriesMetadataLookup != config.LastKnownEnableSeriesMetadataLookup)
                {
                    if (lastSeriesTs > 0)
                        _logger.Info("Series folder naming flags changed — forcing full re-sync");
                    lastSeriesTs = 0;
                    maxSeriesTs = 0;
                    config.SeriesEpisodeHashesJson = string.Empty;
                }
                config.LastKnownEnableSeriesIdFolderNaming = config.EnableSeriesIdFolderNaming;
                config.LastKnownEnableSeriesMetadataLookup = config.EnableSeriesMetadataLookup;
                saveConfig?.Invoke();

                // Excluded series never enter the loop below, so fold their timestamps in here —
                // otherwise the watermark stalls behind an excluded-but-recent title.
                foreach (var s in excludedSeriesRaw)
                {
                    long excludedLm;
                    if (long.TryParse(s.LastModified, NumberStyles.None, CultureInfo.InvariantCulture, out excludedLm)
                        && excludedLm > maxSeriesTs)
                    {
                        maxSeriesTs = excludedLm;
                    }
                }

                _seriesProgress.Total = allSeries.Count;
                _seriesProgress.Phase = "Writing STRM files";

                int deltaNew = 0, deltaExisting = 0;
                if (lastSeriesTs > 0)
                {
                    foreach (var s in allSeries)
                    {
                        long lm;
                        if (long.TryParse(s.LastModified, NumberStyles.None, CultureInfo.InvariantCulture, out lm) && lm > lastSeriesTs)
                            deltaNew++;
                        else
                            deltaExisting++;
                    }
                    _logger.Info("Delta series sync: {0} changed, {1} unchanged (since timestamp {2})",
                        deltaNew, deltaExisting, lastSeriesTs);
                }
                else
                {
                    _logger.Info("Starting series STRM sync for {0} series", allSeries.Count);
                }

                var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var semaphore = new SemaphoreSlim(config.SyncParallelism);

                // Episode hash cache: load stored hashes so we can skip file I/O for unchanged series
                var storedHashes = DeserializeEpisodeHashes(config.SeriesEpisodeHashesJson);
                var updatedHashes = new ConcurrentDictionary<string, string>();
                int hashSkippedCount = 0;

                // Pre-fetch directory index: subFolder → {strippedSeriesName → fullDirPath}
                // Built once before the parallel loop (one readdir per unique subfolder, no per-task races).
                var subFolderDirIndex = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                if (config.SmartSkipExisting && lastSeriesTs > 0)
                {
                    var uniqueSubFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in allSeries)
                    {
                        var sf = BuildContentFolderPath(
                            config.SeriesFolderMode, s.CategoryId, categoryNames, folderMappings, "Shows");
                        if (sf != null) uniqueSubFolders.Add(sf);
                    }
                    foreach (var sf in uniqueSubFolders)
                    {
                        var fullPath = Path.Combine(config.StrmLibraryPath, sf);
                        var idx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (Directory.Exists(fullPath))
                        {
                            foreach (var dir in Directory.GetDirectories(fullPath))
                            {
                                var stripped = StripFolderIdSuffix(Path.GetFileName(dir));
                                if (!string.IsNullOrEmpty(stripped) && !idx.ContainsKey(stripped))
                                    idx[stripped] = dir;
                            }
                        }
                        subFolderDirIndex[sf] = idx;
                    }
                }

                var tasks = allSeries.Select(async series =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var cleanedName = config.EnableContentNameCleaning
                            ? ContentNameCleaner.CleanContentName(series.Name, config.ContentRemoveTerms)
                            : series.Name;
                        var seriesName = SanitizeFileName(cleanedName);
                        if (string.IsNullOrWhiteSpace(seriesName))
                        {
                            Interlocked.Increment(ref _seriesProgress.Failed);
                            return;
                        }

                        var subFolder = BuildContentFolderPath(
                            config.SeriesFolderMode, series.CategoryId, categoryNames, folderMappings, "Shows");

                        if (subFolder == null)
                        {
                            Interlocked.Increment(ref _seriesProgress.Skipped);
                            Interlocked.Increment(ref _seriesProgress.Completed);
                            ReportTaskProgress(_seriesProgress, taskProgress);
                            return;
                        }

                        // Track delta timestamp before the API call (series.LastModified comes from the list, no extra HTTP needed)
                        long seriesLm = 0;
                        long.TryParse(series.LastModified, NumberStyles.None, CultureInfo.InvariantCulture, out seriesLm);
                        if (seriesLm > 0)
                        {
                            lock (_historyLock) { if (seriesLm > maxSeriesTs) maxSeriesTs = seriesLm; }
                        }

                        var isChangedSeries = lastSeriesTs == 0 || seriesLm > lastSeriesTs;

                        // Pre-fetch smart skip: for delta-unchanged series, locate folder on disk by name
                        // (avoids one get_series_info HTTP call per unchanged series)
                        if (!isChangedSeries && config.SmartSkipExisting)
                        {
                            Dictionary<string, string> dirIndex;
                            string existingDir;
                            if (subFolderDirIndex.TryGetValue(subFolder, out dirIndex)
                                && dirIndex.TryGetValue(seriesName, out existingDir))
                            {
                                var existingStrms = Directory.GetFiles(existingDir, "*.strm", SearchOption.AllDirectories);
                                if (existingStrms.Length > 0)
                                {
                                    foreach (var strm in existingStrms)
                                        lock (writtenPaths) writtenPaths.Add(strm);
                                    var seriesKey = series.SeriesId.ToString(CultureInfo.InvariantCulture);
                                    string carryHash;
                                    if (storedHashes.TryGetValue(seriesKey, out carryHash))
                                        updatedHashes[seriesKey] = carryHash;
                                    Interlocked.Increment(ref _seriesProgress.Skipped);
                                    Interlocked.Increment(ref _seriesProgress.Completed);
                                    ReportTaskProgress(_seriesProgress, taskProgress);
                                    Interlocked.Add(ref _episodeProgress.Total, existingStrms.Length);
                                    Interlocked.Add(ref _episodeProgress.Skipped, existingStrms.Length);
                                    return;
                                }
                            }
                        }

                        // Fetch series detail (needed for episodes + TMDB ID)
                        SeriesDetailInfo detail;
                        try
                        {
                            detail = await FetchSeriesDetailAsync(series.SeriesId, config, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error("Failed to fetch detail for series '{0}' (id={1}): [{2}] {3}", series.Name, series.SeriesId, ex.GetType().Name, ex.Message);
                            lock (_failedItemsLock)
                            {
                                _failedItems.Add(new FailedSyncItem
                                {
                                    ItemType = "Series",
                                    StreamId = series.SeriesId,
                                    Name = series.Name,
                                    CategoryId = series.CategoryId,
                                    ErrorMessage = ex.Message
                                });
                            }
                            Interlocked.Increment(ref _seriesProgress.Failed);
                            Interlocked.Increment(ref _seriesProgress.Completed);
                            ReportTaskProgress(_seriesProgress, taskProgress);
                            return;
                        }

                        if (detail == null || detail.Episodes == null || detail.Episodes.Count == 0)
                        {
                            // An empty payload for a series that already has files on disk is far
                            // more likely to be a transient provider hiccup (or a tolerated
                            // malformed episode map, see ADR-010) than a show that genuinely lost
                            // every episode. Keep the existing files in the valid set and count the
                            // series as failed, which holds the orphan-cleanup guard below. A real
                            // removal is picked up by a later run that returns cleanly.
                            var strandedStrms = FindExistingSeriesStrms(config, subFolder, seriesName);
                            if (strandedStrms.Length > 0)
                            {
                                foreach (var strm in strandedStrms)
                                {
                                    lock (writtenPaths) { writtenPaths.Add(strm); }
                                }

                                _logger.Warn(
                                    "Series '{0}' (id={1}) returned no episodes but has {2} STRM file(s) on disk — keeping them and skipping orphan cleanup this run",
                                    series.Name, series.SeriesId, strandedStrms.Length);
                                Interlocked.Increment(ref _seriesProgress.Failed);
                            }

                            Interlocked.Increment(ref _seriesProgress.Completed);
                            ReportTaskProgress(_seriesProgress, taskProgress);
                            return;
                        }

                        // Build series folder name with metadata ID
                        var folderName = seriesName;
                        if (config.EnableSeriesIdFolderNaming)
                        {
                            var providerTmdbId = detail.Info != null ? detail.Info.TmdbId : null;
                            int? autoTvdbId = null;

                            // Only do TVDb lookup if no override and no provider TMDB
                            if (config.EnableSeriesMetadataLookup &&
                                (tvdbOverrides == null || !tvdbOverrides.ContainsKey(seriesName)) &&
                                !IsValidTmdbId(providerTmdbId))
                            {
                                var yearMatch = YearInTitleRegex.Match(cleanedName);
                                int? yearForLookup = null;
                                if (yearMatch.Success)
                                {
                                    int y;
                                    if (int.TryParse(yearMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out y))
                                    {
                                        yearForLookup = y;
                                    }
                                }

                                try
                                {
                                    autoTvdbId = await _tmdbLookupService.LookupSeriesTvdbIdAsync(cleanedName, yearForLookup, cancellationToken).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _logger.Debug("TVDb lookup error for '{0}': {1}", cleanedName, ex.Message);
                                }
                            }

                            folderName = BuildSeriesFolderName(seriesName, providerTmdbId, autoTvdbId, tvdbOverrides);
                        }

                        var seriesDir = Path.Combine(config.StrmLibraryPath, subFolder, folderName);
                        var isNewSeries = !Directory.Exists(seriesDir);

                        if (config.EnableNfoFiles)
                        {
                            var showNfoPath = Path.Combine(seriesDir, "tvshow.nfo");
                            var tvdbIdMatch = Regex.Match(folderName, @"\[tvdbid=(\d+)\]");
                            var tmdbIdMatch = Regex.Match(folderName, @"\[tmdbid=(\d+)\]");
                            var showTvdbId = tvdbIdMatch.Success ? tvdbIdMatch.Groups[1].Value : null;
                            var showTmdbId = tmdbIdMatch.Success ? tmdbIdMatch.Groups[1].Value : null;
                            if (showTmdbId == null && detail?.Info?.TmdbId != null)
                                showTmdbId = detail.Info.TmdbId.ToString();
                            Directory.CreateDirectory(seriesDir);
                            try { NfoWriter.WriteShowNfo(showNfoPath, seriesName, showTvdbId, showTmdbId); }
                            catch (Exception ex) { _logger.Debug("Show NFO write failed for '{0}': {1}", seriesName, ex.Message); }
                        }

                        // Episode hash skip: compare episode ID+ext hash to detect unchanged content
                        // even when the provider bumped last_modified globally.
                        var currentEpHash = ComputeSeriesEpisodeHash(detail.Episodes);
                        var epHashKey = series.SeriesId.ToString(CultureInfo.InvariantCulture);
                        updatedHashes[epHashKey] = currentEpHash;

                        string previousHash;
                        if (config.SmartSkipExisting
                            && storedHashes.TryGetValue(epHashKey, out previousHash)
                            && previousHash == currentEpHash
                            && Directory.Exists(seriesDir))
                        {
                            var existingStrms = Directory.GetFiles(seriesDir, "*.strm", SearchOption.AllDirectories);
                            if (existingStrms.Length > 0)
                            {
                                foreach (var existingStrm in existingStrms)
                                {
                                    lock (writtenPaths)
                                    {
                                        writtenPaths.Add(existingStrm);
                                    }
                                }
                                Interlocked.Increment(ref _seriesProgress.Skipped);
                                Interlocked.Increment(ref _seriesProgress.Completed);
                                ReportTaskProgress(_seriesProgress, taskProgress);
                                Interlocked.Add(ref _episodeProgress.Total, existingStrms.Length);
                                Interlocked.Add(ref _episodeProgress.Skipped, existingStrms.Length);
                                Interlocked.Increment(ref hashSkippedCount);
                                return;
                            }
                        }

                        foreach (var seasonEntry in detail.Episodes)
                        {
                            foreach (var episode in seasonEntry.Value)
                            {
                                var seasonNum = episode.Season > 0 ? episode.Season : 1;
                                var episodeNum = episode.EpisodeNum > 0 ? episode.EpisodeNum : 1;
                                var seasonFolder = string.Format(CultureInfo.InvariantCulture, "Season {0:D2}", seasonNum);
                                var seasonDir = Path.Combine(seriesDir, seasonFolder);

                                // Some providers embed the series name + episode code in the title
                                // (e.g. "EN - American Gigolo - S01E01", "Yago - S01E33 - Episode 33").
                                // Strip the duplicate portion so the filename doesn't read
                                // "Show - S01E01 - EN - Show - S01E01".
                                var rawEpisodeTitle = StripEpisodeTitleDuplicate(
                                    episode.Title, seriesName, seasonNum, episodeNum);
                                var episodeTitle = !string.IsNullOrWhiteSpace(rawEpisodeTitle)
                                    ? " - " + SanitizeFileName(rawEpisodeTitle)
                                    : string.Empty;

                                var fileName = string.Format(
                                    CultureInfo.InvariantCulture,
                                    "{0} - S{1:D2}E{2:D2}{3}.strm",
                                    seriesName, seasonNum, episodeNum, episodeTitle);

                                var strmPath = Path.Combine(seasonDir, fileName);

                                var ext = !string.IsNullOrEmpty(episode.ContainerExtension)
                                    ? episode.ContainerExtension
                                    : "mp4";

                                var streamUrl = string.Format(
                                    CultureInfo.InvariantCulture,
                                    "{0}/series/{1}/{2}/{3}.{4}",
                                    config.BaseUrl, config.Username, config.Password, episode.Id, ext);

                                // Skip write if file content is already up to date (avoids Emby library re-scan)
                                var fileExists = File.Exists(strmPath);
                                if (!fileExists || File.ReadAllText(strmPath) != streamUrl)
                                {
                                    Directory.CreateDirectory(seasonDir);
                                    File.WriteAllText(strmPath, streamUrl);

                                    if (!fileExists)
                                    {
                                        Interlocked.Increment(ref _episodeProgress.Added);
                                    }
                                }
                                else
                                {
                                    Interlocked.Increment(ref _episodeProgress.Skipped);
                                }

                                Interlocked.Increment(ref _episodeProgress.Total);

                                lock (writtenPaths)
                                {
                                    writtenPaths.Add(strmPath);
                                }
                            }
                        }

                        if (isNewSeries)
                        {
                            Interlocked.Increment(ref _seriesProgress.Added);
                            lock (addedSeriesTitles)
                            {
                                if (addedSeriesTitles.Count < 20) addedSeriesTitles.Add(cleanedName);
                            }
                        }
                        Interlocked.Increment(ref _seriesProgress.Completed);
                        ReportTaskProgress(_seriesProgress, taskProgress);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Failed to write STRM for series '{0}' (id={1}): [{2}] {3}", series.Name, series.SeriesId, ex.GetType().Name, ex.Message);
                        lock (_failedItemsLock)
                        {
                            _failedItems.Add(new FailedSyncItem
                            {
                                ItemType = "Series",
                                StreamId = series.SeriesId,
                                Name = series.Name,
                                CategoryId = series.CategoryId,
                                ErrorMessage = ex.Message
                            });
                        }
                        Interlocked.Increment(ref _seriesProgress.Failed);
                        Interlocked.Increment(ref _seriesProgress.Completed);
                        ReportTaskProgress(_seriesProgress, taskProgress);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);

                // Remove folders for explicitly excluded series. Deliberately before orphan
                // cleanup and independent of it — see RemoveExcludedContent remarks.
                // Note both passes accumulate into Deleted, which therefore counts folders
                // (exclusions) and files (orphans) together. The dashboard shows one number.
                if (excludedSeriesItems.Count > 0)
                {
                    _seriesProgress.Phase = "Removing excluded series";
                    _seriesProgress.Deleted += RemoveExcludedContent(
                        config, excludedSeriesItems, config.SeriesFolderMode, categoryNames, folderMappings, "Shows");
                }

                // Cleanup orphans. Skipped when any category failed to answer — see the
                // matching block in SyncMoviesAsync.
                if (config.CleanupOrphans && _seriesProgress.Failed == 0 && !seriesFetch.HadFailures)
                {
                    _seriesProgress.Phase = "Cleaning up orphaned files";
                    var showsRoot = Path.Combine(config.StrmLibraryPath, "Shows");
                    var deletedEpisodes = CleanupOrphans(showsRoot, writtenPaths, config.OrphanSafetyThreshold, config);
                    _seriesProgress.Deleted += deletedEpisodes;
                    _episodeProgress.Deleted = deletedEpisodes;
                }

                // Persist the highest LastModified timestamp seen
                if (maxSeriesTs > config.LastSeriesSyncTimestamp)
                {
                    config.LastSeriesSyncTimestamp = maxSeriesTs;
                    saveConfig?.Invoke();
                }

                // Persist episode hashes for next run
                config.SeriesEpisodeHashesJson = SerializeEpisodeHashes(updatedHashes);
                saveConfig?.Invoke();

                if (hashSkippedCount > 0)
                    _logger.Info("Episode hash skip: {0} series unchanged (episode IDs identical to previous sync)", hashSkippedCount);

                _logger.Info("Series STRM sync completed: {0} written, {1} skipped, {2} failed",
                    _seriesProgress.Completed - _seriesProgress.Skipped, _seriesProgress.Skipped, _seriesProgress.Failed);
            }
            catch (Exception ex)
            {
                _logger.Error("Series sync failed: {0}", ex.Message);
                _seriesProgress.Phase = "Failed: " + ex.Message;
                seriesSyncSuccess = false;
                throw;
            }
            finally
            {
                _seriesProgress.IsRunning = false;
                if (string.IsNullOrEmpty(_seriesProgress.AbortReason))
                {
                    _seriesProgress.Phase = "Complete";
                }

                AddHistoryEntry(new SyncHistoryEntry
                {
                    StartTime = seriesSyncStart,
                    EndTime = DateTime.UtcNow,
                    Success = seriesSyncSuccess,
                    WasSeriesSync = true,
                    SeriesTotal = _seriesProgress.Total,
                    SeriesCompleted = _seriesProgress.Completed,
                    SeriesAdded = _seriesProgress.Added,
                    SeriesSkipped = _seriesProgress.Skipped,
                    SeriesFailed = _seriesProgress.Failed,
                    SeriesDeleted = _seriesProgress.Deleted,
                    EpisodeTotal = _episodeProgress.Total,
                    EpisodeAdded = _episodeProgress.Added,
                    EpisodeSkipped = _episodeProgress.Skipped,
                    EpisodeFailed = _episodeProgress.Failed,
                    EpisodeDeleted = _episodeProgress.Deleted,
                    AddedSeriesTitles = addedSeriesTitles,
                });
            }
        }

        private void EnsureStrmLibraryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("STRM Library Path is not configured. Set it in the plugin settings.");
            }

            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    string.Format("Cannot create STRM Library Path '{0}': {1}. Check the path is valid and Emby has write permission.", path, ex.Message), ex);
            }
        }

        public async Task RetryFailedAsync(CancellationToken cancellationToken)
        {
            List<FailedSyncItem> items;
            lock (_failedItemsLock) { items = _failedItems.ToList(); }
            if (items.Count == 0) return;

            var config = Plugin.Instance.Configuration;
            _movieProgress = new SyncProgress { IsRunning = true, Phase = "Retrying failed items", Total = items.Count };

            try
            {
                var semaphore = new SemaphoreSlim(config.SyncParallelism);
                var categoryNames = new Dictionary<int, string>();
                var folderMappings = FolderMappingParser.Parse(config.MovieFolderMappings);
                var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var succeeded = new List<FailedSyncItem>();
                var succeededLock = new object();

                var tasks = items.Select(async item =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (item.ItemType == "Movie")
                            await RetryMovieItemAsync(item, config, categoryNames, folderMappings, writtenPaths, cancellationToken).ConfigureAwait(false);
                        else if (item.ItemType == "Series")
                            await RetrySeriesItemAsync(item, config, cancellationToken).ConfigureAwait(false);

                        lock (succeededLock) { succeeded.Add(item); }
                        Interlocked.Increment(ref _movieProgress.Completed);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Retry still failed for '{0}': {1}", item.Name, ex.Message);
                        Interlocked.Increment(ref _movieProgress.Failed);
                        Interlocked.Increment(ref _movieProgress.Completed);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);

                lock (_failedItemsLock)
                {
                    foreach (var s in succeeded)
                        _failedItems.Remove(s);
                }
            }
            finally
            {
                _movieProgress.IsRunning = false;
                _movieProgress.Phase = "Retry complete";
            }
        }

        private async Task RetryMovieItemAsync(
            FailedSyncItem item,
            PluginConfiguration config,
            Dictionary<int, string> categoryNames,
            Dictionary<int, string> folderMappings,
            HashSet<string> writtenPaths,
            CancellationToken cancellationToken)
        {
            var cleanedName = config.EnableContentNameCleaning
                ? ContentNameCleaner.CleanContentName(item.Name, config.ContentRemoveTerms)
                : item.Name;
            var folderName = BuildMovieFolderName(cleanedName, item.TmdbId);
            if (string.IsNullOrWhiteSpace(folderName)) return;

            var subFolder = BuildContentFolderPath(
                config.MovieFolderMode, item.CategoryId, categoryNames, folderMappings, "Movies");
            if (subFolder == null) return;

            var movieDir = Path.Combine(config.StrmLibraryPath, subFolder, folderName);
            var strmPath = Path.Combine(movieDir, folderName + ".strm");
            var ext = !string.IsNullOrEmpty(item.ContainerExtension) ? item.ContainerExtension : "mp4";
            var streamUrl = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/movie/{1}/{2}/{3}.{4}",
                config.BaseUrl, config.Username, config.Password, item.StreamId, ext);

            var fileExists = File.Exists(strmPath);

            // Skip write if file content is already up to date (avoids Emby library re-scan)
            if (!fileExists || File.ReadAllText(strmPath) != streamUrl)
            {
                Directory.CreateDirectory(movieDir);
                File.WriteAllText(strmPath, streamUrl);

                if (!fileExists)
                {
                    Interlocked.Increment(ref _movieProgress.Added);
                }
            }

            lock (writtenPaths) { writtenPaths.Add(strmPath); }

            if (config.EnableNfoFiles && !string.IsNullOrEmpty(item.TmdbId))
            {
                var nfoPath = Path.Combine(movieDir, folderName + ".nfo");
                var yearMatch = YearInTitleRegex.Match(cleanedName);
                int? nfoYear = null;
                if (yearMatch.Success)
                {
                    int y;
                    if (int.TryParse(yearMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out y))
                        nfoYear = y;
                }
                try { NfoWriter.WriteMovieNfo(nfoPath, cleanedName, item.TmdbId, nfoYear); }
                catch (Exception ex) { _logger.Debug("NFO write failed on retry for '{0}': {1}", item.Name, ex.Message); }
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private async Task RetrySeriesItemAsync(
            FailedSyncItem item,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var detail = await FetchSeriesDetailAsync(item.StreamId, config, cancellationToken).ConfigureAwait(false);
            if (detail == null || detail.Episodes == null || detail.Episodes.Count == 0) return;

            var cleanedName = config.EnableContentNameCleaning
                ? ContentNameCleaner.CleanContentName(item.Name, config.ContentRemoveTerms)
                : item.Name;

            var seriesDir = Path.Combine(config.StrmLibraryPath, "Shows", SanitizeFileName(cleanedName));
            Directory.CreateDirectory(seriesDir);

            foreach (var kvp in detail.Episodes)
            {
                var seasonNum = kvp.Key;
                var episodes = kvp.Value;
                if (episodes == null) continue;

                var seasonDir = Path.Combine(seriesDir, string.Format(CultureInfo.InvariantCulture, "Season {0:D2}", seasonNum));
                Directory.CreateDirectory(seasonDir);

                foreach (var ep in episodes)
                {
                    if (ep == null) continue;
                    var epFile = string.Format(CultureInfo.InvariantCulture,
                        "S{0:D2}E{1:D2}.strm", seasonNum, ep.EpisodeNum);
                    var epPath = Path.Combine(seasonDir, epFile);

                    var ext = !string.IsNullOrEmpty(ep.ContainerExtension) ? ep.ContainerExtension : "mp4";
                    var epUrl = string.Format(CultureInfo.InvariantCulture,
                        "{0}/series/{1}/{2}/{3}.{4}",
                        config.BaseUrl, config.Username, config.Password, ep.Id, ext);

                    var fileExists = File.Exists(epPath);

                    // Skip write if file content is already up to date (avoids Emby library re-scan)
                    if (!fileExists || File.ReadAllText(epPath) != epUrl)
                    {
                        Directory.CreateDirectory(seasonDir);
                        File.WriteAllText(epPath, epUrl);

                        if (!fileExists)
                        {
                            Interlocked.Increment(ref _episodeProgress.Added);
                        }
                    }
                }
            }
        }

        private void AddHistoryEntry(SyncHistoryEntry entry)
        {
            string historyJson;
            lock (_historyLock)
            {
                var history = GetOrLoadHistory();
                history.Insert(0, entry);
                while (history.Count > MaxHistoryEntries)
                {
                    history.RemoveAt(history.Count - 1);
                }
                historyJson = STJ.JsonSerializer.Serialize(_syncHistory, JsonOptions);
            }

            try
            {
                Plugin.Instance.Configuration.SyncHistoryJson = historyJson;
                Plugin.Instance.SaveConfiguration();
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to persist sync history: {0}", ex.Message);
            }
        }

        internal static string BuildMovieFolderName(string cleanedName, string tmdbId)
        {
            var sanitized = SanitizeFileName(cleanedName);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return string.Empty;
            }

            if (IsValidTmdbId(tmdbId))
            {
                return sanitized + " [tmdbid=" + tmdbId.Trim() + "]";
            }

            return sanitized;
        }

        private static bool IsValidTmdbId(string tmdbId)
        {
            if (string.IsNullOrWhiteSpace(tmdbId))
            {
                return false;
            }

            int id;
            return int.TryParse(tmdbId, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
        }

        internal static Dictionary<string, int> ParseTvdbOverrides(string config)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(config))
            {
                return result;
            }

            var lines = config.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx <= 0)
                {
                    continue;
                }

                var folderName = trimmed.Substring(0, eqIdx).Trim();
                var idStr = trimmed.Substring(eqIdx + 1).Trim();

                int tvdbId;
                if (!string.IsNullOrEmpty(folderName) &&
                    int.TryParse(idStr, NumberStyles.None, CultureInfo.InvariantCulture, out tvdbId) &&
                    tvdbId > 0)
                {
                    result[folderName] = tvdbId;
                }
            }

            return result;
        }

        internal static string BuildSeriesFolderName(
            string sanitizedName, string tmdbId,
            int? autoTvdbId, Dictionary<string, int> tvdbOverrides)
        {
            if (string.IsNullOrWhiteSpace(sanitizedName))
            {
                return string.Empty;
            }

            // Priority 1: manual TVDb override
            int overrideId;
            if (tvdbOverrides != null && tvdbOverrides.TryGetValue(sanitizedName, out overrideId))
            {
                return sanitizedName + " [tvdbid=" + overrideId.ToString(CultureInfo.InvariantCulture) + "]";
            }

            // Priority 2: Xtream provider TMDB ID
            if (IsValidTmdbId(tmdbId))
            {
                return sanitizedName + " [tmdbid=" + tmdbId.Trim() + "]";
            }

            // Priority 3: auto TVDb lookup
            if (autoTvdbId.HasValue && autoTvdbId.Value > 0)
            {
                return sanitizedName + " [tvdbid=" + autoTvdbId.Value.ToString(CultureInfo.InvariantCulture) + "]";
            }

            // Priority 4: no ID
            return sanitizedName;
        }

        private static string StripFolderIdSuffix(string folderName)
        {
            return FolderIdSuffixRegex.Replace(folderName, string.Empty);
        }

        /// <summary>
        /// Strips provider-embedded series name + episode code prefixes from an episode title.
        /// Handles two patterns:
        ///   1. "{CleanedSeriesName} - SxxExx" at start/anywhere (full name match)
        ///   2. "AnyPrefix - SxxExx - ..." where SxxExx matches the exact season/episode numbers
        /// Returns the human-readable remainder, or empty string if the title was only a prefix.
        /// </summary>
        internal static string StripEpisodeTitleDuplicate(string episodeTitle, string seriesName, int seasonNum, int episodeNum)
        {
            var result = episodeTitle?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(result))
                return result;

            // Pass 1: strip "{seriesName} - SxxExx" pattern (handles clean name match including year)
            if (!string.IsNullOrEmpty(seriesName))
            {
                result = Regex.Replace(
                    result,
                    @"[\s\-]*" + Regex.Escape(seriesName) + @"[\s\-]*S\d+E\d+[\s\-]*",
                    string.Empty,
                    RegexOptions.IgnoreCase).Trim('-', ' ');
            }

            // Pass 2: if the exact episode code still appears (e.g. provider used a short series
            // name without the year), strip everything up to and including that code.
            // "Yago - S01E33 - Episode 33" → "Episode 33"
            var episodeCode = string.Format(CultureInfo.InvariantCulture, "S{0:D2}E{1:D2}", seasonNum, episodeNum);
            var codeIdx = result.IndexOf(episodeCode, StringComparison.OrdinalIgnoreCase);
            if (codeIdx >= 0)
            {
                result = result.Substring(codeIdx + episodeCode.Length).Trim('-', ' ');
            }

            return result;
        }

        internal static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var result = InvalidFileCharsRegex.Replace(name, string.Empty);
            // Remove leading/trailing dots and spaces (invalid on Windows)
            result = result.Trim('.', ' ');
            // Collapse multiple spaces
            result = Regex.Replace(result, @"\s{2,}", " ");
            return result;
        }

        private static string BuildContentFolderPath(
            string folderMode,
            int? categoryId,
            Dictionary<int, string> categoryNames,
            Dictionary<int, string> folderMappings,
            string rootFolder)
        {
            if (string.Equals(folderMode, "single", StringComparison.OrdinalIgnoreCase))
            {
                return rootFolder;
            }

            if (string.Equals(folderMode, "custom", StringComparison.OrdinalIgnoreCase) && categoryId.HasValue)
            {
                string mappedFolder;
                if (folderMappings.TryGetValue(categoryId.Value, out mappedFolder))
                {
                    return Path.Combine(rootFolder, SanitizeFileName(mappedFolder));
                }
                return null;
            }

            if (string.Equals(folderMode, "multiple", StringComparison.OrdinalIgnoreCase) && categoryId.HasValue)
            {
                string categoryName;
                if (categoryNames.TryGetValue(categoryId.Value, out categoryName) &&
                    !string.IsNullOrWhiteSpace(categoryName))
                {
                    return Path.Combine(rootFolder, SanitizeFileName(categoryName));
                }
                return null;
            }

            return rootFolder;
        }

        private async Task<List<Category>> FetchCategoriesAsync(string action, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action={3}",
                config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty), action);

            var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
            return STJ.JsonSerializer.Deserialize<List<Category>>(json, JsonOptions)
                ?? new List<Category>();
        }

        private async Task<List<Category>> FetchSeriesCategoriesWithFallbackAsync(
            PluginConfiguration config, CancellationToken cancellationToken)
        {
            var categories = await FetchCategoriesAsync("get_series_categories", config, cancellationToken).ConfigureAwait(false);
            if (categories.Count > 0)
            {
                return categories;
            }

            // Fallback: derive categories from series list
            _logger.Info("get_series_categories returned empty, deriving from series list");
            var seriesList = await FetchSeriesListAsync(null, config, cancellationToken).ConfigureAwait(false);
            return seriesList.Items
                .Where(s => s.CategoryId.HasValue)
                .GroupBy(s => s.CategoryId.Value)
                .Select(g => new Category
                {
                    CategoryId = g.Key,
                    CategoryName = g.FirstOrDefault(s => !string.IsNullOrEmpty(s.CategoryName))?.CategoryName
                        ?? "Category " + g.Key,
                })
                .OrderBy(c => c.CategoryName)
                .ToList();
        }

        private async Task<CatalogueFetchResult<VodStreamInfo>> FetchVodStreamsAsync(
            int[] categoryIds, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var result = new CatalogueFetchResult<VodStreamInfo>();
            var allStreams = new List<VodStreamInfo>();

            if (categoryIds == null || categoryIds.Length == 0)
            {
                // Fetch all VOD streams. A throw here propagates: the caller's outer catch
                // aborts the sync before cleanup, which is the correct outcome for the
                // whole-catalogue request failing.
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}&action=get_vod_streams",
                    config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty));

                var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                allStreams = STJ.JsonSerializer.Deserialize<List<VodStreamInfo>>(json, JsonOptions)
                    ?? new List<VodStreamInfo>();
            }
            else
            {
                result.RequestedCategoryCount = categoryIds.Length;
                var semaphore = new SemaphoreSlim(config.SyncParallelism);
                var tasks = categoryIds.Select(async catId =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var url = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}/player_api.php?username={1}&password={2}&action=get_vod_streams&category_id={3}",
                            config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty), catId);

                        var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                        var streams = STJ.JsonSerializer.Deserialize<List<VodStreamInfo>>(json, JsonOptions)
                            ?? new List<VodStreamInfo>();

                        // Override category_id to match the requested category.
                        // The Xtream API can return cross-listed movies whose primary
                        // category_id differs from the category we queried. Without
                        // this, custom folder mapping skips them as unmapped.
                        foreach (var s in streams)
                        {
                            s.CategoryId = catId;
                        }

                        return Tuple.Create(streams, true);
                    }
                    catch (Exception ex)
                    {
                        // Report the failure rather than returning an empty list. An empty list
                        // is indistinguishable from an empty category and would let orphan
                        // cleanup delete this category's existing files.
                        _logger.Warn("Failed to fetch VOD streams for category {0}: {1}", catId, ex.Message);
                        return Tuple.Create(new List<VodStreamInfo>(), false);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var r in results)
                {
                    if (r.Item2)
                    {
                        allStreams.AddRange(r.Item1);
                    }
                    else
                    {
                        result.FailedCategoryCount++;
                    }
                }

                // Deduplicate by StreamId (first occurrence wins, keeping its assigned category)
                allStreams = allStreams.GroupBy(s => s.StreamId).Select(g => g.First()).ToList();
            }

            result.Items = allStreams;
            return result;
        }

        private async Task<CatalogueFetchResult<SeriesInfo>> FetchSeriesListAsync(
            int[] categoryIds, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var result = new CatalogueFetchResult<SeriesInfo>();
            var allSeries = new List<SeriesInfo>();

            if (categoryIds == null || categoryIds.Length == 0)
            {
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}&action=get_series",
                    config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty));

                var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                allSeries = STJ.JsonSerializer.Deserialize<List<SeriesInfo>>(json, JsonOptions)
                    ?? new List<SeriesInfo>();
            }
            else
            {
                result.RequestedCategoryCount = categoryIds.Length;
                var semaphore = new SemaphoreSlim(config.SyncParallelism);
                var tasks = categoryIds.Select(async catId =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var url = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}/player_api.php?username={1}&password={2}&action=get_series&category_id={3}",
                            config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty), catId);

                        var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                        var series = STJ.JsonSerializer.Deserialize<List<SeriesInfo>>(json, JsonOptions)
                            ?? new List<SeriesInfo>();

                        // Override category_id to match the requested category (same
                        // cross-listing issue as VOD streams — see FetchVodStreamsAsync).
                        foreach (var s in series)
                        {
                            s.CategoryId = catId;
                        }

                        return Tuple.Create(series, true);
                    }
                    catch (Exception ex)
                    {
                        // See FetchVodStreamsAsync: a failure must not look like an empty category.
                        _logger.Warn("Failed to fetch series for category {0}: {1}", catId, ex.Message);
                        return Tuple.Create(new List<SeriesInfo>(), false);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var r in results)
                {
                    if (r.Item2)
                    {
                        allSeries.AddRange(r.Item1);
                    }
                    else
                    {
                        result.FailedCategoryCount++;
                    }
                }

                allSeries = allSeries.GroupBy(s => s.SeriesId).Select(g => g.First()).ToList();
            }

            result.Items = allSeries;
            return result;
        }

        /// <summary>
        /// Finds the STRM files already on disk for <paramref name="seriesName"/>, if any.
        /// </summary>
        /// <remarks>
        /// Used only on the empty-detail path, so the per-series readdir stays off the hot loop.
        /// Folder names carry an optional metadata-ID suffix, so the comparison strips it the same
        /// way the pre-fetch smart-skip index does.
        /// </remarks>
        private string[] FindExistingSeriesStrms(PluginConfiguration config, string subFolder, string seriesName)
        {
            try
            {
                var parent = Path.Combine(config.StrmLibraryPath, subFolder);
                if (!Directory.Exists(parent))
                {
                    return Array.Empty<string>();
                }

                foreach (var dir in Directory.GetDirectories(parent))
                {
                    var stripped = StripFolderIdSuffix(Path.GetFileName(dir));
                    if (string.Equals(stripped, seriesName, StringComparison.OrdinalIgnoreCase))
                    {
                        return Directory.GetFiles(dir, "*.strm", SearchOption.AllDirectories);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Could not probe existing STRM files for series '{0}': {1}", seriesName, ex.Message);
            }

            return Array.Empty<string>();
        }

        private async Task<SeriesDetailInfo> FetchSeriesDetailAsync(
            int seriesId, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_series_info&series_id={3}",
                config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty), seriesId);

            var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
            return STJ.JsonSerializer.Deserialize<SeriesDetailInfo>(json, JsonOptions);
        }

        /// <summary>
        /// Deletes the on-disk folders of items the user has explicitly excluded.
        /// </summary>
        /// <remarks>
        /// Runs independently of <see cref="PluginConfiguration.CleanupOrphans"/> and ignores
        /// <see cref="PluginConfiguration.OrphanSafetyThreshold"/>. That threshold exists to survive
        /// a provider returning a truncated catalogue; an exclusion is a deliberate user action, so
        /// suppressing the delete would just look like the filter doing nothing.
        ///
        /// Folder matching strips any metadata-ID suffix, so an excluded title is found whether it
        /// was written as "Some Movie", "Some Movie [tmdbid=123]" or "Some Show [tvdbid=456]".
        /// </remarks>
        /// <param name="config">Active plugin configuration (supplies the library root).</param>
        /// <param name="excludedItems">Cleaned display name + category ID for each excluded item.</param>
        /// <param name="folderMode">"single", "multiple" or "custom".</param>
        /// <param name="categoryNames">Category ID → name, used by "multiple" mode.</param>
        /// <param name="folderMappings">Category ID → folder, used by "custom" mode.</param>
        /// <param name="rootFolder">"Movies" or "Shows".</param>
        /// <returns>The number of folders deleted.</returns>
        private int RemoveExcludedContent(
            PluginConfiguration config,
            List<Tuple<string, int?>> excludedItems,
            string folderMode,
            Dictionary<int, string> categoryNames,
            Dictionary<int, string> folderMappings,
            string rootFolder)
        {
            if (excludedItems == null || excludedItems.Count == 0)
            {
                return 0;
            }

            var removed = 0;

            // subFolder → { folderNameWithoutIdSuffix → fullPath }. One readdir per subfolder.
            var dirIndexCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in excludedItems)
            {
                var sanitized = SanitizeFileName(item.Item1);
                if (string.IsNullOrWhiteSpace(sanitized))
                {
                    continue;
                }

                var subFolder = BuildContentFolderPath(
                    folderMode, item.Item2, categoryNames, folderMappings, rootFolder);
                if (subFolder == null)
                {
                    continue;
                }

                Dictionary<string, string> dirIndex;
                if (!dirIndexCache.TryGetValue(subFolder, out dirIndex))
                {
                    dirIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var fullPath = Path.Combine(config.StrmLibraryPath, subFolder);
                    if (Directory.Exists(fullPath))
                    {
                        foreach (var dir in Directory.GetDirectories(fullPath))
                        {
                            var stripped = StripFolderIdSuffix(Path.GetFileName(dir));
                            if (!string.IsNullOrEmpty(stripped) && !dirIndex.ContainsKey(stripped))
                            {
                                dirIndex[stripped] = dir;
                            }
                        }
                    }

                    dirIndexCache[subFolder] = dirIndex;
                }

                string existingDir;
                if (!dirIndex.TryGetValue(sanitized, out existingDir))
                {
                    continue;
                }

                try
                {
                    // Delete only the files this plugin actually wrote, verified by content, then
                    // prune whatever that emptied. Matching is by title alone, so a match is not
                    // proof of ownership: without this a user's own "Ben-Hur" folder would be
                    // destroyed by excluding the provider's "Ben-Hur", and a hand-written .nfo or a
                    // trailer.strm sitting beside our output would go with it. See ADR-014.
                    bool folderGone;
                    var deletedFiles = StrmOwnership.DeleteOwnedFiles(
                        existingDir, config.BaseUrl, config.DispatcharrUrl, out folderGone);

                    if (deletedFiles == 0)
                    {
                        _logger.Debug(
                            "Skipping '{0}' for excluded item '{1}': nothing in it was written by this plugin",
                            existingDir, sanitized);
                        continue;
                    }

                    dirIndex.Remove(sanitized);
                    removed++;
                    _logger.Info(
                        folderGone
                            ? "Removed excluded item folder: {0}"
                            : "Removed plugin files for excluded item, folder kept (still has other content): {0}",
                        existingDir);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Failed to remove excluded item folder '{0}': {1}", existingDir, ex.Message);
                }
            }

            if (removed > 0)
            {
                _logger.Info("Removed {0} folder(s) for explicitly excluded items under {1}", removed, rootFolder);
            }


            return removed;
        }


        private int CleanupOrphans(
            string rootPath, HashSet<string> validPaths, double safetyThreshold, PluginConfiguration config)
        {
            if (!Directory.Exists(rootPath))
            {
                return 0;
            }

            var existingStrms = Directory.GetFiles(rootPath, "*.strm", SearchOption.AllDirectories);

            // Nothing was written or preserved this run, but files exist on disk. That is a
            // provider returning an empty catalogue, not the user deleting their whole library.
            // The ratio guard below cannot catch this at small N (it only applies above 10 files),
            // so refuse outright rather than emptying the library. See ADR-013.
            if (validPaths.Count == 0 && existingStrms.Length > 0)
            {
                _logger.Warn(
                    "Orphan cleanup skipped: the catalogue produced no files this run but {0} STRM file(s) exist under {1} — refusing to treat an empty catalogue as a deletion",
                    existingStrms.Length, rootPath);
                return 0;
            }

            // Being a .strm under our library root is not proof we wrote it. Verify ownership
            // before considering anything for deletion — a user's own STRM that the provider
            // never listed would otherwise look exactly like an orphan. Only orphan candidates
            // are read, so the cost stays proportional to deletions, not to library size.
            var orphans = existingStrms
                .Where(s => !validPaths.Contains(s))
                .Where(s => StrmOwnership.IsOwnedStrm(s, config.BaseUrl, config.DispatcharrUrl))
                .ToList();

            var foreignCount = existingStrms.Length - validPaths.Count - orphans.Count;
            if (foreignCount > 0)
            {
                _logger.Info(
                    "Orphan cleanup: leaving {0} STRM file(s) under {1} that this plugin did not write",
                    foreignCount, rootPath);
            }

            // Ratio is taken over the files we could actually have written (what we wrote or kept,
            // plus the orphans we own). Counting foreign files in the denominator would dilute the
            // ratio and make the guard fire less often than intended.
            var ownedTotal = validPaths.Count + orphans.Count;
            if (safetyThreshold > 0 && ownedTotal > 10 && orphans.Count > 0)
            {
                double ratio = (double)orphans.Count / ownedTotal;
                if (ratio > safetyThreshold)
                {
                    _logger.Warn(
                        "Orphan cleanup skipped: {0}/{1} ({2:P0}) exceeds safety threshold {3:P0} — possible provider issue",
                        orphans.Count, ownedTotal, ratio, safetyThreshold);
                    return 0;
                }
            }

            var removed = 0;

            foreach (var strmFile in orphans)
            {
                try
                {
                    File.Delete(strmFile);
                    removed++;

                    // Remove empty parent directories
                    var dir = Path.GetDirectoryName(strmFile);
                    while (!string.IsNullOrEmpty(dir) &&
                           !string.Equals(dir, rootPath, StringComparison.OrdinalIgnoreCase) &&
                           Directory.Exists(dir) &&
                           Directory.GetFileSystemEntries(dir).Length == 0)
                    {
                        Directory.Delete(dir);
                        dir = Path.GetDirectoryName(dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug("Failed to cleanup orphan '{0}': {1}", strmFile, ex.Message);
                }
            }

            if (removed > 0)
            {
                _logger.Info("Removed {0} orphaned STRM files from {1}", removed, rootPath);
            }

            return removed;
        }
    }
}
