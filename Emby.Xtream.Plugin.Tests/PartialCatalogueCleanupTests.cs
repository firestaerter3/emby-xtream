using System.IO;
using System.Net;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Regression tests for the "incomplete upstream data drives deletion" bug class.
    ///
    /// A per-category request that failed used to be swallowed into an empty list, which orphan
    /// cleanup could not tell apart from a category that is genuinely empty, so it deleted that
    /// category's existing files. Likewise an empty <c>get_series_info</c> payload looked like a
    /// show that had lost every episode.
    ///
    /// Every test here starts with files ALREADY ON DISK and asserts they survive. A test that
    /// starts from an empty directory passes trivially and proves nothing, which is exactly why
    /// the pre-existing empty-response test missed this.
    /// </summary>
    public class PartialCatalogueCleanupTests : SyncTestBase
    {
        private string MovieStrmPath(string movieName)
            => Path.Combine(TempDir.Path, "Movies", movieName, movieName + ".strm");

        private string SeedMovieStrm(string movieName, string content = "http://fake-xtream/movie/user/pass/99.mkv")
        {
            var path = MovieStrmPath(movieName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            return path;
        }

        private string SeedSeriesStrm(string showName, string season, string fileName,
            string content = "http://fake-xtream/series/user/pass/99.mp4")
        {
            var path = Path.Combine(TempDir.Path, "Shows", showName, season, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            return path;
        }

        // -----------------------------------------------------------------
        // Movies: one category 502s
        // -----------------------------------------------------------------

        [Fact]
        public async Task MovieCategoryFetchFails_ExistingFilesForThatCategorySurvive()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            config.SelectedVodCategoryIds = new[] { 1, 2 };

            // Category 1 answers with one movie. Category 2 fails.
            Handler.RespondWith("category_id=1", VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));
            Handler.RespondWith("category_id=2", "{}", HttpStatusCode.InternalServerError);

            // A movie from the failed category is already on disk.
            var strandedPath = SeedMovieStrm("Movie From Broken Category");

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(strandedPath),
                "A movie belonging to the category that failed must not be deleted as an orphan");
            Assert.True(File.Exists(MovieStrmPath("Kept Movie")),
                "The category that succeeded should still have been written");
        }

        [Fact]
        public async Task MovieCategoryFetchFails_WatermarkStillAdvances()
        {
            var config = DefaultConfig();
            config.SelectedVodCategoryIds = new[] { 1, 2 };

            Handler.RespondWith("category_id=1", VodStreamsJson(VodStream(streamId: 1, name: "New Movie", added: 5000)));
            Handler.RespondWith("category_id=2", "{}", HttpStatusCode.InternalServerError);

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            // Freezing the watermark on a partial failure would make every later sync
            // re-process the categories that did succeed.
            Assert.Equal(5000, config.LastMovieSyncTimestamp);
        }

        [Fact]
        public async Task AllMovieCategoriesSucceed_CleanupStillRuns()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            config.SelectedVodCategoryIds = new[] { 1 };

            Handler.RespondWith("category_id=1", VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));

            var orphanPath = SeedMovieStrm("Genuinely Removed Movie");

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(orphanPath),
                "With every category answering, a title the provider no longer lists is a real orphan");
        }

        // -----------------------------------------------------------------
        // Series: one category 502s
        // -----------------------------------------------------------------

        [Fact]
        public async Task SeriesCategoryFetchFails_ExistingEpisodesSurvive()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            config.SelectedSeriesCategoryIds = new[] { 1, 2 };

            Handler.RespondWith("get_series&category_id=1", SeriesListJson(Series(seriesId: 1, name: "Kept Show")));
            Handler.RespondWith("get_series&category_id=2", "{}", HttpStatusCode.InternalServerError);
            Handler.RespondWith("get_series_info", SeriesDetailJson(seriesId: 1));

            var strandedPath = SeedSeriesStrm("Show From Broken Category", "Season 01", "S01E01.strm");

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(strandedPath),
                "Episodes of a show in the category that failed must not be deleted as orphans");
        }

        // -----------------------------------------------------------------
        // Series: transient empty episode payload
        // -----------------------------------------------------------------

        [Fact]
        public async Task SeriesReturnsNoEpisodes_ExistingEpisodesOnDiskSurvive()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;

            Handler.RespondWith("get_series", SeriesListJson(Series(seriesId: 1, name: "Test Show")));
            // Provider hiccups and reports the show with no episodes at all.
            Handler.RespondWith("get_series_info",
                "{\"info\":{\"series_id\":1,\"name\":\"Test Show\"},\"seasons\":[],\"episodes\":{}}");

            var existingPath = SeedSeriesStrm("Test Show", "Season 01", "S01E01.strm");

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(existingPath),
                "A transient empty episode payload must not wipe a show that already has files");
        }

        [Fact]
        public async Task SeriesReturnsNoEpisodes_AndHasNoFilesOnDisk_DoesNotBlockCleanup()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;

            // One healthy show (so the run produces files and the empty-catalogue guard does not
            // fire) plus one show that reports no episodes and owns nothing on disk.
            Handler.RespondWith("get_series", SeriesListJson(
                Series(seriesId: 1, name: "Good Show"),
                Series(seriesId: 2, name: "Empty Show")));
            Handler.RespondWith("series_id=1", SeriesDetailJson(seriesId: 1));
            Handler.RespondWith("series_id=2",
                "{\"info\":{\"series_id\":2,\"name\":\"Empty Show\"},\"seasons\":[],\"episodes\":{}}");

            var orphanPath = SeedSeriesStrm("Removed Show", "Season 01", "S01E01.strm");

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(orphanPath),
                "A show that returns empty while owning nothing on disk is not a failure, so unrelated orphans are still cleaned");
        }

        [Fact]
        public async Task EverySeriesReturnsEmpty_DeletesNothing()
        {
            // The catalogue is not empty, but nothing survived it. Deleting the whole library on
            // the strength of that is the catastrophic case, so cleanup refuses.
            var config = DefaultConfig();
            config.CleanupOrphans = true;

            Handler.RespondWith("get_series", SeriesListJson(Series(seriesId: 1, name: "Empty Show")));
            Handler.RespondWith("get_series_info",
                "{\"info\":{\"series_id\":1,\"name\":\"Empty Show\"},\"seasons\":[],\"episodes\":{}}");

            var existingPath = SeedSeriesStrm("Removed Show", "Season 01", "S01E01.strm");

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(existingPath),
                "A run that produced no files at all must not be read as a library-wide deletion");
        }

        // -----------------------------------------------------------------
        // Empty catalogue must never be read as a deletion
        // -----------------------------------------------------------------

        [Fact]
        public async Task EmptyCatalogue_WithFilesOnDisk_DeletesNothing()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            config.OrphanSafetyThreshold = 0.2;

            // Provider returns a valid but empty catalogue.
            Handler.RespondWith("get_vod_streams", "[]");

            var a = SeedMovieStrm("Movie A");
            var b = SeedMovieStrm("Movie B");

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(a), "An empty catalogue must not empty the library");
            Assert.True(File.Exists(b), "An empty catalogue must not empty the library");
        }

        [Fact]
        public async Task EmptyCatalogue_SmallLibrary_DeletesNothing()
        {
            // The ratio guard only applies above 10 files, so a small library had no protection
            // at all. This is the case that could lose an entire modest collection.
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            config.OrphanSafetyThreshold = 0.2;

            Handler.RespondWith("get_vod_streams", "[]");

            var only = SeedMovieStrm("The Only Movie");

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(only),
                "A transient empty response must not delete the last file in a small library");
        }
    }
}
