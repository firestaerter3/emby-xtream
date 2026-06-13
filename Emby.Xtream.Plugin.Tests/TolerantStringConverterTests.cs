using System.Text.Json;
using System.Text.Json.Serialization;
using Emby.Xtream.Plugin.Client.Models;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Regression coverage for GitHub #32: providers returning <c>info.releasedate</c> (and other
    /// nominally-string fields) as numbers, booleans, null, or arrays must not abort the series
    /// parse with "The JSON value could not be converted to System.String".
    /// </summary>
    public class TolerantStringConverterTests
    {
        // Mirrors StrmSyncService.JsonOptions.
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
            Converters = { new TolerantStringConverter() },
        };

        private static SeriesInfo DeserializeInfo(string releaseDateToken)
        {
            // Note: provider uses lowercase "releasedate"; the model maps "releaseDate".
            // Case-insensitive matching plus the converter must cope with both.
            var json = $$"""
                { "info": { "series_id": 42, "name": "Test", "releasedate": {{releaseDateToken}} } }
                """;
            var detail = JsonSerializer.Deserialize<SeriesDetailInfo>(json, Options);
            Assert.NotNull(detail);
            Assert.NotNull(detail.Info);
            return detail.Info;
        }

        [Fact]
        public void ReleaseDate_AsNumber_IsCoercedToString()
        {
            // The exact shape that crashed in GitHub #32.
            var info = DeserializeInfo("2020");
            Assert.Equal("2020", info.ReleaseDate);
            Assert.Equal(42, info.SeriesId);
        }

        [Fact]
        public void ReleaseDate_AsDecimalNumber_IsCoercedInvariant()
        {
            var info = DeserializeInfo("2020.5");
            Assert.Equal("2020.5", info.ReleaseDate);
        }

        [Fact]
        public void ReleaseDate_AsNull_IsNull()
        {
            var info = DeserializeInfo("null");
            Assert.Null(info.ReleaseDate);
        }

        [Fact]
        public void ReleaseDate_AsEmptyArray_IsDiscarded()
        {
            var info = DeserializeInfo("[]");
            Assert.Null(info.ReleaseDate);
        }

        [Fact]
        public void ReleaseDate_AsObject_IsDiscarded()
        {
            var info = DeserializeInfo("""{ "y": 2020 }""");
            Assert.Null(info.ReleaseDate);
        }

        [Fact]
        public void ReleaseDate_AsBoolean_IsCoercedToString()
        {
            var info = DeserializeInfo("false");
            Assert.Equal("false", info.ReleaseDate);
        }

        [Fact]
        public void ReleaseDate_AsString_IsUnchanged()
        {
            var info = DeserializeInfo("\"2020-05-01\"");
            Assert.Equal("2020-05-01", info.ReleaseDate);
        }

        [Fact]
        public void MultipleStringFields_AsNumbers_AllCoerced()
        {
            var json = """
                {
                  "info": {
                    "series_id": "7",
                    "name": "Mixed",
                    "rating": 8,
                    "tmdb": 12345,
                    "releasedate": 1999
                  }
                }
                """;

            var detail = JsonSerializer.Deserialize<SeriesDetailInfo>(json, Options);

            Assert.Equal(7, detail.Info.SeriesId);     // NumberHandling: "7" -> 7
            Assert.Equal("8", detail.Info.Rating);
            Assert.Equal("12345", detail.Info.TmdbId);
            Assert.Equal("1999", detail.Info.ReleaseDate);
        }

        [Fact]
        public void FullSeriesPayload_WithNumericReleaseDate_ParsesEpisodes()
        {
            // End-to-end: a numeric releasedate must not stop seasons/episodes from parsing.
            var json = """
                {
                  "info": { "name": "Show", "releasedate": 2021 },
                  "seasons": [ { "season_number": 1, "name": "S1" } ],
                  "episodes": {
                    "1": [ { "id": 100, "episode_num": 1, "title": "Pilot", "container_extension": "mkv" } ]
                  }
                }
                """;

            var detail = JsonSerializer.Deserialize<SeriesDetailInfo>(json, Options);

            Assert.Equal("2021", detail.Info.ReleaseDate);
            Assert.Single(detail.Seasons);
            Assert.True(detail.Episodes.ContainsKey("1"));
            Assert.Equal("Pilot", detail.Episodes["1"][0].Title);
            Assert.Equal("mkv", detail.Episodes["1"][0].ContainerExtension);
        }
    }
}
