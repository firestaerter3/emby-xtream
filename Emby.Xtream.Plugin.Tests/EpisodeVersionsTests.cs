using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Issue #111: one .strm per Dispatcharr provider for series episodes, like movies.
    /// </summary>
    public class EpisodeVersionsTests : SyncTestBase
    {
        private const string Dispatcharr = "http://fake-dispatcharr";

        private string SeasonDir => Path.Combine(TempDir.Path, "Shows", "Test Show", "Season 01");
        private string MainFile => Path.Combine(SeasonDir, "Test Show - S01E01 - Episode Title.strm");
        private string UkFile => Path.Combine(SeasonDir, "Test Show - S01E01 - Episode Title - UK.strm");

        private PluginConfiguration VersionsConfig(bool on = true)
        {
            var config = DefaultConfig();
            config.EnableDispatcharr = true;
            config.DispatcharrUrl = Dispatcharr;
            config.DispatcharrUser = "admin";
            config.DispatcharrPass = "secret";
            config.DispatcharrEpisodeVersions = on;
            return config;
        }

        private StrmSyncService Service()
        {
            var svc = MakeService();
            svc.DispatcharrHandler = Handler;
            return svc;
        }

        private void RegisterSeries(string lastModified = "2000", int times = 1)
        {
            // Detail rule first: "action=get_series" is a prefix of "action=get_series_info".
            Handler.RespondWithSequence("action=get_series_info&series_id=1",
                Enumerable.Repeat(SeriesDetailJson(seriesId: 1), times));
            Handler.RespondWithSequence("action=get_series",
                Enumerable.Repeat(SeriesListJson(Series(seriesId: 1, lastModified: lastModified)), times));
        }

        private void RegisterDispatcharr(string providersJson, int times = 1)
        {
            Handler.RespondWithSequence("/api/accounts/token/",
                Enumerable.Repeat("{\"access\":\"tok\",\"refresh\":\"ref\"}", times));
            Handler.RespondWithSequence("api/vod/episodes/101/",
                Enumerable.Repeat("{\"id\":101,\"uuid\":\"ep-uuid\",\"series\":{\"id\":55}}", times));
            Handler.RespondWithSequence("api/vod/series/55/episodes/",
                Enumerable.Repeat("[{\"id\":101,\"uuid\":\"ep-uuid\",\"providers\":" + providersJson + "}]", times));
        }

        private const string TwoProviders =
            "[{\"id\":7,\"stream_id\":\"b2\",\"m3u_account\":{\"id\":2,\"name\":\"UK\",\"priority\":5}}," +
            " {\"id\":3,\"stream_id\":\"a1\",\"m3u_account\":{\"id\":1,\"name\":\"Main\",\"priority\":10}}]";

        [Fact]
        public async Task TwoProviders_WritesOneFilePerProvider_BestFirstUnderTheUsualName()
        {
            RegisterSeries();
            RegisterDispatcharr(TwoProviders);

            await Service().SyncSeriesAsync(VersionsConfig(), None, SaveConfig);

            Assert.Equal(Dispatcharr + "/proxy/vod/episode/ep-uuid?stream_id=a1", File.ReadAllText(MainFile));
            Assert.Equal(Dispatcharr + "/proxy/vod/episode/ep-uuid?stream_id=b2", File.ReadAllText(UkFile));
            Assert.Equal(2, Directory.GetFiles(SeasonDir, "*.strm").Length);
        }

        [Fact]
        public async Task OneProvider_KeepsTheSingleXtreamFile()
        {
            RegisterSeries();
            RegisterDispatcharr("[{\"id\":3,\"stream_id\":\"a1\",\"m3u_account\":{\"id\":1,\"name\":\"Main\"}}]");

            await Service().SyncSeriesAsync(VersionsConfig(), None, SaveConfig);

            Assert.Equal("http://fake-xtream/series/user/pass/101.mp4", File.ReadAllText(MainFile));
            Assert.Single(Directory.GetFiles(SeasonDir, "*.strm"));
        }

        [Fact]
        public async Task SettingOff_AsksDispatcharrNothing()
        {
            RegisterSeries();

            await Service().SyncSeriesAsync(VersionsConfig(on: false), None, SaveConfig);

            Assert.Equal("http://fake-xtream/series/user/pass/101.mp4", File.ReadAllText(MainFile));
            Assert.DoesNotContain(Handler.ReceivedUrls, u => u.Contains("/api/vod/"));
        }

        [Fact]
        public async Task TurningItOn_RewritesSeriesTheProviderDidNotChange()
        {
            // Same last_modified both runs, smart skip on: without a forced full pass the second run
            // would skip the series before reading its episodes, and the setting would do nothing.
            RegisterSeries(times: 2);
            var config = VersionsConfig(on: false);
            config.SmartSkipExisting = true;
            var svc = Service();
            await svc.SyncSeriesAsync(config, None, SaveConfig);
            Assert.False(File.Exists(UkFile));

            RegisterDispatcharr(TwoProviders);
            config.DispatcharrEpisodeVersions = true;
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(UkFile));
            Assert.Equal(Dispatcharr + "/proxy/vod/episode/ep-uuid?stream_id=a1", File.ReadAllText(MainFile));
        }

        [Fact]
        public async Task LookupFails_WritesTheSingleFile_AndKeepsVersionsAlreadyOnDisk()
        {
            RegisterSeries(lastModified: "2000");
            RegisterDispatcharr(TwoProviders);
            var config = VersionsConfig();
            config.CleanupOrphans = true;
            var svc = Service();
            await svc.SyncSeriesAsync(config, None, SaveConfig);
            Assert.True(File.Exists(UkFile));

            // The series changed, so it is written again, and Dispatcharr answers garbage this time.
            RegisterSeries(lastModified: "3000");
            Handler.RespondWith("/api/accounts/token/", "{\"access\":\"tok\",\"refresh\":\"ref\"}");
            Handler.RespondWith("api/vod/episodes/101/", "not json");
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.Equal("http://fake-xtream/series/user/pass/101.mp4", File.ReadAllText(MainFile));
            Assert.True(File.Exists(UkFile), "A failed lookup must not let cleanup remove the other versions");
        }

        [Fact]
        public async Task LookupReturnsNothing_KeepsVersionsAlreadyOnDisk()
        {
            RegisterSeries(lastModified: "2000");
            RegisterDispatcharr(TwoProviders);
            var config = VersionsConfig();
            config.CleanupOrphans = true;
            var svc = Service();
            await svc.SyncSeriesAsync(config, None, SaveConfig);
            Assert.True(File.Exists(UkFile));

            // Dispatcharr answers, but without a series: GetSeriesEpisodeProvidersAsync returns null.
            RegisterSeries(lastModified: "3000");
            Handler.RespondWith("/api/accounts/token/", "{\"access\":\"tok\",\"refresh\":\"ref\"}");
            Handler.RespondWith("api/vod/episodes/101/", "{\"id\":101,\"uuid\":\"ep-uuid\",\"series\":null}");
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.Equal("http://fake-xtream/series/user/pass/101.mp4", File.ReadAllText(MainFile));
            Assert.True(File.Exists(UkFile), "No answer from Dispatcharr must not let cleanup remove the other versions");
        }

        [Fact]
        public async Task TickedBeforeDispatcharrWasSetUp_SettingItUpLaterStillWritesVersions()
        {
            // First run: box ticked, Dispatcharr not configured yet, so plain files only.
            RegisterSeries(times: 2);
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            config.DispatcharrEpisodeVersions = true;
            var svc = Service();
            await svc.SyncSeriesAsync(config, None, SaveConfig);
            Assert.False(File.Exists(UkFile));

            // Dispatcharr set up afterwards; the provider has not touched the series.
            RegisterDispatcharr(TwoProviders);
            config.EnableDispatcharr = true;
            config.DispatcharrUrl = Dispatcharr;
            config.DispatcharrUser = "admin";
            config.DispatcharrPass = "secret";
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(UkFile));
        }

        // ----- EpisodeVersions.Build -----

        private static DispatcharrEpisodeProvider P(int id, string stream, string name, int? priority = null) =>
            new DispatcharrEpisodeProvider
            {
                Id = id,
                StreamId = stream,
                Account = new DispatcharrAccountRef { Id = id, Name = name, Priority = priority },
            };

        private static DispatcharrSeriesEpisode Ep(params DispatcharrEpisodeProvider[] providers) =>
            new DispatcharrSeriesEpisode { Id = 1, Uuid = "u", Providers = providers.ToList() };

        [Fact]
        public void Build_SamePriority_KeepsRelationOrder()
        {
            var v = EpisodeVersions.Build(Ep(P(9, "x", "Late"), P(2, "y", "Early")), Dispatcharr);
            Assert.Null(v[0].Label);
            Assert.EndsWith("stream_id=y", v[0].Url);
            Assert.Equal("Late", v[1].Label);
        }

        [Fact]
        public void Build_DuplicateOrEmptyNames_GetDistinctLabels()
        {
            var v = EpisodeVersions.Build(Ep(P(1, "a", "Main"), P(2, "b", "UK"), P(3, "c", "UK"), P(4, "d", " ")), Dispatcharr);
            Assert.Equal(new[] { null, "UK", "UK 2", "Version 4" }, v.Select(x => x.Label).ToArray());
        }

        [Fact]
        public void Build_NameWithPathCharacters_IsSanitized()
        {
            var v = EpisodeVersions.Build(Ep(P(1, "a", "Main"), P(2, "b", "4K/DV: HDR")), Dispatcharr);
            Assert.DoesNotContain("/", v[1].Label);
            Assert.DoesNotContain(":", v[1].Label);
        }

        [Fact]
        public void Build_NoUuidOrOneProvider_GivesNothing()
        {
            Assert.Empty(EpisodeVersions.Build(new DispatcharrSeriesEpisode { Providers = new List<DispatcharrEpisodeProvider> { P(1, "a", "A"), P(2, "b", "B") } }, Dispatcharr));
            Assert.Empty(EpisodeVersions.Build(Ep(P(1, "a", "A")), Dispatcharr));
        }

        [Fact]
        public void Build_StopsAtEmbysLimitOfEight()
        {
            var many = Enumerable.Range(1, 12).Select(i => P(i, "s" + i, "P" + i)).ToArray();
            Assert.Equal(EpisodeVersions.MaxVersions, EpisodeVersions.Build(Ep(many), Dispatcharr).Count);
        }

        [Fact]
        public void FileName_AddsTheLabelBeforeTheExtension()
        {
            Assert.Equal("Show - S01E01 - T - UK.strm", EpisodeVersions.FileName("Show - S01E01 - T.strm", "UK"));
            Assert.Equal("Show - S01E01 - T.strm", EpisodeVersions.FileName("Show - S01E01 - T.strm", null));
        }
    }
}
