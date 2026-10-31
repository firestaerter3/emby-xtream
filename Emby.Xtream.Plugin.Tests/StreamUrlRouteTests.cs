using System.Collections.Generic;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// A channel Dispatcharr knows can play from the proxy URL (default) or from Dispatcharr's
    /// Xtream URL, which makes Dispatcharr show the configured user instead of an anonymous
    /// client. The Xtream URL is only used when the Xtream connection is the Dispatcharr server,
    /// so the setting can never route a stream around Dispatcharr. See ADR-022.
    /// </summary>
    public class StreamUrlRouteTests
    {
        private const string Dispatcharr = "http://dispatcharr.local:9191";
        private static readonly Dictionary<int, string> Uuids = new Dictionary<int, string> { [42] = "uuid-42" };

        [Fact]
        public void Default_UsesTheProxyUrl()
        {
            var r = XtreamTunerHost.ResolveStreamUrl(Config(Dispatcharr), 42, Uuids);

            Assert.Equal(XtreamTunerHost.StreamRoute.DispatcharrProxy, r.Route);
            Assert.Equal(Dispatcharr + "/proxy/ts/stream/uuid-42?output_format=mpegts", r.Url);
        }

        [Fact]
        public void XtreamEndpoint_OnTheDispatcharrServer_UsesTheXtreamUrlAsTs()
        {
            var config = Config(Dispatcharr, "xtream");
            config.LiveTvOutputFormat = "m3u8";

            var r = XtreamTunerHost.ResolveStreamUrl(config, 42, Uuids);

            Assert.Equal(XtreamTunerHost.StreamRoute.DispatcharrXtream, r.Route);
            Assert.Equal(Dispatcharr + "/live/emby/secret/42.ts", r.Url);
            Assert.False(r.XtreamEndpointIgnored);
        }

        [Fact]
        public void XtreamEndpoint_WithTheXtreamConnectionOnAnotherServer_KeepsTheProxyUrl()
        {
            var r = XtreamTunerHost.ResolveStreamUrl(Config("http://provider.example", "xtream"), 42, Uuids);

            Assert.Equal(XtreamTunerHost.StreamRoute.DispatcharrProxy, r.Route);
            Assert.Equal(Dispatcharr + "/proxy/ts/stream/uuid-42?output_format=mpegts", r.Url);
            Assert.True(r.XtreamEndpointIgnored);
        }

        [Fact]
        public void XtreamEndpoint_WithoutAUuid_FallsBackAsBefore()
        {
            var config = Config("http://provider.example", "xtream");
            config.LiveTvOutputFormat = "m3u8";

            var r = XtreamTunerHost.ResolveStreamUrl(config, 7, Uuids);

            Assert.Equal(XtreamTunerHost.StreamRoute.DirectXtream, r.Route);
            Assert.Equal("http://provider.example/live/emby/secret/7.m3u8", r.Url);
        }

        [Fact]
        public void XtreamEndpoint_WithoutAUuid_AndFallbackOff_IsSkipped()
        {
            var config = Config(Dispatcharr, "xtream");
            config.DispatcharrFallbackToXtream = false;

            var r = XtreamTunerHost.ResolveStreamUrl(config, 7, Uuids);

            Assert.Equal(XtreamTunerHost.StreamRoute.Skipped, r.Route);
            Assert.Null(r.Url);
        }

        [Theory]
        [InlineData("http://dispatcharr.local:9191", "http://dispatcharr.local:9191/", true)]
        [InlineData("http://Dispatcharr.Local:9191", "http://dispatcharr.local:9191", true)]
        [InlineData("http://dispatcharr.local", "http://dispatcharr.local:80", true)]
        [InlineData("http://dispatcharr.local:9191", "http://dispatcharr.local:9192", false)]
        [InlineData("https://dispatcharr.local:9191", "http://dispatcharr.local:9191", false)]
        [InlineData("http://provider.example", "http://dispatcharr.local:9191", false)]
        [InlineData("", "http://dispatcharr.local:9191", false)]
        [InlineData("http://proxy.local/dispatcharr", "http://proxy.local/dispatcharr/", true)]
        [InlineData("http://proxy.local/provider", "http://proxy.local/dispatcharr", false)]
        [InlineData("http://proxy.local", "http://proxy.local/dispatcharr", false)]
        public void IsSameServer_ComparesSchemeHostPortAndPath(string a, string b, bool expected)
        {
            Assert.Equal(expected, XtreamTunerHost.IsSameServer(a, b));
        }

        private static PluginConfiguration Config(string baseUrl, string endpoint = "proxy")
        {
            return new PluginConfiguration
            {
                BaseUrl = baseUrl,
                Username = "emby",
                Password = "secret",
                EnableDispatcharr = true,
                DispatcharrUrl = Dispatcharr,
                DispatcharrStreamEndpoint = endpoint,
                DispatcharrFallbackToXtream = true,
            };
        }
    }
}
