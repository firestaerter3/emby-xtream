# ADR-016: Don't Trust Dispatcharr `stream_stats.video_codec` When a Stream Profile Transcodes

**Date**: 2026-09-02
**Status**: Accepted
**Affects**: `XtreamTunerHost.CreateMediaSourceInfo()`, `PluginConfiguration.DispatcharrUseStatsCodec`

---

## Context

When Dispatcharr is enabled and `stream_stats` are cached for a stream, `XtreamTunerHost.CreateMediaSourceInfo` has been declaring the codec from `stats.VideoCodec` on the Live TV `MediaSource` and setting `SupportsProbing = false`. The reasoning: probing opens a short-lived HTTP connection that Dispatcharr interprets as a client and tears down on close, causing a retry storm (see ADR-001 and `docs/AGENTS.md → "Emby probes MediaSource.Path directly — disable for Dispatcharr"`).

That logic is correct for streams where Dispatcharr is a pure pass-through. The codec Dispatcharr reports on `stream_stats.video_codec` is the codec it *ingested* from the source — the same bytes leaving the proxy.

But Dispatcharr also supports stream profiles that transcode. A common configuration is "HEVC source → H.264 output for compatibility". In that mode, `stream_stats.video_codec` reports `hevc` (the ingested codec), while the bytes leaving the proxy are H.264. Reporting `hevc` to Emby on the MediaSource causes Emby to force the HEVC decoder on the output — which fails, because the bytes are H.264.

