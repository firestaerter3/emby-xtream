using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Emby.Xtream.Plugin.Client.Models;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Regression coverage for GitHub #35: providers returning the <c>episodes</c> field of
    /// <c>get_series_info</c> as something other than a <c>Dictionary&lt;string, List&lt;EpisodeInfo&gt;&gt;</c>
    /// must not crash the series sync.
    ///
    /// Expected shape:  { "1": [ { "id": 101, "episode_num": 1, "title": "...", ... } ] }
    ///
    /// Observed breakage: providers returning <c>null</c>, <c>[]</c>, arrays of arrays, or
    /// malformed entries cause <c>JsonException</c> with the default dictionary deserializer.
    /// </summary>
    public class TolerantEpisodeDictionaryConverterTests
    {
        // Mirrors the JsonOptions used in StrmSyncService.
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
            Converters =
            {
                new TolerantStringConverter(),
            },
        };

        /// <summary>
        /// Helper: deserialize a complete <c>get_series_info</c> payload and return its episodes dict.
        /// </summary>
        private static Dictionary<string, List<EpisodeInfo>> DeserializeEpisodes(string episodesToken)
        {
            var json = $$"""
                {
                  "info": { "series_id": 1, "name": "Test" },
                  "seasons": [],
                  "episodes": {{episodesToken}}
                }
                """;
            var detail = JsonSerializer.Deserialize<SeriesDetailInfo>(json, Options);
            Assert.NotNull(detail);
            return detail!.Episodes;
        }

        // ---------------------------------------------------------------
        // Happy path
        // ---------------------------------------------------------------

        [Fact]
        public void Episodes_NormalObject_ParsesCorrectly()
        {
            var episodes = DeserializeEpisodes("""
                {
                  "1": [
                    { "id": 101, "episode_num": 1, "title": "Pilot", "container_extension": "mkv", "season": 1 }
                  ]
                }
                """);
            Assert.Single(episodes);
            Assert.True(episodes.ContainsKey("1"));
            Assert.Single(episodes["1"]);
            Assert.Equal("Pilot", episodes["1"][0].Title);
            Assert.Equal(101, episodes["1"][0].Id);
        }

        [Fact]
        public void Episodes_MultipleSeasons_ParsesAll()
        {
            var episodes = DeserializeEpisodes("""
                {
                  "1": [
                    { "id": 101, "episode_num": 1, "title": "Pilot", "container_extension": "mkv", "season": 1 },
                    { "id": 102, "episode_num": 2, "title": "Second", "container_extension": "mkv", "season": 1 }
                  ],
                  "2": [
                    { "id": 201, "episode_num": 1, "title": "Premiere", "container_extension": "mkv", "season": 2 }
                  ]
                }
                """);
            Assert.Equal(2, episodes.Count);
            Assert.Equal(2, episodes["1"].Count);
            Assert.Single(episodes["2"]);
        }

        // ---------------------------------------------------------------
        // Provider quirks that crashed with the default converter
        // ---------------------------------------------------------------

        [Fact]
        public void Episodes_Null_ReturnsEmptyDictionary()
        {
            var episodes = DeserializeEpisodes("null");
            Assert.Empty(episodes);
        }

        [Fact]
        public void Episodes_EmptyArray_ReturnsEmptyDictionary()
        {
            var episodes = DeserializeEpisodes("[]");
            Assert.Empty(episodes);
        }

        [Fact]
        public void Episodes_NonEmptyArray_ReturnsEmptyDictionary()
        {
            // Some providers may return an array where a dict is expected.
            var episodes = DeserializeEpisodes("[1, 2, 3]");
            Assert.Empty(episodes);
        }

        [Fact]
        public void Episodes_ArrayOfArrays_ReturnsEmptyDictionary()
        {
            var episodes = DeserializeEpisodes("""
                [["101", "Pilot"], ["102", "Second"]]
                """);
            Assert.Empty(episodes);
        }

        [Fact]
        public void Episodes_NumberToken_ReturnsEmptyDictionary()
        {
            var episodes = DeserializeEpisodes("42");
            Assert.Empty(episodes);
        }

        // ---------------------------------------------------------------
        // Malformed entries within an otherwise-valid dictionary
        // ---------------------------------------------------------------

        [Fact]
        public void Episodes_EntryWithNullValue_IsSkipped()
        {
            // Season "1" has a valid episode list, season "2" has null.
            var episodes = DeserializeEpisodes("""
                {
                  "1": [
                    { "id": 101, "episode_num": 1, "title": "Pilot", "container_extension": "mkv", "season": 1 }
                  ],
                  "2": null
                }
                """);
            // "2": null should be skipped; "1" should still be parsed.
            Assert.Single(episodes);
            Assert.True(episodes.ContainsKey("1"));
        }

        [Fact]
        public void Episodes_EntryWithNonArrayValue_IsSkipped()
        {
            var episodes = DeserializeEpisodes("""
                {
                  "1": [
                    { "id": 101, "episode_num": 1, "title": "Pilot", "container_extension": "mkv", "season": 1 }
                  ],
                  "2": { "not": "an array" }
                }
                """);
            Assert.Single(episodes);
            Assert.True(episodes.ContainsKey("1"));
        }

        [Fact]
        public void Episodes_EntryWithEmptyEpisodeArray_IsSkipped()
        {
            var episodes = DeserializeEpisodes("""
                {
                  "1": [
                    { "id": 101, "episode_num": 1, "title": "Pilot", "container_extension": "mkv", "season": 1 }
                  ],
                  "2": []
                }
                """);
            // Season "2" has no episodes -> skipped entirely
            Assert.Single(episodes);
        }

        [Fact]
        public void Episodes_MalformedEpisodeObject_IsSkipped()
        {
            // An episode entry that's missing required fields or has wrong types
            // should not crash nor corrupt the rest.
            var episodes = DeserializeEpisodes("""
                {
                  "1": [
                    { "id": 101, "episode_num": 1, "title": "Good", "container_extension": "mkv", "season": 1 },
                    { "this is": "garbage" },
                    { "id": 102, "episode_num": 2, "title": "Also Good", "container_extension": "mp4", "season": 1 }
                  ]
                }
                """);
            Assert.Single(episodes);
            // The malformed entry should be skipped but the valid ones retained.
            Assert.Equal(2, episodes["1"].Count);
            Assert.Equal("Good", episodes["1"][0].Title);
            Assert.Equal("Also Good", episodes["1"][1].Title);
        }

        // ---------------------------------------------------------------
        // Field-level tolerance still works alongside the dict converter
        // ---------------------------------------------------------------

        [Fact]
        public void Episodes_WithTolerantStringFields_DoesNotCrash()
        {
            // Episode fields like rating, duration etc. should still be tolerated
            // as numbers/booleans even inside the dict.
            var episodes = DeserializeEpisodes("""
                {
                  "1": [
                    {
                      "id": 101,
                      "episode_num": 1,
                      "title": "Tolerant",
                      "container_extension": "mp4",
                      "season": 1,
                      "rating": 9,
                      "duration": 1800
                    }
                  ]
                }
                """);
            Assert.Single(episodes);
            Assert.Equal("9", episodes["1"][0].Rating);
            Assert.Equal("1800", episodes["1"][0].Duration);
        }

        [Fact]
        public void Episodes_MultipleSeasonsPartialCorruption_ParsesBestEffort()
        {
            // Season 2 is an array of arrays (malformed), season 1 is fine.
            var episodes = DeserializeEpisodes("""
                {
                  "1": [
                    { "id": 101, "episode_num": 1, "title": "Good Ep", "container_extension": "mkv", "season": 1 }
                  ],
                  "2": [
                    ["not", "an", "episode", "object"]
                  ],
                  "3": [
                    { "id": 301, "episode_num": 1, "title": "Season 3 Ep", "container_extension": "mp4", "season": 3 }
                  ]
                }
                """);
            // Season 1 parsed, season 2 malformed and skipped, season 3 parsed.
            Assert.Equal(2, episodes.Count);
            Assert.True(episodes.ContainsKey("1"));
            Assert.True(episodes.ContainsKey("3"));
            Assert.Equal("Good Ep", episodes["1"][0].Title);
            Assert.Equal("Season 3 Ep", episodes["3"][0].Title);
        }
    }
}
