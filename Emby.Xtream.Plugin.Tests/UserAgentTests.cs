using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// #104: a panel answered the plugin's <c>player_api.php</c> request with 404 while serving
    /// the same URL to curl from the same container with 200. The plugin sent no User-Agent at
    /// all in that setup, because the header was only added when the user filled the field in.
    /// A request without a User-Agent is not an unusual request, and a panel is free to answer it
    /// with a 404 rather than the JSON body.
    ///
    /// These lock in the default. The configured value still wins, since a provider that requires
    /// a specific User-Agent is the case the field exists for.
    /// </summary>
    public class UserAgentTests
    {
        /// <summary>
        /// Reads the header as it will go on the wire. <c>GetValues("User-Agent")</c> splits on
        /// commas, so it would report one header containing a comma as two entries. The raw string
        /// is what actually goes out.
        /// </summary>
        private static string HeaderValue(HttpClient client)
        {
            return client.DefaultRequestHeaders.UserAgent.ToString() ?? string.Empty;
        }

        [Fact]
        public void BlankConfig_StillSendsTheDefaultUserAgent()
        {
            using (var client = new HttpClient())
            {
                XtreamUserAgent.Apply(client, null);

                Assert.Equal(XtreamUserAgent.Default, HeaderValue(client));
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void WhitespaceOnlyConfig_CountsAsBlank(string configured)
        {
            // A field holding only spaces is what a config page leaves behind after a clear, and
            // it must not resurrect the empty-header case.
            using (var client = new HttpClient())
            {
                XtreamUserAgent.Apply(client, configured);

                Assert.Equal(XtreamUserAgent.Default, HeaderValue(client));
            }
        }

        [Fact]
        public void ConfiguredUserAgent_WinsAndIsSentUnchanged()
        {
            using (var client = new HttpClient())
            {
                XtreamUserAgent.Apply(client, "MyProviderClient/2.3 (build 77)");

                Assert.Equal("MyProviderClient/2.3 (build 77)", HeaderValue(client));
            }
        }

        [Fact]
        public void ClearingTheField_RestoresTheDefaultRatherThanLeavingTheOldHeader()
        {
            // The shared sync client is reused for the life of the plugin, so a second Apply after
            // the user clears the field has to replace the header, not add to it.
            using (var client = new HttpClient())
            {
                XtreamUserAgent.Apply(client, "MyProviderClient/2.3");
                XtreamUserAgent.Apply(client, null);

                var values = client.DefaultRequestHeaders.GetValues("User-Agent").ToArray();
                Assert.Single(values);
                Assert.Equal(XtreamUserAgent.Default, values[0]);
                Assert.DoesNotContain("MyProviderClient", HeaderValue(client));
            }
        }

        [Fact]
        public void Default_IdentifiesThePluginAndTheVersion()
        {
            Assert.StartsWith("Emby-Xtream-Plugin/", XtreamUserAgent.Default);

            var version = typeof(XtreamUserAgent).Assembly.GetName().Version;
            Assert.Equal("Emby-Xtream-Plugin/" + (version == null ? "1.0" : version.ToString(3)),
                XtreamUserAgent.Default);
        }

        [Fact]
        public void Apply_ToNullClient_IsANoOp()
        {
            // Emby can construct a service before the plugin instance exists; the callers guard
            // against that, and Apply must not turn it into a NullReferenceException.
            XtreamUserAgent.Apply(null, "whatever");
        }

        [Fact]
        public void PluginHttpClient_CarriesTheDefaultWhenNothingIsConfigured()
        {
            // The path #104 actually took: Plugin.CreateHttpClient with no configured value. The
            // plugin instance is null under test, which is the same null-safe path production
            // takes before the plugin finishes initialising.
            using (var client = Plugin.CreateHttpClient())
            {
                Assert.Equal(XtreamUserAgent.Default, HeaderValue(client));
            }
        }

        [Fact]
        public async Task PanelThat404sAHeaderlessRequest_NowGetsThrough()
        {
            // Stands in for the panel in #104: it serves curl with 200 and the JSON body, and
            // answers a request carrying no User-Agent with 404. The connection test is the
            // request that failed, so this drives the real call rather than asserting on a header
            // collection.
            const string json = "{\"user_info\":{\"auth\":1,\"status\":\"Active\"}}";
            var panel = new UserAgentCheckingPanel(json);
            using (var client = new HttpClient(panel))
            {
                XtreamUserAgent.Apply(client, null);

                var (success, message) = await XtreamConnectionTester.RunAsync(
                    "http://panel.invalid:8080", "alice", "secret", client, CancellationToken.None);

                Assert.True(success, message);
                Assert.Contains("Connected as alice", message);
            }

            Assert.Equal(XtreamUserAgent.Default, panel.LastUserAgent);
        }

        /// <summary>Answers 200 to a request with a User-Agent and 404 to one without.</summary>
        private sealed class UserAgentCheckingPanel : HttpMessageHandler
        {
            private readonly string _json;

            public UserAgentCheckingPanel(string json)
            {
                _json = json;
            }

            public string LastUserAgent { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastUserAgent = string.Join(", ", request.Headers.UserAgent.Select(p => p.ToString()));

                var hasUserAgent = request.Headers.UserAgent.Any();

                return Task.FromResult(new HttpResponseMessage(
                    hasUserAgent ? HttpStatusCode.OK : HttpStatusCode.NotFound)
                {
                    Content = new StringContent(_json, Encoding.UTF8, "application/json")
                });
            }
        }
    }
}