The reporter (VoltsLee, issue [#66](https://github.com/firestaerter3/emby-xtream/issues/66)) hit this on 13 channels. Confirmed in code at `XtreamTunerHost.cs:1529-1531` (codec declaration) and `XtreamTunerHost.cs:1479` (`suppressProbing = disableProbing || hasStats`).

## Problem

The plugin has one declaration policy ("trust stats, suppress probing") that is correct for pass-through and wrong for transcoded streams. There is no way to distinguish the two cases from `stream_stats` alone — Dispatcharr does not currently expose the profile output codec via this endpoint.

The no-probe rule for `/proxy/ts/stream/{uuid}` URLs is absolute (`docs/AGENTS.md → "Emby probes MediaSource.Path directly — disable for Dispatcharr"`): the probe opens a short-lived HTTP connection that Dispatcharr interprets as a client and tears down on close, so probing MUST stay disabled for these URLs regardless of stats availability. Any fix for the codec-mismatch bug must respect this rule.

## Alternatives Considered

### 1. Per-profile output codec map in plugin settings (reporter suggestion)

Add a config setting `DispatcharrProfileOutputCodecs: Dictionary<int, string>` keyed by stream profile ID. When a channel uses a transcoding profile, the plugin reads the output codec from this map instead of `stream_stats.video_codec`.

**Pros**: Most precise. The plugin declares exactly what Dispatcharr will emit.
**Cons**:
- Requires the user to know which profile their channel uses and which output codec it produces.
- Profile IDs are server-side identifiers that can change across Dispatcharr versions.
- The reporter's actual channel list spans multiple profiles — they'd need to enter all of them.
- Adds UI surface that most users never need.

### 2. Re-enable probing when the user opts out of stats codec (originally chosen, then revised)

Add a single boolean `DispatcharrUseStatsCodec` (default `true`). When `false`, drop the dispatcharr-side `disableProbing` flag so probing runs and Emby discovers the output codec.

**Pros**: Single toggle, restores the correct behavior via the documented probing mechanism.
**Cons**:
- Violates the absolute no-probe rule in `docs/AGENTS.md` for `/proxy/ts/stream/{uuid}` URLs. CodeRabbit flagged this as a `🟠 Major` finding on PR #67 — the dispatcharr teardown concern returns even with `channel_shutdown_delay` set, because the teardown happens *before* `channel_shutdown_delay` starts ticking (the probe connection closes too quickly).
- Re-introduces the documented retry-storm risk for affected users.

### 3. Always probe (no opt-out)

Drop the trust-stats-codec path entirely. Always let Emby probe.

**Cons**: Breaks every existing Dispatcharr install. The teardown storm returns for users who haven't set `channel_shutdown_delay`. Too aggressive.

### 4. Leave the codec field unset on the MediaStream when the user opts out of stats codec (chosen)

Add a single boolean `DispatcharrUseStatsCodec` (default `true`, preserves current behavior). When `false`, the plugin ignores `stats.VideoCodec` and leaves the codec field unset on the video `MediaStream`. Probing stays disabled (per `docs/AGENTS.md`). Resolution, FPS, bitrate, profile, level, and audio codec continue to flow from stats — only the video codec and its display title are gated.

Emby's behaviour when codec is unset: it applies whatever decoder the actual MPEG-TS bytes need. Pass-through streams (H.264 bytes in, H.264 bytes out) play correctly because the decoder matches the bytes. Transcoded streams (HEVC source, H.264 bytes out) play correctly because the codec field does not falsely claim HEVC. There is a small visual cost: the player UI shows `"1080p"` instead of `"1080p H264"` for affected channels, but playback works.

**Pros**:
- Single user-facing toggle. No per-profile state to maintain.
- Respects the absolute no-probe rule for Dispatcharr proxy URLs (`docs/AGENTS.md`).
- Default `true` keeps all existing installs unchanged. Users who hit the bug flip a checkbox.
- No probing cost, no teardown risk, no `channel_shutdown_delay` dependency.
- Audio codec, resolution, FPS, bitrate, profile, and level keep flowing from stats regardless of the toggle.

**Cons**:
- The display title loses the codec label for affected channels (`"1080p"` instead of `"1080p H264"`). Acceptable: playback matters more than the label.
- Users who set `DispatcharrUseStatsCodec = false` lose the codec hint Emby would otherwise have. This is the explicit trade-off the toggle captures.

### 5. Wait for Dispatcharr to add a `stream_profile_output_codec` field

Out of our control. Filing a Dispatcharr issue is a good follow-up, but the reporter's channels are broken *today*.

## Decision

**Alternative 4**: add `DispatcharrUseStatsCodec` (default `true`). When `false`, the plugin ignores `stats.VideoCodec` and leaves the codec field unset on the video `MediaStream`. Probing stays disabled.

The decision logic is split into two static helpers, `XtreamTunerHost.ShouldSuppressProbing` and `XtreamTunerHost.ShouldDeclareVideoCodec`, so regression tests can pin the truth tables without instantiating a full `MediaSourceInfo` (the static `_streamStats` cache blocks full integration tests — see the placeholder in `XtreamTunerHostTests.cs`). A third helper, `XtreamTunerHost.BuildVideoDisplayTitle`, owns the four-shape display-title contract.

Audio-only detection is preserved unchanged: `isAudioOnly` still reads `stats.AudioCodec` regardless of `DispatcharrUseStatsCodec`. The audio flag remains trustworthy in all configurations.

## Consequences

- **Existing installs**: no behavior change. `DispatcharrUseStatsCodec = true` is the default and reproduces the legacy trust-stats-codec path verbatim.
- **Affected users** (Dispatcharr stream profile transcoding): a single checkbox, *Plugin Config → Dispatcharr → Declare Dispatcharr's reported video codec*, on by default. Unticking it leaves the codec field unset on the `MediaStream`; Emby picks whatever decoder the actual bytes need. Display title shows resolution only (`"1080p"`) instead of resolution + codec.
- **API surface**: `PluginConfiguration.DispatcharrUseStatsCodec` (new field, defaults to `true` — existing config XML files deserialize unchanged).
- **Tests**: `XtreamTunerHostTests.cs` pins both helpers and `BuildVideoDisplayTitle` (4-row Theory for ShouldSuppressProbing, 4-row Theory for ShouldDeclareVideoCodec, 5-row Theory for BuildVideoDisplayTitle, plus Facts covering the reporter's exact bug inputs end-to-end). All green.
- **AGENTS.md alignment**: the fix lives entirely within the "don't probe, don't falsely declare" envelope. The absolute no-probe rule for `/proxy/ts/stream/{uuid}` URLs is unchanged.
- **Future work**: file an upstream Dispatcharr issue asking for `stream_profile_output_codec` on the stream-stats endpoint. If/when shipped, `DispatcharrUseStatsCodec` can be deprecated and the plugin can auto-detect transcoding profiles.

## Code Citations

Cited by symbol rather than line number: this ADR has already been through one
commit whose only purpose was re-syncing line references, and they went stale
again three commits later.

- Bug and fix both live in `XtreamTunerHost.CreateMediaSourceInfo`, in the `if (hasStats)` branch that builds the video `MediaStream`.
- Probe suppression: `XtreamTunerHost.ShouldSuppressProbing` (`disableProbing || hasStats`), unchanged by this ADR.
- Decision helpers: `ShouldSuppressProbing`, `HasStats`, `ShouldDeclareVideoCodec`, `ShouldUseStatsCodec`, `BuildVideoDisplayTitle`.
- Config: `PluginConfiguration.DispatcharrUseStatsCodec`.
- UI: `Configuration/Web/config.html` (`chkDispatcharrUseStatsCodec`, in the Dispatcharr section) and the matching read/write pair in `config.js`.
- Tests: `Emby.Xtream.Plugin.Tests/XtreamTunerHostTests.cs` (helper truth tables, factory output for the opt-out and for the direct-Xtream fallback).

## Follow-up: `hasStats` gate was over-coupled to codec trust

**Date**: 2026-09-02
**Trigger**: CodeRabbit review on PR [#67](https://github.com/firestaerter3/emby-xtream/pull/67) head `3c6d056` (review 5084623350 at 01:07:35Z).

The original implementation wrote

```csharp
bool hasVideoCodecFromStats = stats?.VideoCodec != null && useStatsCodec;
bool hasStats = hasVideoCodecFromStats || isAudioOnly;
```

on the assumption that `hasStats` would always be true when we have any stats to honour. The coupling was wrong: when a video channel had `DispatcharrUseStatsCodec = false`, `hasVideoCodecFromStats` collapsed to false, and a video channel is not audio-only, so `hasStats` collapsed to false. The `if (hasStats) { ... }` block then skipped every stat-derived field — resolution, FPS, bitrate, audio codec, audio channels, video profile/level/bit depth/reference frames. Emby ended up with a `MediaSource` whose video `MediaStream` had no codec, no width, no height, no frame rate, and no audio stream at all. The "Audio codec, resolution, FPS, bitrate, profile, and level keep flowing from stats regardless of the toggle" claim in the Consequences section above turned out to be wrong.

The fix: replace the `hasVideoCodecFromStats || isAudioOnly` expression with `stats != null` (extracted into a new static helper `XtreamTunerHost.HasStats(statsPresent, isAudioOnly)` for symmetry with the other decision helpers and to make the bug class regression-testable). The video codec declaration stays gated separately by `ShouldDeclareVideoCodec`. Truth table for `HasStats`:

| stats != null | isAudioOnly | hasStats | Notes                                        |
|---------------|-------------|----------|----------------------------------------------|
| true          | false       | true     | video channel, legacy or opt-out            |
| true          | true        | true     | audio-only channel                           |
| false         | false       | false    | no stats at all (Dispatcharr 404 / cache miss) |
| false         | true        | false    | unreachable: isAudioOnly implies stats != null |

`XtreamTunerHostTests.Issue66_HasStats_TracksStatsPresenceNotVideoCodec_Gate` pins this table. With the helper in place, the original ADR claim ("Audio codec, resolution, FPS, bitrate, profile, and level keep flowing from stats regardless of the toggle") is now actually true.

The discovery was a useful reminder: the original `hasStats = hasVideoCodecFromStats || isAudioOnly` looked symmetric and tidy, but it conflated "stats are present" with "the codec field on the stats is trustworthy". Once the codec gate split off into `ShouldDeclareVideoCodec`, the only thing left to check was stats presence.

## Follow-up: codec-derived attributes follow the codec

**Date**: 2026-09-05

The first cut gated the codec but kept declaring `Profile`, `Level`, `BitDepth` and
`RefFrames` from the same `stream_stats` object. Those four describe the codec
Dispatcharr ingested, so they are worth exactly as much as the codec field is: a level
number means something different in HEVC than in H.264, and neither bit depth nor
reference-frame count needs to survive a re-encode. Declaring `Main / level 41 / 10-bit`
from an HEVC source on an H.264 output is the same bug class as #66, one field over.

They now sit behind the same `ShouldDeclareVideoCodec` gate as the codec itself. What
stays honoured on the opt-out path is what genuinely describes the output: resolution,
frame rate, `ffmpeg_output_bitrate` (output-side, per its own name) and everything on
the audio stream. Both sides of the gate are pinned in
`XtreamTunerHostTests.CreateMediaSourceInfo_DispatcharrOptOut_*` (omitted) and
`CreateMediaSourceInfo_DirectXtreamFallback_*` (declared).

## Open verification: an unset codec on the recording path

**Status**: not yet verified on a real server.

The chosen alternative rests on one claim: that Emby, handed a video `MediaStream` with
no codec, works the decoder out from the bytes. That is asserted in this ADR without a
source, and the no-stats fallback branch in the same method carries a comment claiming
the opposite:

> Codec must be non-null: Emby's `RecordingRequiresEncoding` accesses it directly and
> throws `NullReferenceException` when it is null.

That comment arrived with commit `75a616a`, whose message attributes the crash it fixed
to a null `liveStream` rather than a null codec, and whose only codec-related change was
on the audio side. So the comment may be a conclusion drawn too broadly from that
debugging session, or an accurate separate observation. The test suite cannot settle it:
every test here asserts either a boolean truth table or the shape of the returned
`MediaSourceInfo`, and none of them has Emby consume that object.

What settles it is one opted-out channel played *and* recorded on a real server.
`RecordingRequiresEncoding` sits on the timer path, not the playback path, so playback
succeeding proves nothing about recording. If the crash is real, alternative 4 is dead
and the route is the one the reporter proposed in the PR thread: read the channel's
stream profile command string through the connector the plugin already authenticates to
and map `-c:v h264_nvenc`/`libx264` → `h264`, `hevc_nvenc`/`libx265` → `hevc`, `copy` →
stats as today. That declares a correct non-null codec, keeps the display title, needs
no probe, and makes this question moot. Alternative 1 above rejects a weaker version of
that idea (a user-maintained profile-ID → codec map) for reasons that do not apply to
parsing a string the plugin can already read.
