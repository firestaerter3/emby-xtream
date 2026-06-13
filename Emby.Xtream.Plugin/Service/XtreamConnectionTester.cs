using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Runs an Xtream <c>player_api.php</c> connection test and turns the response into a
    /// user-facing success/failure message.
    ///
    /// Extracted from <c>XtreamTunerApi.Post(TestXtreamConnection)</c> so the request and
    /// response parsing can be unit-tested with a mock <see cref="HttpClient"/>, mirroring the
    /// testability of <c>DispatcharrClient.TestConnectionDetailedAsync</c>.
    ///
    /// This is a <c>static</c> class on purpose: it has no constructor, so Emby's service
    /// scanner cannot auto-instantiate it via SimpleInjector (see CLAUDE.md).
    /// </summary>
    public static class XtreamConnectionTester
    {
        /// <summary>
        /// Tests the supplied Xtream credentials against the server.
        /// </summary>
        /// <param name="baseUrl">Xtream server base URL (with or without a trailing slash).</param>
        /// <param name="username">Xtream account username.</param>
        /// <param name="password">Xtream account password.</param>
        /// <param name="httpClient">HTTP client used for the request (injected for testing).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A tuple of success flag and a human-readable message.</returns>
        public static async Task<(bool Success, string Message)> RunAsync(
            string baseUrl, string username, string password,
            HttpClient httpClient, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(baseUrl) ||
                string.IsNullOrEmpty(username) ||
                string.IsNullOrEmpty(password))
            {
                return (false, "Please enter server URL, username, and password.");
            }

            try
            {
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}",
                    baseUrl.TrimEnd('/'), Uri.EscapeDataString(username), Uri.EscapeDataString(password));

                var response = await httpClient.GetStringAsync(url).ConfigureAwait(false);

                try
                {
                    using (var doc = JsonDocument.Parse(response))
                    {
                        if (!doc.RootElement.TryGetProperty("user_info", out var userInfo))
                        {
                            return (false, "Server responded but returned an unexpected format. Verify the server URL.");
                        }

                        var auth = 0;
                        if (userInfo.TryGetProperty("auth", out var authEl))
                        {
                            if (authEl.ValueKind == JsonValueKind.Number)
                                auth = authEl.GetInt32();
                            else if (authEl.ValueKind == JsonValueKind.String
                                     && int.TryParse(authEl.GetString(), out var n))
                                auth = n;
                        }

                        string status = null;
                        if (userInfo.TryGetProperty("status", out var statusEl))
                            status = statusEl.GetString();

                        if (auth != 1)
                        {
                            return (false, string.Format(
                                CultureInfo.InvariantCulture,
                                "Authentication failed: account status is '{0}'.",
                                status ?? "unknown"));
                        }

                        var msg = string.Format(
                            CultureInfo.InvariantCulture,
                            "Connected as {0} — status: {1}",
                            username, status ?? "unknown");

                        if (userInfo.TryGetProperty("active_cons", out var activeEl))
                        {
                            msg += ", " + activeEl.ToString();
                            if (userInfo.TryGetProperty("max_connections", out var maxEl))
                                msg += "/" + maxEl.ToString();
                            msg += " active streams";
                        }

                        return (true, msg);
                    }
                }
                catch (JsonException)
                {
                    return (false, "Server did not return a valid Xtream API response. Verify the server URL.");
                }
            }
            catch (Exception ex)
            {
                return (false, "Connection failed: " + ex.Message);
            }
        }
    }
}
