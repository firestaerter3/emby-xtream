using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class XtreamConnectionTesterTests
    {
        private static HttpClient ClientReturning(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new HttpClient(new MockHandler(_ =>
                new HttpResponseMessage(status)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                }));
        }

        private static Task<(bool Success, string Message)> Run(HttpClient client,
            string baseUrl = "http://server:8080", string user = "alice", string pass = "secret")
        {
            return XtreamConnectionTester.RunAsync(baseUrl, user, pass, client, CancellationToken.None);
        }

        // -------------------------------------------------------------------------
        // Authenticated successfully
        // -------------------------------------------------------------------------

        [Fact]
        public async Task Auth1_WithActiveAndMaxConnections_ReportsStreamCount()
        {
            var json = JsonSerializer.Serialize(new
            {
                user_info = new { auth = 1, status = "Active", active_cons = 2, max_connections = 5 }
            });

            var (success, message) = await Run(ClientReturning(json));

            Assert.True(success);
            Assert.Contains("Connected as alice", message);
            Assert.Contains("status: Active", message);
            Assert.Contains("2/5 active streams", message);
        }

        [Fact]
        public async Task Auth_AsString_IsTreatedAsAuthenticated()
        {
            // Some Xtream servers return auth as the string "1" rather than the number 1.
            var json = JsonSerializer.Serialize(new
            {
                user_info = new { auth = "1", status = "Active" }
            });

            var (success, message) = await Run(ClientReturning(json));

            Assert.True(success);
            Assert.Contains("Connected as alice", message);
        }

        [Fact]
        public async Task Auth1_WithoutActiveConnections_OmitsStreamCount()
        {
            var json = JsonSerializer.Serialize(new
            {
                user_info = new { auth = 1, status = "Active" }
            });

            var (success, message) = await Run(ClientReturning(json));

            Assert.True(success);
            Assert.DoesNotContain("active streams", message);
        }

        [Fact]
        public async Task Auth1_ActiveWithoutMax_OmitsSlash()
        {
            var json = JsonSerializer.Serialize(new
            {
                user_info = new { auth = 1, status = "Active", active_cons = 3 }
            });

            var (success, message) = await Run(ClientReturning(json));

            Assert.True(success);
            Assert.Contains("3 active streams", message);
            Assert.DoesNotContain("/", message.Substring(message.IndexOf("3 active", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task Auth1_MissingStatus_FallsBackToUnknown()
        {
            var json = JsonSerializer.Serialize(new { user_info = new { auth = 1 } });

            var (success, message) = await Run(ClientReturning(json));

            Assert.True(success);
            Assert.Contains("status: unknown", message);
        }

        // -------------------------------------------------------------------------
        // Authentication rejected
        // -------------------------------------------------------------------------

        [Fact]
        public async Task Auth0_ReportsAccountStatus()
        {
            var json = JsonSerializer.Serialize(new
            {
                user_info = new { auth = 0, status = "Expired" }
            });

            var (success, message) = await Run(ClientReturning(json));

            Assert.False(success);
            Assert.Contains("Authentication failed", message);
            Assert.Contains("'Expired'", message);
        }

        [Fact]
        public async Task NoAuthProperty_IsTreatedAsFailedAuth()
        {
            var json = JsonSerializer.Serialize(new { user_info = new { status = "Active" } });

            var (success, message) = await Run(ClientReturning(json));

            Assert.False(success);
            Assert.Contains("Authentication failed", message);
        }

        // -------------------------------------------------------------------------
        // Malformed / unexpected responses
        // -------------------------------------------------------------------------

        [Fact]
        public async Task NoUserInfo_ReportsUnexpectedFormat()
        {
            var json = JsonSerializer.Serialize(new { something_else = true });

            var (success, message) = await Run(ClientReturning(json));

            Assert.False(success);
            Assert.Contains("unexpected format", message);
        }

        [Fact]
        public async Task NonJsonBody_ReportsInvalidResponse()
        {
            var (success, message) = await Run(ClientReturning("<html>not json</html>"));

            Assert.False(success);
            Assert.Contains("valid Xtream API response", message);
        }

        // -------------------------------------------------------------------------
        // Input validation
        // -------------------------------------------------------------------------

        [Theory]
        [InlineData("", "user", "pass")]
        [InlineData("http://server", "", "pass")]
        [InlineData("http://server", "user", "")]
        public async Task MissingCredential_ReturnsPromptWithoutCallingServer(string url, string user, string pass)
        {
            var called = false;
            var client = new HttpClient(new MockHandler(_ =>
            {
                called = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));

            var (success, message) = await XtreamConnectionTester.RunAsync(
                url, user, pass, client, CancellationToken.None);

            Assert.False(success);
            Assert.Contains("Please enter server URL", message);
            Assert.False(called, "No HTTP request should be made when a field is missing");
        }

        [Fact]
        public async Task TrailingSlashInBaseUrl_DoesNotProduceDoubleSlash()
        {
            string requestedPath = null;
            var client = new HttpClient(new MockHandler(req =>
            {
                requestedPath = req.RequestUri.AbsolutePath;
                var json = JsonSerializer.Serialize(new { user_info = new { auth = 1, status = "Active" } });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }));

            await XtreamConnectionTester.RunAsync(
                "http://server:8080/", "alice", "secret", client, CancellationToken.None);

            Assert.Equal("/player_api.php", requestedPath);
        }

        // -------------------------------------------------------------------------
        // Network failures
        // -------------------------------------------------------------------------

        [Fact]
        public async Task ConnectionRefused_ReportsConnectionFailed()
        {
            var client = new HttpClient(new MockHandler(_ =>
                throw new HttpRequestException("Connection refused")));

            var (success, message) = await Run(client);

            Assert.False(success);
            Assert.Contains("Connection failed", message);
        }

        [Fact]
        public async Task HttpErrorStatus_ReportsConnectionFailed()
        {
            // GetStringAsync throws on a non-success status; the tester surfaces it as a failure.
            var (success, message) = await Run(ClientReturning("nope", HttpStatusCode.InternalServerError));

            Assert.False(success);
            Assert.Contains("Connection failed", message);
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
    }
}
