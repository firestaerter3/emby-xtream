using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.Xtream.Plugin.Api
{
    [Route("/XtreamTuner/Epg", "GET", Summary = "Gets XMLTV EPG data for Live TV channels")]
    public class GetEpgXml : IReturnVoid
    {
    }

    [Route("/XtreamTuner/LiveTv", "GET", Summary = "Gets M3U playlist for Live TV channels")]
    public class GetM3UPlaylist : IReturnVoid
    {
    }

    [Route("/XtreamTuner/Catchup", "GET", Summary = "Gets M3U playlist for catch-up enabled channels")]
    public class GetCatchupM3U : IReturnVoid
    {
    }

    [Route("/XtreamTuner/Categories/Live", "GET", Summary = "Gets Live TV categories from Xtream API")]
    public class GetLiveCategories : IReturn<List<Category>>
    {
    }

    [Route("/XtreamTuner/RefreshCache", "POST", Summary = "Invalidates M3U and EPG caches")]
    public class RefreshCache : IReturnVoid
    {
    }

    [Route("/XtreamTuner/Categories/Vod", "GET", Summary = "Gets VOD movie categories from Xtream API")]
    public class GetVodCategories : IReturn<List<Category>>
    {
    }

    [Route("/XtreamTuner/Categories/Series", "GET", Summary = "Gets Series categories from Xtream API")]
    public class GetSeriesCategories : IReturn<List<Category>>
    {
    }

    [Route("/XtreamTuner/Sync/Movies", "POST", Summary = "Triggers VOD movie STRM sync")]
    public class SyncMovies : IReturn<SyncResult>
    {
    }

    [Route("/XtreamTuner/Sync/Series", "POST", Summary = "Triggers series STRM sync")]
    public class SyncSeries : IReturn<SyncResult>
    {
    }

    [Route("/XtreamTuner/Sync/Status", "GET", Summary = "Gets current sync progress")]
    public class GetSyncStatus : IReturn<SyncStatusResult>
    {
    }

    [Route("/XtreamTuner/Dashboard", "GET", Summary = "Gets dashboard data including sync history and library stats")]
    public class GetDashboard : IReturn<DashboardResult>
    {
    }

    [Route("/XtreamTuner/Content/Movies", "DELETE", Summary = "Deletes all movie STRM content")]
    public class DeleteMovieContent : IReturn<DeleteContentResult>
    {
    }

    [Route("/XtreamTuner/Content/Series", "DELETE", Summary = "Deletes all series STRM content")]
    public class DeleteSeriesContent : IReturn<DeleteContentResult>
    {
    }

    [Route("/XtreamTuner/TestConnection", "POST", Summary = "Tests connection to Xtream server")]
    public class TestXtreamConnection : IReturn<TestConnectionResult>
    {
    }

    [Route("/XtreamTuner/TestDispatcharr", "POST", Summary = "Tests connection to Dispatcharr")]
    public class TestDispatcharrConnection : IReturn<TestConnectionResult>
    {
    }

    [Route("/XtreamTuner/Logs", "GET", Summary = "Downloads sanitized plugin logs")]
    public class GetSanitizedLogs : IReturnVoid
    {
    }

    [Route("/XtreamTuner/TestTmdbLookup", "GET", Summary = "Tests TMDB fallback lookup")]
    public class TestTmdbLookup : IReturn<TestConnectionResult>
    {
        public string Name { get; set; }
        public int? Year { get; set; }
    }

    public class TestConnectionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class DeleteContentResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int DeletedFolders { get; set; }
    }

    public class SyncResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int Total { get; set; }
        public int Completed { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
    }

    public class SyncStatusResult
    {
        public SyncProgressInfo Movies { get; set; }
        public SyncProgressInfo Series { get; set; }
    }

    public class SyncProgressInfo
    {
        public string Phase { get; set; }
        public int Total { get; set; }
        public int Completed { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public bool IsRunning { get; set; }
    }

    public class DashboardResult
    {
        public SyncHistoryEntry LastSync { get; set; }
        public List<SyncHistoryEntry> History { get; set; }
        public bool IsRunning { get; set; }
        public LibraryStats LibraryStats { get; set; }
    }

    public class LibraryStats
    {
        public int MovieFolders { get; set; }
        public int SeriesFolders { get; set; }
    }

    public class XtreamTunerApi : BaseApiService
    {
        public async Task<object> Get(GetEpgXml request)
        {
            var liveTvService = Plugin.Instance.LiveTvService;
            var xml = await liveTvService.GetXmltvEpgAsync(CancellationToken.None).ConfigureAwait(false);

            var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            return ResultFactory.GetResult(Request, stream, "application/xml", new Dictionary<string, string>());
        }

        public async Task<object> Get(GetM3UPlaylist request)
        {
            var liveTvService = Plugin.Instance.LiveTvService;
            var m3u = await liveTvService.GetM3UPlaylistAsync(CancellationToken.None).ConfigureAwait(false);

            var stream = new MemoryStream(Encoding.UTF8.GetBytes(m3u));
            return ResultFactory.GetResult(Request, stream, "audio/x-mpegurl", new Dictionary<string, string>());
        }

        public async Task<object> Get(GetCatchupM3U request)
        {
            var liveTvService = Plugin.Instance.LiveTvService;
            var m3u = await liveTvService.GetCatchupM3UPlaylistAsync(CancellationToken.None).ConfigureAwait(false);

            var stream = new MemoryStream(Encoding.UTF8.GetBytes(m3u));
            return ResultFactory.GetResult(Request, stream, "audio/x-mpegurl", new Dictionary<string, string>());
        }

        public async Task<object> Get(GetLiveCategories request)
        {
            var liveTvService = Plugin.Instance.LiveTvService;
            var categories = await liveTvService.GetLiveCategoriesAsync(CancellationToken.None).ConfigureAwait(false);

            // Cache for instant UI loading
            var config = Plugin.Instance.Configuration;
            config.CachedLiveCategories = System.Text.Json.JsonSerializer.Serialize(
                    categories.Select(c => new { c.CategoryId, c.CategoryName }).ToList());
            Plugin.Instance.SaveConfiguration();

            return categories;
        }

        public async Task<object> Get(GetVodCategories request)
        {
            var config = Plugin.Instance.Configuration;
            var url = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_vod_categories",
                config.BaseUrl, config.Username, config.Password);

            using (var httpClient = new System.Net.Http.HttpClient())
            {
                var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                var categories = System.Text.Json.JsonSerializer.Deserialize<List<Category>>(json,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
                        PropertyNameCaseInsensitive = true,
                    }) ?? new List<Category>();
                var sorted = categories.OrderBy(c => c.CategoryName).ToList();

                // Cache for instant UI loading
                config.CachedVodCategories = System.Text.Json.JsonSerializer.Serialize(
                    sorted.Select(c => new { c.CategoryId, c.CategoryName }).ToList());
                Plugin.Instance.SaveConfiguration();

                return sorted;
            }
        }

        public async Task<object> Get(GetSeriesCategories request)
        {
            var config = Plugin.Instance.Configuration;
            var jsonOptions = new System.Text.Json.JsonSerializerOptions
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
                PropertyNameCaseInsensitive = true,
            };

            using (var httpClient = new System.Net.Http.HttpClient())
            {
                var url = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}&action=get_series_categories",
                    config.BaseUrl, config.Username, config.Password);

                var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                var categories = System.Text.Json.JsonSerializer.Deserialize<List<Category>>(json, jsonOptions)
                    ?? new List<Category>();
                var sorted = categories.OrderBy(c => c.CategoryName).ToList();

                // Fallback: derive categories from series list when server returns empty
                if (sorted.Count == 0)
                {
                    var seriesUrl = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0}/player_api.php?username={1}&password={2}&action=get_series",
                        config.BaseUrl, config.Username, config.Password);

                    var seriesJson = await httpClient.GetStringAsync(seriesUrl).ConfigureAwait(false);
                    var seriesList = System.Text.Json.JsonSerializer.Deserialize<List<SeriesInfo>>(seriesJson, jsonOptions)
                        ?? new List<SeriesInfo>();

                    sorted = seriesList
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

                // Cache for instant UI loading
                config.CachedSeriesCategories = System.Text.Json.JsonSerializer.Serialize(
                    sorted.Select(c => new { c.CategoryId, c.CategoryName }).ToList());
                Plugin.Instance.SaveConfiguration();

                return sorted;
            }
        }

        public async Task<object> Post(SyncMovies request)
        {
            var config = Plugin.Instance.Configuration;
            var syncService = Plugin.Instance.StrmSyncService;
            var result = new SyncResult();

            if (!config.SyncMovies)
            {
                result.Success = false;
                result.Message = "Movie sync is not enabled. Enable it in Settings first.";
                return result;
            }

            if (syncService.MovieProgress.IsRunning)
            {
                result.Success = false;
                result.Message = "Movie sync is already running.";
                return result;
            }

            try
            {
                await syncService.SyncMoviesAsync(CancellationToken.None).ConfigureAwait(false);
                var progress = syncService.MovieProgress;
                result.Success = true;
                result.Message = "Movie sync completed.";
                result.Total = progress.Total;
                result.Completed = progress.Completed;
                result.Skipped = progress.Skipped;
                result.Failed = progress.Failed;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Movie sync failed: " + ex.Message;
            }

            return result;
        }

        public async Task<object> Post(SyncSeries request)
        {
            var config = Plugin.Instance.Configuration;
            var syncService = Plugin.Instance.StrmSyncService;
            var result = new SyncResult();

            if (!config.SyncSeries)
            {
                result.Success = false;
                result.Message = "Series sync is not enabled. Enable it in Settings first.";
                return result;
            }

            if (syncService.SeriesProgress.IsRunning)
            {
                result.Success = false;
                result.Message = "Series sync is already running.";
                return result;
            }

            try
            {
                await syncService.SyncSeriesAsync(CancellationToken.None).ConfigureAwait(false);
                var progress = syncService.SeriesProgress;
                result.Success = true;
                result.Message = "Series sync completed.";
                result.Total = progress.Total;
                result.Completed = progress.Completed;
                result.Skipped = progress.Skipped;
                result.Failed = progress.Failed;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Series sync failed: " + ex.Message;
            }

            return result;
        }

        public object Get(GetSyncStatus request)
        {
            var syncService = Plugin.Instance.StrmSyncService;
            var movieProg = syncService.MovieProgress;
            var seriesProg = syncService.SeriesProgress;

            return new SyncStatusResult
            {
                Movies = new SyncProgressInfo
                {
                    Phase = movieProg.Phase,
                    Total = movieProg.Total,
                    Completed = movieProg.Completed,
                    Skipped = movieProg.Skipped,
                    Failed = movieProg.Failed,
                    IsRunning = movieProg.IsRunning,
                },
                Series = new SyncProgressInfo
                {
                    Phase = seriesProg.Phase,
                    Total = seriesProg.Total,
                    Completed = seriesProg.Completed,
                    Skipped = seriesProg.Skipped,
                    Failed = seriesProg.Failed,
                    IsRunning = seriesProg.IsRunning,
                },
            };
        }

        public object Get(GetDashboard request)
        {
            var syncService = Plugin.Instance.StrmSyncService;
            var config = Plugin.Instance.Configuration;
            var history = syncService.GetSyncHistory();

            var movieFolders = 0;
            var seriesFolders = 0;

            try
            {
                var moviesRoot = Path.Combine(config.StrmLibraryPath, "Movies");
                if (Directory.Exists(moviesRoot))
                {
                    movieFolders = Directory.GetDirectories(moviesRoot, "*", SearchOption.TopDirectoryOnly).Length;
                }
            }
            catch { }

            try
            {
                var seriesRoot = Path.Combine(config.StrmLibraryPath, "Shows");
                if (Directory.Exists(seriesRoot))
                {
                    seriesFolders = Directory.GetDirectories(seriesRoot, "*", SearchOption.TopDirectoryOnly).Length;
                }
            }
            catch { }

            return new DashboardResult
            {
                LastSync = history.Count > 0 ? history[0] : null,
                History = history,
                IsRunning = syncService.MovieProgress.IsRunning || syncService.SeriesProgress.IsRunning,
                LibraryStats = new LibraryStats
                {
                    MovieFolders = movieFolders,
                    SeriesFolders = seriesFolders,
                },
            };
        }

        public object Delete(DeleteMovieContent request)
        {
            return DeleteContentFolder("Movies");
        }

        public object Delete(DeleteSeriesContent request)
        {
            return DeleteContentFolder("Shows");
        }

        private DeleteContentResult DeleteContentFolder(string folderName)
        {
            var config = Plugin.Instance.Configuration;
            var result = new DeleteContentResult();

            try
            {
                var root = Path.Combine(config.StrmLibraryPath, folderName);
                if (!Directory.Exists(root))
                {
                    result.Success = true;
                    result.Message = folderName + " folder does not exist. Nothing to delete.";
                    return result;
                }

                var dirs = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly);
                result.DeletedFolders = dirs.Length;

                Directory.Delete(root, true);
                Directory.CreateDirectory(root);

                result.Success = true;
                result.Message = string.Format("Deleted {0} folders from {1}.", dirs.Length, folderName);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Failed to delete " + folderName + " content: " + ex.Message;
            }

            return result;
        }

        public async Task<object> Get(TestTmdbLookup request)
        {
            var result = new TestConnectionResult();
            try
            {
                var host = Plugin.Instance?.ApplicationHost;
                if (host == null)
                {
                    result.Message = "ApplicationHost is null";
                    return result;
                }

                var providerManager = host.Resolve<MediaBrowser.Controller.Providers.IProviderManager>();
                if (providerManager == null)
                {
                    result.Message = "IProviderManager resolved to null";
                    return result;
                }

                result.Message = "IProviderManager resolved: " + providerManager.GetType().FullName;

                var name = request.Name ?? "Apocalypto";
                var movieType = typeof(MediaBrowser.Controller.Entities.Movies.Movie);
                var lookupInfoType = typeof(MediaBrowser.Controller.Providers.ItemLookupInfo);

                // Find MovieInfo type at runtime (not in compile-time SDK)
                Type movieInfoType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        foreach (var t in asm.GetTypes())
                        {
                            if (t.Name == "MovieInfo" && lookupInfoType.IsAssignableFrom(t))
                            {
                                movieInfoType = t;
                                break;
                            }
                        }
                        if (movieInfoType != null) break;
                    }
                    catch { }
                }

                result.Message += " | MovieInfoType: " + (movieInfoType != null ? movieInfoType.FullName : "NOT FOUND");

                if (movieInfoType != null)
                {
                    var searchInfo = Activator.CreateInstance(movieInfoType);
                    movieInfoType.GetProperty("Name").SetValue(searchInfo, name);
                    if (request.Year.HasValue)
                        movieInfoType.GetProperty("Year").SetValue(searchInfo, request.Year);

                    var queryType = typeof(MediaBrowser.Controller.Providers.RemoteSearchQuery<>).MakeGenericType(movieInfoType);
                    var queryObj = Activator.CreateInstance(queryType);
                    queryType.GetProperty("SearchInfo").SetValue(queryObj, searchInfo);
                    queryType.GetProperty("IncludeDisabledProviders").SetValue(queryObj, true);

                    // Use GetMethods() filtering to avoid AmbiguousMatchException
                    var methods = typeof(MediaBrowser.Controller.Providers.IProviderManager).GetMethods();
                    System.Reflection.MethodInfo method = null;
                    foreach (var m in methods)
                    {
                        if (m.Name == "GetRemoteSearchResults" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2)
                        {
                            method = m;
                            break;
                        }
                    }

                    if (method == null)
                    {
                        result.Message += " | GetRemoteSearchResults method not found";
                        return result;
                    }

                    var genericMethod = method.MakeGenericMethod(movieType, movieInfoType);
                    var task = (Task)genericMethod.Invoke(providerManager, new object[] { queryObj, CancellationToken.None });
                    await task.ConfigureAwait(false);

                    var resultProp = task.GetType().GetProperty("Result");
                    var searchResults = resultProp.GetValue(task) as System.Collections.IEnumerable;
                    var count = 0;
                    MediaBrowser.Model.Providers.RemoteSearchResult firstResult = null;
                    foreach (var item in searchResults)
                    {
                        if (count == 0) firstResult = item as MediaBrowser.Model.Providers.RemoteSearchResult;
                        count++;
                    }

                    result.Message += " | Results: " + count;
                    if (firstResult != null)
                    {
                        result.Message += " | First: " + firstResult.Name;
                        result.Success = true;
                        if (firstResult.ProviderIds != null)
                        {
                            foreach (var kvp in firstResult.ProviderIds)
                                result.Message += " | " + kvp.Key + "=" + kvp.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Message = "Exception: [" + ex.GetType().FullName + "] " + ex.Message;
                if (ex.InnerException != null)
                {
                    result.Message += " | Inner: [" + ex.InnerException.GetType().FullName + "] " + ex.InnerException.Message;
                }
            }

            return result;
        }

        public void Post(RefreshCache request)
        {
            Plugin.Instance.LiveTvService.InvalidateCache();
            XtreamTunerHost.Instance?.ClearCaches();
        }

        public async Task<object> Post(TestXtreamConnection request)
        {
            var config = Plugin.Instance.Configuration;
            var result = new TestConnectionResult();

            if (string.IsNullOrEmpty(config.BaseUrl) ||
                string.IsNullOrEmpty(config.Username) ||
                string.IsNullOrEmpty(config.Password))
            {
                result.Success = false;
                result.Message = "Please configure server URL, username, and password first.";
                return result;
            }

            try
            {
                var url = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}",
                    config.BaseUrl, config.Username, config.Password);

                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(10);
                    var response = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                    result.Success = true;
                    result.Message = "Connection successful!";
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Connection failed: " + ex.Message;
            }

            return result;
        }

        public async Task<object> Post(TestDispatcharrConnection request)
        {
            var config = Plugin.Instance.Configuration;
            var result = new TestConnectionResult();

            if (string.IsNullOrEmpty(config.DispatcharrUrl))
            {
                result.Success = false;
                result.Message = "Please configure Dispatcharr URL first.";
                return result;
            }

            try
            {
                var logManager = Plugin.Instance.ApplicationHost.Resolve<ILogManager>();
                var client = new DispatcharrClient(logManager.GetLogger("XtreamTuner.DispatcharrTest"));
                client.Configure(config.DispatcharrUser ?? "", config.DispatcharrPass ?? "");

                var (success, message) = await client.TestConnectionDetailedAsync(
                    config.DispatcharrUrl, config.DispatcharrUser ?? "", config.DispatcharrPass ?? "",
                    CancellationToken.None).ConfigureAwait(false);

                result.Success = success;
                result.Message = message;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Unexpected error: " + ex.Message;
            }

            return result;
        }

        public object Get(GetSanitizedLogs request)
        {
            var config = Plugin.Instance.Configuration;
            var logDir = Plugin.Instance.ApplicationPaths.LogDirectoryPath;
            var lines = new List<string>();

            try
            {
                var logFiles = Directory.GetFiles(logDir, "*.txt")
                    .Concat(Directory.GetFiles(logDir, "*.log"))
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .Take(5)
                    .ToArray();

                var keywords = new[] { "XtreamTuner", "Xtream", "Dispatcharr", "LiveTv" };

                foreach (var logFile in logFiles)
                {
                    try
                    {
                        using (var reader = new StreamReader(logFile, Encoding.UTF8))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                foreach (var kw in keywords)
                                {
                                    if (line.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        lines.Add(line);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // Sanitize PII
            var sanitized = new StringBuilder();
            foreach (var line in lines)
            {
                var s = line;

                // Redact specific config values if non-empty
                if (!string.IsNullOrEmpty(config.Username))
                    s = s.Replace(config.Username, "<redacted>");
                if (!string.IsNullOrEmpty(config.Password))
                    s = s.Replace(config.Password, "<redacted>");
                if (!string.IsNullOrEmpty(config.DispatcharrUser))
                    s = s.Replace(config.DispatcharrUser, "<redacted>");
                if (!string.IsNullOrEmpty(config.DispatcharrPass))
                    s = s.Replace(config.DispatcharrPass, "<redacted>");

                // Redact IP addresses
                s = Regex.Replace(s, @"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}", "<ip-redacted>");

                // Redact Xtream credentials in URLs: /live/user/pass/
                s = Regex.Replace(s, @"/live/[^/]+/[^/]+/", "/live/<user>/<pass>/");

                // Redact email patterns
                s = Regex.Replace(s, @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", "<email-redacted>");

                // Redact hostnames in stream URLs (http(s)://host:port patterns, keep path)
                s = Regex.Replace(s, @"(https?://)([^/:]+)(:\d+)?(/player_api\.php|/live/|/movie/|/series/)",
                    "$1<provider-host>$3$4");

                sanitized.AppendLine(s);
            }

            if (sanitized.Length == 0)
                sanitized.AppendLine("No plugin-related log entries found.");

            var stream = new MemoryStream(Encoding.UTF8.GetBytes(sanitized.ToString()));
            var headers = new Dictionary<string, string>
            {
                { "Content-Disposition", "attachment; filename=\"xtream-tuner-log.txt\"" },
            };
            return ResultFactory.GetResult(Request, stream, "text/plain", headers);
        }
    }
}
