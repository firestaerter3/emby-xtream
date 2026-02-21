using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;

namespace Emby.Xtream.Plugin.Service
{
    public class TmdbLookupService
    {
        private static readonly string[] TmdbProviderKeys = { "Tmdb", "TheMovieDb", "tmdb" };
        private static readonly string[] TvdbProviderKeys = { "Tvdb", "TheTvDb", "tvdb" };
        private static readonly SemaphoreSlim RateLimiter = new SemaphoreSlim(2, 2);

        private readonly ILogger _logger;
        private IProviderManager _providerManager;
        private MethodInfo _searchMethod;
        private Type _movieInfoType;
        private bool _resolveAttempted;

        private MethodInfo _seriesSearchMethod;
        private Type _seriesInfoType;
        private bool _seriesResolveAttempted;

        public TmdbLookupService(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<string> LookupTmdbIdAsync(string movieName, int? year, CancellationToken cancellationToken)
        {
            if (!EnsureResolved())
            {
                return null;
            }

            await RateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Create MovieInfo instance via reflection
                var searchInfo = Activator.CreateInstance(_movieInfoType);
                _movieInfoType.GetProperty("Name").SetValue(searchInfo, movieName);
                if (year.HasValue)
                    _movieInfoType.GetProperty("Year").SetValue(searchInfo, year);

                // Create RemoteSearchQuery<MovieInfo> via reflection
                var queryType = typeof(RemoteSearchQuery<>).MakeGenericType(_movieInfoType);
                var queryObj = Activator.CreateInstance(queryType);
                queryType.GetProperty("SearchInfo").SetValue(queryObj, searchInfo);
                queryType.GetProperty("IncludeDisabledProviders").SetValue(queryObj, true);
                // Restrict to TMDb only — avoids fanning out to OMDB and other slow providers
                var movieProviderProp = queryType.GetProperty("SearchProviderName");
                if (movieProviderProp != null) movieProviderProp.SetValue(queryObj, "TheMovieDb");

                // Invoke GetRemoteSearchResults<Movie, MovieInfo>
                var task = (Task)_searchMethod.Invoke(_providerManager, new object[] { queryObj, cancellationToken });
                await task.ConfigureAwait(false);

                var resultProp = task.GetType().GetProperty("Result");
                var searchResults = resultProp.GetValue(task) as System.Collections.IEnumerable;
                if (searchResults == null)
                {
                    return null;
                }

                RemoteSearchResult first = null;
                foreach (var item in searchResults)
                {
                    first = item as RemoteSearchResult;
                    break;
                }

                if (first == null || first.ProviderIds == null)
                {
                    return null;
                }

                foreach (var key in TmdbProviderKeys)
                {
                    string tmdbId;
                    if (first.ProviderIds.TryGetValue(key, out tmdbId) && !string.IsNullOrWhiteSpace(tmdbId))
                    {
                        _logger.Debug("TMDB fallback: '{0}' ({1}) -> tmdbid={2}", movieName, year, tmdbId);
                        return tmdbId;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.Warn("TMDB fallback lookup failed for '{0}': [{1}] {2}", movieName, ex.GetType().Name, ex.Message);
                return null;
            }
            finally
            {
                RateLimiter.Release();
            }
        }

        public async Task<int?> LookupSeriesTvdbIdAsync(string seriesName, int? year, CancellationToken cancellationToken)
        {
            if (!EnsureSeriesResolved())
            {
                return null;
            }

            await RateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var searchInfo = Activator.CreateInstance(_seriesInfoType);
                _seriesInfoType.GetProperty("Name").SetValue(searchInfo, seriesName);
                if (year.HasValue)
                    _seriesInfoType.GetProperty("Year").SetValue(searchInfo, year);

                var queryType = typeof(RemoteSearchQuery<>).MakeGenericType(_seriesInfoType);
                var queryObj = Activator.CreateInstance(queryType);
                queryType.GetProperty("SearchInfo").SetValue(queryObj, searchInfo);
                queryType.GetProperty("IncludeDisabledProviders").SetValue(queryObj, true);
                // Restrict to TVDb only — avoids fanning out to other providers
                var seriesProviderProp = queryType.GetProperty("SearchProviderName");
                if (seriesProviderProp != null) seriesProviderProp.SetValue(queryObj, "TheTVDB");

                var task = (Task)_seriesSearchMethod.Invoke(_providerManager, new object[] { queryObj, cancellationToken });
                await task.ConfigureAwait(false);

                var resultProp = task.GetType().GetProperty("Result");
                var searchResults = resultProp.GetValue(task) as System.Collections.IEnumerable;
                if (searchResults == null)
                {
                    return null;
                }

                RemoteSearchResult first = null;
                foreach (var item in searchResults)
                {
                    first = item as RemoteSearchResult;
                    break;
                }

                if (first == null || first.ProviderIds == null)
                {
                    return null;
                }

                foreach (var key in TvdbProviderKeys)
                {
                    string tvdbId;
                    if (first.ProviderIds.TryGetValue(key, out tvdbId) && !string.IsNullOrWhiteSpace(tvdbId))
                    {
                        int id;
                        if (int.TryParse(tvdbId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id) && id > 0)
                        {
                            _logger.Debug("TVDb lookup: '{0}' ({1}) -> tvdbid={2}", seriesName, year, id);
                            return id;
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.Warn("TVDb lookup failed for '{0}': [{1}] {2}", seriesName, ex.GetType().Name, ex.Message);
                return null;
            }
            finally
            {
                RateLimiter.Release();
            }
        }

        private bool EnsureSeriesResolved()
        {
            if (_seriesSearchMethod != null)
            {
                return true;
            }

            if (_seriesResolveAttempted)
            {
                return false;
            }

            _seriesResolveAttempted = true;

            try
            {
                // Ensure base resolution happened (gets _providerManager)
                if (_providerManager == null)
                {
                    if (!EnsureResolved() && _providerManager == null)
                    {
                        return false;
                    }
                }

                // Find SeriesInfo type at runtime (extends ItemLookupInfo, not our Client.Models.SeriesInfo)
                var lookupInfoType = typeof(ItemLookupInfo);
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        foreach (var t in asm.GetTypes())
                        {
                            if (t.Name == "SeriesInfo" && lookupInfoType.IsAssignableFrom(t))
                            {
                                _seriesInfoType = t;
                                break;
                            }
                        }
                        if (_seriesInfoType != null) break;
                    }
                    catch { }
                }

                if (_seriesInfoType == null)
                {
                    _logger.Warn("TVDb lookup: SeriesInfo type not found at runtime");
                    return false;
                }

                var seriesType = typeof(MediaBrowser.Controller.Entities.TV.Series);
                foreach (var m in typeof(IProviderManager).GetMethods())
                {
                    if (m.Name == "GetRemoteSearchResults" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2)
                    {
                        _seriesSearchMethod = m.MakeGenericMethod(seriesType, _seriesInfoType);
                        break;
                    }
                }

                if (_seriesSearchMethod == null)
                {
                    _logger.Warn("TVDb lookup: GetRemoteSearchResults method not found for Series");
                    return false;
                }

                _logger.Info("TVDb lookup: resolved IProviderManager + SeriesInfo + GetRemoteSearchResults successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn("TVDb lookup: Failed to resolve: {0}", ex.Message);
                return false;
            }
        }

        private bool EnsureResolved()
        {
            if (_searchMethod != null)
            {
                return true;
            }

            if (_resolveAttempted)
            {
                return false;
            }

            _resolveAttempted = true;

            try
            {
                var host = Plugin.Instance?.ApplicationHost;
                if (host == null)
                {
                    _logger.Warn("TMDB fallback: ApplicationHost not available");
                    return false;
                }

                _providerManager = host.Resolve<IProviderManager>();
                if (_providerManager == null)
                {
                    _logger.Warn("TMDB fallback: IProviderManager could not be resolved");
                    return false;
                }

                // Find MovieInfo type at runtime (not in compile-time SDK 4.8.0.80)
                var lookupInfoType = typeof(ItemLookupInfo);
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        foreach (var t in asm.GetTypes())
                        {
                            if (t.Name == "MovieInfo" && lookupInfoType.IsAssignableFrom(t))
                            {
                                _movieInfoType = t;
                                break;
                            }
                        }
                        if (_movieInfoType != null) break;
                    }
                    catch { }
                }

                if (_movieInfoType == null)
                {
                    _logger.Warn("TMDB fallback: MovieInfo type not found at runtime");
                    return false;
                }

                // Find GetRemoteSearchResults with 2 generic type params (avoids AmbiguousMatchException)
                var movieType = typeof(MediaBrowser.Controller.Entities.Movies.Movie);
                foreach (var m in typeof(IProviderManager).GetMethods())
                {
                    if (m.Name == "GetRemoteSearchResults" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2)
                    {
                        _searchMethod = m.MakeGenericMethod(movieType, _movieInfoType);
                        break;
                    }
                }

                if (_searchMethod == null)
                {
                    _logger.Warn("TMDB fallback: GetRemoteSearchResults method not found");
                    return false;
                }

                _logger.Info("TMDB fallback: resolved IProviderManager + MovieInfo + GetRemoteSearchResults successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn("TMDB fallback: Failed to resolve: {0}", ex.Message);
                return false;
            }
        }
    }
}
