# ADR-016: Take the Dispatcharr Video Codec From the Stream Profile, Not From `stream_stats`

**Date**: 2026-09-05 (supersedes the 2026-09-02 draft of this ADR, which chose alternative 4)
**Status**: Accepted
**Affects**: `XtreamTunerHost.CreateMediaSourceInfo()`, `XtreamTunerHost.ResolveVideoCodec()`, `StreamProfileCodec`, `DispatcharrClient`, `PluginConfiguration.DispatcharrVideoCodecSource`

---

## Context

When Dispatcharr is enabled and `stream_stats` are cached for a stream, `CreateMediaSourceInfo`
declared the codec from `stats.VideoCodec` on the Live TV `MediaSource` and set
`SupportsProbing = false`. Probing has to stay off: the probe opens a short-lived HTTP
connection that Dispatcharr reads as a client and tears down on close, causing a retry storm
(ADR-001 and `AGENTS.md → "Emby probes MediaSource.Path directly — disable for Dispatcharr"`).

That is correct while Dispatcharr is a pure pass-through, because the codec it ingested is also
the codec it emits. But Dispatcharr supports stream profiles that transcode, and "HEVC source →
H.264 output for compatibility" is a common one. There `stream_stats.video_codec` says `hevc`
while the bytes leaving the proxy are H.264, Emby forces the HEVC decoder on an H.264 stream,
ffmpeg floods `Invalid NAL unit` and exits, and the client shows "No compatible streams are
currently available".

