using System;
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
        public bool WasMovieSync { get; set; }
        public bool WasSeriesSync { get; set; }
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
        };

        private static readonly Regex InvalidFileCharsRegex = new Regex(
            @"[<>:""/\\|?*\x00-\x1F]",
            RegexOptions.Compiled);

        private static readonly Regex YearInTitleRegex = new Regex(
            @"\((\d{4})\)\s*$",
            RegexOptions.Compiled);

        private static readonly int MaxHistoryEntries = 10;
        private static readonly HttpClient SharedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private readonly ILogger _logger;
        private readonly TmdbLookupService _tmdbLookupService;
        private readonly List<SyncHistoryEntry> _syncHistory = new List<SyncHistoryEntry>();
        private readonly object _historyLock = new object();
        private readonly List<FailedSyncItem> _failedItems = new List<FailedSyncItem>();
        private readonly object _failedItemsLock = new object();

        private SyncProgress _movieProgress = new SyncProgress();
        private SyncProgress _seriesProgress = new SyncProgress();

        public StrmSyncService(ILogger logger)
        {
            _logger = logger;
            _tmdbLookupService = new TmdbLookupService(logger);
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

        public SyncProgress MovieProgress => _movieProgress;
        public SyncProgress SeriesProgress => _seriesProgress;

        public IReadOnlyList<FailedSyncItem> FailedItems
        {
            get { lock (_failedItemsLock) { return _failedItems.ToList(); } }
        }

        public List<SyncHistoryEntry> GetSyncHistory()
        {
            lock (_historyLock)
            {
                return new List<SyncHistoryEntry>(_syncHistory);
            }
        }

        public async Task SyncMoviesAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            _movieProgress = new SyncProgress { IsRunning = true, Phase = "Starting movie sync" };
            lock (_failedItemsLock) { _failedItems.Clear(); }
            var movieSyncStart = DateTime.UtcNow;
            var movieSyncSuccess = true;

            try
            {
                EnsureStrmLibraryPath(config.StrmLibraryPath);

                var categoryNames = new Dictionary<int, string>();

                // Fetch category names if needed for folder organization
                if (!string.Equals(config.MovieFolderMode, "single", StringComparison.OrdinalIgnoreCase))
                {
                    _movieProgress.Phase = "Fetching VOD categories";
                    var categories = await FetchCategoriesAsync("get_vod_categories", cancellationToken).ConfigureAwait(false);
                    foreach (var cat in categories)
                    {
                        categoryNames[cat.CategoryId] = cat.CategoryName;
                    }
                }

                var folderMappings = FolderMappingParser.Parse(config.MovieFolderMappings);

                // Fetch streams for selected categories
                _movieProgress.Phase = "Fetching VOD streams";
                var allStreams = await FetchVodStreamsAsync(config.SelectedVodCategoryIds, config, cancellationToken).ConfigureAwait(false);

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
                        foreach (var entry in strmEntries)
                        {
                            var isNewFile = !File.Exists(entry.Item1);
                            File.WriteAllText(entry.Item1, entry.Item2);
                            if (isNewFile) Interlocked.Increment(ref _movieProgress.Added);
                            lock (writtenPaths) { writtenPaths.Add(entry.Item1); }
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
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);

                // Cleanup orphans
                if (config.CleanupOrphans)
                {
                    _movieProgress.Phase = "Cleaning up orphaned files";
                    var moviesRoot = Path.Combine(config.StrmLibraryPath, "Movies");
                    _movieProgress.Deleted = CleanupOrphans(moviesRoot, writtenPaths, config.OrphanSafetyThreshold);
                }

                // Persist the highest Added timestamp seen so next sync can delta from here
                if (allStreams.Count > 0)
                {
                    var maxAdded = allStreams.Max(m => m.Added);
                    if (maxAdded > config.LastMovieSyncTimestamp)
                    {
                        config.LastMovieSyncTimestamp = maxAdded;
                        Plugin.Instance.SaveConfiguration();
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
                _movieProgress.Phase = "Complete";

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
                });
            }
        }

        public async Task SyncSeriesAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            _seriesProgress = new SyncProgress { IsRunning = true, Phase = "Starting series sync" };
            lock (_failedItemsLock) { _failedItems.RemoveAll(i => i.ItemType == "Series"); }
            var seriesSyncStart = DateTime.UtcNow;
            var seriesSyncSuccess = true;

            try
            {
                EnsureStrmLibraryPath(config.StrmLibraryPath);

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

                var folderMappings = FolderMappingParser.Parse(config.SeriesFolderMappings);

                // Parse TVDb overrides once before the loop
                var tvdbOverrides = config.EnableSeriesIdFolderNaming
                    ? ParseTvdbOverrides(config.TvdbFolderIdOverrides)
                    : null;

                _seriesProgress.Phase = "Fetching series list";
                var allSeries = await FetchSeriesListAsync(config.SelectedSeriesCategoryIds, config, cancellationToken).ConfigureAwait(false);

                // Delta sync: split into changed and unchanged using LastModified timestamp
                var lastSeriesTs = config.LastSeriesSyncTimestamp;
                long maxSeriesTs = lastSeriesTs;

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
                            return;
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
                            return;
                        }

                        if (detail == null || detail.Episodes == null || detail.Episodes.Count == 0)
                        {
                            Interlocked.Increment(ref _seriesProgress.Completed);
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

                        // Track max LastModified for delta state
                        long seriesLm = 0;
                        long.TryParse(series.LastModified, NumberStyles.None, CultureInfo.InvariantCulture, out seriesLm);
                        if (seriesLm > 0)
                        {
                            lock (_historyLock)
                            {
                                if (seriesLm > maxSeriesTs) maxSeriesTs = seriesLm;
                            }
                        }

                        // Smart skip: skip unchanged series (delta) that already have episodes on disk
                        var isChangedSeries = lastSeriesTs == 0 || seriesLm > lastSeriesTs;
                        if (!isChangedSeries && config.SmartSkipExisting && Directory.Exists(seriesDir))
                        {
                            var existingStrms = Directory.GetFiles(seriesDir, "*.strm", SearchOption.AllDirectories);
                            if (existingStrms.Length > 0)
                            {
                                // Add existing paths so orphan cleanup doesn't remove them
                                foreach (var existingStrm in existingStrms)
                                {
                                    lock (writtenPaths)
                                    {
                                        writtenPaths.Add(existingStrm);
                                    }
                                }
                                Interlocked.Increment(ref _seriesProgress.Skipped);
                                Interlocked.Increment(ref _seriesProgress.Completed);
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

                                var episodeTitle = !string.IsNullOrWhiteSpace(episode.Title)
                                    ? " - " + SanitizeFileName(episode.Title)
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

                                Directory.CreateDirectory(seasonDir);
                                File.WriteAllText(strmPath, streamUrl);

                                lock (writtenPaths)
                                {
                                    writtenPaths.Add(strmPath);
                                }
                            }
                        }

                        if (isNewSeries) Interlocked.Increment(ref _seriesProgress.Added);
                        Interlocked.Increment(ref _seriesProgress.Completed);
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
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);

                // Cleanup orphans
                if (config.CleanupOrphans)
                {
                    _seriesProgress.Phase = "Cleaning up orphaned files";
                    var showsRoot = Path.Combine(config.StrmLibraryPath, "Shows");
                    _seriesProgress.Deleted = CleanupOrphans(showsRoot, writtenPaths, config.OrphanSafetyThreshold);
                }

                // Persist the highest LastModified timestamp seen
                if (maxSeriesTs > config.LastSeriesSyncTimestamp)
                {
                    config.LastSeriesSyncTimestamp = maxSeriesTs;
                    Plugin.Instance.SaveConfiguration();
                }

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
                _seriesProgress.Phase = "Complete";

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

            var isNewFile = !File.Exists(strmPath);
            Directory.CreateDirectory(movieDir);
            File.WriteAllText(strmPath, streamUrl);
            if (isNewFile) Interlocked.Increment(ref _movieProgress.Added);
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
                    if (File.Exists(epPath)) continue;

                    var ext = !string.IsNullOrEmpty(ep.ContainerExtension) ? ep.ContainerExtension : "mp4";
                    var epUrl = string.Format(CultureInfo.InvariantCulture,
                        "{0}/series/{1}/{2}/{3}.{4}",
                        config.BaseUrl, config.Username, config.Password, ep.Id, ext);
                    File.WriteAllText(epPath, epUrl);
                    Interlocked.Increment(ref _movieProgress.Added);
                }
            }
        }

        private void AddHistoryEntry(SyncHistoryEntry entry)
        {
            string historyJson;
            lock (_historyLock)
            {
                _syncHistory.Insert(0, entry);
                while (_syncHistory.Count > MaxHistoryEntries)
                {
                    _syncHistory.RemoveAt(_syncHistory.Count - 1);
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

        private async Task<List<Category>> FetchCategoriesAsync(string action, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action={3}",
                config.BaseUrl, config.Username, config.Password, action);

            var json = await SharedHttpClient.GetStringAsync(url).ConfigureAwait(false);
            return STJ.JsonSerializer.Deserialize<List<Category>>(json, JsonOptions)
                ?? new List<Category>();
        }

        private async Task<List<Category>> FetchSeriesCategoriesWithFallbackAsync(
            PluginConfiguration config, CancellationToken cancellationToken)
        {
            var categories = await FetchCategoriesAsync("get_series_categories", cancellationToken).ConfigureAwait(false);
            if (categories.Count > 0)
            {
                return categories;
            }

            // Fallback: derive categories from series list
            _logger.Info("get_series_categories returned empty, deriving from series list");
            var seriesList = await FetchSeriesListAsync(null, config, cancellationToken).ConfigureAwait(false);
            return seriesList
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

        private async Task<List<VodStreamInfo>> FetchVodStreamsAsync(
            int[] categoryIds, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var allStreams = new List<VodStreamInfo>();

            if (categoryIds == null || categoryIds.Length == 0)
            {
                // Fetch all VOD streams
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}&action=get_vod_streams",
                    config.BaseUrl, config.Username, config.Password);

                var json = await SharedHttpClient.GetStringAsync(url).ConfigureAwait(false);
                allStreams = STJ.JsonSerializer.Deserialize<List<VodStreamInfo>>(json, JsonOptions)
                    ?? new List<VodStreamInfo>();
            }
            else
            {
                var semaphore = new SemaphoreSlim(config.SyncParallelism);
                var tasks = categoryIds.Select(async catId =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var url = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}/player_api.php?username={1}&password={2}&action=get_vod_streams&category_id={3}",
                            config.BaseUrl, config.Username, config.Password, catId);

                        var json = await SharedHttpClient.GetStringAsync(url).ConfigureAwait(false);
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

                        return streams;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("Failed to fetch VOD streams for category {0}: {1}", catId, ex.Message);
                        return new List<VodStreamInfo>();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var result in results)
                {
                    allStreams.AddRange(result);
                }

                // Deduplicate by StreamId (first occurrence wins, keeping its assigned category)
                allStreams = allStreams.GroupBy(s => s.StreamId).Select(g => g.First()).ToList();
            }

            return allStreams;
        }

        private async Task<List<SeriesInfo>> FetchSeriesListAsync(
            int[] categoryIds, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var allSeries = new List<SeriesInfo>();

            if (categoryIds == null || categoryIds.Length == 0)
            {
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}&action=get_series",
                    config.BaseUrl, config.Username, config.Password);

                var json = await SharedHttpClient.GetStringAsync(url).ConfigureAwait(false);
                allSeries = STJ.JsonSerializer.Deserialize<List<SeriesInfo>>(json, JsonOptions)
                    ?? new List<SeriesInfo>();
            }
            else
            {
                var semaphore = new SemaphoreSlim(config.SyncParallelism);
                var tasks = categoryIds.Select(async catId =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var url = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}/player_api.php?username={1}&password={2}&action=get_series&category_id={3}",
                            config.BaseUrl, config.Username, config.Password, catId);

                        var json = await SharedHttpClient.GetStringAsync(url).ConfigureAwait(false);
                        var series = STJ.JsonSerializer.Deserialize<List<SeriesInfo>>(json, JsonOptions)
                            ?? new List<SeriesInfo>();

                        // Override category_id to match the requested category (same
                        // cross-listing issue as VOD streams — see FetchVodStreamsAsync).
                        foreach (var s in series)
                        {
                            s.CategoryId = catId;
                        }

                        return series;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("Failed to fetch series for category {0}: {1}", catId, ex.Message);
                        return new List<SeriesInfo>();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var result in results)
                {
                    allSeries.AddRange(result);
                }

                allSeries = allSeries.GroupBy(s => s.SeriesId).Select(g => g.First()).ToList();
            }

            return allSeries;
        }

        private async Task<SeriesDetailInfo> FetchSeriesDetailAsync(
            int seriesId, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_series_info&series_id={3}",
                config.BaseUrl, config.Username, config.Password, seriesId);

            var json = await SharedHttpClient.GetStringAsync(url).ConfigureAwait(false);
            return STJ.JsonSerializer.Deserialize<SeriesDetailInfo>(json, JsonOptions);
        }

        private int CleanupOrphans(string rootPath, HashSet<string> validPaths, double safetyThreshold)
        {
            if (!Directory.Exists(rootPath))
            {
                return 0;
            }

            var existingStrms = Directory.GetFiles(rootPath, "*.strm", SearchOption.AllDirectories);
            var orphanCount = existingStrms.Count(s => !validPaths.Contains(s));

            if (safetyThreshold > 0 && existingStrms.Length > 10 && orphanCount > 0)
            {
                double ratio = (double)orphanCount / existingStrms.Length;
                if (ratio > safetyThreshold)
                {
                    _logger.Warn(
                        "Orphan cleanup skipped: {0}/{1} ({2:P0}) exceeds safety threshold {3:P0} — possible provider issue",
                        orphanCount, existingStrms.Length, ratio, safetyThreshold);
                    return 0;
                }
            }
            var removed = 0;

            foreach (var strmFile in existingStrms)
            {
                if (!validPaths.Contains(strmFile))
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
            }

            if (removed > 0)
            {
                _logger.Info("Removed {0} orphaned STRM files from {1}", removed, rootPath);
            }

            return removed;
        }
    }
}
