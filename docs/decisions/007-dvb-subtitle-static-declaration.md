# ADR-007: DVB Subtitle Support via Static Stream Declaration

**Date**: 2026-04-03
**Status**: Accepted
**Affects**: `XtreamTunerHost.CreateMediaSourceInfo()`, `PluginConfiguration`, Live TV config UI

---

## Context

Users watching DVB broadcasts (e.g. Norwegian NRK channels) expect subtitle tracks to be
selectable in Emby. The plugin disables stream probing by design to enable fast channel
switching — `AnalyzeDurationMs = 0` and `SupportsProbing = false` are set for Dispatcharr
proxy URLs. Without probing, Emby never discovers subtitle tracks embedded in the MPEG-TS
stream, so no subtitle options appear in the player UI.

## Problem

How do we surface DVB subtitle tracks in Emby without re-enabling stream probing?

## Alternatives Considered

### 1. Re-enable probing when subtitles are requested

Set `AnalyzeDurationMs > 0` when a "detect subtitles" option is on.

**Rejected.** For Dispatcharr proxy URLs this is destructive: the probe opens a short-lived
connection, Dispatcharr interprets the close as the last client leaving and tears down the
channel, and the real playback connection triggers a retry storm. See CONTRIBUTING.md and
ADR-001 for the full explanation.

### 2. Fetch subtitle track info from Dispatcharr stream stats

Dispatcharr's `/api/channels/channels/?include_streams=true` already provides per-stream
codec metadata via Streamflow. The plan was to add subtitle fields to `StreamStatsInfo` and
use them to declare tracks with correct language tags.

**Rejected.** Querying `GET /XtreamTuner/StreamStats` confirmed that Dispatcharr does not
expose subtitle track metadata — `audio_language` and any subtitle fields are absent from
all 150 streams in the test environment. Streamflow only captures video/audio codec info.

### 3. Manual language configuration per channel

Let users map stream IDs to language codes in the plugin config.

**Rejected.** Too complex to maintain and requires users to know the language of every
channel in advance.

### 4. Global fallback language field

A single text field (e.g. `nor`) used as the language tag on all declared subtitle tracks.

**Partially implemented, then removed.** Added and removed during development because
`audio_language` is absent from Dispatcharr stats, making even the auto-inference approach
fail. A manual global field adds UI complexity for marginal benefit — the tracks work
regardless of whether a language tag is present, they just show as "DVB Subtitles" instead
of e.g. "Norwegian (DVBSUB)".

## Decision

Declare subtitle `MediaStream` entries statically in `CreateMediaSourceInfo()` when the
`DeclareDvbSubtitles` config flag is set. Two tracks are always declared: one regular
(`dvb_subtitle`) and one hearing impaired (`dvb_subtitle` + `IsHearingImpaired = true`).
No language tag is set since the source does not provide this information.

A single toggle — **"Enable DVB subtitles"** — is added to the Live TV settings tab,
outside the Dispatcharr section so it works for both direct Xtream and Dispatcharr users.

A diagnostic endpoint `GET /XtreamTuner/StreamStats` is also added so users can inspect
the raw stats received from Dispatcharr, useful for future debugging.

## Consequences

- Subtitle tracks appear in Emby without any probing overhead.
- Fast channel switching is preserved.
- Tracks show as "DVB Subtitles" and "DVB Subtitles SDH" — no language name, because
  Dispatcharr does not provide it.
- Channels that carry no subtitles will show the two tracks anyway; ffmpeg will find
  nothing to map and the options will silently produce no output. This is acceptable.
- If Dispatcharr adds subtitle/language metadata in a future release, `StreamStatsInfo`
  can be extended and the language inference re-introduced.
