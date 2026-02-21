using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client.Models;
using MediaBrowser.Model.Logging;

namespace Emby.Xtream.Plugin.Client
{
    public class DispatcharrClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
        };

        private readonly ILogger _logger;
        private readonly HttpMessageHandler _handler;

        private string _username = string.Empty;
        private string _password = string.Empty;
        private string _accessToken;
        private string _refreshToken;
        private DateTime _tokenExpiry = DateTime.MinValue;

        public DispatcharrClient(ILogger logger, HttpMessageHandler handler = null)
        {
            _logger = logger;
            _handler = handler;
        }

        public void Configure(string username, string password)
        {
            if (!string.Equals(_username, username, StringComparison.Ordinal) ||
                !string.Equals(_password, password, StringComparison.Ordinal))
            {
                _username = username;
                _password = password;
                _accessToken = null;
                _refreshToken = null;
                _tokenExpiry = DateTime.MinValue;
            }
        }

        /// <summary>
        /// Fetches channels with embedded stream sources in a single API call and returns both
        /// the UUID map and stream stats map, keyed by the Xtream provider's stream_id.
        /// Requires Dispatcharr v0.19.0+ (stream_id field in stream objects).
        /// </summary>
        public async Task<(Dictionary<int, string> UuidMap, Dictionary<int, StreamStatsInfo> StatsMap)>
            GetChannelDataAsync(string baseUrl, CancellationToken cancellationToken)
        {
            var uuidMap = new Dictionary<int, string>();
            var statsMap = new Dictionary<int, StreamStatsInfo>();

            var json = await GetAuthenticatedAsync(
                baseUrl + "/api/channels/channels/?include_streams=true&limit=2000",
                baseUrl, cancellationToken).ConfigureAwait(false);
            if (json == null) return (uuidMap, statsMap);

            var channels = JsonSerializer.Deserialize<List<DispatcharrChannelWithStreams>>(json, JsonOptions);
            if (channels == null) return (uuidMap, statsMap);

            foreach (var ch in channels)
            {
                if (string.IsNullOrEmpty(ch.Uuid) || ch.Id == 0) continue;

                // Key by ch.Id — Dispatcharr's Xtream emulation always uses ch.Id as the
                // stream_id it presents to Emby.  The stream_id field inside embedded stream
                // sources reflects the upstream provider's own ID (or, for URL-based sources,
                // the source's internal Dispatcharr ID), neither of which reliably matches
                // what Emby stores as the channel's stream_id.
                uuidMap[ch.Id] = ch.Uuid;

                // Take stream stats from the first source that carries them.
                foreach (var stream in ch.Streams)
                {
                    if (stream.StreamStats?.VideoCodec != null && !statsMap.ContainsKey(ch.Id))
                        statsMap[ch.Id] = stream.StreamStats;
                }
            }

            if (uuidMap.Count == 0)
            {
                _logger.Warn(
                    "Dispatcharr UUID map is empty — channels may have no stream sources or no UUIDs assigned");
            }
            else
            {
                _logger.Info("Loaded {0} UUIDs and {1} stream stats from Dispatcharr", uuidMap.Count, statsMap.Count);
            }

            return (uuidMap, statsMap);
        }

        /// <summary>Returns the Dispatcharr VOD movie detail (UUID) for a given Xtream stream ID.</summary>
        public async Task<DispatcharrVodMovieDetail> GetVodMovieDetailAsync(
            string baseUrl, int xtreamStreamId, CancellationToken cancellationToken)
        {
            var json = await GetAuthenticatedAsync(
                string.Format(CultureInfo.InvariantCulture, "{0}/api/vod/movies/{1}/", baseUrl, xtreamStreamId),
                baseUrl, cancellationToken).ConfigureAwait(false);
            if (json == null) return null;
            return JsonSerializer.Deserialize<DispatcharrVodMovieDetail>(json, JsonOptions);
        }

        /// <summary>Returns provider streams for a given Dispatcharr VOD movie.</summary>
        public async Task<List<DispatcharrVodProvider>> GetVodMovieProvidersAsync(
            string baseUrl, int xtreamStreamId, CancellationToken cancellationToken)
        {
            var json = await GetAuthenticatedAsync(
                string.Format(CultureInfo.InvariantCulture, "{0}/api/vod/movies/{1}/providers/", baseUrl, xtreamStreamId),
                baseUrl, cancellationToken).ConfigureAwait(false);
            if (json == null) return new List<DispatcharrVodProvider>();
            return JsonSerializer.Deserialize<List<DispatcharrVodProvider>>(json, JsonOptions)
                   ?? new List<DispatcharrVodProvider>();
        }

        public async Task<bool> TestConnectionAsync(string baseUrl, CancellationToken cancellationToken)
        {
            try
            {
                var token = await LoginAsync(baseUrl, cancellationToken).ConfigureAwait(false);
                return token != null;
            }
            catch (Exception ex)
            {
                _logger.Warn("Dispatcharr connection test failed: {0}", ex.Message);
                return false;
            }
        }

        public async Task<(bool Success, string Message)> TestConnectionDetailedAsync(
            string baseUrl, string username, string password, CancellationToken cancellationToken)
        {
            var steps = new List<string>();

            // Step 1: Validate URL
            if (string.IsNullOrWhiteSpace(baseUrl))
                return (false, "Dispatcharr URL is empty.");

            Uri uri;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
                return (false, "Invalid URL format. Use http:// or https://.");

            steps.Add("URL format OK");

            // Step 2: Login to get JWT token
            try
            {
                using (var httpClient = CreateHttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(10);
                    var payload = JsonSerializer.Serialize(new { username = username, password = password });
                    using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                    using (var response = await httpClient.PostAsync(
                        baseUrl + "/api/accounts/token/", content, cancellationToken).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            var snippet = body.Length > 200 ? body.Substring(0, 200) : body;
                            return (false, string.Format("Login failed (HTTP {0}). Response: {1}",
                                (int)response.StatusCode, snippet));
                        }

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var tokenResponse = JsonSerializer.Deserialize<LoginResponse>(json, JsonOptions);

                        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.Access))
                            return (false, "Login succeeded but no access token in response.");

                        steps.Add("JWT login OK");

                        // Step 3: Test API access with a channels query
                        try
                        {
                            using (var request = new HttpRequestMessage(HttpMethod.Get,
                                baseUrl + "/api/channels/channels/?limit=1"))
                            {
                                request.Headers.Authorization =
                                    new AuthenticationHeaderValue("Bearer", tokenResponse.Access);

                                using (var apiResponse = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                                {
                                    if (apiResponse.IsSuccessStatusCode)
                                    {
                                        steps.Add("API access OK");
                                    }
                                    else
                                    {
                                        steps.Add(string.Format("API returned HTTP {0} (channels query)", (int)apiResponse.StatusCode));
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            steps.Add("API probe failed: " + ex.Message);
                        }
                    }
                }
            }
            catch (TaskCanceledException)
            {
                return (false, "Connection timed out after 10 seconds.");
            }
            catch (HttpRequestException ex)
            {
                return (false, "Connection failed: " + ex.Message);
            }
            catch (Exception ex)
            {
                return (false, "Unexpected error: " + ex.Message);
            }

            return (true, string.Join(" > ", steps));
        }

        private async Task<string> GetAuthenticatedAsync(
            string url, string baseUrl, CancellationToken cancellationToken)
        {
            await EnsureTokenAsync(baseUrl, cancellationToken).ConfigureAwait(false);

            if (_accessToken == null) return null;

            int retryCount = 0;
            int maxRetries = 3;
            int currentDelay = 1000;

            while (true)
            {
                using (var httpClient = CreateHttpClient())
                {
                    try
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                        {
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

                            using (var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                            {
                                if (response.StatusCode == HttpStatusCode.Unauthorized)
                                {
                                    _logger.Debug("Dispatcharr token expired, refreshing");
                                    var refreshed = await RefreshTokenAsync(baseUrl, cancellationToken).ConfigureAwait(false);
                                    if (!refreshed)
                                    {
                                        await LoginAsync(baseUrl, cancellationToken).ConfigureAwait(false);
                                        if (_accessToken == null) return null;
                                    }

                                    using (var retryRequest = new HttpRequestMessage(HttpMethod.Get, url))
                                    {
                                        retryRequest.Headers.Authorization =
                                            new AuthenticationHeaderValue("Bearer", _accessToken);
                                        using (var retryResponse = await httpClient.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false))
                                        {
                                            retryResponse.EnsureSuccessStatusCode();
                                            return await retryResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                                        }
                                    }
                                }

                                response.EnsureSuccessStatusCode();
                                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            }
                        }
                    }
                    catch (HttpRequestException ex) when (retryCount < maxRetries)
                    {
                        _logger.Warn("HTTP error for {0}, retry {1}/{2} after {3}ms: {4}",
                            url, retryCount + 1, maxRetries, currentDelay, ex.Message);
                        await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false);
                        retryCount++;
                        currentDelay *= 2;
                    }
                }
            }
        }

        private async Task EnsureTokenAsync(string baseUrl, CancellationToken cancellationToken)
        {
            if (_accessToken != null && DateTime.UtcNow < _tokenExpiry) return;

            if (_refreshToken != null)
            {
                var refreshed = await RefreshTokenAsync(baseUrl, cancellationToken).ConfigureAwait(false);
                if (refreshed) return;
            }

            await LoginAsync(baseUrl, cancellationToken).ConfigureAwait(false);
        }

        private async Task<LoginResponse> LoginAsync(string baseUrl, CancellationToken cancellationToken)
        {
            try
            {
                using (var httpClient = CreateHttpClient())
                {
                    var payload = JsonSerializer.Serialize(new { username = _username, password = _password });
                    using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                    using (var response = await httpClient.PostAsync(
                        baseUrl + "/api/accounts/token/", content, cancellationToken).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.Warn("Dispatcharr JWT login failed with status {0}", response.StatusCode);
                            _accessToken = null;
                            _refreshToken = null;
                            return null;
                        }

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var tokenResponse = JsonSerializer.Deserialize<LoginResponse>(json, JsonOptions);

                        if (tokenResponse != null && !string.IsNullOrEmpty(tokenResponse.Access))
                        {
                            _accessToken = tokenResponse.Access;
                            _refreshToken = tokenResponse.Refresh;
                            _tokenExpiry = DateTime.UtcNow.AddMinutes(25);
                            _logger.Debug("Dispatcharr JWT login successful");
                        }

                        return tokenResponse;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("Dispatcharr JWT login failed: {0}", ex.Message);
                _accessToken = null;
                _refreshToken = null;
                return null;
            }
        }

        private async Task<bool> RefreshTokenAsync(string baseUrl, CancellationToken cancellationToken)
        {
            if (_refreshToken == null) return false;

            try
            {
                using (var httpClient = CreateHttpClient())
                {
                    var payload = JsonSerializer.Serialize(new { refresh = _refreshToken });
                    using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                    using (var response = await httpClient.PostAsync(
                        baseUrl + "/api/accounts/token/refresh/", content, cancellationToken).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.Debug("Dispatcharr token refresh failed with status {0}", response.StatusCode);
                            _refreshToken = null;
                            return false;
                        }

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var tokenResponse = JsonSerializer.Deserialize<LoginResponse>(json, JsonOptions);

                        if (tokenResponse != null && !string.IsNullOrEmpty(tokenResponse.Access))
                        {
                            _accessToken = tokenResponse.Access;
                            _tokenExpiry = DateTime.UtcNow.AddMinutes(4);
                            _logger.Debug("Dispatcharr token refreshed successfully");
                            return true;
                        }

                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Dispatcharr token refresh failed: {0}", ex.Message);
                _refreshToken = null;
                return false;
            }
        }

        private HttpClient CreateHttpClient()
        {
            return _handler != null ? new HttpClient(_handler, disposeHandler: false) : new HttpClient();
        }
    }
}
