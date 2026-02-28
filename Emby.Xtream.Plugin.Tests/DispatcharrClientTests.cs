using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class DispatcharrClientTests
    {
        private static DispatcharrClient CreateClient(HttpMessageHandler handler)
        {
            return new DispatcharrClient(new NullLogger(), handler);
        }

        // -------------------------------------------------------------------------
        // TestConnectionDetailedAsync
        // -------------------------------------------------------------------------

        [Fact]
        public async Task TestConnectionDetailed_SuccessfulLoginAndApiProbe()
        {
            var handler = new MockHandler(request =>
            {
                if (request.RequestUri.AbsolutePath.Contains("/api/accounts/token/"))
                {
                    var json = JsonSerializer.Serialize(new { access = "tok123", refresh = "ref456" });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                };
            });

            var client = CreateClient(handler);
            var (success, message) = await client.TestConnectionDetailedAsync(
                "http://localhost:8080", "admin", "pass", CancellationToken.None);

            Assert.True(success);
            Assert.Contains("JWT login OK", message);
            Assert.Contains("API access OK", message);
        }

        [Fact]
        public async Task TestConnectionDetailed_FailedLogin_Returns401()
        {
            var handler = new MockHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("Unauthorized", Encoding.UTF8, "text/plain")
                });

            var client = CreateClient(handler);
            var (success, message) = await client.TestConnectionDetailedAsync(
                "http://localhost:8080", "bad", "creds", CancellationToken.None);

            Assert.False(success);
            Assert.Contains("Login failed", message);
        }

        [Fact]
        public async Task TestConnectionDetailed_InvalidUrl_ReturnsError()
        {
            var handler = new MockHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var client = CreateClient(handler);

            var (success, message) = await client.TestConnectionDetailedAsync(
                "not-a-url", "u", "p", CancellationToken.None);

            Assert.False(success);
            Assert.Contains("Invalid URL", message);
        }

        [Fact]
        public async Task TestConnectionDetailed_EmptyUrl_ReturnsError()
        {
            var handler = new MockHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var client = CreateClient(handler);

            var (success, message) = await client.TestConnectionDetailedAsync(
                "", "u", "p", CancellationToken.None);

            Assert.False(success);
            Assert.Contains("empty", message);
        }

        [Fact]
        public async Task TestConnectionDetailed_LoginSuccessButNoToken()
        {
            var handler = new MockHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });

            var client = CreateClient(handler);
            var (success, message) = await client.TestConnectionDetailedAsync(
                "http://localhost:8080", "u", "p", CancellationToken.None);

            Assert.False(success);
            Assert.Contains("no access token", message);
        }

        [Fact]
        public async Task TestConnectionDetailed_Timeout()
        {
            var handler = new MockHandler(_ =>
                throw new TaskCanceledException("The request timed out"));

            var client = CreateClient(handler);
            var (success, message) = await client.TestConnectionDetailedAsync(
                "http://localhost:8080", "u", "p", CancellationToken.None);

            Assert.False(success);
            Assert.Contains("timed out", message);
        }

        [Fact]
        public async Task TestConnectionDetailed_ConnectionRefused()
        {
            var handler = new MockHandler(_ =>
                throw new HttpRequestException("Connection refused"));

            var client = CreateClient(handler);
            var (success, message) = await client.TestConnectionDetailedAsync(
                "http://localhost:8080", "u", "p", CancellationToken.None);

            Assert.False(success);
            Assert.Contains("Connection failed", message);
        }

        [Fact]
        public async Task TestConnectionDetailed_ApiProbeReturnsNon200_StillSuccess()
        {
            var handler = new MockHandler(request =>
            {
                if (request.RequestUri.AbsolutePath.Contains("/api/accounts/token/"))
                {
                    var json = JsonSerializer.Serialize(new { access = "tok123", refresh = "ref456" });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("Forbidden", Encoding.UTF8, "text/plain")
                };
            });

            var client = CreateClient(handler);
            var (success, message) = await client.TestConnectionDetailedAsync(
                "http://localhost:8080", "admin", "pass", CancellationToken.None);

            Assert.True(success);
            Assert.Contains("JWT login OK", message);
            Assert.Contains("API returned HTTP 403", message);
        }

        [Fact]
        public async Task TestConnectionAsync_Success()
        {
            var handler = new MockHandler(request =>
            {
                var json = JsonSerializer.Serialize(new { access = "tok123", refresh = "ref456" });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            });

            var client = CreateClient(handler);
            client.Configure("admin", "pass");
            var result = await client.TestConnectionAsync("http://localhost:8080", CancellationToken.None);

            Assert.True(result);
        }

        [Fact]
        public async Task TestConnectionAsync_Failure()
        {
            var handler = new MockHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });

            var client = CreateClient(handler);
            client.Configure("bad", "creds");
            var result = await client.TestConnectionAsync("http://localhost:8080", CancellationToken.None);

            Assert.False(result);
        }

        // -------------------------------------------------------------------------
        // GetChannelDataAsync — key strategy
        //
        // Maps are keyed by BOTH stream.StreamId (Config A: plugin → upstream Xtream
        // provider) AND ch.Id (Config B: plugin → Dispatcharr Xtream emulation).
        // -------------------------------------------------------------------------

        [Fact]
        public async Task GetChannelDataAsync_ConfigA_UuidFoundByXtreamStreamId()
        {
            // Moonshine's scenario: plugin points at the upstream Xtream provider.
            // ch.Id (5398) is Dispatcharr's internal ID.
            // stream.stream_id (69307) is the provider's stream_id — what Emby stores.
            // The UUID must be reachable via the provider stream_id (69307).
            var channelsJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = 5398,
                    uuid = "41f6df70-4531-4555-bb13-e4a39c2c242b",
                    name = "Food Network East",
                    streams = new[]
                    {
                        new
                        {
                            id = 39300,
                            name = "US : Food Network East",
                            stream_id = 69307,
                            stream_stats = (object)null
                        }
                    }
                }
            });

            var (uuidMap, _, _, _, _) = await RunGetChannelData(channelsJson);

            Assert.True(uuidMap.ContainsKey(69307), "Config A: UUID must be reachable via Xtream stream_id");
            Assert.Equal("41f6df70-4531-4555-bb13-e4a39c2c242b", uuidMap[69307]);
        }

        [Fact]
        public async Task GetChannelDataAsync_ConfigB_UuidFoundByDispatcharrChannelId()
        {
            // Config B: plugin points at Dispatcharr's Xtream emulation.
            // Dispatcharr presents ch.Id as the stream_id to Emby.
            // The UUID must be reachable via ch.Id even when a stream has no stream_id.
            var channelsJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = 42,
                    uuid = "uuid-config-b",
                    name = "Some Channel",
                    streams = new object[0]  // no embedded stream sources
                }
            });

            var (uuidMap, _, _, _, _) = await RunGetChannelData(channelsJson);

            Assert.True(uuidMap.ContainsKey(42), "Config B: UUID must be reachable via ch.Id");
            Assert.Equal("uuid-config-b", uuidMap[42]);
        }

        [Fact]
        public async Task GetChannelDataAsync_BothConfigs_UuidReachableByBothKeys()
        {
            // When ch.Id and stream.stream_id differ, both must be valid map keys.
            // This lets the same Dispatcharr data serve Config A and Config B users.
            var channelsJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = 5398,
                    uuid = "dual-key-uuid",
                    name = "Dual Key Channel",
                    streams = new[]
                    {
                        new { id = 39300, name = "src", stream_id = 69307, stream_stats = (object)null }
                    }
                }
            });

            var (uuidMap, _, _, _, _) = await RunGetChannelData(channelsJson);

            Assert.True(uuidMap.ContainsKey(69307), "Config A key (stream_id) must be present");
            Assert.True(uuidMap.ContainsKey(5398),  "Config B key (ch.Id) must be present");
            Assert.Equal("dual-key-uuid", uuidMap[69307]);
            Assert.Equal("dual-key-uuid", uuidMap[5398]);
        }

        [Fact]
        public async Task GetChannelDataAsync_ConfigA_StatsFoundByXtreamStreamId()
        {
            // Stats must also be reachable by the Xtream stream_id so that
            // GetChannelStreamMediaSources can suppress probing and set codec info.
            var channelsJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = 5398,
                    uuid = "uuid-with-stats",
                    name = "Food Network East",
                    streams = new[]
                    {
                        new
                        {
                            id = 39300,
                            name = "src",
                            stream_id = 69307,
                            stream_stats = new
                            {
                                video_codec = "h264",
                                resolution = "1920x1080",
                                source_fps = 59.94,
                                audio_codec = "ac3",
                                audio_channels = "5.1",
                                audio_bitrate = 384.0,
                                ffmpeg_output_bitrate = 3795.0
                            }
                        }
                    }
                }
            });

            var (_, statsMap, _, _, _) = await RunGetChannelData(channelsJson);

            Assert.True(statsMap.ContainsKey(69307), "Stats must be reachable via Xtream stream_id");
            var stats = statsMap[69307];
            Assert.Equal("h264", stats.VideoCodec);
            Assert.Equal("ac3", stats.AudioCodec);
            Assert.Equal("5.1", stats.AudioChannels);
            Assert.Equal(384.0, stats.AudioBitrate);
        }

        [Fact]
        public async Task GetChannelDataAsync_MultipleStreamSources_AllStreamIdsMapToSameUuid()
        {
            // A Dispatcharr channel may have multiple upstream stream sources (e.g. redundant
            // providers). Each source has a different Xtream stream_id.  All should resolve
            // to the same Dispatcharr UUID proxy.
            var channelsJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = 10,
                    uuid = "shared-uuid",
                    name = "Redundant Channel",
                    streams = new[]
                    {
                        new { id = 1, name = "primary",   stream_id = 100, stream_stats = (object)null },
                        new { id = 2, name = "secondary", stream_id = 200, stream_stats = (object)null }
                    }
                }
            });

            var (uuidMap, _, _, _, _) = await RunGetChannelData(channelsJson);

            Assert.True(uuidMap.ContainsKey(100), "First stream_id must map to UUID");
            Assert.True(uuidMap.ContainsKey(200), "Second stream_id must map to UUID");
            Assert.Equal("shared-uuid", uuidMap[100]);
            Assert.Equal("shared-uuid", uuidMap[200]);
        }

        [Fact]
        public async Task GetChannelDataAsync_NullStreamId_ChannelStillFoundByChannelId()
        {
            // A stream source with a null stream_id (e.g. custom URL without an Xtream ID)
            // contributes no stream_id key but the ch.Id fallback must still be written.
            var channelsJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = 7,
                    uuid = "uuid-null-stream-id",
                    name = "Custom URL Channel",
                    streams = new[]
                    {
                        new { id = 99, name = "custom", stream_id = (int?)null, stream_stats = (object)null }
                    }
                }
            });

            var (uuidMap, _, _, _, _) = await RunGetChannelData(channelsJson);

            Assert.True(uuidMap.ContainsKey(7), "ch.Id fallback must be written when stream_id is null");
            Assert.Equal("uuid-null-stream-id", uuidMap[7]);
            Assert.False(uuidMap.ContainsKey(99), "Dispatcharr stream source id must not leak as a map key");
        }

        [Fact]
        public async Task GetChannelDataAsync_EmptyUuid_ChannelSkipped()
        {
            // Channels without a UUID cannot be proxied; they must not appear in any map.
            var channelsJson = JsonSerializer.Serialize(new[]
            {
                new { id = 1, uuid = "",   name = "No UUID",    streams = new object[0] },
                new { id = 2, uuid = "valid-uuid", name = "OK", streams = new object[0] }
            });

            var (uuidMap, _, _, _, _) = await RunGetChannelData(channelsJson);

            Assert.False(uuidMap.ContainsKey(1), "Channel without UUID must be skipped");
            Assert.True(uuidMap.ContainsKey(2));
        }

        [Fact]
        public async Task GetChannelDataAsync_StatsFromFirstStreamWithVideoCodec()
        {
            // Only the first stream source that carries a VideoCodec contributes stats.
            // A second stream source with stats must not overwrite the first.
            var channelsJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = 3,
                    uuid = "uuid-multi-stats",
                    name = "Channel",
                    streams = new[]
                    {
                        new
                        {
                            id = 1, name = "primary", stream_id = 300,
                            stream_stats = new
                            {
                                video_codec = "h264", resolution = "1920x1080",
                                source_fps = 30.0, audio_codec = "aac",
                                ffmpeg_output_bitrate = 4000.0
                            }
                        },
                        new
                        {
                            id = 2, name = "secondary", stream_id = 301,
                            stream_stats = new
                            {
                                video_codec = "hevc", resolution = "3840x2160",
                                source_fps = 60.0, audio_codec = "ac3",
                                ffmpeg_output_bitrate = 8000.0
                            }
                        }
                    }
                }
            });

            var (_, statsMap, _, _, _) = await RunGetChannelData(channelsJson);

            // stream_id=300 gets the first (h264) stats
            Assert.True(statsMap.ContainsKey(300));
            Assert.Equal("h264", statsMap[300].VideoCodec);

            // stream_id=301 gets the second (hevc) stats — it is a distinct key
            Assert.True(statsMap.ContainsKey(301));
            Assert.Equal("hevc", statsMap[301].VideoCodec);

            // ch.Id=3 gets the first stats (not overwritten by second stream)
            Assert.True(statsMap.ContainsKey(3));
            Assert.Equal("h264", statsMap[3].VideoCodec);
        }

        [Fact]
        public async Task GetChannelDataAsync_MapsGracenoteAndTvgIdFields()
        {
            // tvg_id and tvc_guide_stationid are mapped per channel; empty/null values excluded.
            var channelsJson = JsonSerializer.Serialize(new object[]
            {
                new
                {
                    id = 10,
                    uuid = "uuid-ch10",
                    name = "ESPN",
                    tvg_id = "ESPN.us",
                    tvc_guide_stationid = "36099",
                    streams = new object[0]
                },
                new
                {
                    id = 11,
                    uuid = "uuid-ch11",
                    name = "CNN",
                    tvg_id = "CNN.us",
                    tvc_guide_stationid = (string)null,
                    streams = new object[0]
                },
                new
                {
                    id = 12,
                    uuid = "uuid-ch12",
                    name = "Mystery Channel",
                    tvg_id = (string)null,
                    tvc_guide_stationid = "",
                    streams = new object[0]
                }
            });

            var (uuidMap, _, tvgIdMap, stationIdMap, _) = await RunGetChannelData(channelsJson);

            Assert.Equal(3, uuidMap.Count);

            Assert.True(tvgIdMap.ContainsKey(10));
            Assert.Equal("ESPN.us", tvgIdMap[10]);
            Assert.True(tvgIdMap.ContainsKey(11));
            Assert.Equal("CNN.us", tvgIdMap[11]);
            Assert.False(tvgIdMap.ContainsKey(12), "Null tvg_id must not be mapped");

            Assert.True(stationIdMap.ContainsKey(10));
            Assert.Equal("36099", stationIdMap[10]);
            Assert.False(stationIdMap.ContainsKey(11), "Null station ID must not be mapped");
            Assert.False(stationIdMap.ContainsKey(12), "Empty station ID must not be mapped");
        }

        // -------------------------------------------------------------------------
        // StreamStatsInfo deserialization
        // -------------------------------------------------------------------------

        [Fact]
        public void StreamStatsInfo_DeserializesAllFields()
        {
            var json = """
                {
                    "resolution": "1920x1080",
                    "video_codec": "h264",
                    "audio_codec": "ac3",
                    "source_fps": 59.94,
                    "ffmpeg_output_bitrate": 3795.0,
                    "audio_channels": "5.1",
                    "audio_bitrate": 384.0,
                    "sample_rate": 48000
                }
                """;

            var stats = System.Text.Json.JsonSerializer.Deserialize<StreamStatsInfo>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            Assert.NotNull(stats);
            Assert.Equal("1920x1080", stats.Resolution);
            Assert.Equal("h264", stats.VideoCodec);
            Assert.Equal("ac3", stats.AudioCodec);
            Assert.Equal(59.94, stats.SourceFps);
            Assert.Equal(3795.0, stats.Bitrate);
            Assert.Equal("5.1", stats.AudioChannels);
            Assert.Equal(384.0, stats.AudioBitrate);
            Assert.Equal(48000, stats.SampleRate);
        }

        [Fact]
        public void StreamStatsInfo_MissingOptionalFields_AreNull()
        {
            // Older Dispatcharr versions or Streamflow-populated entries may lack
            // audio_channels, audio_bitrate, and sample_rate.
            var json = """{ "video_codec": "h264" }""";

            var stats = System.Text.Json.JsonSerializer.Deserialize<StreamStatsInfo>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            Assert.NotNull(stats);
            Assert.Null(stats.AudioChannels);
            Assert.Null(stats.AudioBitrate);
            Assert.Null(stats.SampleRate);
        }

        // -------------------------------------------------------------------------
        // ParseAudioChannelCount
        // -------------------------------------------------------------------------

        [Theory]
        [InlineData("mono",   1)]
        [InlineData("stereo", 2)]
        [InlineData("2.0",    2)]
        [InlineData("5.1",    6)]
        [InlineData("7.1",    8)]
        [InlineData("4.0",    4)]
        [InlineData("6.1",    7)]
        [InlineData("2",      2)]
        [InlineData("6",      6)]
        public void ParseAudioChannelCount_KnownLayouts_ReturnsCorrectCount(string layout, int expected)
        {
            Assert.Equal(expected, XtreamTunerHost.ParseAudioChannelCount(layout));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("unknown")]
        [InlineData("dolby")]
        public void ParseAudioChannelCount_UnrecognisedInput_ReturnsNull(string layout)
        {
            Assert.Null(XtreamTunerHost.ParseAudioChannelCount(layout));
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        private static async Task<(
            System.Collections.Generic.Dictionary<int, string> UuidMap,
            System.Collections.Generic.Dictionary<int, StreamStatsInfo> StatsMap,
            System.Collections.Generic.Dictionary<int, string> TvgIdMap,
            System.Collections.Generic.Dictionary<int, string> StationIdMap,
            System.Collections.Generic.HashSet<int> AllowedStreamIds)>
            RunGetChannelData(string channelsJson)
        {
            var handler = new MockHandler(request =>
            {
                if (request.RequestUri.AbsolutePath.Contains("/api/accounts/token/"))
                {
                    var json = JsonSerializer.Serialize(new { access = "tok", refresh = "ref" });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(channelsJson, Encoding.UTF8, "application/json")
                };
            });

            var client = new DispatcharrClient(new NullLogger(), handler);
            client.Configure("admin", "pass");
            return await client.GetChannelDataAsync("http://localhost:8080", CancellationToken.None);
        }

        private class MockHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

            public MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_handler(request));
            }
        }

        private class NullLogger : ILogger
        {
            public void Info(string message, params object[] paramList) { }
            public void Error(string message, params object[] paramList) { }
            public void Warn(string message, params object[] paramList) { }
            public void Debug(string message, params object[] paramList) { }
            public void Fatal(string message, params object[] paramList) { }
            public void FatalException(string message, Exception exception, params object[] paramList) { }
            public void ErrorException(string message, Exception exception, params object[] paramList) { }
            public void LogMultiline(string message, LogSeverity severity, System.Text.StringBuilder additionalContent) { }
            public void Log(LogSeverity severity, string message, params object[] paramList) { }
            public void Info(ReadOnlyMemory<char> message) { }
            public void Error(ReadOnlyMemory<char> message) { }
            public void Warn(ReadOnlyMemory<char> message) { }
            public void Debug(ReadOnlyMemory<char> message) { }
            public void Log(LogSeverity severity, ReadOnlyMemory<char> message) { }
        }
    }
}
