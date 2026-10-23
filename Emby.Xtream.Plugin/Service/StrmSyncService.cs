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
using Emby.Xtream.Plugin.Client;
using Emby.Xtream.Plugin.Client.Models;
using MediaBrowser.Controller.Library;
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

    public partial class StrmSyncService
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
            XtreamRateLimitHandler.CreateDefault())
        { Timeout = TimeSpan.FromSeconds(30) };

        // Increment when naming logic changes so existing installs force a full re-sync on next run.
        // 2: specials moved from Season 01 / E01 to Season 00 / E00. The episode hash only covers
        // episode IDs, so without the bump shows with misplaced specials would be skipped as
        // unchanged and never corrected.
        internal const int CurrentStrmNamingVersion = 2;

        private static void ApplyUserAgentToSharedClient()
        {
            XtreamUserAgent.Apply(SharedHttpClient, Plugin.InstanceOrNull?.Configuration?.HttpUserAgent);
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

        // Replaces the call into Emby's library monitor; tests set it to see what would be
        // reported. Null in production.
        internal Action<string> LibraryChangedNotifier { get; set; }

        // Delay before re-fetching an empty get_series_info answer (see FetchSeriesDetailAsync).
        // Internal so tests can set it to zero.
        internal int SeriesDetailRetryDelayMs = 1000;

        // Single-flight gates. Each sync replaces its progress object wholesale and shares a
        // written-path set, so two overlapping runs of the same kind corrupt each other's state.
        // Movies and series are gated separately because they touch different roots and are
        // deliberately runnable at the same time. RetryFailedAsync takes the movie gate: it writes
        // movie files and reuses _movieProgress.
        private readonly SemaphoreSlim _movieSyncGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _seriesSyncGate = new SemaphoreSlim(1, 1);

        /// <summary>Lowest usable value for <see cref="PluginConfiguration.SyncParallelism"/>.</summary>
        private const int MinSyncParallelism = 1;

        /// <summary>Highest usable value for <see cref="PluginConfiguration.SyncParallelism"/>.</summary>
        private const int MaxSyncParallelism = 10;

        /// <summary>
        /// Returns a usable parallelism, correcting a persisted value that is out of range.
        /// </summary>
        /// <remarks>
        /// The config UI validates this, but the value is persisted to XML and survives hand edits
        /// and migrations. Zero is the dangerous one: <c>new SemaphoreSlim(0)</c> has no permits, so
        /// the first task waits forever and the sync hangs with no way out but editing the file.
        /// </remarks>
        internal static int GetSyncParallelism(PluginConfiguration config)
        {
            var configured = config.SyncParallelism;
            if (configured >= MinSyncParallelism && configured <= MaxSyncParallelism)
            {
                return configured;
            }

            return configured < MinSyncParallelism ? MinSyncParallelism : MaxSyncParallelism;
        }

        private int ResolveSyncParallelism(PluginConfiguration config)
        {
            var resolved = GetSyncParallelism(config);
            if (resolved != config.SyncParallelism)
            {
                _logger.Warn(
                    "SyncParallelism is {0}, which is outside the usable range {1}-{2} — using {3} for this run",
                    config.SyncParallelism, MinSyncParallelism, MaxSyncParallelism, resolved);
            }

            return resolved;
        }

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
        /// <summary>
        /// Tells Emby that the Movies or Shows folder changed, so content this sync wrote or
        /// removed shows up without waiting for a scheduled scan. Only when files were actually
        /// added or removed: a sync that changed nothing must not make Emby scan (or wake the
        /// disk). The folder is the one users add to Emby as a library, and it also covers the
        /// category subfolders of the multiple and custom folder modes.
        ///
        /// Reports the change rather than starting a library scan, so Emby refreshes only that
        /// folder. A failure is logged and ignored: the files are already correct and the
        /// scheduled scan still picks them up. From andyj682/emby-xtream-dedupe (771ac8c).
        /// </summary>
        private void NotifyEmbyLibraryChanged(PluginConfiguration config, string rootFolder, int added, int deleted)
        {
            if (!config.RefreshEmbyLibraryAfterSync || (added <= 0 && deleted <= 0))
            {
                return;
            }

            var path = Path.Combine(config.StrmLibraryPath ?? string.Empty, rootFolder);
            if (LibraryChangedNotifier != null)
            {
                LibraryChangedNotifier(path);
                return;
            }

            // Null outside a running Emby.
            var host = Plugin.InstanceOrNull?.ApplicationHost;
            if (host == null)
            {
                return;
            }

            try
            {
                var monitor = host.Resolve<ILibraryMonitor>();
                if (monitor == null)
                {
                    _logger.Warn("Library refresh: Emby's library monitor is not available; '{0}' is picked up by the next scheduled scan", path);
                    return;
                }

                monitor.ReportFileSystemChanged(path);
                _logger.Info("Library refresh: told Emby that '{0}' changed ({1} added, {2} removed)", path, added, deleted);
            }
            catch (Exception ex)
            {
                _logger.Warn("Library refresh failed for '{0}': {1}", path, ex.Message);
            }
        }

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

        /// <summary>
        /// Syncs movie STRM files. At most one movie sync runs at a time.
        /// </summary>
        /// <returns>
        /// False when a movie sync was already in progress and this request was ignored.
        /// True when this call ran (check <see cref="SyncProgress.AbortReason"/> for a run that
        /// started and then bailed on configuration).
        /// </returns>
        public async Task<bool> SyncMoviesAsync(PluginConfiguration config, CancellationToken cancellationToken, Action saveConfig = null, IProgress<double> taskProgress = null)
        {
            // Single-flight gate, held for the whole operation. Callers check IsRunning first, but
            // that is a fast path, not a lock: between the check and the assignment below, a second
            // request or the scheduled task can pass it too. Two runs then share writtenPaths and
            // the progress object, and whichever finishes first clears IsRunning while the other is
            // still writing — admitting a third run mid-cleanup.
            if (!await _movieSyncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _logger.Warn("Movie sync requested while one is already running — ignoring the duplicate request");
                return false;
            }

            try
            {
                await SyncMoviesCoreAsync(config, cancellationToken, saveConfig, taskProgress).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _movieSyncGate.Release();
            }
        }

        private async Task SyncMoviesCoreAsync(PluginConfiguration config, CancellationToken cancellationToken, Action saveConfig, IProgress<double> taskProgress)
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
                var semaphore = new SemaphoreSlim(ResolveSyncParallelism(config));

                // Counted at the write: Completed minus Skipped counted failures as writes.
                int moviesWrittenCount = 0;

                // Shared Dispatcharr VOD client — only queried per-movie, after smart-skip
                var dispatcharrVodClient = CreateDispatcharrVodClient(config);

                var writeContext = new MovieWriteContext
                {
                    Config = config,
                    CategoryNames = categoryNames,
                    FolderMappings = folderMappings,
                    WrittenPaths = writtenPaths,
                    DispatcharrVodClient = dispatcharrVodClient,
                    LastMovieTimestamp = lastMovieTs,
                };

                var tasks = allStreams.Select(async movie =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var write = new MovieWriteResult();
                    try
                    {
                        var outcome = await WriteMovieAsync(movie, writeContext, write, cancellationToken).ConfigureAwait(false);
                        if (outcome == MovieWriteOutcome.InvalidName)
                        {
                            Interlocked.Increment(ref _movieProgress.Failed);
                            return;
                        }

                        if (outcome == MovieWriteOutcome.NoFolder || outcome == MovieWriteOutcome.Skipped)
                        {
                            Interlocked.Increment(ref _movieProgress.Skipped);
                            Interlocked.Increment(ref _movieProgress.Completed);
                            ReportTaskProgress(_movieProgress, taskProgress);
                            return;
                        }

                        if (write.AddedNewFile)
                        {
                            lock (addedMovieTitles)
                            {
                                if (addedMovieTitles.Count < 20) addedMovieTitles.Add(write.CleanedName);
                            }
                        }

                        Interlocked.Increment(ref moviesWrittenCount);
                        Interlocked.Increment(ref _movieProgress.Completed);
                        ReportTaskProgress(_movieProgress, taskProgress);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Failed to write STRM for movie '{0}': [{1}] {2}", movie.Name, ex.GetType().Name, ex.Message);

                        // Keep what an earlier sync wrote for this movie in writtenPaths, so the
                        // exclusion pass treats its folder as in use (ADR-018). Orphan cleanup is
                        // unaffected: it does not run on a sync with a failed item.
                        RecordExistingStrms(write.MovieDir, writtenPaths);
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
                // Postponed when a category failed to load: a kept title that shares an excluded
                // title's folder is only protected by being in writtenPaths, and titles from that
                // category never got there. A failed item is recorded there by its catch block,
                // so item failures do not postpone exclusions (ADR-012, ADR-018).
                if (excludedMovies.Count > 0)
                {
                    if (!vodFetch.HadFailures)
                    {
                        _movieProgress.Phase = "Removing excluded movies";
                        _movieProgress.Deleted += RemoveExcludedContent(
                            config, excludedMovies, config.MovieFolderMode, categoryNames, folderMappings, "Movies", writtenPaths);
                    }
                    else
                    {
                        _logger.Warn(
                            "Removing excluded movies postponed to the next sync: a category failed to load");
                    }
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
                    moviesWrittenCount, _movieProgress.Skipped, _movieProgress.Failed);
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
                // In the finally: a sync that wrote files and then failed on a later step still
                // changed the library. A run that changed nothing reports nothing.
                NotifyEmbyLibraryChanged(config, "Movies", _movieProgress.Added, _movieProgress.Deleted);

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

        /// <summary>
        /// Syncs series STRM files. At most one series sync runs at a time.
        /// </summary>
        /// <returns>
        /// False when a series sync was already in progress and this request was ignored.
        /// True when this call ran.
        /// </returns>
        public async Task<bool> SyncSeriesAsync(PluginConfiguration config, CancellationToken cancellationToken, Action saveConfig = null, IProgress<double> taskProgress = null)
        {
            // See SyncMoviesAsync for why the caller's IsRunning check is not sufficient.
            if (!await _seriesSyncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _logger.Warn("Series sync requested while one is already running — ignoring the duplicate request");
                return false;
            }

            try
            {
                await SyncSeriesCoreAsync(config, cancellationToken, saveConfig, taskProgress).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _seriesSyncGate.Release();
            }
        }

        private async Task SyncSeriesCoreAsync(PluginConfiguration config, CancellationToken cancellationToken, Action saveConfig, IProgress<double> taskProgress)
        {
            ApplyUserAgentToSharedClient();
            CheckAndUpgradeNamingVersion(config, saveConfig);
            _seriesProgress = new SyncProgress { IsRunning = true, Phase = "Starting series sync" };
            _episodeProgress = new SyncProgress { IsRunning = true };
            // Series that failed last time are processed again whatever their LastModified says.
            // The watermark moves past a series before its detail is fetched, so after a failed
            // fetch it looks unchanged, and with smart skip on and its folder on disk it would be
            // skipped without a fetch on every later run.
            // The list itself is only cleared once the catalogue has loaded (below): a run that
            // stops before then has not processed these series and must not forget them.
            List<FailedSyncItem> previouslyFailedSeries;
            lock (_failedItemsLock)
            {
                previouslyFailedSeries = _failedItems.Where(i => i.ItemType == "Series").ToList();
            }
            var forcedSeriesIds = new HashSet<int>(previouslyFailedSeries.Select(i => i.StreamId));
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

                // The catalogue has loaded, so this run accounts for every previously failed series:
                // it is processed below (and re-added if it fails again), or it is not listed.
                lock (_failedItemsLock) { _failedItems.RemoveAll(i => i.ItemType == "Series"); }

                if (forcedSeriesIds.Count > 0)
                {
                    var seenIds = new HashSet<int>(fetchedSeries.Select(x => x.SeriesId));
                    var unseen = previouslyFailedSeries
                        .Where(i => !seenIds.Contains(i.StreamId) && !ContentExclusionFilter.IsExcluded(excludedSeriesSet, i.StreamId))
                        .ToList();
                    if (unseen.Count > 0 && seriesFetch.HadFailures)
                    {
                        // Probably in a category that failed to load. It stays in the failed list;
                        // orphan cleanup does not run on this sync, so its files are safe.
                        _logger.Warn(
                            "{0} series that failed last time were not seen because a category failed to load; they stay in the failed list: {1}",
                            unseen.Count, string.Join(", ", unseen.Select(i => i.Name)));
                        lock (_failedItemsLock) { _failedItems.AddRange(unseen); }
                    }
                    else if (unseen.Count > 0)
                    {
                        // Every category loaded, so the provider no longer lists them. They leave
                        // the failed list and orphan cleanup treats them like any dropped series.
                        _logger.Info(
                            "{0} series that failed last time are no longer in the catalogue: {1}",
                            unseen.Count, string.Join(", ", unseen.Select(i => i.Name)));
                    }

                    _logger.Info("Re-processing {0} series that failed last time", forcedSeriesIds.Count - unseen.Count);
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
                var semaphore = new SemaphoreSlim(ResolveSyncParallelism(config));

                // Episode hash cache: load stored hashes so we can skip file I/O for unchanged series
                var storedHashes = DeserializeEpisodeHashes(config.SeriesEpisodeHashesJson);
                var updatedHashes = new ConcurrentDictionary<string, string>();
                int hashSkippedCount = 0;

                // Counted where they happen rather than derived afterwards: "written" used to be
                // Completed minus Skipped, and failures increment Completed too, so they were
                // reported as writes. From andyj682/emby-xtream-dedupe (575cb64, 048d7f2).
                int writtenCount = 0;
                int preFetchSkippedCount = 0;
                int unmappedSkippedCount = 0;
                // Series already explained elsewhere in the log (failed, unmapped, empty), which
                // the "not checked this run" warning below leaves out.
                var reportedSeriesIds = new ConcurrentDictionary<int, byte>();
                var emptySeries = new ConcurrentBag<string>();

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
                    string seriesSubFolderForFailure = null;
                    string seriesNameForFailure = null;
                    try
                    {
                        var cleanedName = config.EnableContentNameCleaning
                            ? ContentNameCleaner.CleanContentName(series.Name, config.ContentRemoveTerms)
                            : series.Name;
                        var seriesName = SanitizeFileName(cleanedName);
                        if (string.IsNullOrWhiteSpace(seriesName))
                        {
                            reportedSeriesIds[series.SeriesId] = 0;
                            Interlocked.Increment(ref _seriesProgress.Failed);
                            return;
                        }

                        var subFolder = BuildContentFolderPath(
                            config.SeriesFolderMode, series.CategoryId, categoryNames, folderMappings, "Shows");
                        seriesSubFolderForFailure = subFolder;
                        seriesNameForFailure = seriesName;

                        if (subFolder == null)
                        {
                            reportedSeriesIds[series.SeriesId] = 0;
                            Interlocked.Increment(ref unmappedSkippedCount);
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

                        var isForcedSeries = forcedSeriesIds.Contains(series.SeriesId);
                        var isChangedSeries = lastSeriesTs == 0 || seriesLm > lastSeriesTs || isForcedSeries;

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
                                    Interlocked.Increment(ref preFetchSkippedCount);
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

                            // Existing episodes still count as in use, so an exclusion that
                            // matches this show's folder leaves it alone (ADR-018).
                            RecordStrms(FindExistingSeriesStrms(config, subFolder, seriesName), writtenPaths);
                            reportedSeriesIds[series.SeriesId] = 0;
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

                                // In the failed list so the next sync fetches it again: the
                                // watermark is already past it, so otherwise it is skipped as
                                // unchanged until the provider touches it.
                                lock (_failedItemsLock)
                                {
                                    _failedItems.Add(new FailedSyncItem
                                    {
                                        ItemType = "Series",
                                        StreamId = series.SeriesId,
                                        Name = series.Name,
                                        CategoryId = series.CategoryId,
                                        ErrorMessage = "Returned no episodes but has files on disk"
                                    });
                                }
                                Interlocked.Increment(ref _seriesProgress.Failed);
                            }
                            else
                            {
                                // Nothing to write and nothing to protect. This used to return
                                // without a word; it is reported in one line after the loop.
                                emptySeries.Add(string.Format(
                                    CultureInfo.InvariantCulture, "'{0}' (id={1})", series.Name, series.SeriesId));
                            }

                            reportedSeriesIds[series.SeriesId] = 0;
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
                            && !isForcedSeries
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
                            // The episodes map is keyed by season. Some providers only put the
                            // season there and leave the per-episode field at 0, so the key is the
                            // fallback, not season 1.
                            int keySeason;
                            var haveKeySeason = int.TryParse(
                                seasonEntry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out keySeason)
                                && keySeason >= 0;

                            foreach (var episode in seasonEntry.Value)
                            {
                                // Season 0 and episode 0 are specials. Forcing them to 1 put them on
                                // the real Season 01 / E01, where a different title wrote a second
                                // file beside the real episode. Emby files Season 00 under Specials.
                                // From andyj682/emby-xtream-dedupe (4c3e0aa).
                                var seasonNum = episode.Season > 0
                                    ? episode.Season
                                    : (haveKeySeason ? keySeason : 1);
                                var episodeNum = episode.EpisodeNum >= 0 ? episode.EpisodeNum : 1;
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

                                var streamUrl = XtreamUrls.Stream(config, "series", episode.Id, ext);

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
                        Interlocked.Increment(ref writtenCount);
                        Interlocked.Increment(ref _seriesProgress.Completed);
                        ReportTaskProgress(_seriesProgress, taskProgress);
                    }
                    catch (Exception ex)
                    {
                        reportedSeriesIds[series.SeriesId] = 0;
                        _logger.Error("Failed to write STRM for series '{0}' (id={1}): [{2}] {3}", series.Name, series.SeriesId, ex.GetType().Name, ex.Message);
                        if (seriesSubFolderForFailure != null)
                        {
                            // As for a failed detail fetch: existing episodes still count as in use.
                            RecordStrms(FindExistingSeriesStrms(config, seriesSubFolderForFailure, seriesNameForFailure), writtenPaths);
                        }
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
                // Postponed when a category failed to load, as for movies above. A series whose
                // episode list came back empty already keeps its existing files in writtenPaths.
                if (excludedSeriesItems.Count > 0)
                {
                    if (!seriesFetch.HadFailures)
                    {
                        _seriesProgress.Phase = "Removing excluded series";
                        _seriesProgress.Deleted += RemoveExcludedContent(
                            config, excludedSeriesItems, config.SeriesFolderMode, categoryNames, folderMappings, "Shows", writtenPaths);
                    }
                    else
                    {
                        _logger.Warn(
                            "Removing excluded series postponed to the next sync: a category failed to load");
                    }
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

                if (!emptySeries.IsEmpty)
                {
                    var names = emptySeries.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                    _logger.Warn(
                        "{0} series returned no episodes and have no files on disk, so there was nothing to write: {1}{2}. They are checked again every sync; exclude them to stop that.",
                        names.Count, string.Join(", ", names.Take(20)), names.Count > 20 ? ", ..." : string.Empty);
                }

                // Every other series leaves an episode hash behind: computed after a fetch, or
                // carried over by the skip. One without either was skipped without a fetch and
                // with no record of its episodes, while the run still reported success.
                var uncheckedSeries = allSeries
                    .Where(x => !reportedSeriesIds.ContainsKey(x.SeriesId)
                        && !updatedHashes.ContainsKey(x.SeriesId.ToString(CultureInfo.InvariantCulture)))
                    .Select(x => string.Format(CultureInfo.InvariantCulture, "'{0}' (id={1})", x.Name, x.SeriesId))
                    .ToList();
                if (uncheckedSeries.Count > 0)
                {
                    _logger.Warn(
                        "{0} series were skipped as unchanged but have no stored episode list, so their episodes were not checked this run: {1}{2}",
                        uncheckedSeries.Count, string.Join(", ", uncheckedSeries.Take(20)), uncheckedSeries.Count > 20 ? ", ..." : string.Empty);
                }

                _logger.Info(
                    "Series STRM sync completed: {0} series, {1} written, {2} skipped ({3} unchanged, {4} same episodes{5}), {6} failed",
                    _seriesProgress.Total,
                    writtenCount,
                    _seriesProgress.Skipped,
                    preFetchSkippedCount,
                    hashSkippedCount,
                    unmappedSkippedCount > 0
                        ? string.Format(CultureInfo.InvariantCulture, ", {0} in unmapped categories", unmappedSkippedCount)
                        : string.Empty,
                    _seriesProgress.Failed);
            }
            catch (Exception ex)
            {
                _logger.Error("Series sync failed: {0}", ex.Message);
                _seriesProgress.Phase = "Failed: " + ex.Message;
                seriesSyncSuccess = false;

                // A run that stopped part-way may have cleared the failed list before reaching
                // these series. Put back any that are not in it, so they are retried next time.
                lock (_failedItemsLock)
                {
                    var listed = new HashSet<int>(_failedItems.Where(i => i.ItemType == "Series").Select(i => i.StreamId));
                    _failedItems.AddRange(previouslyFailedSeries.Where(i => !listed.Contains(i.StreamId)));
                }

                throw;
            }
            finally
            {
                // In the finally, as for movies. Episodes added, not series written: a series
                // counts as written even when every episode file already matched. Deletions come
                // from the series counter, which also includes excluded series removed this run.
                NotifyEmbyLibraryChanged(config, "Shows", _episodeProgress.Added, _seriesProgress.Deleted);

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

        /// <summary>Handler for the Dispatcharr VOD client; null uses the real network. For tests.</summary>
        internal System.Net.Http.HttpMessageHandler DispatcharrHandler { get; set; }

        /// <summary>The Dispatcharr client the movie writer asks for extra versions, or null without Dispatcharr.</summary>
        private Emby.Xtream.Plugin.Client.DispatcharrClient CreateDispatcharrVodClient(PluginConfiguration config)
        {
            if (!config.EnableDispatcharr || string.IsNullOrEmpty(config.DispatcharrUrl))
                return null;
            var client = new Emby.Xtream.Plugin.Client.DispatcharrClient(_logger, DispatcharrHandler);
            client.Configure(config.DispatcharrUser, config.DispatcharrPass);
            return client;
        }

        private enum MovieWriteOutcome { InvalidName, NoFolder, Skipped, Written }

        /// <summary>What one movie write produced, filled in as it goes so a caller's catch can read it.</summary>
        private sealed class MovieWriteResult
        {
            public string CleanedName;
            public string MovieDir;
            public bool AddedNewFile;
        }

        private sealed class MovieWriteContext
        {
            public PluginConfiguration Config;
            public Dictionary<int, string> CategoryNames;
            public Dictionary<int, string> FolderMappings;
            public HashSet<string> WrittenPaths;
            public Emby.Xtream.Plugin.Client.DispatcharrClient DispatcharrVodClient;
            /// <summary>0 treats every movie as new, so smart skip never applies (the retry passes 0).</summary>
            public long LastMovieTimestamp;
        }

        /// <summary>
        /// Writes one movie's .strm files (and NFO) exactly as the movie sync does. The sync and
        /// "Retry failed items" both call this, so a retried movie lands where the sync puts it;
        /// a separate retry writer drifted before, for movies and for series (ADR-020). Counters
        /// other than Added are the caller's. Throws on a failed write.
        /// </summary>
        private async Task<MovieWriteOutcome> WriteMovieAsync(
            VodStreamInfo movie, MovieWriteContext ctx, MovieWriteResult result, CancellationToken cancellationToken)
        {
            var config = ctx.Config;
            var categoryNames = ctx.CategoryNames;
            var folderMappings = ctx.FolderMappings;
            var writtenPaths = ctx.WrittenPaths;
            var dispatcharrVodClient = ctx.DispatcharrVodClient;
            var lastMovieTs = ctx.LastMovieTimestamp;

            var cleanedName = config.EnableContentNameCleaning
                ? ContentNameCleaner.CleanContentName(movie.Name, config.ContentRemoveTerms)
                : movie.Name;
            result.CleanedName = cleanedName;
            var movieName = SanitizeFileName(cleanedName);
            if (string.IsNullOrWhiteSpace(movieName))
            {
                return MovieWriteOutcome.InvalidName;
            }

            // Two distinct skip probes, picked by the flag combination:
            //   * Folder naming ON — folder path depends on the TMDB ID, so resolve
            //     first and probe the suffixed path. Otherwise we'd skip on a path
            //     that never gets written.
            //   * Folder naming OFF — probe the plain (un-suffixed) path first. If
            //     the STRM already exists, we skip without ever running the
            //     network fallback. Only resolve the TMDB ID when we'll proceed
            //     past the skip check (the NFO writer may still need it).
            //
            // See ADR-015 for the NFO/folder-naming decoupling context.
            string tmdbId = null;
            if (config.EnableTmdbFolderNaming)
            {
                tmdbId = await ResolveMovieTmdbIdAsync(
                    movie, cleanedName, config,
                    (n, y, ct) => _tmdbLookupService.LookupTmdbIdAsync(n, y, ct),
                    _logger,
                    cancellationToken).ConfigureAwait(false);
            }

            var folderName = BuildMovieFolderName(cleanedName, tmdbId);
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return MovieWriteOutcome.InvalidName;
            }

            var subFolder = BuildContentFolderPath(
                config.MovieFolderMode, movie.CategoryId, categoryNames, folderMappings, "Movies");
            if (subFolder == null)
            {
                return MovieWriteOutcome.NoFolder;
            }

            var movieDir = Path.Combine(config.StrmLibraryPath, subFolder, folderName);
            result.MovieDir = movieDir;
            var strmPath = Path.Combine(movieDir, folderName + ".strm");

            // Smart skip: if file already exists AND the movie is not new (delta), skip
            var isNewMovie = lastMovieTs == 0 || movie.Added > lastMovieTs;
            if (!isNewMovie && config.SmartSkipExisting && File.Exists(strmPath))
            {
                lock (writtenPaths)
                {
                    writtenPaths.Add(strmPath);
                }
                return MovieWriteOutcome.Skipped;
            }

            // Folder naming off + NFO on: defer the TMDB fallback lookup until after
            // the skip check. Movies that get skipped never trigger a network call.
            if (!config.EnableTmdbFolderNaming && config.EnableNfoFiles && tmdbId == null)
            {
                tmdbId = await ResolveMovieTmdbIdAsync(
                    movie, cleanedName, config,
                    (n, y, ct) => _tmdbLookupService.LookupTmdbIdAsync(n, y, ct),
                    _logger,
                    cancellationToken).ConfigureAwait(false);
            }

            var ext = !string.IsNullOrEmpty(movie.ContainerExtension)
                ? movie.ContainerExtension
                : "mp4";

            var streamUrl = XtreamUrls.Stream(config, "movie", movie.StreamId, ext);

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
            result.AddedNewFile = isAnyNewFile;

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

            return MovieWriteOutcome.Written;
        }

        /// <summary>
        /// Re-attempts the items that failed in the last sync.
        /// </summary>
        /// <returns>False when a movie sync or another retry was already running.</returns>
        public Task<bool> RetryFailedAsync(CancellationToken cancellationToken)
            => RetryFailedAsync(null, null, cancellationToken);

        /// <summary>
        /// Retries failed items. Movies are rewritten one by one; series go through a normal
        /// series sync, which re-processes every series in the failed list (see
        /// SyncSeriesCoreAsync), so a retried series is written exactly as the sync writes it.
        /// </summary>
        /// <param name="configOverride">Configuration to use; the plugin's own when null. For tests.</param>
        /// <param name="saveConfig">Persists the configuration; the plugin's own save when null.</param>
        internal async Task<bool> RetryFailedAsync(
            PluginConfiguration configOverride, Action saveConfig, CancellationToken cancellationToken)
        {
            List<FailedSyncItem> items;
            lock (_failedItemsLock) { items = _failedItems.ToList(); }
            if (items.Count == 0) return true;

            // Shares _movieProgress with SyncMoviesAsync and writes into the same movie tree, so it
            // takes the same gate rather than racing a sync that is already underway.
            if (!await _movieSyncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _logger.Warn("Retry requested while a movie sync is already running — ignoring the duplicate request");
                return false;
            }

            // Nothing between acquiring a gate and its finally may throw, or the gate is held for
            // good and every later movie sync is refused until Emby restarts. The series
            // WaitAsync below throws on a cancelled token, and Plugin.Instance.Configuration
            // throws when ApplicationPaths is not initialised yet, so both sit inside.
            var seriesGateHeld = false;
            var retryStarted = false;
            try
            {
                // A retry batch can contain series items, and those write episodes under Shows.
                // Without the series gate a concurrent series sync would not have the
                // retry-written paths in its valid set, so its orphan cleanup would delete them —
                // and they would pass the ownership check precisely because the retry wrote them.
                if (items.Any(i => i.ItemType == "Series"))
                {
                    seriesGateHeld = await _seriesSyncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
                    if (!seriesGateHeld)
                    {
                        _logger.Warn("Retry requested while a series sync is already running — ignoring the duplicate request");
                        return false;
                    }
                }

                var config = configOverride ?? Plugin.Instance.Configuration;
                if (saveConfig == null)
                {
                    saveConfig = () => Plugin.Instance.SaveConfiguration();
                }

                var movieItems = items.Where(i => i.ItemType == "Movie").ToList();
                var hasSeriesItems = items.Any(i => i.ItemType == "Series");
                _movieProgress = new SyncProgress { IsRunning = true, Phase = "Retrying failed items", Total = movieItems.Count };
                retryStarted = true;

                var semaphore = new SemaphoreSlim(ResolveSyncParallelism(config));
                var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var succeeded = new List<FailedSyncItem>();
                var succeededLock = new object();

                // The same inputs the movie sync gives its writer. Only the old category-name mode
                // ("multiple", converted to "custom" when the config page saves) reads the names;
                // without them that retry wrote nothing and still counted as done.
                var categoryNames = new Dictionary<int, string>();
                Exception categoryError = null;
                if (movieItems.Count > 0 && string.Equals(config.MovieFolderMode, "multiple", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        foreach (var cat in await FetchCategoriesAsync("get_vod_categories", config, cancellationToken).ConfigureAwait(false))
                            categoryNames[cat.CategoryId] = cat.CategoryName;
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        categoryError = ex;
                    }
                }

                var dispatcharrVodClient = CreateDispatcharrVodClient(config);

                var writeContext = new MovieWriteContext
                {
                    Config = config,
                    CategoryNames = categoryNames,
                    FolderMappings = FolderMappingParser.Parse(config.MovieFolderMappings),
                    WrittenPaths = writtenPaths,
                    DispatcharrVodClient = dispatcharrVodClient,
                    // The movie failed last time, so write it whatever is on disk, as the old retry did.
                    LastMovieTimestamp = 0,
                };

                var tasks = movieItems.Select(async item =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        // Writing into a guessed folder would split the movie from the sync's copy.
                        if (categoryError != null)
                            throw new InvalidOperationException("Could not load VOD categories: " + categoryError.Message, categoryError);

                        var movie = new VodStreamInfo
                        {
                            StreamId = item.StreamId,
                            Name = item.Name,
                            CategoryId = item.CategoryId,
                            TmdbId = item.TmdbId,
                            ContainerExtension = item.ContainerExtension,
                        };
                        var outcome = await WriteMovieAsync(movie, writeContext, new MovieWriteResult(), cancellationToken).ConfigureAwait(false);
                        // Only a written movie is done. A retry that wrote nothing (no folder for its
                        // category, or no usable name) stays in the failed list for the next sync.
                        if (outcome != MovieWriteOutcome.Written)
                            throw new InvalidOperationException("Nothing written (" + outcome + ")");

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

                // Retried series are reported by the series sync below; movies are written here.
                NotifyEmbyLibraryChanged(config, "Movies", _movieProgress.Added, 0);

                // After the movie bookkeeping above, so a series sync that throws cannot leave
                // successfully retried movies marked as failed. The series gate is held, so call
                // the core rather than SyncSeriesAsync. It removes the series it processes from
                // the failed list and adds back any that fail again.
                if (hasSeriesItems)
                {
                    _movieProgress.Phase = "Retrying failed series";
                    await SyncSeriesCoreAsync(config, cancellationToken, saveConfig, null).ConfigureAwait(false);
                }

                return true;
            }
            finally
            {
                // Only report a finished retry if one actually started. Bailing out because the
                // series gate was taken leaves the previous run's progress object in place, and
                // stamping "Retry complete" on it would describe a run that never happened.
                if (retryStarted)
                {
                    _movieProgress.IsRunning = false;
                    _movieProgress.Phase = "Retry complete";
                }

                if (seriesGateHeld)
                {
                    _seriesSyncGate.Release();
                }
                _movieSyncGate.Release();
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

        /// <summary>
        /// Resolves a movie's TMDB ID for any downstream consumer that needs one. The lookup runs
        /// when EITHER <see cref="PluginConfiguration.EnableTmdbFolderNaming"/> OR
        /// <see cref="PluginConfiguration.EnableNfoFiles"/> is set, so that an NFO-only config can
        /// still resolve IDs to populate sidecar files. Returns null if neither flag wants a lookup
        /// or both the provider-supplied ID and the fallback lookup produced nothing.
        ///
        /// Caller is responsible for deciding whether to use the returned ID for folder naming
        /// (gate on <see cref="PluginConfiguration.EnableTmdbFolderNaming"/>) or for NFO content
        /// (gate on <see cref="PluginConfiguration.EnableNfoFiles"/>) — this helper does not
        /// enforce either, since the two are independent.
        /// </summary>
        internal static async Task<string> ResolveMovieTmdbIdAsync(
            VodStreamInfo movie, string cleanedName, PluginConfiguration config,
            Func<string, int?, CancellationToken, Task<string>> lookupTmdbIdAsync,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (!config.EnableTmdbFolderNaming && !config.EnableNfoFiles)
            {
                return null;
            }

            if (IsValidTmdbId(movie.TmdbId))
            {
                return movie.TmdbId.Trim();
            }

            if (config.EnableTmdbFallbackLookup)
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
                    return await lookupTmdbIdAsync(cleanedName, yearForLookup, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.Debug("TMDB fallback error for '{0}': {1}", cleanedName, ex.Message);
                }
            }

            return null;
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
            var url = XtreamUrls.PlayerApi(config, action);

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
                var url = XtreamUrls.PlayerApi(config, "get_vod_streams");

                var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                allStreams = STJ.JsonSerializer.Deserialize<List<VodStreamInfo>>(json, JsonOptions)
                    ?? new List<VodStreamInfo>();
            }
            else
            {
                result.RequestedCategoryCount = categoryIds.Length;
                var semaphore = new SemaphoreSlim(ResolveSyncParallelism(config));
                var tasks = categoryIds.Select(async catId =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var url = XtreamUrls.PlayerApi(config, "get_vod_streams", "category_id", catId);

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
                var url = XtreamUrls.PlayerApi(config, "get_series");

                var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                allSeries = STJ.JsonSerializer.Deserialize<List<SeriesInfo>>(json, JsonOptions)
                    ?? new List<SeriesInfo>();
            }
            else
            {
                result.RequestedCategoryCount = categoryIds.Length;
                var semaphore = new SemaphoreSlim(ResolveSyncParallelism(config));
                var tasks = categoryIds.Select(async catId =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var url = XtreamUrls.PlayerApi(config, "get_series", "category_id", catId);

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

        private async Task<SeriesDetailInfo> FetchSeriesDetailAsync(
            int seriesId, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var url = XtreamUrls.PlayerApi(config, "get_series_info", "series_id", seriesId);

            var detail = await GetSeriesDetailAsync(url).ConfigureAwait(false);
            if (HasEpisodes(detail))
            {
                return detail;
            }

            // Some providers answer 200 with an empty episode list when several detail requests
            // arrive at once, and the same series comes back fine a moment later. Retry once.
            // Only once: a series that really has no episodes never gets an episode hash, so it
            // is fetched on every sync, and each extra attempt is paid on every one of them.
            // Found in andyj682/emby-xtream-dedupe (ff63da3).
            _logger.Debug("Series {0} returned no episodes; retrying once", seriesId);
            if (SeriesDetailRetryDelayMs > 0)
            {
                await Task.Delay(SeriesDetailRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }

            var retried = await GetSeriesDetailAsync(url).ConfigureAwait(false);
            if (HasEpisodes(retried))
            {
                _logger.Info("Series {0} returned episodes on retry after an empty answer", seriesId);
            }

            return retried;
        }

        private async Task<SeriesDetailInfo> GetSeriesDetailAsync(string url)
        {
            var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
            return STJ.JsonSerializer.Deserialize<SeriesDetailInfo>(json, JsonOptions);
        }

        private static bool HasEpisodes(SeriesDetailInfo detail)
            => detail != null && detail.Episodes != null && detail.Episodes.Count > 0;
    }
}
