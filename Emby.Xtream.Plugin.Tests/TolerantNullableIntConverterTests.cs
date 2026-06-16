using System.Text.Json;
using System.Text.Json.Serialization;
using Emby.Xtream.Plugin.Client.Models;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Regression coverage for the GitHub #32 follow-up: providers returning <c>info.category_id</c>
    /// (and other nominally-integer fields) as an empty string, a non-numeric string, null, or an
    /// array must not abort the series parse with
    /// "The JSON value could not be converted to System.Nullable`1[System.Int32]".
    /// </summary>
    public class TolerantNullableIntConverterTests
    {
        // Mirrors StrmSyncService.JsonOptions.
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
            Converters =
            {
                new TolerantStringConverter(),
                new TolerantNullableIntConverter(),
            },
        };

        private static SeriesInfo DeserializeInfo(string categoryIdToken)
        {
            var json = $$"""
                { "info": { "series_id": 42, "name": "Test", "category_id": {{categoryIdToken}} } }
                """;
            var detail = JsonSerializer.Deserialize<SeriesDetailInfo>(json, Options);
            Assert.NotNull(detail);
            Assert.NotNull(detail.Info);
            return detail.Info;
        }

        [Fact]
        public void CategoryId_AsNumber_IsParsed()
        {
            var info = DeserializeInfo("510");
            Assert.Equal(510, info.CategoryId);
        }

        [Fact]
        public void CategoryId_AsNumericString_IsParsed()
        {
            var info = DeserializeInfo("\"510\"");
            Assert.Equal(510, info.CategoryId);
        }

        [Fact]
        public void CategoryId_AsEmptyString_IsNull()
        {
            // The exact shape that crashed in the GitHub #32 follow-up.
            var info = DeserializeInfo("\"\"");
            Assert.Null(info.CategoryId);
            Assert.Equal(42, info.SeriesId);
        }

        [Fact]
        public void CategoryId_AsNonNumericString_IsNull()
        {
            var info = DeserializeInfo("\"none\"");
            Assert.Null(info.CategoryId);
        }

        [Fact]
        public void CategoryId_AsNull_IsNull()
        {
            var info = DeserializeInfo("null");
            Assert.Null(info.CategoryId);
        }

        [Fact]
        public void CategoryId_AsArray_IsDiscarded()
        {
            var info = DeserializeInfo("[1, 2]");
            Assert.Null(info.CategoryId);
        }

        [Fact]
        public void CategoryId_AsObject_IsDiscarded()
        {
            var info = DeserializeInfo("""{ "id": 3 }""");
            Assert.Null(info.CategoryId);
        }

        [Fact]
        public void CategoryId_AsDecimalString_IsTruncated()
        {
            var info = DeserializeInfo("\"7.0\"");
            Assert.Equal(7, info.CategoryId);
        }

        [Fact]
        public void FullSeriesPayload_WithEmptyStringCategoryId_ParsesEpisodes()
        {
            // End-to-end: an empty-string category_id must not stop seasons/episodes from parsing.
            var json = """
                {
                  "info": { "name": "Show", "category_id": "" },
                  "seasons": [ { "season_number": 1, "name": "S1" } ],
                  "episodes": {
                    "1": [ { "id": 100, "episode_num": 1, "title": "Pilot", "container_extension": "mkv" } ]
                  }
                }
                """;

            var detail = JsonSerializer.Deserialize<SeriesDetailInfo>(json, Options);

            Assert.Null(detail.Info.CategoryId);
            Assert.Single(detail.Seasons);
            Assert.Equal("Pilot", detail.Episodes["1"][0].Title);
        }

        [Fact]
        public void Roundtrip_PreservesValueAndNull()
        {
            var withValue = JsonSerializer.Serialize(new SeriesInfo { CategoryId = 12 }, Options);
            Assert.Contains("\"category_id\":12", withValue);

            var withNull = JsonSerializer.Serialize(new SeriesInfo { CategoryId = null }, Options);
            Assert.Contains("\"category_id\":null", withNull);
        }
    }
}
