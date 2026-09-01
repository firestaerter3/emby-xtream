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

## Alternatives Considered

### 1. Per-profile output codec map in plugin settings (reporter suggestion)

Add a config setting `DispatcharrProfileOutputCodecs: Dictionary<int, string>` keyed by stream profile ID. When a channel uses a transcoding profile, the plugin reads the output codec from this map instead of `stream_stats.video_codec`.

**Pros**: Most precise. The plugin declares exactly what Dispatcharr will emit.
**Cons**:
- Requires the user to know which profile their channel uses and which output codec it produces.
- Profile IDs are server-side identifiers that can change across Dispatcharr versions.
- The reporter's actual channel list spans multiple profiles — they'd need to enter all of them.
- Adds UI surface that most users never need.

### 2. Re-enable probing when the user opts out of stats codec (chosen)

Add a single boolean `DispatcharrUseStatsCodec` (default `true`, preserves current behavior). When `false`:
- The plugin ignores `stats.VideoCodec`.
- `hasStats` collapses to "audio-only or absent".
- The dispatcharr-side `disableProbing` flag is dropped, so probing is allowed to run.
- Emby probes the proxy URL and discovers the actual output codec on first tune (~100ms cost).

**Pros**:
- Single user-facing toggle. No per-profile state to maintain.
- Restores the plugin's correct behavior (declare nothing → let Emby discover).
- Default `true` keeps all existing installs unchanged. Users who hit the bug flip a checkbox.
- Probing is a well-understood mechanism. The dispatcharr-side teardown concern (ADR-001) is mitigated by `channel_shutdown_delay` — a Dispatcharr setting the user already controls.

**Cons**:
- Adds ~100ms to first tune on Dispatcharr streams for affected users (acceptable, documented).
- The dispatcharr teardown concern is back into scope for these users. Document the `channel_shutdown_delay` mitigation in the config flag's XML doc.

### 3. Always probe (no opt-out)

Drop the trust-stats-codec path entirely. Always let Emby probe.

**Cons**: Breaks every existing Dispatcharr install. The teardown storm returns for users who haven't set `channel_shutdown_delay`. Too aggressive.

### 4. Wait for Dispatcharr to add a `stream_profile_output_codec` field

Out of our control. Filing a Dispatcharr issue is a good follow-up, but the reporter's channels are broken *today*.

## Decision

**Alternative 2**: add `DispatcharrUseStatsCodec` (default `true`). When `false`, the plugin ignores `stats.VideoCodec` and allows probing.

The decision logic is split into two static helpers, `XtreamTunerHost.ShouldSuppressProbing` and `XtreamTunerHost.ShouldDisableProbing`, so regression tests can pin the truth table without instantiating a full `MediaSourceInfo` (the static `_streamStats` cache blocks full integration tests — see the placeholder in `XtreamTunerHostTests.cs`).

Audio-only detection is preserved unchanged: `isAudioOnly` still reads `stats.AudioCodec` regardless of `DispatcharrUseStatsCodec`. The audio flag remains trustworthy in all configurations.

## Consequences

- **Existing installs**: no behavior change. `DispatcharrUseStatsCodec = true` is the default and reproduces the legacy trust-stats-codec path verbatim.
- **Affected users** (Dispatcharr stream profile transcoding): a single checkbox in Plugin Config. Probing restores correct codec discovery at the cost of ~100ms on first tune.
- **API surface**: `PluginConfiguration.DispatcharrUseStatsCodec` (new field, defaults to `true` — existing config XML files deserialize unchanged).
- **Tests**: `XtreamTunerHostTests.cs` adds 11 regression rows (4-row Theory + 3-row Theory + 4 Facts) pinning both helpers and a hand-walk through the reporter's exact bug inputs.
- **Future work**: file an upstream Dispatcharr issue asking for `stream_profile_output_codec` on the stream-stats endpoint. If/when shipped, `DispatcharrUseStatsCodec` can be deprecated and the plugin can auto-detect transcoding profiles.
- **Operational note**: probe-induced teardown concern returns for opted-out users. Mitigation is the existing `channel_shutdown_delay` setting in Dispatcharr — users who opt out of stats codec must set it to a positive value to avoid the retry storm documented in ADR-001. The XML doc on the config field calls this out.

## Code Citations

- Bug confirmed: `XtreamTunerHost.cs:1515-1521` (codec declaration from `stats.VideoCodec`).
- Original probe-disable: `XtreamTunerHost.cs:1458` (`suppressProbing = disableProbing || hasStats`).
- Fix (new helpers + escape route): `XtreamTunerHost.cs:1458-1468` (`ShouldDisableProbing` + `ShouldSuppressProbing` wiring).
- Config: `PluginConfiguration.cs:51-68` (new `DispatcharrUseStatsCodec` field).
- Tests: `Emby.Xtream.Plugin.Tests/XtreamTunerHostTests.cs` (12 tests, 0 skipped, all green).
