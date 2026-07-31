using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Tests for values that reach the service from persisted configuration rather than from the
    /// config UI, which is the only thing that validates them today.
    /// </summary>
    public class ConfigValidationTests : SyncTestBase
    {
        // -----------------------------------------------------------------
        // SyncParallelism
        // -----------------------------------------------------------------

        [Theory]
        [InlineData(0, 1)]      // the dangerous one: a semaphore with no permits never releases
        [InlineData(-5, 1)]
        [InlineData(1, 1)]
        [InlineData(3, 3)]
        [InlineData(10, 10)]
        [InlineData(50, 10)]
        [InlineData(int.MaxValue, 10)]
        public void GetSyncParallelism_ClampsToUsableRange(int configured, int expected)
        {
            var config = DefaultConfig();
            config.SyncParallelism = configured;

            Assert.Equal(expected, StrmSyncService.GetSyncParallelism(config));
        }

        [Fact]
        public async Task ZeroParallelism_SyncStillCompletes()
        {
            // Unclamped this constructs SemaphoreSlim(0): the first task waits forever, the sync
            // hangs, and the scheduled task never releases. Recovery meant hand-editing the XML.
            var config = DefaultConfig();
            config.SyncParallelism = 0;

            Handler.RespondWith("get_vod_streams",
                VodStreamsJson(VodStream(streamId: 1, name: "Test Movie", added: 1000)));

            var sync = MakeService().SyncMoviesAsync(config, None, SaveConfig);
            var finished = await Task.WhenAny(sync, Task.Delay(5000));

            Assert.Same(sync, finished);
            Assert.True(File.Exists(Path.Combine(TempDir.Path, "Movies", "Test Movie", "Test Movie.strm")));
        }

        // -----------------------------------------------------------------
        // Credentials in URL path segments
        // -----------------------------------------------------------------

        [Fact]
        public async Task CredentialsWithSpecialCharacters_AreEscapedInStrmUrl()
        {
            // A password containing '/' or '?' silently produced a broken playback URL: the extra
            // separator changed which path segment the provider saw, and '#' truncated everything
            // after it into a fragment.
            var config = DefaultConfig();
            config.Username = "user name";
            config.Password = "p/a?ss#w d";

            Handler.RespondWith("get_vod_streams",
                VodStreamsJson(VodStream(streamId: 42, name: "Test Movie", added: 1000, ext: "mkv")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var strm = Path.Combine(TempDir.Path, "Movies", "Test Movie", "Test Movie.strm");
            var url = File.ReadAllText(strm);

            Assert.Equal("http://fake-xtream/movie/user%20name/p%2Fa%3Fss%23w%20d/42.mkv", url);

            // The stream id must still be the last path segment, which is what breaks without this.
            Assert.EndsWith("/42.mkv", url);
            Assert.Equal(5, new System.Uri(url).Segments.Length);
        }

        [Fact]
        public async Task PlainCredentials_UrlIsUnchanged()
        {
            // Escaping is a no-op for ordinary credentials, so existing STRM files are not rewritten
            // and no library re-scan is triggered on upgrade.
            var config = DefaultConfig();

            Handler.RespondWith("get_vod_streams",
                VodStreamsJson(VodStream(streamId: 42, name: "Test Movie", added: 1000, ext: "mkv")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var url = File.ReadAllText(Path.Combine(TempDir.Path, "Movies", "Test Movie", "Test Movie.strm"));
            Assert.Equal("http://fake-xtream/movie/user/pass/42.mkv", url);
        }

        [Fact]
        public async Task CredentialsWithSpecialCharacters_AreEscapedInSeriesStrmUrl()
        {
            var config = DefaultConfig();
            config.Username = "us er";
            config.Password = "p/w";

            Handler.RespondWith("get_series", SeriesListJson(Series(seriesId: 1, name: "Test Show")));
            Handler.RespondWith("get_series_info", SeriesDetailJson(seriesId: 1, ext: "mp4"));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var strm = Directory
                .GetFiles(Path.Combine(TempDir.Path, "Shows"), "*.strm", SearchOption.AllDirectories)
                .Single();
            var url = File.ReadAllText(strm);

            Assert.Contains("/us%20er/p%2Fw/", url);
            Assert.EndsWith(".mp4", url);
        }
    }
}
