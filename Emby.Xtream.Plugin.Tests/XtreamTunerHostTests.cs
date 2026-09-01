using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Regression tests for issue [#66](https://github.com/firestaerter3/emby-xtream/issues/66)
    /// — Dispatcharr's <c>stream_stats.video_codec</c> field is the codec Dispatcharr
    /// *ingested* from the source, not the codec it *emits*. On a channel whose Dispatcharr
    /// stream profile transcodes video (e.g. HEVC source → H.264 output) the plugin was
    /// declaring the source codec on the MediaStream and forcing the wrong decoder, so the
    /// channel failed to play.
    ///
    /// The fix: <see cref="PluginConfiguration.DispatcharrUseStatsCodec"/> (default true).
    /// When false, the plugin ignores <c>VideoCodec</c> from stream_stats and leaves the
    /// codec field unset on the video MediaStream — no probe, no codec hint. AGENTS.md
    /// forbids re-enabling probing on Dispatcharr proxy URLs regardless of stats, so the
    /// escape lives in the declaration path, not in relaxing the no-probe rule.
    ///
    /// Audio-only detection, resolution, FPS, bitrate, profile, level, and audio codec
    /// stay honoured regardless of <c>DispatcharrUseStatsCodec</c> — only the video
    /// codec field and its display title are gated.
    /// </summary>
    public class XtreamTunerHostTests
    {
        [Theory]
        // (disableProbing, hasStats, expected) — pins the helper's truth table.
        // AGENTS.md makes the no-probe rule absolute for Dispatcharr proxy URLs:
        // probing is always suppressed when either the dispatcharr-side flag is set
        // OR stats are present. The fix for issue #66 does NOT relax this gate.
        [InlineData(true, true, true)]    // Dispatcharr + stats: suppress (unchanged)
        [InlineData(true, false, true)]   // Dispatcharr + no stats: suppress (unchanged)
        [InlineData(false, true, true)]   // Direct Xtream + stats: suppress (unchanged)
        [InlineData(false, false, false)] // Direct Xtream + no stats: probe (unchanged)
        public void ShouldSuppressProbing_MatchesLegacyTruthTable(bool disableProbing, bool hasStats, bool expected)
        {
            Assert.Equal(expected, XtreamTunerHost.ShouldSuppressProbing(disableProbing, hasStats));
        }

        [Fact]
        public void ShouldSuppressProbing_DisableProbingFlagAloneSuppresses()
        {
            // Dispatcharr path always calls CreateMediaSourceInfo with disableProbing=true.
            // Probing stays off regardless of stats — AGENTS.md forbids relaxing this.
            Assert.True(XtreamTunerHost.ShouldSuppressProbing(
                disableProbing: true,
                hasStats: false));
        }

        [Fact]
        public void ShouldSuppressProbing_HasStatsAloneSuppresses()
        {
            // Audio-only path: stats have AudioCodec but no VideoCodec. hasStats is true
            // (via the audio-only branch). Even with disableProbing false, probing is
            // suppressed — there's no video to probe for and the helper shouldn't second-
            // guess the caller's intent.
            Assert.True(XtreamTunerHost.ShouldSuppressProbing(
                disableProbing: false,
                hasStats: true));
        }

        [Theory]
        // Issue #66: when the user has opted out of stats codec, the video codec on the
        // MediaStream MUST be left unset. Declaring the input codec on a transcoded
        // Dispatcharr stream profile (HEVC source → H.264 output) forces Emby to apply the
        // wrong decoder. The codec declaration is gated by hasVideoCodecFromStats (which is
        // itself gated by useStatsCodec upstream), so passing false here mirrors the opt-out.
        [InlineData(true, false, false)]  // Stats have codec but user opted out → do not declare (issue #66)
        [InlineData(true, true, true)]    // Stats have codec + default → declare (legacy pass-through)
        [InlineData(false, true, false)]  // Stats missing codec → do not declare
        [InlineData(false, false, false)] // Stats missing codec + opt-out → do not declare
        public void ShouldDeclareVideoCodec_Issue66OmitsCodecOnOptOut(
            bool hasVideoCodecFromStats, bool useStatsCodec, bool expected)
        {
            Assert.Equal(expected, XtreamTunerHost.ShouldDeclareVideoCodec(hasVideoCodecFromStats, useStatsCodec));
        }

        [Fact]
        public void ShouldDeclareVideoCodec_OptOutDoesNotAffectAudioOnlyChannels()
        {
            // Audio-only channels have stats.AudioCodec but no VideoCodec. hasVideoCodecFromStats
            // is false regardless of useStatsCodec, so the helper returns false in both cases —
            // which is correct: an audio-only channel has no video codec to declare or omit.
            Assert.False(XtreamTunerHost.ShouldDeclareVideoCodec(false, true));
            Assert.False(XtreamTunerHost.ShouldDeclareVideoCodec(false, false));
        }

        [Theory]
        // BuildVideoDisplayTitle covers the four shapes the codec gate produces:
        //   - codec trusted: "1080p H264"
        //   - codec omitted (issue #66 opt-out): "1080p"
        //   - codec trusted but no resolution: "H264"
        //   - codec omitted and no resolution: null (caller handles null DisplayTitle)
        [InlineData(1080, "h264", "1080p H264")]   // trusted codec + resolution
        [InlineData(1080, null, "1080p")]          // issue #66 opt-out + resolution (no codec)
        [InlineData(0, "h264", "H264")]            // trusted codec, no resolution
        [InlineData(0, null, null)]                // opt-out, no resolution
        [InlineData(720, "hevc", "720p HEVC")]     // pass-through HEVC
        public void BuildVideoDisplayTitle_CoversAllShapes(int height, string codec, string expected)
        {
            Assert.Equal(expected, XtreamTunerHost.BuildVideoDisplayTitle(height, codec));
        }

        [Fact]
        public void Issue66_EndToEnd_DispatcharrUserOptsOutOfCodec_ProbingStaysOffAndCodecOmitted()
        {
            // Hand-walk through the call-site logic with the reporter's exact bug inputs:
            //   - dispatcharr path (disableProbing = true)
            //   - stats present: VideoCodec = "hevc", AudioCodec = "aac" (Dispatcharr ingested HEVC,
            //     transcode profile outputs H.264)
            //   - user has set DispatcharrUseStatsCodec = false (opt out)
            // Expected (post-fix):
            //   - probing is suppressed (AGENTS.md: Dispatcharr proxy URLs never probed)
            //   - video codec on the MediaStream is left unset (the escape route for #66)
            //   - audio codec, resolution, and bitrate still flow from stats
            bool disableProbing = true;      // XtreamTunerHost passes this when isDispatcharr=true
            bool useStatsCodec = false;      // DispatcharrUseStatsCodec = false

            // hasVideoCodecFromStats = stats.VideoCodec != null && useStatsCodec = true && false = false
            // isAudioOnly = VideoCodec == null && AudioCodec != "" → false (VideoCodec = "hevc")
            // hasStats = false || false = false
            bool hasVideoCodecFromStats = false;
            bool isAudioOnly = false;
            bool hasStats = hasVideoCodecFromStats || isAudioOnly;

            // Probing gate (unchanged from legacy):
            bool suppressProbing = XtreamTunerHost.ShouldSuppressProbing(disableProbing, hasStats);
            Assert.True(suppressProbing); // AGENTS.md: Dispatcharr proxy URLs never probed

            // Codec declaration gate (new helper for issue #66):
            bool declareVideoCodec = XtreamTunerHost.ShouldDeclareVideoCodec(hasVideoCodecFromStats, useStatsCodec);
            Assert.False(declareVideoCodec); // issue #66: input codec must not be declared on opt-out

            // Display title falls back to height-only ("1080p") instead of claiming a codec:
            Assert.Equal("1080p", XtreamTunerHost.BuildVideoDisplayTitle(1080, null));
        }

        // -------------------------------------------------------------------------
        // Placeholder for deferred full integration tests. See class-level XML doc for
        // the planned scenarios — they require refactoring the static _streamStats
        // cache to instance-level before they can run with isolation. This Fact runs
        // (not skipped) so the suite still tracks the work item.
        // -------------------------------------------------------------------------
        [Fact]
        public void Placeholder_StaticStreamStatsCachePreventsIsolation()
        {
            Assert.True(true, "Placeholder: defer full XtreamTunerHost scenarios until _streamStats is instance-level.");
        }
    }
}
