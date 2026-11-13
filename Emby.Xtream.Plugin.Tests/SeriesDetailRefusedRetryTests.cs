using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Issue #111: some providers refuse get_series_info with 403 at random and answer the same
    /// request a few seconds later. The sync tries again on 403 and 429.
    /// </summary>
    public class SeriesDetailRefusedRetryTests : SyncTestBase
    {
        private Emby.Xtream.Plugin.Service.StrmSyncService Service()
        {
            var svc = MakeService();
            svc.SeriesDetailRefusedRetryDelaysMs = new[] { 0, 0, 0, 0, 0 };
            return svc;
        }

        private string EpisodeFile(string show) =>
            Path.Combine(TempDir.Path, "Shows", show, "Season 01", show + " - S01E01 - Episode Title.strm");

        private int DetailRequests(int seriesId) =>
            Handler.ReceivedUrls.Count(u => u.Contains("action=get_series_info&series_id=" + seriesId));

        [Theory]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData((HttpStatusCode)429)]
        public async Task RefusedTwice_ThenAnswered_WritesTheSeries(HttpStatusCode refusal)
        {
            Handler.RespondWithSequence("action=get_series_info&series_id=1", new[] { "", "" }, refusal);
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));
            Handler.RespondWith("action=get_series", SeriesListJson(Series(seriesId: 1)));

            await Service().SyncSeriesAsync(DefaultConfig(), None, SaveConfig);

            Assert.True(File.Exists(EpisodeFile("Test Show")));
            Assert.Equal(3, DetailRequests(1));
        }

        [Fact]
        public async Task RefusedEveryTime_GivesUpAfterFiveRetries()
        {
            Handler.RespondWithSequence("action=get_series_info&series_id=1", Enumerable.Repeat("", 6), HttpStatusCode.Forbidden);
            Handler.RespondWith("action=get_series", SeriesListJson(Series(seriesId: 1)));

            await Service().SyncSeriesAsync(DefaultConfig(), None, SaveConfig);

            Assert.False(File.Exists(EpisodeFile("Test Show")));
            Assert.Equal(6, DetailRequests(1));
        }

        [Fact]
        public async Task NotFound_IsNotRetried()
        {
            Handler.RespondWith("action=get_series_info&series_id=1", "", HttpStatusCode.NotFound);
            Handler.RespondWith("action=get_series", SeriesListJson(Series(seriesId: 1)));

            await Service().SyncSeriesAsync(DefaultConfig(), None, SaveConfig);

            Assert.Equal(1, DetailRequests(1));
        }

        [Fact]
        public async Task ProviderRefusingEverything_StopsRetryingAfterFiveSeries()
        {
            const int seriesCount = 7;
            for (int id = 1; id <= seriesCount; id++)
                Handler.RespondWithSequence("action=get_series_info&series_id=" + id, Enumerable.Repeat("", 6), HttpStatusCode.Forbidden);
            Handler.RespondWith("action=get_series", SeriesListJson(
                Enumerable.Range(1, seriesCount).Select(id => Series(seriesId: id, name: "Show " + id)).ToArray()));

            await Service().SyncSeriesAsync(DefaultConfig(), None, SaveConfig);

            // Five series get every try (6 each); after that each refused series gets one.
            var total = Enumerable.Range(1, seriesCount).Sum(DetailRequests);
            Assert.Equal(5 * 6 + 2 * 1, total);
        }
    }
}
