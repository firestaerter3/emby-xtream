using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Tests for the single-flight sync gate.
    ///
    /// The callers' <c>IsRunning</c> check is a fast path, not a lock: between that check and the
    /// point where the service marks itself running, a second request or the scheduled task could
    /// pass it too. Two runs then shared the written-path set and the progress object, and whichever
    /// finished first cleared <c>IsRunning</c> while the other was still writing.
    /// </summary>
    public class ConcurrentSyncGuardTests : SyncTestBase
    {
        [Fact]
        public async Task ConcurrentMovieSyncs_OnlyOneRuns()
        {
            var config = DefaultConfig();
            Handler.RespondWith("get_vod_streams",
                VodStreamsJson(VodStream(streamId: 1, name: "Test Movie", added: 1000)));

            var service = MakeService();

            // Hold the first sync inside its HTTP call so the two are genuinely in flight at
            // once. Without this the fake responds synchronously and the first run completes
            // before the second even starts, which tests nothing.
            Handler.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Start both without awaiting the first, so they overlap the way a manual sync and the
            // scheduled task do.
            var first = service.SyncMoviesAsync(config, None, SaveConfig);
            var second = service.SyncMoviesAsync(config, None, SaveConfig);

            Handler.Gate.SetResult(true);
            var results = await Task.WhenAll(first, second);

            Assert.Equal(1, System.Array.FindAll(results, r => r).Length);
            Assert.Equal(1, System.Array.FindAll(results, r => !r).Length);
        }

        [Fact]
        public async Task ConcurrentSeriesSyncs_OnlyOneRuns()
        {
            var config = DefaultConfig();
            Handler.RespondWith("get_series", SeriesListJson(Series(seriesId: 1, name: "Test Show")));
            Handler.RespondWith("get_series_info", SeriesDetailJson(seriesId: 1));

            var service = MakeService();

            // Hold the first sync inside its HTTP call so the two are genuinely in flight at
            // once. Without this the fake responds synchronously and the first run completes
            // before the second even starts, which tests nothing.
            Handler.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var first = service.SyncSeriesAsync(config, None, SaveConfig);
            var second = service.SyncSeriesAsync(config, None, SaveConfig);

            Handler.Gate.SetResult(true);
            var results = await Task.WhenAll(first, second);

            Assert.Equal(1, System.Array.FindAll(results, r => r).Length);
            Assert.Equal(1, System.Array.FindAll(results, r => !r).Length);
        }

        [Fact]
        public async Task GateIsReleased_SecondSyncAfterFirstCompletes_Runs()
        {
            var config = DefaultConfig();
            Handler.RespondWithSequence("get_vod_streams", new[]
            {
                VodStreamsJson(VodStream(streamId: 1, name: "First Movie", added: 1000)),
                VodStreamsJson(VodStream(streamId: 2, name: "Second Movie", added: 2000)),
            });

            var service = MakeService();

            Assert.True(await service.SyncMoviesAsync(config, None, SaveConfig));
            Assert.True(await service.SyncMoviesAsync(config, None, SaveConfig),
                "The gate must be released once a sync finishes, or every later sync is blocked");

            Assert.True(File.Exists(Path.Combine(TempDir.Path, "Movies", "Second Movie", "Second Movie.strm")));
        }

        [Fact]
        public async Task MovieAndSeriesSyncs_RunConcurrently()
        {
            // Separate gates on purpose: they write to different roots and are meant to overlap.
            var config = DefaultConfig();
            Handler.RespondWith("get_vod_streams",
                VodStreamsJson(VodStream(streamId: 1, name: "Test Movie", added: 1000)));
            Handler.RespondWith("get_series", SeriesListJson(Series(seriesId: 1, name: "Test Show")));
            Handler.RespondWith("get_series_info", SeriesDetailJson(seriesId: 1));

            var service = MakeService();

            // Hold both in flight at once. Without this they run one after the other and the test
            // would pass even if movies and series shared a single gate, which is the thing it is
            // supposed to rule out.
            Handler.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var movies = service.SyncMoviesAsync(config, None, SaveConfig);
            var series = service.SyncSeriesAsync(config, None, SaveConfig);

            Handler.Gate.SetResult(true);

            Assert.True(await movies);
            Assert.True(await series, "Series sync must not be blocked by an in-flight movie sync");
        }

        [Fact]
        public async Task RejectedSync_DoesNotClobberRunningSyncProgress()
        {
            var config = DefaultConfig();
            Handler.RespondWith("get_vod_streams",
                VodStreamsJson(VodStream(streamId: 1, name: "Test Movie", added: 1000)));

            var service = MakeService();

            // Hold the first sync inside its HTTP call so the two are genuinely in flight at
            // once. Without this the fake responds synchronously and the first run completes
            // before the second even starts, which tests nothing.
            Handler.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var first = service.SyncMoviesAsync(config, None, SaveConfig);
            var second = service.SyncMoviesAsync(config, None, SaveConfig);

            Handler.Gate.SetResult(true);
            await Task.WhenAll(first, second);

            // The rejected call must not have replaced the progress object or reset its counters.
            Assert.Equal(1, service.MovieProgress.Total);
            Assert.False(service.MovieProgress.IsRunning);
        }
    }
}
