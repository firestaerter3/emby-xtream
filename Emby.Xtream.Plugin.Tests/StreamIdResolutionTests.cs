using System.Collections.Generic;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Which stream a channel plays. A channel with a Gracenote station ID carries that ID as its
    /// <c>TunerChannelId</c>, so the stream ID has to come from somewhere else. After
    /// <c>ClearCaches()</c> (Refresh Channel &amp; Guide Cache) or an Emby restart the plugin's
    /// station-to-stream map is empty until the channel list is fetched again, and the old code
    /// read the station ID as a stream ID: NPO 1 (stream 66336, station 107185) asked for stream
    /// 107185, which has no Dispatcharr UUID, and failed with "Dispatcharr proxy unavailable and
    /// fallback disabled". The Emby channel Id is built from the stream ID and Emby keeps it in
    /// its own channel cache, so it still names the right stream when the map is empty.
    /// </summary>
    public class StreamIdResolutionTests
    {
        private const string Prefix = XtreamTunerHost.TunerType + "_";
        private static readonly IReadOnlyDictionary<string, int> EmptyMap = new Dictionary<string, int>();

        [Fact]
        public void EmptyMap_StationIdChannel_ResolvesStreamFromEmbyChannelId()
        {
            // The reported case: map cleared, TunerChannelId is the Gracenote station ID.
            Assert.True(XtreamTunerHost.ResolveStreamId(Prefix + "66336", "107185", Prefix, EmptyMap, out var streamId));
            Assert.Equal(66336, streamId);
        }

        [Fact]
        public void LoadedMap_StationIdChannel_GivesSameStream()
        {
            var map = new Dictionary<string, int> { ["107185"] = 66336 };
            Assert.True(XtreamTunerHost.ResolveStreamId(Prefix + "66336", "107185", Prefix, map, out var streamId));
            Assert.Equal(66336, streamId);
        }

        [Fact]
        public void LoadedMap_IsUsedWhenEmbyChannelIdCannotBeRead()
        {
            var map = new Dictionary<string, int> { ["107185"] = 66336 };
            Assert.True(XtreamTunerHost.ResolveStreamId("someother_abc", "107185", Prefix, map, out var streamId));
            Assert.Equal(66336, streamId);
        }

        [Fact]
        public void PrefixIsMatchedIgnoringCase()
        {
            // Emby strips the prefix with OrdinalIgnoreCase, so do the same.
            Assert.True(XtreamTunerHost.ResolveStreamId(Prefix.ToUpperInvariant() + "66336", "107185", Prefix, EmptyMap, out var streamId));
            Assert.Equal(66336, streamId);
        }

        [Theory]
        [InlineData("66336")]               // no prefix
        [InlineData("xtream-tuner_abc")]    // prefix, but not a number
        [InlineData("xtream-tuner_")]       // prefix only
        [InlineData(null)]
        public void UnreadableEmbyChannelId_FallsBackToTunerChannelId(string embyChannelId)
        {
            // A channel without a station ID has the stream ID as TunerChannelId, so the old
            // last-resort parse is still right for it.
            Assert.True(XtreamTunerHost.ResolveStreamId(embyChannelId, "12345", Prefix, EmptyMap, out var streamId));
            Assert.Equal(12345, streamId);
        }

        [Fact]
        public void NothingReadable_ReturnsFalse()
        {
            Assert.False(XtreamTunerHost.ResolveStreamId("abc", "def", Prefix, EmptyMap, out _));
        }
    }
}
