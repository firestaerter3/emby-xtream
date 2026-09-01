# ADR-016: Don't Trust Dispatcharr `stream_stats.video_codec` When a Stream Profile Transcodes

**Date**: 2026-09-02
**Status**: Accepted
**Affects**: `XtreamTunerHost.CreateMediaSourceInfo()`, `PluginConfiguration.DispatcharrUseStatsCodec`

---

## Context

When Dispatcharr is enabled and `stream_stats` are cached for a stream, `XtreamTunerHost.CreateMediaSourceInfo` has been declaring the codec from `stats.VideoCodec` on the Live TV `MediaSource` and setting `SupportsProbing = false`. The reasoning: probing opens a short-lived HTTP connection that Dispatcharr interprets as a client and tears down on close, causing a retry storm (see ADR-001 and `docs/AGENTS.md → "Emby probes MediaSource.Path directly — disable for Dispatcharr"`).

That logic is correct for streams where Dispatcharr is a pure pass-through. The codec Dispatcharr reports on `stream_stats.video_codec` is the codec it *ingested* from the source — the same bytes leaving the proxy.

But Dispatcharr also supports stream profiles that transcode. A common configuration is "HEVC source → H.264 output for compatibility". In that mode, `stream_stats.video_codec` reports `hevc` (the ingested codec), while the bytes leaving the proxy are H.264. Reporting `hevc` to Emby on the MediaSource causes Emby to force the HEVC decoder on the output — which fails, because the bytes are H.264.

The reporter (VoltsLee, issue [#66](https://github.com/firestaerter3/emby-xtream/issues/66)) hit this on 13 channels. Confirmed in code at `XtreamTunerHost.cs:1515-1521` (codec declaration) and `XtreamTunerHost.cs:1458` (`suppressProbing = disableProbing || hasStats`).

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
- **Affected users** (Dispatcharr stream profile transcoding): a single checkbox in Plugin Config. The codec field is left unset on the `MediaStream`; Emby picks whatever decoder the actual bytes need. Display title shows resolution only (`"1080p"`) instead of resolution + codec.
- **API surface**: `PluginConfiguration.DispatcharrUseStatsCodec` (new field, defaults to `true` — existing config XML files deserialize unchanged).
- **Tests**: `XtreamTunerHostTests.cs` pins both helpers and `BuildVideoDisplayTitle` (4-row Theory for ShouldSuppressProbing, 4-row Theory for ShouldDeclareVideoCodec, 5-row Theory for BuildVideoDisplayTitle, plus Facts covering the reporter's exact bug inputs end-to-end). All green.
- **AGENTS.md alignment**: the fix lives entirely within the "don't probe, don't falsely declare" envelope. The absolute no-probe rule for `/proxy/ts/stream/{uuid}` URLs is unchanged.
- **Future work**: file an upstream Dispatcharr issue asking for `stream_profile_output_codec` on the stream-stats endpoint. If/when shipped, `DispatcharrUseStatsCodec` can be deprecated and the plugin can auto-detect transcoding profiles.

## Code Citations

- Bug confirmed: `XtreamTunerHost.cs:1515-1521` (codec declaration from `stats.VideoCodec`).
- Original probe-disable: `XtreamTunerHost.cs:1458` (`suppressProbing = disableProbing || hasStats`).
- Fix (new helpers + escape route): `XtreamTunerHost.cs:1446-1457` (audio detection + hasVideoCodecFromStats gating), `XtreamTunerHost.cs:1529-1543` (codec declaration + display title).
- Static helpers for regression testing: `XtreamTunerHost.cs:1775-1834` (`ShouldSuppressProbing`, `ShouldDeclareVideoCodec`, `BuildVideoDisplayTitle`).
- Config: `PluginConfiguration.cs:51-68` (new `DispatcharrUseStatsCodec` field).
- Tests: `Emby.Xtream.Plugin.Tests/XtreamTunerHostTests.cs` (regression rows for both helpers plus end-to-end hand-walk).