The reporter (VoltsLee, issue [#66](https://github.com/firestaerter3/emby-xtream/issues/66)) hit
this on 13 channels, growing as more channels get probed by Dispatcharr and gain stats.

## Problem

One declaration policy ("trust stats, suppress probing") is right for pass-through and wrong for
transcoding, and `stream_stats` alone cannot tell the two apart. Any fix has to respect the
absolute no-probe rule for `/proxy/ts/stream/{uuid}` URLs, and it has to give Emby a codec:
the no-stats branch of the same method records that `RecordingRequiresEncoding` dereferences
`MediaStream.Codec`, so a null there turns a recording into a `NullReferenceException`.

## Alternatives Considered

### 1. Per-profile output codec map in plugin settings

A `Dictionary<int, string>` in config, keyed by stream profile ID, filled in by the user.

**Rejected**: the user has to know which profile each channel uses and what it outputs, profile
IDs are server-side and can change, and the reporter's channels span several profiles.

### 2. Re-enable probing when the user opts out of the stats codec

Drop the Dispatcharr `disableProbing` flag so Emby discovers the output codec itself.

**Rejected**: violates the no-probe rule. CodeRabbit flagged it Major on PR #67. The teardown
fires when the probe connection closes, before `channel_shutdown_delay` starts counting, so
setting that delay does not save it.

### 3. Always probe, no opt-out

**Rejected**: breaks every existing Dispatcharr install and brings the teardown storm back for
everyone who has not set `channel_shutdown_delay`.

### 4. Leave the codec field unset when the user opts out

Add a boolean; when off, declare no video codec at all and let Emby work it out from the bytes.
This was the accepted decision in the first version of this ADR and shipped as
`DispatcharrUseStatsCodec` on PR #67.

**Rejected on review.** The claim it rests on — that Emby picks the decoder from the bytes when
the codec is unset — was never verified against a real server, and the no-stats branch of the
same method says the opposite in a comment:

> Codec must be non-null: Emby's `RecordingRequiresEncoding` accesses it directly and throws
> `NullReferenceException` when it is null.

That comment came in with `75a616a`, whose message attributes the crash it fixed to a null
`liveStream` rather than a null codec, so it may be drawn too broadly. Codex review on PR #67
reached the same conclusion independently and marked it P1: the setting deliberately steers
users with transcoding profiles onto the null path, so if the comment is right, the fix breaks
recordings for exactly the people it is meant to help. No test in the suite can settle it, since
none of them has Emby consume the `MediaSourceInfo`.

The deciding argument is that alternative 6 makes the question moot rather than answering it.

### 5. Wait for Dispatcharr to expose the profile output codec on the stats endpoint

**Rejected as the primary fix**: out of our control, and the reporter's channels are broken now.
Still worth filing upstream.

### 6. Read the output codec from the channel's stream profile (chosen)

Dispatcharr already knows what it emits, because the user configured it: each channel resolves to
a stream profile, and the profile's `parameters` string carries the ffmpeg arguments. `-c:v
h264_nvenc` means H.264 leaves the proxy whatever went in. The reporter proposed this in the
PR #67 thread.

**Chosen.** It declares a correct, non-null codec, needs no probe, and costs one extra pair of
API calls on the cached refresh path rather than anything at playback time.

## Decision

The plugin resolves the declared video codec in this order, for Dispatcharr URLs:

1. **The channel's stream profile**, parsed by `StreamProfileCodec.Parse` from the profile's
   command and parameters. `-c:v`, `-c:v:0`, `-codec:v`, `-vcodec` and bare `-c`/`-codec` are all
   read, last occurrence wins (that is how ffmpeg resolves them), and the encoder name is mapped
   onto a codec: `libx264`/`h264_*` → `h264`, `libx265`/`hevc_*`/`h265_*` → `hevc`, `mpeg2*` →
   `mpeg2video`.
2. **`stream_stats.video_codec`**, when the profile has no answer. `copy`, a non-ffmpeg profile
   (the built-in redirect and proxy profiles, streamlink, custom scripts), an unrecognised
   encoder, an unreadable profile endpoint and an unresolvable profile ID all count as "no
   answer". That is deliberate: an unknown encoder returns null rather than its own name, because
   handing Emby `av1_nvenc` as a codec is worse than handing it nothing.
3. **`h264`**, when neither knows. This is the same fallback the no-stats branch has always used,
   and it exists so that no configuration can produce a null codec.

Channel → profile comes from `effective_stream_profile_id` (override-aware, newer serializers)
falling back to `stream_profile_id`, and a channel with neither uses the server's
`default_stream_profile` setting. All of it is fetched during the cached channel refresh, never
at playback: doing Dispatcharr lookups per play is what BUG-007 was about, and it put ~3s on the
first tune.

Probing stays off. Nothing in this ADR touches that.

`PluginConfiguration.DispatcharrVideoCodecSource` (default `auto`) overrides the resolution for
installs where profile detection cannot work or gets it wrong: `stats` restores the pre-#66
behaviour, `h264` and `hevc` declare that codec on every Dispatcharr channel. All three are
non-null by construction. It appears as *Video codec for Dispatcharr channels* in the Dispatcharr
section of the config page.

Direct Xtream URLs (the `DispatcharrFallbackToXtream` path) always use the reported codec: those
bytes are the provider's own, so the profile does not describe them and the Dispatcharr setting
does not apply.

## Consequences

- **Pass-through installs**: no change. The profile has no answer, so the reported codec is used
  exactly as before.
- **Transcoding installs**: channels that failed with "No compatible streams are currently
  available" now play, with no setting to find and no checkbox to tick. The player still shows
  resolution and codec, because a codec is always declared.
- **Codec-derived attributes**: `Profile`, `Level`, `BitDepth` and `RefFrames` describe the
  ingested codec, so they are only declared when the declared codec *is* the reported one
  (`ShouldDeclareCodecAttributes`). Declaring "Main / level 41 / 10-bit" from an HEVC source on an
  H.264 output is #66 one field over. Resolution, frame rate, `ffmpeg_output_bitrate` and the
  audio stream describe the output and are always honoured.
- **Recording**: no path declares a null codec any more, so the `RecordingRequiresEncoding`
  question is closed by construction rather than by testing.
- **Extra API calls**: two per cached refresh (`/api/core/streamprofiles/` and
  `/api/core/settings/`), none per playback. Both failing is a supported state and degrades to
  the reported codec.
- **Older Dispatcharr / restricted accounts**: an empty profile list or a missing setting logs at
  info level and falls back to the reported codec, which is the pre-#66 behaviour rather than an
  error.
- **API surface**: `DispatcharrVideoCodecSource` replaces the unreleased `DispatcharrUseStatsCodec`
  from earlier commits on this branch. Existing config XML has neither field and deserializes to
  `auto`.

## Follow-up

- File a Dispatcharr issue asking for the emitted codec on the stream-stats endpoint. If it ever
  lands, `StreamProfileCodec` becomes a fallback for older servers instead of the primary source.
- Extend the encoder map when a real profile shows up that it does not recognise. The parser
  fails safe, so an unknown encoder is a missed fix rather than a broken channel.

## Code Citations

Cited by symbol rather than line number: this ADR has already been through one commit whose only
purpose was re-syncing line references, and they went stale again three commits later.

- Resolution order: `XtreamTunerHost.ResolveVideoCodec`, applied at both playback call sites.
- Profile parsing and the stream-id → codec map: `StreamProfileCodec.Parse` / `BuildCodecMap`.
- Fetch path: `XtreamTunerHost.BuildProfileCodecMapAsync`, `DispatcharrClient.GetStreamProfilesAsync`,
  `DispatcharrClient.GetDefaultStreamProfileIdAsync`.
- Per-channel profile IDs: `DispatcharrChannelWithStreams.ResolvedStreamProfileId`, mapped under
  both keys in `DispatcharrClient.GetChannelDataAsync`.
- Attribute gate: `XtreamTunerHost.ShouldDeclareCodecAttributes`.
- Probe suppression, unchanged: `XtreamTunerHost.ShouldSuppressProbing`.
- Config: `PluginConfiguration.DispatcharrVideoCodecSource`; UI in `Configuration/Web/config.html`
  (`selDispatcharrCodecSource`) and the read/write pair in `config.js`.
- Tests: `XtreamTunerHostTests` (parser table, resolution table, attribute gate, assembled
  `MediaSourceInfo` per scenario, and the guarantee that no path declares a null codec) and
  `DispatcharrClientTests` (both endpoints, and the dual-key profile-ID map).
