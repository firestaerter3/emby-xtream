using System;
using System.Collections.Generic;
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

        private string _username = string.Empty;
        private string _password = string.Empty;
        private string _accessToken;
        private string _refreshToken;
        private DateTime _tokenExpiry = DateTime.MinValue;

        public DispatcharrClient(ILogger logger)
        {
            _logger = logger;
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
        /// Fetches stream stats from Dispatcharr and maps them by Xtream channel ID.
        /// Each channel may have multiple underlying stream sources (failover). We use
        /// the first source with valid stats (primary/highest-priority stream), since
        /// that's what Dispatcharr will serve under normal conditions.
        /// </summary>
        public async Task<Dictionary<int, StreamStatsInfo>> GetStreamStatsAsync(
            string baseUrl, CancellationToken cancellationToken)
        {
            var result = new Dictionary<int, StreamStatsInfo>();

            try
            {
                // Fetch channels (gives us channel_id → [stream_source_ids])
                var channelJson = await GetAuthenticatedAsync(
                    baseUrl + "/api/channels/channels/?limit=2000", baseUrl, cancellationToken).ConfigureAwait(false);
                if (channelJson == null) return result;

                var channels = JsonSerializer.Deserialize<List<DispatcharrChannelInfo>>(channelJson, JsonOptions);
                if (channels == null) return result;

                // Build reverse map: stream_source_id → channel_id (Xtream stream_id)
                var streamToChannel = new Dictionary<int, int>();
                foreach (var ch in channels)
                {
                    foreach (var streamSourceId in ch.Streams)
                    {
                        streamToChannel[streamSourceId] = ch.Id;
                    }
                }

                // Fetch stream sources with stats
                var streamJson = await GetAuthenticatedAsync(
                    baseUrl + "/api/channels/streams/?page_size=2000", baseUrl, cancellationToken).ConfigureAwait(false);
                if (streamJson == null) return result;

                var page = JsonSerializer.Deserialize<PaginatedResponse<DispatcharrChannel>>(streamJson, JsonOptions);
                if (page?.Results == null) return result;

                // Use the first source with stats per channel (primary stream)
                foreach (var stream in page.Results)
                {
                    if (stream.StreamStats?.VideoCodec == null) continue;

                    if (streamToChannel.TryGetValue(stream.Id, out var channelId) &&
                        !result.ContainsKey(channelId))
                    {
                        result[channelId] = stream.StreamStats;
                    }
                }

                _logger.Info("Fetched stream stats for {0} channels from Dispatcharr ({1} sources, {2} channels)",
                    result.Count, page.Results.Count, channels.Count);
            }
            catch (Exception ex)
            {
                _logger.Warn("Failed to fetch stream stats from Dispatcharr: {0}", ex.Message);
            }

            return result;
        }

        /// <summary>
        /// Fetches channels from Dispatcharr's REST API and returns a mapping
        /// of channel ID (which matches Xtream emulation stream_id) to UUID.
        /// The UUID is needed to build proxy stream URLs: /proxy/ts/stream/{uuid}
        /// </summary>
        public async Task<Dictionary<int, string>> GetChannelUuidMapAsync(
            string baseUrl, CancellationToken cancellationToken)
        {
            var result = new Dictionary<int, string>();

            try
            {
                var json = await GetAuthenticatedAsync(
                    baseUrl + "/api/channels/channels/?limit=2000", baseUrl, cancellationToken).ConfigureAwait(false);
                if (json == null) return result;

                var channels = JsonSerializer.Deserialize<List<DispatcharrChannelInfo>>(json, JsonOptions);
                if (channels == null) return result;

                foreach (var ch in channels)
                {
                    if (!string.IsNullOrEmpty(ch.Uuid))
                    {
                        result[ch.Id] = ch.Uuid;
                    }
                }

                _logger.Info("Fetched UUID mapping for {0} channels from Dispatcharr", result.Count);
            }
            catch (Exception ex)
            {
                _logger.Warn("Failed to fetch channel UUIDs from Dispatcharr: {0}", ex.Message);
            }

            return result;
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
                using (var httpClient = new HttpClient())
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
                using (var httpClient = new HttpClient())
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
                using (var httpClient = new HttpClient())
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
    }
}
