using System.Collections.Generic;
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
    /// The fix reads the channel's Dispatcharr stream profile, which states what the proxy
    /// actually emits, and declares that. Probing stays off for Dispatcharr proxy URLs
    /// (AGENTS.md is absolute on this), and the declared codec is never null: the no-stats
    /// branch documents that Emby's RecordingRequiresEncoding dereferences the field.
    /// <see cref="PluginConfiguration.DispatcharrVideoCodecSource"/> can override the
    /// automatic answer for installs where the profile cannot be read.
    /// </summary>
    public class XtreamTunerHostTests
    {
        // ---------------------------------------------------------------------
        // Probing gate — unchanged by the #66 fix, pinned so it stays that way.
        // ---------------------------------------------------------------------

        [Theory]
        // (disableProbing, hasStats, expected) — pins the helper's truth table.
        // AGENTS.md makes the no-probe rule absolute for Dispatcharr proxy URLs:
        // probing is always suppressed when either the dispatcharr-side flag is set
        // OR stats are present. The fix for issue #66 does NOT relax this gate.
        [InlineData(true, true, true)]    // Dispatcharr + stats: suppress
        [InlineData(true, false, true)]   // Dispatcharr + no stats: suppress
        [InlineData(false, true, true)]   // Direct Xtream + stats: suppress
        [InlineData(false, false, false)] // Direct Xtream + no stats: probe
        public void ShouldSuppressProbing_MatchesLegacyTruthTable(bool disableProbing, bool hasStats, bool expected)
        {
            Assert.Equal(expected, XtreamTunerHost.ShouldSuppressProbing(disableProbing, hasStats));
        }

        [Fact]
        public void HasStats_TracksStatsPresenceNotVideoCodec()
        {
            // CodeRabbit review on PR #67 head 3c6d056 caught that gating hasStats on codec
            // trust drops every other stat (resolution, FPS, bitrate, audio) for a video
            // channel whose codec is not being taken from stats. Stats presence and codec
            // trust are separate questions.
            Assert.True(XtreamTunerHost.HasStats(statsPresent: true, isAudioOnly: false));
            Assert.True(XtreamTunerHost.HasStats(statsPresent: true, isAudioOnly: true));
            Assert.False(XtreamTunerHost.HasStats(statsPresent: false, isAudioOnly: false));
            Assert.False(XtreamTunerHost.HasStats(statsPresent: false, isAudioOnly: true));
        }

        // ---------------------------------------------------------------------
        // Reading the output codec out of a Dispatcharr stream profile.
        // ---------------------------------------------------------------------

        [Theory]
        // The reporter's own profile shape: NVENC H.264 output from whatever comes in.
        [InlineData("ffmpeg", "-i {streamUrl} -c:v h264_nvenc -c:a aac -f mpegts pipe:1", "h264")]
        [InlineData("ffmpeg", "-i {streamUrl} -c:v libx264 -f mpegts pipe:1", "h264")]
        [InlineData("ffmpeg", "-i {streamUrl} -c:v libx265 -f mpegts pipe:1", "hevc")]
        [InlineData("ffmpeg", "-i {streamUrl} -c:v hevc_qsv -f mpegts pipe:1", "hevc")]
        [InlineData("ffmpeg", "-i {streamUrl} -c:v:0 h264_vaapi -f mpegts pipe:1", "h264")]
        [InlineData("ffmpeg", "-i {streamUrl} -vcodec libx264 -f mpegts pipe:1", "h264")]
        [InlineData("ffmpeg", "-i {streamUrl} -codec:v mpeg2video -f mpegts pipe:1", "mpeg2video")]
        // Pass-through: the ingested codec is also the emitted one, so the profile has no
        // answer to add and the caller falls back to stream_stats.
        [InlineData("ffmpeg", "-i {streamUrl} -c:v copy -c:a aac -f mpegts pipe:1", null)]
        [InlineData("ffmpeg", "-i {streamUrl} -c copy -f mpegts pipe:1", null)]
        // An encoder we don't recognise must read as "no answer". Handing Emby "av1_nvenc"
        // as a codec name is worse than handing it nothing, because Emby will try to use it.
        [InlineData("ffmpeg", "-i {streamUrl} -c:v av1_nvenc -f mpegts pipe:1", null)]
        // Non-ffmpeg profiles (the built-in redirect/proxy profiles, streamlink, scripts)
        // never re-encode.
        [InlineData("streamlink", "{streamUrl} best -O", null)]
        [InlineData("redirect", "{streamUrl}", null)]
        [InlineData("ffmpeg", "", null)]
        [InlineData("ffmpeg", null, null)]
        public void StreamProfileCodec_ParsesOutputCodec(string command, string parameters, string expected)
        {
            Assert.Equal(expected, StreamProfileCodec.Parse(command, parameters));
        }

        [Theory]
        // Codex review on PR #67: profiles are written by hand in Dispatcharr's UI, so the
        // encoder turns up quoted, and the flag turns up joined to its value with "=".
        // Both used to read as "no answer", which left the transcoding channel broken.
        [InlineData("-i {streamUrl} -c:v \"h264_nvenc\" -f mpegts pipe:1", "h264")]
        [InlineData("-i {streamUrl} -c:v 'hevc_nvenc' -f mpegts pipe:1", "hevc")]
        [InlineData("-i {streamUrl} -c:v=h264_nvenc -f mpegts pipe:1", "h264")]
        [InlineData("-i {streamUrl} -vcodec=libx265 -f mpegts pipe:1", "hevc")]
        [InlineData("-i {streamUrl} -c:v=\"h264_qsv\" -f mpegts pipe:1", "h264")]
        [InlineData("-i {streamUrl} -c:v=copy -f mpegts pipe:1", null)]
        public void StreamProfileCodec_ParsesQuotedAndEqualsForms(string parameters, string expected)
        {
            Assert.Equal(expected, StreamProfileCodec.Parse("ffmpeg", parameters));
        }

        [Fact]
        public void StreamProfileCodec_LastCodecFlagWins()
        {
            // ffmpeg applies the last option for a given stream, so a hand-tuned profile that
            // sets the codec twice must resolve to the later one, in both orders.
            Assert.Equal("h264", StreamProfileCodec.Parse(
                "ffmpeg", "-i {streamUrl} -c:v copy -c:v h264_nvenc -f mpegts pipe:1"));
            Assert.Null(StreamProfileCodec.Parse(
                "ffmpeg", "-i {streamUrl} -c:v h264_nvenc -c:v copy -f mpegts pipe:1"));
        }

        [Fact]
        public void StreamProfileCodec_BuildCodecMap_ResolvesOwnProfileAndServerDefault()
        {
            var profiles = new List<DispatcharrStreamProfile>
            {
                new DispatcharrStreamProfile { Id = 1, Name = "Proxy", Command = "ffmpeg", Parameters = "-i {streamUrl} -c copy -f mpegts pipe:1" },
                new DispatcharrStreamProfile { Id = 2, Name = "NVENC H.264", Command = "ffmpeg", Parameters = "-i {streamUrl} -c:v h264_nvenc -c:a aac -f mpegts pipe:1" },
                new DispatcharrStreamProfile { Id = 3, Name = "NVENC HEVC", Command = "ffmpeg", Parameters = "-i {streamUrl} -c:v hevc_nvenc -f mpegts pipe:1" },
            };

            var assignments = new Dictionary<int, int>
            {
                { 100, 2 },   // channel with its own transcoding profile
                { 200, 1 },   // channel on the pass-through profile
                { 300, 0 },   // no profile of its own -> server default (3)
                { 400, 99 },  // profile that no longer exists
            };

            var map = StreamProfileCodec.BuildCodecMap(assignments, profiles, defaultProfileId: 3);

            Assert.Equal("h264", map[100]);
            // Pass-through profiles contribute nothing: the reported codec is already right.
            Assert.False(map.ContainsKey(200));
            Assert.Equal("hevc", map[300]);
            // An unresolvable profile must leave the stream absent rather than guess.
            Assert.False(map.ContainsKey(400));
        }

        [Fact]
        public void StreamProfileCodec_BuildCodecMap_WithoutServerDefault_LeavesUnassignedStreamsOut()
        {
            var profiles = new List<DispatcharrStreamProfile>
            {
                new DispatcharrStreamProfile { Id = 2, Command = "ffmpeg", Parameters = "-c:v h264_nvenc" },
            };
            var assignments = new Dictionary<int, int> { { 100, 0 } };

            var map = StreamProfileCodec.BuildCodecMap(assignments, profiles, defaultProfileId: null);

            Assert.Empty(map);
        }

        // ---------------------------------------------------------------------
        // Which codec gets declared.
        // ---------------------------------------------------------------------

        [Theory]
        // (isDispatcharr, codecSource, profileCodec, statsCodec, expected)
        // Automatic: the profile wins when it has an answer. This is issue #66 — the
        // reporter's channel ingests HEVC and the profile emits H.264.
        [InlineData(true, "auto", "h264", "hevc", "h264")]
        // Automatic with a pass-through profile: nothing from the profile, so the reported
        // codec stands, which is exactly the behaviour before the fix.
        [InlineData(true, "auto", null, "hevc", "hevc")]
        // An empty source string is what a config XML written before this setting existed
        // deserializes to, and must behave as automatic.
        [InlineData(true, "", "h264", "hevc", "h264")]
        [InlineData(true, null, "h264", "hevc", "h264")]
        // Escape hatch: trust what Dispatcharr reports even when a profile was read.
        [InlineData(true, "stats", "h264", "hevc", "hevc")]
        // Manual overrides for installs where the profile endpoint is unreachable.
        [InlineData(true, "h264", null, "hevc", "h264")]
        [InlineData(true, "hevc", "h264", "h264", "hevc")]
        // Direct Xtream URLs carry the provider's own bytes, so the reported codec is right
        // and the Dispatcharr setting does not apply to them.
        [InlineData(false, "auto", "h264", "hevc", "hevc")]
        [InlineData(false, "h264", "h264", "hevc", "hevc")]
        // Nothing known at all: null, which the factory turns into its H.264 fallback.
        [InlineData(true, "auto", null, null, null)]
        public void ResolveVideoCodec_MatchesTruthTable(
            bool isDispatcharr, string codecSource, string profileCodec, string statsCodec, string expected)
        {
            Assert.Equal(expected, XtreamTunerHost.ResolveVideoCodec(
                isDispatcharr, codecSource, profileCodec, statsCodec));
        }

        [Theory]
        // Profile, level, bit depth and reference frames describe the ingested codec, so they
        // only survive when that is also the codec being declared.
        [InlineData("h264", "h264", true)]
        [InlineData("H264", "h264", true)]   // comparison is case-insensitive
        [InlineData("h264", "hevc", false)]  // transcoded: source attributes do not apply
        [InlineData("h264", null, false)]
        [InlineData(null, "h264", false)]
        [InlineData("", "h264", false)]
        public void ShouldDeclareCodecAttributes_OnlyWhenDeclaredCodecIsTheStatsCodec(
            string declaredCodec, string statsCodec, bool expected)
        {
            Assert.Equal(expected, XtreamTunerHost.ShouldDeclareCodecAttributes(declaredCodec, statsCodec));
        }

        [Theory]
        [InlineData(1080, "h264", "1080p H264")]
        [InlineData(720, "hevc", "720p HEVC")]
        [InlineData(0, "h264", "H264")]      // stats with no parsable resolution
        [InlineData(1080, null, "1080p")]    // defensive: the stats path never passes null now
        [InlineData(0, null, null)]
        public void BuildVideoDisplayTitle_CoversAllShapes(int height, string codec, string expected)
        {
            Assert.Equal(expected, XtreamTunerHost.BuildVideoDisplayTitle(height, codec));
        }

        // ---------------------------------------------------------------------
        // The assembled MediaSourceInfo.
        // ---------------------------------------------------------------------

        [Fact]
        public void CreateMediaSourceInfo_TranscodingProfile_DeclaresOutputCodecAndDropsSourceAttributes()
        {
            // The reporter's scenario end to end: Dispatcharr ingested HEVC, the stream profile
            // emits H.264, and the plugin now declares H.264 because the profile said so.
            var stats = new StreamStatsInfo
            {
                VideoCodec = "hevc",   // what Dispatcharr ingested
                AudioCodec = "aac",
                Resolution = "1920x1080",
                SourceFps = 50,
                Bitrate = 4500,        // kbps from ffmpeg_output_bitrate
                AudioBitrate = 128,
                AudioChannels = "stereo",
                SampleRate = 48000,
                VideoProfile = "Main", // HEVC Main — meaningless for the H.264 output
                VideoLevel = 41,
                VideoBitDepth = 10,
                VideoRefFrames = 4,
            };

            var source = XtreamTunerHost.CreateMediaSourceInfo(
                streamId: 12345,
                streamUrl: "http://dispatcharr.local/proxy/ts/stream/abc-uuid",
                stats: stats,
                disableProbing: true,          // isDispatcharr = true
                forceAudioTranscode: false,
                userAgent: null,
                fallbackBitrateMbps: 0,
                declareDvbSubtitles: false,
                declaredVideoCodec: "h264");   // resolved from the stream profile

            // Probing stays off — AGENTS.md is absolute on this for Dispatcharr.
            Assert.False(source.SupportsProbing);
            Assert.Equal(0, source.AnalyzeDurationMs);

            var video = Assert.Single(source.MediaStreams, s => s.Type == MediaStreamType.Video);
            var audio = Assert.Single(source.MediaStreams, s => s.Type == MediaStreamType.Audio);

            // The fix for #66: what the proxy emits, not what it ingested.
            Assert.Equal("h264", video.Codec);
            Assert.Equal("1080p H264", video.DisplayTitle);

            // Output-side stats stay honoured.
            Assert.Equal(1920, video.Width);
            Assert.Equal(1080, video.Height);
            Assert.Equal(50f, video.RealFrameRate);
            Assert.Equal(4_500_000, video.BitRate);
            Assert.Equal("aac", audio.Codec);
            Assert.Equal("stereo", audio.ChannelLayout);
            Assert.Equal(2, audio.Channels);
            Assert.Equal(48000, audio.SampleRate);
            Assert.Equal(128000, audio.BitRate);

            // Source-codec attributes are dropped: "Main / level 41 / 10-bit / 4 ref frames"
            // describes the HEVC input, and declaring it on an H.264 output is the same bug
            // as #66 one field over.
            Assert.Null(video.Profile);
            Assert.Null(video.Level);
            Assert.Null(video.BitDepth);
            Assert.Null(video.RefFrames);
        }

        [Fact]
        public void CreateMediaSourceInfo_PassThroughProfile_KeepsReportedCodecAndAttributes()
        {
            // Pass-through profile: the profile has no answer, so the caller passes the codec
            // from stats and every codec-derived attribute is valid.
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
                streamUrl: "http://dispatcharr.local/proxy/ts/stream/abc-uuid",
                stats: stats,
                disableProbing: true,
                declaredVideoCodec: "hevc");

            var video = Assert.Single(source.MediaStreams, s => s.Type == MediaStreamType.Video);
            Assert.Equal("hevc", video.Codec);
            Assert.Equal("1080p HEVC", video.DisplayTitle);
            Assert.Equal("Main", video.Profile);
            Assert.Equal(41.0, video.Level);
            Assert.Equal(10, video.BitDepth);
            Assert.Equal(4, video.RefFrames);
        }

        [Fact]
        public void CreateMediaSourceInfo_NeverDeclaresNullVideoCodec()
        {
            // Codex review on PR #67 flagged this as critical: the no-stats branch documents
            // that Emby's RecordingRequiresEncoding dereferences MediaStream.Codec, so a null
            // codec turns a recording into a NullReferenceException. No configuration may
            // produce one — not a caller with no answer, not stats that carry no codec.
            var statsWithoutCodec = new StreamStatsInfo
            {
                Resolution = "1920x1080",
                Bitrate = 4500,
            };

            var source = XtreamTunerHost.CreateMediaSourceInfo(
                streamId: 12345,
                streamUrl: "http://dispatcharr.local/proxy/ts/stream/abc-uuid",
                stats: statsWithoutCodec,
                disableProbing: true,
                declaredVideoCodec: null);

            var video = Assert.Single(source.MediaStreams, s => s.Type == MediaStreamType.Video);
            Assert.False(string.IsNullOrEmpty(video.Codec));
            Assert.Equal("h264", video.Codec);

            // Same guarantee on the no-stats path, which has always used the H.264 fallback.
            var noStats = XtreamTunerHost.CreateMediaSourceInfo(
                streamId: 12345,
                streamUrl: "http://dispatcharr.local/proxy/ts/stream/abc-uuid",
                stats: null,
                disableProbing: true,
                declaredVideoCodec: null);
            var noStatsVideo = Assert.Single(noStats.MediaStreams, s => s.Type == MediaStreamType.Video);
            Assert.Equal("h264", noStatsVideo.Codec);
        }

        [Fact]
        public void CreateMediaSourceInfo_AudioOnlyChannel_IsUnaffectedByTheCodecDecision()
        {
            // Audio-only channels build no video stream at all, whatever the caller declares.
            var stats = new StreamStatsInfo
            {
                AudioCodec = "aac",
                AudioBitrate = 128,
                AudioChannels = "stereo",
            };

            var source = XtreamTunerHost.CreateMediaSourceInfo(
                streamId: 12345,
                streamUrl: "http://dispatcharr.local/proxy/ts/stream/abc-uuid",
                stats: stats,
                disableProbing: true,
                declaredVideoCodec: "h264");

            Assert.DoesNotContain(source.MediaStreams, s => s.Type == MediaStreamType.Video);
            var audio = Assert.Single(source.MediaStreams, s => s.Type == MediaStreamType.Audio);
            Assert.Equal("aac", audio.Codec);
            Assert.Equal(0, source.DefaultAudioStreamIndex);
        }

        [Fact]
        public void CreateMediaSourceInfo_DirectXtreamFallback_UsesReportedCodec()
        {
            // When BuildStreamUrl falls back to a direct Xtream URL the bytes are the
            // provider's own, so ResolveVideoCodec hands the factory the reported codec even
            // when a Dispatcharr profile is cached for the channel.
            var statsCodec = XtreamTunerHost.MapVideoCodec("hevc");
            var declared = XtreamTunerHost.ResolveVideoCodec(
                isDispatcharr: false, codecSource: "auto", profileCodec: "h264", statsCodec: statsCodec);

            var source = XtreamTunerHost.CreateMediaSourceInfo(
                streamId: 12345,
                streamUrl: "http://xtream.example.com/live/user/pass/12345.ts",
                stats: new StreamStatsInfo { VideoCodec = "hevc", AudioCodec = "aac", Resolution = "1920x1080" },
                disableProbing: false,
                declaredVideoCodec: declared);

            // Stats are present, so probing is still suppressed — via the hasStats branch of
            // ShouldSuppressProbing, not via the Dispatcharr flag.
            Assert.False(source.SupportsProbing);

            var video = Assert.Single(source.MediaStreams, s => s.Type == MediaStreamType.Video);
            Assert.Equal("hevc", video.Codec);
            Assert.Equal("1080p HEVC", video.DisplayTitle);
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
