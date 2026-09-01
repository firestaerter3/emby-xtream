using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Regression tests for issue #66 — Dispatcharr's <c>stream_stats.video_codec</c> field
    /// is the codec Dispatcharr *ingested* from the source, not the codec it *emits*.
    /// On a channel whose Dispatcharr stream profile transcodes video (e.g. HEVC source
    /// → H.264 output) the plugin was declaring the source codec and setting
    /// <c>SupportsProbing=false</c>, which forced Emby to apply the wrong decoder and
    /// the channel failed to play.
    ///
    /// The fix: <see cref="PluginConfiguration.DispatcharrUseStatsCodec"/> (default true).
    /// When false, the plugin ignores the VideoCodec from stream_stats, collapses
    /// <c>hasStats</c> to audio-only-or-absent, and lets Emby probe the proxy URL to
    /// discover the real output codec. The placeholder for full integration tests
    /// remains — this file exercises the static decision helper, which is enough to
    /// pin the bug-fix logic without touching the static <c>_streamStats</c> cache.
    /// </summary>
    public class XtreamTunerHostTests
    {
        [Theory]
        // (disableProbing, hasStats, expected) — pins the helper's truth table. The helper
        // is intentionally just `disableProbing || hasStats`. The decision lives in the
        // helper, but the call site wires `effectiveDisableProbing = disableProbing && useStatsCodec`
        // (issue #66) so probing is allowed when the user has opted out of stats codec.
        [InlineData(true, true, true)]
        // Audio-only path with stats (hasStats=true via isAudioOnly, even with no video).
        // Probing still suppressed — there's no video stream to discover.
        [InlineData(true, false, true)]
        // Non-Dispatcharr path (direct Xtream URL) with stats: legacy behavior suppresses
        // probing. Stats are accurate because there's no proxy transcoding.
        [InlineData(false, true, true)]
        // No stats, no Dispatcharr flag: probe, fall back to ffprobe.
        [InlineData(false, false, false)]
        public void ShouldSuppressProbing_MatchesLegacyTruthTable(bool disableProbing, bool hasStats, bool expected)
        {
            // The helper is intentionally just `disableProbing || hasStats`. This test pins
            // that identity — if anyone tries to "simplify" it later, the truth-table will
            // catch the change and they'll need to update both the test and the ADR.
            Assert.Equal(expected, XtreamTunerHost.ShouldSuppressProbing(disableProbing, hasStats));
        }

        [Fact]
        public void ShouldSuppressProbing_DisableProbingFlagAloneSuppresses()
        {
            // The Dispatcharr path always calls CreateMediaSourceInfo with
            // disableProbing=true. The fix in CreateMediaSourceInfo (#66) is to AND the
            // disableProbing flag with useStatsCodec BEFORE calling this helper, so the
            // helper itself still receives the raw disableProbing/hasStats pair. This
            // test documents that contract: when disableProbing is true, probing is
            // suppressed regardless of hasStats. The escape route for #66 lives in the
            // caller, not the helper.
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
        // Issue #66 escape: the dispatcharr-side disable flag is dropped when the user
        // has opted out of the stats codec. The helper pins the wiring logic so the
        // bug can't regress without both the helper and the test changing.
        [InlineData(true, false, false)]   // Dispatcharr + user opted out → probing allowed
        [InlineData(true, true, true)]     // Dispatcharr + default (legacy) → suppress
        [InlineData(false, false, false)]  // Direct Xtream + user opted out → probing allowed
        [InlineData(false, true, false)]   // Direct Xtream + default → probing allowed
        public void ShouldDisableProbing_Issue66DropsFlagWhenUserOptsOut(
            bool disableProbing, bool useStatsCodec, bool expected)
        {
            Assert.Equal(expected, XtreamTunerHost.ShouldDisableProbing(disableProbing, useStatsCodec));
        }

        [Fact]
        public void Issue66_EndToEnd_DispatcharrWithStatsUserOptsOutOfCodec_ProbingRuns()
        {
            // Hand-walk through the call-site logic with the reporter's exact bug inputs:
            //   - dispatcharr path (disableProbing = true)
            //   - stats present: VideoCodec = "hevc", AudioCodec = "aac" (Dispatcharr ingested HEVC)
            //   - user has set DispatcharrUseStatsCodec = false (opt out)
            // Expected: probing is allowed, so Emby probes the proxy URL and discovers
            // the real output codec (H.264 in the reporter's setup).
            bool disableProbing = true;      // XtreamTunerHost passes this when isDispatcharr=true
            bool useStatsCodec = false;      // DispatcharrUseStatsCodec = false

            // Mimic CreateMediaSourceInfo's hasStats derivation:
            // hasVideoCodecFromStats = VideoCodec != null && useStatsCodec
            //                       = true       && false        = false
            // isAudioOnly = VideoCodec == null && AudioCodec != "" (NOT this case — VideoCodec set)
            // hasStats = false || false = false
            bool hasStats = false;

            bool effective = XtreamTunerHost.ShouldDisableProbing(disableProbing, useStatsCodec);
            bool suppress = XtreamTunerHost.ShouldSuppressProbing(effective, hasStats);

            Assert.False(effective);
            Assert.False(suppress);
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
