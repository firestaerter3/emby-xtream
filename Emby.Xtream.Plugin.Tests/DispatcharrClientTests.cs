using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client;
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
                        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                    };
                }

                // Channel probe
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
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
                    Content = new StringContent("Unauthorized", System.Text.Encoding.UTF8, "text/plain")
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
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
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
                        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                    };
                }

                // Channel probe returns 403
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("Forbidden", System.Text.Encoding.UTF8, "text/plain")
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
        public async Task GetChannelDataAsync_MapsUuidAndStatsByStreamId()
        {
            var channelsJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = 1,
                    uuid = "aaaabbbb-cccc-dddd-eeee-ffff00001111",
                    name = "Test Channel",
                    streams = new[]
                    {
                        new
                        {
                            id = 99,
                            name = "source1",
                            stream_id = 73857,
                            stream_stats = new
                            {
                                video_codec = "H264",
                                resolution = "1920x1080",
                                source_fps = (double?)25.0,
                                bitrate = (double?)4000,
                                audio_codec = (string)null
                            }
                        }
                    }
                }
            });

            var handler = new MockHandler(request =>
            {
                if (request.RequestUri.AbsolutePath.Contains("/api/accounts/token/"))
                {
                    var json = JsonSerializer.Serialize(new { access = "tok123", refresh = "ref456" });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(channelsJson, System.Text.Encoding.UTF8, "application/json")
                };
            });

            var client = CreateClient(handler);
            client.Configure("admin", "pass");
            var (uuidMap, statsMap) = await client.GetChannelDataAsync("http://localhost:8080", CancellationToken.None);

            Assert.True(uuidMap.ContainsKey(73857), "UUID map should be keyed by Xtream stream_id");
            Assert.Equal("aaaabbbb-cccc-dddd-eeee-ffff00001111", uuidMap[73857]);
            Assert.True(statsMap.ContainsKey(73857), "Stats map should be keyed by Xtream stream_id");
            Assert.Equal("H264", statsMap[73857].VideoCodec);
        }

        [Fact]
        public async Task GetChannelDataAsync_NullStreamId_FallsBackToChannelId()
        {
            // Simulates Dispatcharr < v0.19.0: stream sources have no stream_id field.
            // The fallback should map ch.Id → ch.Uuid so channels are still playable.
            var channelsJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = 1,
                    uuid = "aaaabbbb-cccc-dddd-eeee-ffff00001111",
                    name = "Old Channel",
                    streams = new[]
                    {
                        new
                        {
                            id = 99,
                            name = "source1",
                            stream_id = (int?)null,
                            stream_stats = (object)null
                        }
                    }
                }
            });

            var handler = new MockHandler(request =>
            {
                if (request.RequestUri.AbsolutePath.Contains("/api/accounts/token/"))
                {
                    var json = JsonSerializer.Serialize(new { access = "tok123", refresh = "ref456" });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(channelsJson, System.Text.Encoding.UTF8, "application/json")
                };
            });

            var client = CreateClient(handler);
            client.Configure("admin", "pass");
            var (uuidMap, statsMap) = await client.GetChannelDataAsync("http://localhost:8080", CancellationToken.None);

            Assert.True(uuidMap.ContainsKey(1), "Fallback should key UUID map by channel id");
            Assert.Equal("aaaabbbb-cccc-dddd-eeee-ffff00001111", uuidMap[1]);
            Assert.Empty(statsMap);
        }

        [Fact]
        public async Task TestConnectionAsync_Success()
        {
            var handler = new MockHandler(request =>
            {
                var json = JsonSerializer.Serialize(new { access = "tok123", refresh = "ref456" });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
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
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                });

            var client = CreateClient(handler);
            client.Configure("bad", "creds");
            var result = await client.TestConnectionAsync("http://localhost:8080", CancellationToken.None);

            Assert.False(result);
        }

        /// <summary>
        /// Minimal mock HttpMessageHandler for tests.
        /// </summary>
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

        /// <summary>
        /// Minimal ILogger implementation that does nothing.
        /// </summary>
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
