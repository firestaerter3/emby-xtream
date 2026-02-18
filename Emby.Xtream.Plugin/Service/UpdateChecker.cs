using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    public class UpdateCheckResult
    {
        public bool UpdateAvailable { get; set; }
        public bool UpdateInstalled { get; set; }
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public string ReleaseUrl { get; set; }
        public string ReleaseNotes { get; set; }
        public string PublishedAt { get; set; }
        public string DownloadUrl { get; set; }
        public string Error { get; set; }
    }

    public static class UpdateChecker
    {
        private const string GitHubApiUrl = "https://api.github.com/repos/firestaerter3/emby-xtream/releases/latest";
        private const string DllAssetName = "Emby.Xtream.Plugin.dll";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

        private static UpdateCheckResult _cachedResult;
        private static DateTime _cacheTime = DateTime.MinValue;
        private static readonly object _cacheLock = new object();
        private static bool _updateInstalled;

        public static bool UpdateInstalled
        {
            get { return _updateInstalled; }
            set { _updateInstalled = value; }
        }

        public static void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cachedResult = null;
                _cacheTime = DateTime.MinValue;
            }
        }

        public static async Task<UpdateCheckResult> CheckForUpdateAsync()
        {
            lock (_cacheLock)
            {
                if (_cachedResult != null && (DateTime.UtcNow - _cacheTime) < CacheTtl)
                {
                    return _cachedResult;
                }
            }

            var currentVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0";

            UpdateCheckResult result;
            try
            {
                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Emby-Xtream-Plugin/1.0");
                    httpClient.Timeout = TimeSpan.FromSeconds(10);

                    var json = await httpClient.GetStringAsync(GitHubApiUrl).ConfigureAwait(false);

                    var tagName = ExtractJsonString(json, "tag_name");
                    var htmlUrl = ExtractJsonString(json, "html_url");
                    var body = ExtractJsonString(json, "body");
                    var publishedAt = ExtractJsonString(json, "published_at");

                    result = CompareVersions(currentVersion, tagName, htmlUrl, body, publishedAt);
                    result.DownloadUrl = ExtractDllDownloadUrl(json, DllAssetName);
                    result.UpdateInstalled = _updateInstalled;

                    // Suppress update banner if this version was already installed
                    if (result.UpdateAvailable && !_updateInstalled)
                    {
                        var config = Plugin.Instance?.Configuration;
                        if (config != null &&
                            !string.IsNullOrEmpty(config.LastInstalledVersion) &&
                            string.Equals(config.LastInstalledVersion, result.LatestVersion, StringComparison.OrdinalIgnoreCase))
                        {
                            result.UpdateAvailable = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result = new UpdateCheckResult
                {
                    CurrentVersion = currentVersion,
                    Error = "Failed to check for updates: " + ex.Message,
                };
            }

            lock (_cacheLock)
            {
                _cachedResult = result;
                _cacheTime = DateTime.UtcNow;
            }

            return result;
        }

        public static UpdateCheckResult CompareVersions(string currentVersion, string tagName, string releaseUrl, string body, string publishedAt)
        {
            var result = new UpdateCheckResult
            {
                CurrentVersion = currentVersion,
                ReleaseUrl = releaseUrl ?? "",
                ReleaseNotes = body ?? "",
                PublishedAt = publishedAt ?? "",
            };

            if (string.IsNullOrEmpty(tagName))
            {
                result.Error = "No tag found in release data.";
                return result;
            }

            var versionStr = tagName.TrimStart('v', 'V');
            result.LatestVersion = versionStr;

            Version current;
            Version latest;

            if (!Version.TryParse(NormalizeVersion(currentVersion), out current))
            {
                result.Error = "Could not parse current version: " + currentVersion;
                return result;
            }

            if (!Version.TryParse(NormalizeVersion(versionStr), out latest))
            {
                result.Error = "Could not parse release tag: " + tagName;
                return result;
            }

            result.UpdateAvailable = latest > current;
            return result;
        }

        private static string NormalizeVersion(string version)
        {
            // Version.Parse requires at least major.minor; pad if needed
            if (version == null) return "0.0";
            var parts = version.Split('.');
            if (parts.Length == 1) return version + ".0";
            return version;
        }

        private static string ExtractJsonString(string json, string key)
        {
            var search = "\"" + key + "\"";
            var idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += search.Length;
            return ExtractJsonStringAt(json, idx);
        }

        /// <summary>
        /// Extracts a JSON string value starting from the given position (after the key).
        /// Skips whitespace/colon, reads quoted string with escape handling.
        /// </summary>
        internal static string ExtractJsonStringAt(string json, int idx)
        {
            // Skip whitespace and colon
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':' || json[idx] == '\t' || json[idx] == '\n' || json[idx] == '\r'))
                idx++;

            if (idx >= json.Length) return null;

            if (json[idx] == 'n') return null; // null value

            if (json[idx] != '"') return null;
            idx++; // skip opening quote

            var sb = new System.Text.StringBuilder();
            while (idx < json.Length)
            {
                var c = json[idx];
                if (c == '\\' && idx + 1 < json.Length)
                {
                    var next = json[idx + 1];
                    if (next == '"') { sb.Append('"'); idx += 2; continue; }
                    if (next == '\\') { sb.Append('\\'); idx += 2; continue; }
                    if (next == 'n') { sb.Append('\n'); idx += 2; continue; }
                    if (next == 'r') { sb.Append('\r'); idx += 2; continue; }
                    if (next == 't') { sb.Append('\t'); idx += 2; continue; }
                    sb.Append(c);
                    idx++;
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
                idx++;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Scans the GitHub release JSON assets array for an asset matching the given name
        /// and returns its browser_download_url. Case-insensitive matching.
        /// </summary>
        public static string ExtractDllDownloadUrl(string json, string assetName)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(assetName))
                return null;

            // Find the "assets" array
            var assetsKey = "\"assets\"";
            var assetsIdx = json.IndexOf(assetsKey, StringComparison.Ordinal);
            if (assetsIdx < 0) return null;

            // Find the opening bracket of the assets array
            var bracketIdx = json.IndexOf('[', assetsIdx + assetsKey.Length);
            if (bracketIdx < 0) return null;

            // Find the closing bracket of the assets array
            var closingBracket = FindMatchingBracket(json, bracketIdx);
            if (closingBracket < 0) return null;

            var assetsSection = json.Substring(bracketIdx, closingBracket - bracketIdx + 1);

            // Scan through each "name" in the assets section
            var searchFrom = 0;
            while (searchFrom < assetsSection.Length)
            {
                var nameKey = "\"name\"";
                var nameIdx = assetsSection.IndexOf(nameKey, searchFrom, StringComparison.Ordinal);
                if (nameIdx < 0) break;

                var valueStart = nameIdx + nameKey.Length;
                var name = ExtractJsonStringAt(assetsSection, valueStart);

                if (name != null && string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
                {
                    // Found the matching asset — now find browser_download_url near this position
                    // Look backwards for the start of this object (the '{' before this "name")
                    var objStart = assetsSection.LastIndexOf('{', nameIdx);
                    // Look forwards for the end of this object
                    var objEnd = FindMatchingBrace(assetsSection, objStart);
                    if (objStart >= 0 && objEnd > objStart)
                    {
                        var assetObj = assetsSection.Substring(objStart, objEnd - objStart + 1);
                        var urlKey = "\"browser_download_url\"";
                        var urlIdx = assetObj.IndexOf(urlKey, StringComparison.Ordinal);
                        if (urlIdx >= 0)
                        {
                            return ExtractJsonStringAt(assetObj, urlIdx + urlKey.Length);
                        }
                    }
                }

                searchFrom = valueStart + 1;
            }

            return null;
        }

        private static int FindMatchingBracket(string json, int openIdx)
        {
            var depth = 0;
            for (var i = openIdx; i < json.Length; i++)
            {
                if (json[i] == '[') depth++;
                else if (json[i] == ']') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static int FindMatchingBrace(string json, int openIdx)
        {
            if (openIdx < 0) return -1;
            var depth = 0;
            for (var i = openIdx; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }
    }
}
