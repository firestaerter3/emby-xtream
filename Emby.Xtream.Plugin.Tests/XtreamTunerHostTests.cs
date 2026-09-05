using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Xunit;

// SupportsProbing and AnalyzeDurationMs are obsolete on MediaSourceInfo but still
// functional — they are read by Emby's pipeline even when flagged obsolete. The
// main project silences CS0612 globally; mirror that here so the test can assert
// on these properties without noise.
#pragma warning disable CS0612

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
            //   - audio codec, resolution, and bitrate still flow from stats (the opt-out
            //     is ONLY about the video codec, not about all stats — see issue #66 follow-up
            //     CodeRabbit review on PR #67 head 3c6d056)
            bool disableProbing = true;      // XtreamTunerHost passes this when isDispatcharr=true
            bool useStatsCodec = false;      // DispatcharrUseStatsCodec = false
            bool statsPresent = true;        // Dispatcharr returned stats for this channel

            // hasVideoCodecFromStats = stats.VideoCodec != null && useStatsCodec = true && false = false
            // hasStats = stats != null (true) — opt-out must NOT drop resolution/audio/FPS
            bool hasVideoCodecFromStats = false;
            bool hasStats = statsPresent;

            // Probing gate (unchanged from legacy):
            bool suppressProbing = XtreamTunerHost.ShouldSuppressProbing(disableProbing, hasStats);
            Assert.True(suppressProbing); // AGENTS.md: Dispatcharr proxy URLs never probed

            // Codec declaration gate (new helper for issue #66):
            bool declareVideoCodec = XtreamTunerHost.ShouldDeclareVideoCodec(hasVideoCodecFromStats, useStatsCodec);
            Assert.False(declareVideoCodec); // issue #66: input codec must not be declared on opt-out

            // Display title falls back to height-only ("1080p") instead of claiming a codec:
            Assert.Equal("1080p", XtreamTunerHost.BuildVideoDisplayTitle(1080, null));
        }

        [Fact]
        public void Issue66_HasStats_TracksStatsPresenceNotVideoCodec_Gate()
        {
            // CodeRabbit review on PR #67 head 3c6d056 caught that the original fix's
            //   bool hasStats = hasVideoCodecFromStats || isAudioOnly;
            // drops ALL stats (audio codec, resolution, FPS, bitrate, profile, level) when
            // a video channel has DispatcharrUseStatsCodec=false. The opt-out is meant to
            // suppress only the (misleading) video codec, not every other stat the
            // Dispatcharr stats endpoint exposes.
            //
            // The fix: gate hasStats on stats presence, and let ShouldDeclareVideoCodec
            // handle the codec-suppression case independently. The corrected helper is
            // XtreamTunerHost.HasStats(stats, isAudioOnly).
            //
            // Truth table this test pins:
            //   stats != null | isAudioOnly | hasStats
            //   true          | false       | true   (video channel, legacy pass-through)
            //   true          | true        | true   (audio-only channel)
            //   true          | false       | true   (video channel, useStatsCodec=false)
            //   false         | false       | false  (no stats at all)

            // Row: video channel, opt-out (issue #66). Old gate returned false; new gate
            // returns true.
            Assert.True(XtreamTunerHost.HasStats(statsPresent: true, isAudioOnly: false));

            // Row: audio-only channel. Must keep hasStats=true.
            Assert.True(XtreamTunerHost.HasStats(statsPresent: true, isAudioOnly: true));

            // Row: no stats at all (Dispatcharr 404 or cache miss).
            Assert.False(XtreamTunerHost.HasStats(statsPresent: false, isAudioOnly: false));
            Assert.False(XtreamTunerHost.HasStats(statsPresent: false, isAudioOnly: true));
        }

        [Fact]
        public void CreateMediaSourceInfo_DispatcharrOptOut_OmitsVideoCodecRetainsAudioResolutionBitrate()
        {
            // CodeRabbit review on PR #67 head aa76f6d noted the existing suite covered the
            // static helpers but not the factory. This test constructs the exact reporter
            // scenario end-to-end: Dispatcharr path, HEVC source / H.264 output via a
            // transcoding stream profile, user opted out via DispatcharrUseStatsCodec=false.
            //
            // Expected post-fix output:
            //   - SupportsProbing = false (AGENTS.md: Dispatcharr proxy URLs never probed)
            //   - AnalyzeDurationMs = 0 (same gate)
            //   - video MediaStream.Codec = null (the escape route for #66)
            //   - video MediaStream.DisplayTitle = "1080p" (no codec appended)
            //   - audio MediaStream.Codec = "aac" (audio stays honoured regardless of opt-out)
            //   - video MediaStream.Width/Height retained (resolution stays honoured)
            //   - video MediaStream.BitRate retained (bitrate stays honoured)
            //   - video MediaStream.Profile/Level/BitDepth/RefFrames omitted: these describe
            //     the ingested codec, so they are no more trustworthy than the codec itself
            //     once the stream profile transcodes (a level number means something
            //     different in HEVC than in H.264)
            //
            // The hand-walk version above (Issue66_EndToEnd_*) covers the helpers; this
            // version pins the assembled MediaSourceInfo so future refactors of the factory
            // can't drift from the documented contract without breaking a test.

            var stats = new StreamStatsInfo
            {
                VideoCodec = "hevc",  // Dispatcharr ingested HEVC; profile transcodes to H.264
                AudioCodec = "aac",
                Resolution = "1920x1080",
                SourceFps = 50,
                Bitrate = 4500,        // 4.5 Mbps (Dispatcharr reports kbps; factory multiplies by 1000)
                AudioBitrate = 128,    // 128 kbps
                AudioChannels = "stereo",
                SampleRate = 48000,
                VideoProfile = "Main",
                VideoLevel = 41,
                VideoBitDepth = 10,
                VideoRefFrames = 4,
            };

            // Reporter scenario: Dispatcharr proxy URL, user opted out of stats codec.
            var source = XtreamTunerHost.CreateMediaSourceInfo(
                streamId: 12345,
                streamUrl: "http://dispatcharr.local/proxy/ts/stream/abc-uuid",
                stats: stats,
                disableProbing: true,        // isDispatcharr=true path
                forceAudioTranscode: false,
                userAgent: null,
                fallbackBitrateMbps: 0,
                declareDvbSubtitles: false,
                useStatsCodec: false);       // DispatcharrUseStatsCodec = false

            // Probing stays off — AGENTS.md is absolute on this for Dispatcharr.
            Assert.False(source.SupportsProbing);
            Assert.Equal(0, source.AnalyzeDurationMs);

            // Locate the video and audio streams in the factory output.
            var video = Assert.Single(source.MediaStreams, s => s.Type == MediaStreamType.Video);
            var audio = Assert.Single(source.MediaStreams, s => s.Type == MediaStreamType.Audio);

            // The escape route: codec MUST be omitted on opt-out.
            Assert.Null(video.Codec);
            // Display title falls back to height only — no codec to append.
            Assert.Equal("1080p", video.DisplayTitle);

            // Audio codec stays honoured (independent of the video opt-out).
            Assert.Equal("aac", audio.Codec);
            Assert.Equal("stereo", audio.ChannelLayout);
            Assert.Equal(2, audio.Channels);
            Assert.Equal(48000, audio.SampleRate);
            // Audio bitrate comes from AudioBitrate (128 kbps) directly.
            Assert.Equal(128000, audio.BitRate);

            // Resolution and bitrate stay honoured — the opt-out is ONLY about the codec.
            Assert.Equal(1920, video.Width);
            Assert.Equal(1080, video.Height);
            Assert.Equal(50f, video.RealFrameRate);
            // Factory converts stats.Bitrate (in kbps from Dispatcharr's ffmpeg_output_bitrate
            // field, per StreamStatsInfo.cs:19) to bps via * 1000 at XtreamTunerHost.cs:1563.
            // For a 4500 kbps stream that yields 4_500_000 bps. The test pins that conversion
            // so a future scope-creep fix that switches to * 1_000_000 is forced to update the
            // assertion alongside.
            Assert.Equal(4_500_000, video.BitRate);

            // Codec-derived attributes are gated with the codec. Declaring "Main / level 41 /
            // 10-bit / 4 ref frames" from an HEVC source on an H.264 output is the same bug
            // class as #66, one field over.
            Assert.Null(video.Profile);
            Assert.Null(video.Level);
            Assert.Null(video.BitDepth);
            Assert.Null(video.RefFrames);
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

        [Theory]
        // CodeRabbit finding on PR #67 head 8c2e46e: when BuildStreamUrl falls back to a
        // direct Xtream URL, isDispatcharr=false but _streamStats may still hold Dispatcharr
        // stats for the channel. Passing config.DispatcharrUseStatsCodec raw then strips
        // the codec hint from the direct fallback. Stats also suppress probing, so the
        // fallback loses BOTH codec and probe. ShouldUseStatsCodec gates the opt-out to
        // Dispatcharr sources only.
        [InlineData(true, true, true)]    // Dispatcharr + default: opt-out not active
        [InlineData(true, false, false)]  // Dispatcharr + opt-out: skip codec (issue #66)
        [InlineData(false, true, true)]   // Direct Xtream + default: trust codec
        [InlineData(false, false, true)]  // Direct Xtream + opt-out: opt-out N/A, trust codec
        public void ShouldUseStatsCodec_GatesOptOutToDispatcharrOnly(bool isDispatcharr, bool dispatcharrUseStatsCodec, bool expected)
        {
            Assert.Equal(expected, XtreamTunerHost.ShouldUseStatsCodec(isDispatcharr, dispatcharrUseStatsCodec));
        }

        [Fact]
        public void CreateMediaSourceInfo_DirectXtreamFallback_RetainsCodecDespiteOptOut()
        {
            // CodeRabbit finding on PR #67 head 8c2e46e regression: when BuildStreamUrl
            // returns a direct Xtream URL but Dispatcharr stats are still cached for the
            // channel, the opt-out must NOT apply — stats already suppress probing, so
            // stripping the codec hint leaves the direct fallback with neither codec nor
            // probe. ShouldUseStatsCodec(now isDispatcharr=false, configOptOut=false) = true,
            // so the caller hands useStatsCodec=true to the factory. This test pins the
            // factory output for the post-gate direct-fallback case.
            //
            // Expected output for direct Xtream fallback:
            //   - disableProbing = false (isDispatcharr=false path)
            //   - useStatsCodec  = true (gating: opt-out does not apply to direct Xtream)
            //   - Probing is suppressed via hasStats path (ShouldSuppressProbing), so
            //     SupportsProbing=false / AnalyzeDurationMs=0 still hold here.
            //   - video MediaStream.Codec declared from stats (codec hint preserved).
            //   - audio MediaStream.Codec = "aac" honoured.

            var stats = new StreamStatsInfo
            {
                VideoCodec = "hevc",
                AudioCodec = "aac",
                Resolution = "1920x1080",
                Bitrate = 4500,
                VideoProfile = "Main",
                VideoLevel = 41,
                VideoBitDepth = 10,
                VideoRefFrames = 4,
            };

            var source = XtreamTunerHost.CreateMediaSourceInfo(
                streamId: 12345,
                streamUrl: "http://xtream.example.com/live/user/pass/12345.ts", // direct Xtream fallback URL
                stats: stats,
                disableProbing: false,       // isDispatcharr=false path
                forceAudioTranscode: false,
                userAgent: null,
                fallbackBitrateMbps: 0,
                declareDvbSubtitles: false,
                useStatsCodec: true);        // post-gate: opt-out does NOT apply to direct Xtream

            // Stats are present, so probing is still suppressed — but via the hasStats
            // branch of ShouldSuppressProbing, not via the Dispatcharr flag.
            Assert.False(source.SupportsProbing);
            Assert.Equal(0, source.AnalyzeDurationMs);

            var video = Assert.Single(source.MediaStreams, s => s.Type == MediaStreamType.Video);
            var audio = Assert.Single(source.MediaStreams, s => s.Type == MediaStreamType.Audio);

            // Direct Xtream fallback: codec hint preserved, NOT stripped by the opt-out.
            Assert.Equal("hevc", video.Codec);
            Assert.Equal("1080p HEVC", video.DisplayTitle);

            // Audio codec honoured.
            Assert.Equal("aac", audio.Codec);

            // The other side of the codec-derived gate: with the codec trusted, profile,
            // level, bit depth and reference frames are declared alongside it.
            Assert.Equal("Main", video.Profile);
            Assert.Equal(41.0, video.Level);
            Assert.Equal(10, video.BitDepth);
            Assert.Equal(4, video.RefFrames);
        }
    }
}
