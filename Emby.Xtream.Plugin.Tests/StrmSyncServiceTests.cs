using System;
using System.Collections.Generic;
using System.Text;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class StrmSyncServiceTests
    {
        // -----------------------------------------------------------------
        // StripEpisodeTitleDuplicate
        // -----------------------------------------------------------------

        [Theory]
        // Provider embeds short series name (without year) + episode code
        [InlineData("Yago - S01E33 - Episode 33", "Yago (2016)", 1, 33, "Episode 33")]
        // Provider embeds full series name + episode code — country prefix "EN" is left as-is
        [InlineData("EN - American Gigolo - S01E01", "American Gigolo", 1, 1, "EN")]
        // Title is just the episode code — no human-readable part left
        [InlineData("Show S02E05", "Show", 2, 5, "")]
        // Title has no episode code — returned as-is
        [InlineData("The Lost City", "Show", 1, 1, "The Lost City")]
        // Title is null/empty — returns empty
        [InlineData(null, "Show", 1, 1, "")]
        [InlineData("", "Show", 1, 1, "")]
        // Code is case-insensitive
        [InlineData("Yago - s01e33 - Episode 33", "Yago (2016)", 1, 33, "Episode 33")]
        // No leading series prefix — title starts with episode code
        [InlineData("S03E07 - The Aftermath", "SomeShow", 3, 7, "The Aftermath")]
        // Issue #9 — provider embeds series name + code; series name in Xtream matches exactly
        [InlineData("EN - Barbie It Takes Two - S01E01", "EN - Barbie It Takes Two", 1, 1, "")]
        // Issue #9 — provider series name differs from Xtream series name; Pass 2 strips code
        [InlineData("EN - Arcane - S01E01", "4K-NF - Arcane (2021) (4K-NF)", 1, 1, "")]
        // Issue #9 — provider prefix + episode title preserved after stripping series + code
        [InlineData("EN - G.I. Joe A Real American Hero (1983) (4K-NF) - S01E01 - The M.A.S.S. Device The Cobra Strikes (1)",
            "EN - G.I. Joe A Real American Hero (1983) (4K-NF)", 1, 1, "The M.A.S.S. Device The Cobra Strikes (1)")]
        [InlineData("EN - Batman The Animated Series (1992) (4K-NF) - S01E01 - The Cat and the Claw (1)",
            "EN - Batman The Animated Series (1992) (4K-NF)", 1, 1, "The Cat and the Claw (1)")]
        public void StripEpisodeTitleDuplicate_ReturnsExpected(
            string title, string seriesName, int season, int episode, string expected)
        {
            var result = StrmSyncService.StripEpisodeTitleDuplicate(title, seriesName, season, episode);
            Assert.Equal(expected, result);
        }

        // -----------------------------------------------------------------
        // BuildMovieFolderName
        // -----------------------------------------------------------------

        [Theory]
        [InlineData("The Matrix", "603",  "The Matrix [tmdbid=603]")]
        [InlineData("The Matrix", "0",    "The Matrix")]
        [InlineData("The Matrix", "",     "The Matrix")]
        [InlineData("The Matrix", null,   "The Matrix")]
        [InlineData("The Matrix", "abc",  "The Matrix")]
        public void BuildMovieFolderName_TmdbHandling(string name, string tmdbId, string expected)
        {
            var result = StrmSyncService.BuildMovieFolderName(name, tmdbId);
            Assert.Equal(expected, result);
        }

        // -----------------------------------------------------------------
        // BuildSeriesFolderName
        // -----------------------------------------------------------------

        [Fact]
        public void BuildSeriesFolderName_TvdbOverride_WinsOverTmdb()
        {
            var overrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Breaking Bad"] = 81189
            };
            var result = StrmSyncService.BuildSeriesFolderName("Breaking Bad", "1396", null, overrides);
            Assert.Equal("Breaking Bad [tvdbid=81189]", result);
        }

        [Fact]
        public void BuildSeriesFolderName_AutoTvdb_UsedWhenNoOverrideAndNoTmdb()
        {
            var result = StrmSyncService.BuildSeriesFolderName("Breaking Bad", null, 81189, null);
            Assert.Equal("Breaking Bad [tvdbid=81189]", result);
        }

        [Fact]
        public void BuildSeriesFolderName_Tmdb_UsedWhenNoTvdb()
        {
            var result = StrmSyncService.BuildSeriesFolderName("Breaking Bad", "1396", null, null);
            Assert.Equal("Breaking Bad [tmdbid=1396]", result);
        }

        [Fact]
        public void BuildSeriesFolderName_TmdbBeatsAutoTvdb()
        {
            // TMDB (priority 2) wins over auto TVDB lookup (priority 3)
            var result = StrmSyncService.BuildSeriesFolderName("Breaking Bad", "1396", 81189, null);
            Assert.Equal("Breaking Bad [tmdbid=1396]", result);
        }

        [Fact]
        public void BuildSeriesFolderName_BareNameWhenNoIds()
        {
            var result = StrmSyncService.BuildSeriesFolderName("Breaking Bad", null, null, null);
            Assert.Equal("Breaking Bad", result);
        }

        // -----------------------------------------------------------------
        // ParseTvdbOverrides
        // -----------------------------------------------------------------

        [Fact]
        public void ParseTvdbOverrides_ParsesBasicMapping()
        {
            var result = StrmSyncService.ParseTvdbOverrides("Breaking Bad=81189");
            Assert.Equal(81189, result["Breaking Bad"]);
        }

        [Fact]
        public void ParseTvdbOverrides_IgnoresCommentLines()
        {
            var result = StrmSyncService.ParseTvdbOverrides("# comment\nBreaking Bad=81189");
            Assert.False(result.ContainsKey("# comment"));
            Assert.Equal(81189, result["Breaking Bad"]);
        }

        [Fact]
        public void ParseTvdbOverrides_IgnoresMalformedLines()
        {
            var result = StrmSyncService.ParseTvdbOverrides("NoEqualsSign\nBreaking Bad=81189");
            Assert.Single(result);
            Assert.Equal(81189, result["Breaking Bad"]);
        }

        [Fact]
        public void ParseTvdbOverrides_DuplicateKey_LastWins()
        {
            var result = StrmSyncService.ParseTvdbOverrides("Breaking Bad=111\nBreaking Bad=81189");
            Assert.Equal(81189, result["Breaking Bad"]);
        }

        [Fact]
        public void ParseTvdbOverrides_NonNumericId_Skipped()
        {
            var result = StrmSyncService.ParseTvdbOverrides("Breaking Bad=abc");
            Assert.Empty(result);
        }

        [Fact]
        public void ParseTvdbOverrides_ZeroId_Skipped()
        {
            var result = StrmSyncService.ParseTvdbOverrides("Breaking Bad=0");
            Assert.Empty(result);
        }

        [Fact]
        public void ParseTvdbOverrides_NullOrEmpty_ReturnsEmpty()
        {
            Assert.Empty(StrmSyncService.ParseTvdbOverrides(null));
            Assert.Empty(StrmSyncService.ParseTvdbOverrides(""));
            Assert.Empty(StrmSyncService.ParseTvdbOverrides("   "));
        }

        // -----------------------------------------------------------------
        // ComputeChannelListHash
        // -----------------------------------------------------------------

        [Fact]
        public void ComputeChannelListHash_SameChannelsDifferentOrder_SameHash()
        {
            var a = new List<Emby.Xtream.Plugin.Client.Models.LiveStreamInfo>
            {
                new Emby.Xtream.Plugin.Client.Models.LiveStreamInfo { StreamId = 1, Name = "BBC One", EpgChannelId = "bbc1", CategoryId = 10 },
                new Emby.Xtream.Plugin.Client.Models.LiveStreamInfo { StreamId = 2, Name = "ITV",     EpgChannelId = "itv",  CategoryId = 10 },
            };
            var b = new List<Emby.Xtream.Plugin.Client.Models.LiveStreamInfo>
            {
                new Emby.Xtream.Plugin.Client.Models.LiveStreamInfo { StreamId = 2, Name = "ITV",     EpgChannelId = "itv",  CategoryId = 10 },
                new Emby.Xtream.Plugin.Client.Models.LiveStreamInfo { StreamId = 1, Name = "BBC One", EpgChannelId = "bbc1", CategoryId = 10 },
            };
            Assert.Equal(StrmSyncService.ComputeChannelListHash(a), StrmSyncService.ComputeChannelListHash(b));
        }

        [Fact]
        public void ComputeChannelListHash_DifferentChannels_DifferentHash()
        {
            var a = new List<Emby.Xtream.Plugin.Client.Models.LiveStreamInfo>
            {
                new Emby.Xtream.Plugin.Client.Models.LiveStreamInfo { StreamId = 1, Name = "BBC One" },
            };
            var b = new List<Emby.Xtream.Plugin.Client.Models.LiveStreamInfo>
            {
                new Emby.Xtream.Plugin.Client.Models.LiveStreamInfo { StreamId = 1, Name = "BBC Two" },
            };
            Assert.NotEqual(StrmSyncService.ComputeChannelListHash(a), StrmSyncService.ComputeChannelListHash(b));
        }

        [Fact]
        public void ComputeChannelListHash_NameChange_DifferentHash()
        {
            var a = new List<Emby.Xtream.Plugin.Client.Models.LiveStreamInfo>
            {
                new Emby.Xtream.Plugin.Client.Models.LiveStreamInfo { StreamId = 1, Name = "BBC One" },
            };
            var b = new List<Emby.Xtream.Plugin.Client.Models.LiveStreamInfo>
            {
                new Emby.Xtream.Plugin.Client.Models.LiveStreamInfo { StreamId = 1, Name = "BBC One HD" },
            };
            Assert.NotEqual(StrmSyncService.ComputeChannelListHash(a), StrmSyncService.ComputeChannelListHash(b));
        }

        [Fact]
        public void ComputeChannelListHash_EmptyList_ReturnsStableHash()
        {
            var h1 = StrmSyncService.ComputeChannelListHash(new List<Emby.Xtream.Plugin.Client.Models.LiveStreamInfo>());
            var h2 = StrmSyncService.ComputeChannelListHash(new List<Emby.Xtream.Plugin.Client.Models.LiveStreamInfo>());
            Assert.Equal(h1, h2);
            Assert.NotEmpty(h1);
        }

        // -----------------------------------------------------------------
        // ComputeSeriesEpisodeHash
        // -----------------------------------------------------------------

        [Fact]
        public void ComputeSeriesEpisodeHash_SameEpisodesDifferentOrder_SameHash()
        {
            var a = new Dictionary<string, List<Emby.Xtream.Plugin.Client.Models.EpisodeInfo>>
            {
                ["1"] = new List<Emby.Xtream.Plugin.Client.Models.EpisodeInfo>
                {
                    new Emby.Xtream.Plugin.Client.Models.EpisodeInfo { Id = 101, Season = 1, EpisodeNum = 1, ContainerExtension = "mp4" },
                    new Emby.Xtream.Plugin.Client.Models.EpisodeInfo { Id = 102, Season = 1, EpisodeNum = 2, ContainerExtension = "mp4" },
                }
            };
            var b = new Dictionary<string, List<Emby.Xtream.Plugin.Client.Models.EpisodeInfo>>
            {
                ["1"] = new List<Emby.Xtream.Plugin.Client.Models.EpisodeInfo>
                {
                    new Emby.Xtream.Plugin.Client.Models.EpisodeInfo { Id = 102, Season = 1, EpisodeNum = 2, ContainerExtension = "mp4" },
                    new Emby.Xtream.Plugin.Client.Models.EpisodeInfo { Id = 101, Season = 1, EpisodeNum = 1, ContainerExtension = "mp4" },
                }
            };
            Assert.Equal(StrmSyncService.ComputeSeriesEpisodeHash(a), StrmSyncService.ComputeSeriesEpisodeHash(b));
        }

        [Fact]
        public void ComputeSeriesEpisodeHash_DifferentEpisodeId_DifferentHash()
        {
            var a = new Dictionary<string, List<Emby.Xtream.Plugin.Client.Models.EpisodeInfo>>
            {
                ["1"] = new List<Emby.Xtream.Plugin.Client.Models.EpisodeInfo>
                {
                    new Emby.Xtream.Plugin.Client.Models.EpisodeInfo { Id = 101, Season = 1, EpisodeNum = 1, ContainerExtension = "mp4" },
                }
            };
            var b = new Dictionary<string, List<Emby.Xtream.Plugin.Client.Models.EpisodeInfo>>
            {
                ["1"] = new List<Emby.Xtream.Plugin.Client.Models.EpisodeInfo>
                {
                    new Emby.Xtream.Plugin.Client.Models.EpisodeInfo { Id = 999, Season = 1, EpisodeNum = 1, ContainerExtension = "mp4" },
                }
            };
            Assert.NotEqual(StrmSyncService.ComputeSeriesEpisodeHash(a), StrmSyncService.ComputeSeriesEpisodeHash(b));
        }

        [Fact]
        public void ComputeSeriesEpisodeHash_DifferentExtension_DifferentHash()
        {
            var a = new Dictionary<string, List<Emby.Xtream.Plugin.Client.Models.EpisodeInfo>>
            {
                ["1"] = new List<Emby.Xtream.Plugin.Client.Models.EpisodeInfo>
                {
                    new Emby.Xtream.Plugin.Client.Models.EpisodeInfo { Id = 101, Season = 1, EpisodeNum = 1, ContainerExtension = "mp4" },
                }
            };
            var b = new Dictionary<string, List<Emby.Xtream.Plugin.Client.Models.EpisodeInfo>>
            {
                ["1"] = new List<Emby.Xtream.Plugin.Client.Models.EpisodeInfo>
                {
                    new Emby.Xtream.Plugin.Client.Models.EpisodeInfo { Id = 101, Season = 1, EpisodeNum = 1, ContainerExtension = "mkv" },
                }
            };
            Assert.NotEqual(StrmSyncService.ComputeSeriesEpisodeHash(a), StrmSyncService.ComputeSeriesEpisodeHash(b));
        }

        [Fact]
        public void ComputeSeriesEpisodeHash_EmptyEpisodes_ReturnsStableHash()
        {
            var empty = new Dictionary<string, List<Emby.Xtream.Plugin.Client.Models.EpisodeInfo>>();
            var h1 = StrmSyncService.ComputeSeriesEpisodeHash(empty);
            var h2 = StrmSyncService.ComputeSeriesEpisodeHash(empty);
            Assert.Equal(h1, h2);
            Assert.NotEmpty(h1);
        }

        // -----------------------------------------------------------------
        // DeserializeEpisodeHashes / SerializeEpisodeHashes
        // -----------------------------------------------------------------

        [Fact]
        public void DeserializeEpisodeHashes_NullOrEmpty_ReturnsEmptyDict()
        {
            Assert.Empty(StrmSyncService.DeserializeEpisodeHashes(null));
            Assert.Empty(StrmSyncService.DeserializeEpisodeHashes(""));
            Assert.Empty(StrmSyncService.DeserializeEpisodeHashes("   "));
        }

        [Fact]
        public void DeserializeEpisodeHashes_InvalidJson_ReturnsEmptyDict()
        {
            Assert.Empty(StrmSyncService.DeserializeEpisodeHashes("not json"));
        }

        [Fact]
        public void DeserializeEpisodeHashes_ValidJson_ReturnsDict()
        {
            var dict = StrmSyncService.DeserializeEpisodeHashes("{\"1\":\"abc\",\"2\":\"def\"}");
            Assert.Equal(2, dict.Count);
            Assert.Equal("abc", dict["1"]);
            Assert.Equal("def", dict["2"]);
        }

        [Fact]
        public void SerializeEpisodeHashes_Roundtrip()
        {
            var original = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            original["42"] = "hashvalue";
            var json = StrmSyncService.SerializeEpisodeHashes(original);
            var deserialized = StrmSyncService.DeserializeEpisodeHashes(json);
            Assert.Equal("hashvalue", deserialized["42"]);
        }

        // -----------------------------------------------------------------
        // CheckAndUpgradeNamingVersion
        // -----------------------------------------------------------------

        [Fact]
        public void CheckAndUpgradeNamingVersion_OldVersion_ResetsTimestampsAndHashes()
        {
            var config = new PluginConfiguration
            {
                StrmNamingVersion       = 0,
                LastMovieSyncTimestamp  = 999,
                LastSeriesSyncTimestamp = 888,
                SeriesEpisodeHashesJson = "{\"1\":\"oldhash\"}",
            };
            var saveCount = 0;
            var svc = new StrmSyncService(new NullLogger());
            var upgraded = svc.CheckAndUpgradeNamingVersion(config, () => saveCount++);
            Assert.True(upgraded);
            Assert.Equal(0, config.LastMovieSyncTimestamp);
            Assert.Equal(0, config.LastSeriesSyncTimestamp);
            Assert.Equal(string.Empty, config.SeriesEpisodeHashesJson);
            Assert.Equal(StrmSyncService.CurrentStrmNamingVersion, config.StrmNamingVersion);
            Assert.Equal(1, saveCount);
        }

        [Fact]
        public void CheckAndUpgradeNamingVersion_CurrentVersion_NoChange()
        {
            var config = new PluginConfiguration
            {
                StrmNamingVersion       = StrmSyncService.CurrentStrmNamingVersion,
                LastMovieSyncTimestamp  = 999,
                LastSeriesSyncTimestamp = 888,
            };
            var saveCount = 0;
            var svc = new StrmSyncService(new NullLogger());
            var upgraded = svc.CheckAndUpgradeNamingVersion(config, () => saveCount++);
            Assert.False(upgraded);
            Assert.Equal(999, config.LastMovieSyncTimestamp);
            Assert.Equal(888, config.LastSeriesSyncTimestamp);
            Assert.Equal(0, saveCount);
        }

        [Fact]
        public void CheckAndUpgradeNamingVersion_CalledTwice_SecondIsNoOp()
        {
            var config = new PluginConfiguration { StrmNamingVersion = 0 };
            var saveCount = 0;
            var svc = new StrmSyncService(new NullLogger());
            svc.CheckAndUpgradeNamingVersion(config, () => saveCount++);
            svc.CheckAndUpgradeNamingVersion(config, () => saveCount++);
            Assert.Equal(1, saveCount);
        }

        [Fact]
        public void CheckAndUpgradeNamingVersion_NullSaveAction_DoesNotThrow()
        {
            var config = new PluginConfiguration { StrmNamingVersion = 0 };
            var svc = new StrmSyncService(new NullLogger());
            var upgraded = svc.CheckAndUpgradeNamingVersion(config, null);
            Assert.True(upgraded);
        }

        // -----------------------------------------------------------------
        // NfoWriter — movie
        // -----------------------------------------------------------------

        [Fact]
        public void NfoWriter_Movie_WritesFileWithTmdbId()
        {
            using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
            {
                var path = System.IO.Path.Combine(tmp.Path, "movie.nfo");
                NfoWriter.WriteMovieNfo(path, "The Matrix", "603", 1999);
                Assert.True(System.IO.File.Exists(path));
                var content = System.IO.File.ReadAllText(path);
                Assert.Contains("<uniqueid type=\"tmdb\" default=\"true\">603</uniqueid>", content);
            }
        }

        [Fact]
        public void NfoWriter_Movie_YearIncludedWhenProvided()
        {
            using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
            {
                var path = System.IO.Path.Combine(tmp.Path, "movie.nfo");
                NfoWriter.WriteMovieNfo(path, "The Matrix", "603", 1999);
                var content = System.IO.File.ReadAllText(path);
                Assert.Contains("<year>1999</year>", content);
            }
        }

        [Fact]
        public void NfoWriter_Movie_YearOmittedWhenNull()
        {
            using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
            {
                var path = System.IO.Path.Combine(tmp.Path, "movie.nfo");
                NfoWriter.WriteMovieNfo(path, "The Matrix", "603", null);
                var content = System.IO.File.ReadAllText(path);
                Assert.DoesNotContain("<year>", content);
            }
        }

        [Fact]
        public void NfoWriter_Movie_NoTmdbId_FileNotCreated()
        {
            using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
            {
                var path = System.IO.Path.Combine(tmp.Path, "movie.nfo");
                NfoWriter.WriteMovieNfo(path, "The Matrix", null, 1999);
                Assert.False(System.IO.File.Exists(path));
            }
        }

        [Fact]
        public void NfoWriter_Movie_EmptyTmdbId_FileNotCreated()
        {
            using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
            {
                var path = System.IO.Path.Combine(tmp.Path, "movie.nfo");
                NfoWriter.WriteMovieNfo(path, "The Matrix", "", 1999);
                Assert.False(System.IO.File.Exists(path));
            }
        }

        [Fact]
        public void NfoWriter_Movie_FileAlreadyExists_NotOverwritten()
        {
            using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
            {
                var path = System.IO.Path.Combine(tmp.Path, "movie.nfo");
                System.IO.File.WriteAllText(path, "SENTINEL");
                NfoWriter.WriteMovieNfo(path, "The Matrix", "603", 1999);
                Assert.Equal("SENTINEL", System.IO.File.ReadAllText(path));
            }
        }

        [Fact]
        public void NfoWriter_Movie_EscapesXmlSpecialChars()
        {
            using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
            {
                var path = System.IO.Path.Combine(tmp.Path, "movie.nfo");
                NfoWriter.WriteMovieNfo(path, "Tom & Jerry <1940>", "12345", null);
                var content = System.IO.File.ReadAllText(path);
                Assert.Contains("Tom &amp; Jerry &lt;1940&gt;", content);
            }
        }

        // -----------------------------------------------------------------
        // NfoWriter — show
        // -----------------------------------------------------------------

        [Fact]
        public void NfoWriter_Show_TvdbId_IsDefault()
        {
            using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
            {
                var path = System.IO.Path.Combine(tmp.Path, "tvshow.nfo");
                NfoWriter.WriteShowNfo(path, "Breaking Bad", "81189", null);
                var content = System.IO.File.ReadAllText(path);
                Assert.Contains("<uniqueid type=\"tvdb\" default=\"true\">81189</uniqueid>", content);
            }
        }

        [Fact]
        public void NfoWriter_Show_BothIds_TvdbIsDefaultTmdbIsNot()
        {
            using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
            {
                var path = System.IO.Path.Combine(tmp.Path, "tvshow.nfo");
                NfoWriter.WriteShowNfo(path, "Breaking Bad", "81189", "1396");
                var content = System.IO.File.ReadAllText(path);
                Assert.Contains("type=\"tvdb\" default=\"true\"", content);
                Assert.Contains("type=\"tmdb\">1396", content);
                Assert.DoesNotContain("type=\"tmdb\" default=\"true\"", content);
            }
        }

        [Fact]
        public void NfoWriter_Show_TmdbOnly_IsDefault()
        {
            using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
            {
                var path = System.IO.Path.Combine(tmp.Path, "tvshow.nfo");
                NfoWriter.WriteShowNfo(path, "Breaking Bad", null, "1396");
                var content = System.IO.File.ReadAllText(path);
                Assert.Contains("<uniqueid type=\"tmdb\" default=\"true\">1396</uniqueid>", content);
            }
        }

        [Fact]
        public void NfoWriter_Show_NoIds_FileNotCreated()
        {
            using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
            {
                var path = System.IO.Path.Combine(tmp.Path, "tvshow.nfo");
                NfoWriter.WriteShowNfo(path, "Breaking Bad", null, null);
                Assert.False(System.IO.File.Exists(path));
            }
        }

        [Fact]
        public void NfoWriter_Show_FileAlreadyExists_NotOverwritten()
        {
            using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
            {
                var path = System.IO.Path.Combine(tmp.Path, "tvshow.nfo");
                System.IO.File.WriteAllText(path, "SENTINEL");
                NfoWriter.WriteShowNfo(path, "Breaking Bad", "81189", null);
                Assert.Equal("SENTINEL", System.IO.File.ReadAllText(path));
            }
        }

        [Fact]
        public void NfoWriter_Show_EscapesXmlSpecialChars()
        {
            using (var tmp = new Emby.Xtream.Plugin.Tests.Fakes.TempDirectory())
            {
                var path = System.IO.Path.Combine(tmp.Path, "tvshow.nfo");
                NfoWriter.WriteShowNfo(path, "Tom & Jerry <1940>", "12345", null);
                var content = System.IO.File.ReadAllText(path);
                Assert.Contains("Tom &amp; Jerry &lt;1940&gt;", content);
            }
        }

        // -----------------------------------------------------------------
        // NullLogger (private — used by CheckAndUpgradeNamingVersion tests)
        // -----------------------------------------------------------------

        private class NullLogger : ILogger
        {
            public void Info(string message, params object[] paramList) { }
            public void Error(string message, params object[] paramList) { }
            public void Warn(string message, params object[] paramList) { }
            public void Debug(string message, params object[] paramList) { }
            public void Fatal(string message, params object[] paramList) { }
            public void FatalException(string message, Exception exception, params object[] paramList) { }
            public void ErrorException(string message, Exception exception, params object[] paramList) { }
            public void LogMultiline(string message, LogSeverity severity, StringBuilder additionalContent) { }
            public void Log(LogSeverity severity, string message, params object[] paramList) { }
            public void Info(ReadOnlyMemory<char> message) { }
            public void Error(ReadOnlyMemory<char> message) { }
            public void Warn(ReadOnlyMemory<char> message) { }
            public void Debug(ReadOnlyMemory<char> message) { }
            public void Log(LogSeverity severity, ReadOnlyMemory<char> message) { }
        }
    }
}
