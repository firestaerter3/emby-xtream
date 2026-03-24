using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Emby.Xtream.Plugin.Client.Models;
using MediaBrowser.Model.Logging;
using Microsoft.Data.Sqlite;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Reads the Channel Identifiarr SQLite database and matches channel names
    /// to Gracenote station IDs. Ports the matching algorithm from the Python
    /// Channel Identifiarr project to C#.
    /// </summary>
    public class GracenoteDbService
    {
        private readonly ILogger _logger;
        private string _dbPath;
        private List<GracenoteLineupStation> _stations;
        private string _loadedLineupId;

        public GracenoteDbService(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Returns all lineups available in the database.
        /// </summary>
        public List<GracenoteLineup> GetLineups(string dbPath)
        {
            var lineups = new List<GracenoteLineup>();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
                return lineups;

            using (var conn = OpenDb(dbPath))
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT lineup_id, name, location, type FROM lineups ORDER BY name";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            lineups.Add(new GracenoteLineup
                            {
                                LineupId = reader.GetString(0),
                                Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                                Location = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                                Type = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                            });
                        }
                    }
                }
            }

            return lineups;
        }

        /// <summary>
        /// Searches stations in the database by query string.
        /// </summary>
        public List<GracenoteStation> SearchStations(string dbPath, string query, int limit = 50)
        {
            var results = new List<GracenoteStation>();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath) || string.IsNullOrEmpty(query))
                return results;

            using (var conn = OpenDb(dbPath))
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT station_id, name, call_sign, language, logo_uri
                        FROM stations
                        WHERE name LIKE @q OR call_sign LIKE @q OR station_id LIKE @q
                        ORDER BY name
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@q", "%" + query + "%");
                    cmd.Parameters.AddWithValue("@limit", limit);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            results.Add(new GracenoteStation
                            {
                                StationId = reader.GetString(0),
                                Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                                CallSign = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                                Language = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                                LogoUri = reader.IsDBNull(4) ? null : reader.GetString(4),
                            });
                        }
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Matches a list of channels against the Gracenote database and returns
        /// a mapping of stream_id to station_id for channels that match above the threshold.
        /// </summary>
        /// <param name="dbPath">Path to the SQLite database file.</param>
        /// <param name="lineupId">Optional lineup ID to scope matching (null = all stations).</param>
        /// <param name="channels">List of (streamId, channelName) pairs to match.</param>
        /// <param name="threshold">Minimum match score (0.0 - 1.0) to accept.</param>
        /// <param name="overrides">Manual overrides: stream_id → station_id.</param>
        /// <returns>Dictionary of stream_id → GracenoteMatchResult.</returns>
        public Dictionary<int, GracenoteMatchResult> MatchChannels(
            string dbPath, string lineupId,
            List<(int StreamId, string Name)> channels,
            double threshold,
            Dictionary<int, string> overrides)
        {
            var result = new Dictionary<int, GracenoteMatchResult>();

            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            {
                _logger.Warn("GracenoteDbService: DB path not configured or file missing: {0}", dbPath ?? "(null)");
                return result;
            }

            // Apply manual overrides first
            var overrideStations = new Dictionary<int, GracenoteStation>();
            if (overrides != null && overrides.Count > 0)
            {
                var stationIds = overrides.Values.Distinct().ToList();
                overrideStations = LoadStationsById(dbPath, stationIds);
            }

            foreach (var kvp in overrides ?? new Dictionary<int, string>())
            {
                GracenoteStation station;
                if (overrideStations.TryGetValue(kvp.Key, out station) ||
                    (int.TryParse(kvp.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _) && false))
                {
                    // We found station info for this override
                }
                else
                {
                    station = null;
                }

                result[kvp.Key] = new GracenoteMatchResult
                {
                    StationId = kvp.Value,
                    StationName = station?.Name ?? "Manual override",
                    CallSign = station?.CallSign,
                    LogoUri = station?.LogoUri,
                    Score = 1.0,
                    IsOverride = true,
                };
            }

            // Load stations from DB (cached if same path and lineup)
            EnsureStationsLoaded(dbPath, lineupId);
            if (_stations == null || _stations.Count == 0)
            {
                _logger.Warn("GracenoteDbService: no stations loaded from DB");
                return result;
            }

            _logger.Info("GracenoteDbService: matching {0} channels against {1} stations (lineup={2}, threshold={3:F2})",
                channels.Count, _stations.Count, lineupId ?? "all", threshold);

            var usedStationIds = new HashSet<string>(StringComparer.Ordinal);

            // Reserve station IDs used by overrides
            foreach (var m in result.Values)
                usedStationIds.Add(m.StationId);

            foreach (var (streamId, name) in channels)
            {
                if (result.ContainsKey(streamId))
                    continue; // Already has an override

                var parsed = ParseChannelName(name);
                var best = FindBestMatch(parsed, usedStationIds);

                if (best != null && best.Score >= threshold)
                {
                    usedStationIds.Add(best.StationId);
                    result[streamId] = best;
                    _logger.Debug("GracenoteDbService: matched '{0}' → station {1} ({2}) score={3:F3}",
                        name, best.StationId, best.StationName, best.Score);
                }
            }

            _logger.Info("GracenoteDbService: matched {0} of {1} channels", result.Count, channels.Count);
            return result;
        }

        /// <summary>
        /// Gets all current matches for display in the UI. Runs matching for all provided channels.
        /// </summary>
        public List<ChannelMatchInfo> GetAllMatches(
            string dbPath, string lineupId,
            List<(int StreamId, string Name)> channels,
            double threshold,
            Dictionary<int, string> overrides)
        {
            EnsureStationsLoaded(dbPath, lineupId);

            var matches = MatchChannels(dbPath, lineupId, channels, 0.0, overrides); // threshold=0 to get all suggestions
            var results = new List<ChannelMatchInfo>();

            foreach (var (streamId, name) in channels)
            {
                GracenoteMatchResult match;
                matches.TryGetValue(streamId, out match);

                results.Add(new ChannelMatchInfo
                {
                    StreamId = streamId,
                    ChannelName = name,
                    StationId = match?.StationId,
                    StationName = match?.StationName,
                    CallSign = match?.CallSign,
                    Score = match?.Score ?? 0.0,
                    IsOverride = match?.IsOverride ?? false,
                    IsAboveThreshold = match != null && match.Score >= threshold,
                });
            }

            return results.OrderByDescending(m => m.Score).ToList();
        }

        private void EnsureStationsLoaded(string dbPath, string lineupId)
        {
            if (_stations != null
                && string.Equals(_dbPath, dbPath, StringComparison.Ordinal)
                && string.Equals(_loadedLineupId, lineupId, StringComparison.Ordinal))
                return;

            _stations = LoadStations(dbPath, lineupId);
            _dbPath = dbPath;
            _loadedLineupId = lineupId;
        }

        private List<GracenoteLineupStation> LoadStations(string dbPath, string lineupId)
        {
            var stations = new List<GracenoteLineupStation>();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
                return stations;

            using (var conn = OpenDb(dbPath))
            {
                using (var cmd = conn.CreateCommand())
                {
                    if (!string.IsNullOrEmpty(lineupId))
                    {
                        cmd.CommandText = @"
                            SELECT s.station_id, s.name, s.call_sign, s.language, s.logo_uri,
                                   sl.channel_number, sl.affiliate_call_sign, sl.video_type
                            FROM stations s
                            JOIN station_lineups sl ON s.station_id = sl.station_id
                            WHERE sl.lineup_id = @lineupId
                            ORDER BY s.name";
                        cmd.Parameters.AddWithValue("@lineupId", lineupId);
                    }
                    else
                    {
                        cmd.CommandText = @"
                            SELECT DISTINCT s.station_id, s.name, s.call_sign, s.language, s.logo_uri,
                                   NULL, NULL, NULL
                            FROM stations s
                            ORDER BY s.name";
                    }

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            stations.Add(new GracenoteLineupStation
                            {
                                StationId = reader.GetString(0),
                                Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                                CallSign = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                                Language = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                                LogoUri = reader.IsDBNull(4) ? null : reader.GetString(4),
                                ChannelNumber = reader.IsDBNull(5) ? null : reader.GetValue(5)?.ToString(),
                                AffiliateCallSign = reader.IsDBNull(6) ? null : reader.GetString(6),
                                VideoType = reader.IsDBNull(7) ? null : reader.GetString(7),
                            });
                        }
                    }
                }
            }

            _logger.Info("GracenoteDbService: loaded {0} stations from DB (lineup={1})", stations.Count, lineupId ?? "all");
            return stations;
        }

        private Dictionary<int, GracenoteStation> LoadStationsById(string dbPath, List<string> stationIds)
        {
            var result = new Dictionary<int, GracenoteStation>();
            if (stationIds == null || stationIds.Count == 0)
                return result;

            using (var conn = OpenDb(dbPath))
            {
                // SQLite doesn't support parameterized IN clauses easily, so use a temp approach
                foreach (var sid in stationIds)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT station_id, name, call_sign, language, logo_uri FROM stations WHERE station_id = @sid";
                        cmd.Parameters.AddWithValue("@sid", sid);
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                var station = new GracenoteStation
                                {
                                    StationId = reader.GetString(0),
                                    Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                                    CallSign = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                                    Language = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                                    LogoUri = reader.IsDBNull(4) ? null : reader.GetString(4),
                                };
                                // Map back: we need stream_id, but this is keyed by station_id
                                // The caller will handle the mapping
                            }
                        }
                    }
                }
            }

            return result;
        }

        // ──────────────────────────────────────────────────────────────
        //  Channel Name Parsing (ported from Python parse_channel_name)
        // ──────────────────────────────────────────────────────────────

        private static readonly Regex ResolutionRegex = new Regex(
            @"\b(4K|UHD|2160[pP]|1080[pPiI]|HDTV|HD|720[pP]|SDTV|SD)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CountryRegex = new Regex(
            @"\b(US|USA|UK|CA|AU|NZ|DE|FR|ES|IT|NL|BE|AT|CH|SE|NO|DK|FI|PT|BR|MX|AR|CL|CO|JP|KR|IN|PH|ZA|IE)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex GenericTermsRegex = new Regex(
            @"\b(East|West|Pacific|Mountain|Central|PPV|LIVE|Linear)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ExtraWhitespace = new Regex(@"\s{2,}", RegexOptions.Compiled);
        private static readonly Regex TrailingPunctuation = new Regex(@"[\s\-_:]+$", RegexOptions.Compiled);
        private static readonly Regex LeadingPunctuation = new Regex(@"^[\s\-_:]+", RegexOptions.Compiled);

        internal static ParsedChannelName ParseChannelName(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return new ParsedChannelName { CleanName = string.Empty };

            var result = new ParsedChannelName { OriginalName = raw };
            var name = raw;

            // Extract resolution
            var resMatch = ResolutionRegex.Match(name);
            if (resMatch.Success)
            {
                result.Resolution = NormalizeResolution(resMatch.Value);
                name = ResolutionRegex.Replace(name, " ");
            }

            // Extract country
            var countryMatch = CountryRegex.Match(name);
            if (countryMatch.Success)
            {
                result.Country = countryMatch.Value.ToUpperInvariant();
                // Don't remove country from name — it may be part of the channel identity
            }

            // Strip generic terms
            name = GenericTermsRegex.Replace(name, " ");

            // Clean up whitespace and punctuation
            name = ExtraWhitespace.Replace(name, " ");
            name = TrailingPunctuation.Replace(name, string.Empty);
            name = LeadingPunctuation.Replace(name, string.Empty);
            name = name.Trim();

            result.CleanName = name;
            return result;
        }

        private static string NormalizeResolution(string res)
        {
            if (string.IsNullOrEmpty(res)) return null;
            var upper = res.ToUpperInvariant();
            if (upper == "4K" || upper == "UHD" || upper.StartsWith("2160")) return "4K";
            if (upper == "HD" || upper == "HDTV" || upper.StartsWith("1080") || upper.StartsWith("720")) return "HD";
            if (upper == "SD" || upper == "SDTV") return "SD";
            return null;
        }

        // ──────────────────────────────────────────────────────────────
        //  Match Scoring (ported from Python calculate_match_score)
        // ──────────────────────────────────────────────────────────────

        private GracenoteMatchResult FindBestMatch(ParsedChannelName parsed, HashSet<string> usedStationIds)
        {
            GracenoteMatchResult best = null;
            double bestScore = 0.0;

            foreach (var station in _stations)
            {
                if (usedStationIds.Contains(station.StationId))
                    continue;

                var score = CalculateMatchScore(parsed, station);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new GracenoteMatchResult
                    {
                        StationId = station.StationId,
                        StationName = station.Name,
                        CallSign = station.CallSign,
                        LogoUri = station.LogoUri,
                        Score = score,
                        IsOverride = false,
                    };
                }
            }

            return best;
        }

        internal static double CalculateMatchScore(ParsedChannelName parsed, GracenoteLineupStation station)
        {
            var channelName = parsed.CleanName ?? string.Empty;
            var stationName = station.Name ?? string.Empty;
            var callSign = station.CallSign ?? string.Empty;

            // Base similarity: best of name-vs-name and name-vs-callsign
            double nameSimilarity = SequenceMatcherRatio(channelName, stationName);
            double callSignSimilarity = SequenceMatcherRatio(channelName, callSign);
            double score = Math.Max(nameSimilarity, callSignSimilarity);

            // Exact match bonuses
            if (string.Equals(channelName, stationName, StringComparison.OrdinalIgnoreCase))
                score += 1.0;

            if (!string.IsNullOrEmpty(callSign) &&
                string.Equals(channelName, callSign, StringComparison.OrdinalIgnoreCase))
                score += 1.0;

            // Resolution alignment bonus/penalty
            if (!string.IsNullOrEmpty(parsed.Resolution) && !string.IsNullOrEmpty(station.VideoType))
            {
                var stationRes = NormalizeResolution(station.VideoType);
                if (string.Equals(parsed.Resolution, stationRes, StringComparison.OrdinalIgnoreCase))
                    score += 0.15;
                else
                    score -= 0.2;
            }

            // Logo presence bonus
            if (!string.IsNullOrEmpty(station.LogoUri))
                score += 0.05;

            // Normalize to 0.0 - 1.0 range (max theoretical score is ~2.2)
            return Math.Min(1.0, Math.Max(0.0, score / 2.2));
        }

        // ──────────────────────────────────────────────────────────────
        //  SequenceMatcher ratio — simplified port of Python's difflib
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Calculates similarity ratio between two strings (0.0 - 1.0),
        /// similar to Python's SequenceMatcher.ratio().
        /// Uses longest common subsequence approach.
        /// </summary>
        internal static double SequenceMatcherRatio(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return 0.0;

            var la = a.ToLowerInvariant();
            var lb = b.ToLowerInvariant();

            if (la == lb) return 1.0;

            int matches = LongestCommonSubsequenceLength(la, lb);
            return 2.0 * matches / (la.Length + lb.Length);
        }

        private static int LongestCommonSubsequenceLength(string a, string b)
        {
            int m = a.Length, n = b.Length;
            // Use two rows to save memory
            var prev = new int[n + 1];
            var curr = new int[n + 1];

            for (int i = 1; i <= m; i++)
            {
                for (int j = 1; j <= n; j++)
                {
                    if (a[i - 1] == b[j - 1])
                        curr[j] = prev[j - 1] + 1;
                    else
                        curr[j] = Math.Max(prev[j], curr[j - 1]);
                }
                var tmp = prev;
                prev = curr;
                curr = tmp;
                Array.Clear(curr, 0, curr.Length);
            }

            return prev[n];
        }

        /// <summary>
        /// Invalidates cached station data so the next MatchChannels call reloads from disk.
        /// </summary>
        public void ClearCache()
        {
            _stations = null;
            _dbPath = null;
            _loadedLineupId = null;
        }

        private static SqliteConnection OpenDb(string dbPath)
        {
            var conn = new SqliteConnection("Data Source=" + dbPath + ";Mode=ReadOnly");
            conn.Open();
            return conn;
        }
    }

    internal class ParsedChannelName
    {
        public string OriginalName { get; set; }
        public string CleanName { get; set; }
        public string Resolution { get; set; }
        public string Country { get; set; }
    }

    /// <summary>
    /// Info about a single channel's match result, used for the UI preview table.
    /// </summary>
    public class ChannelMatchInfo
    {
        public int StreamId { get; set; }
        public string ChannelName { get; set; }
        public string StationId { get; set; }
        public string StationName { get; set; }
        public string CallSign { get; set; }
        public double Score { get; set; }
        public bool IsOverride { get; set; }
        public bool IsAboveThreshold { get; set; }
    }
}
