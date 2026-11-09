# ADR-024: Ask Dispatcharr for Video First Through an Output Profile

**Date**: 2026-11-09
**Status**: Accepted
**Affects**: `XtreamTunerHost.ResolveStreamUrl()`, `XtreamTunerHost.ResolveVideoFirstProfileAsync()`,
`DispatcharrClient.EnsureOutputProfileAsync()`, `PluginConfiguration.DispatcharrVideoFirst`,
`VideoFirstOutputProfile`

---

## Context

Issue #108: on some Dispatcharr channels Emby fails as soon as it has to transcode, with

```
Stream specifier ':0' in filtergraph description [0:0]scale... matches no streams.
```

Those channels carry their audio track before their video track (`Stream #0:0 Audio`,
`Stream #0:1 Video`). Dispatcharr's default stream profile (`-map 0 -c copy`) keeps the source's
track order. The plugin turns probing off for Dispatcharr streams (the teardown rule in AGENTS.md)
and declares video as track 0 and audio as track 1 in `CreateMediaSourceInfo`. Emby builds its
transcode filters from those numbers, so on these channels it points the video filter at audio.
Direct play is not affected, which is why the channel looks fine until a client lowers the quality.

## Problem

The declared numbers have to match the real order, and nothing the plugin has tells it the real
order:

- Dispatcharr's `stream_stats` has codecs, resolution and audio fields, but no track order.
- Emby's transcoder does not read through the plugin. Checked on Emby 4.10 by forcing a transcode
  of a Live TV channel: the ffmpeg log shows `-i http://<dispatcharr>/proxy/ts/stream/<uuid>`.
  This matches commit `c1708fa`, which made `XtreamLiveStream.Open()` a no-op for that reason. So
  the plugin never sees the stream's PMT before Emby starts ffmpeg.

## Alternatives Considered

### 1. Read the PMT in `Open()` and fix the numbers there

**Rejected**: ffmpeg opens its own connection to Dispatcharr, so this is a second connection per
tune. Closing it early is exactly the short-lived client that makes Dispatcharr tear the channel
down (AGENTS.md, ADR on the probe storm); keeping it open is the idle connection `c1708fa` removed.

### 2. Remember each channel's order from plays that do pass through the plugin

**Rejected**: transcoded plays never pass through the plugin, so the first transcode of every
affected channel would still fail, and many would never be learned.

### 3. Serve the stream from a plugin URL and rewrite the PMT on the way through

Would work without Dispatcharr. **Rejected for now**: every transcode would then run through the
plugin, and the URL would need an API key in it, since every plugin route is admin-only. A lot of
new code on the playback path for a problem that so far shows up on some channels of some sources.

### 4. A setting for the order (global or per channel)

**Rejected**: a global "audio first" breaks every channel that has video first, and a per-channel
list asks users to find out what the plugin cannot.

## Decision

Make Dispatcharr deliver every channel with video first, which makes the declared numbers true.

Dispatcharr 0.28+ has output profiles: an ffmpeg step it runs on a channel's stream for clients
that ask for it with `?output_profile={id}`. Both Dispatcharr URLs (`/proxy/ts/stream/{uuid}` and
the Xtream URL) end up in the same view (`stream_ts`), which reads that parameter.

- An opt-in setting, *Live TV → Dispatcharr Playback → Send video as the first track*
  (`DispatcharrVideoFirst`), default off.
- On each channel refresh the plugin looks for an output profile named exactly
  `Emby Xtream: video first` and creates it when there is none. The profile copies the stream with
  `-map 0:v:0? -map 0:a? -map 0:s? -c copy`, using the same surrounding flags as Dispatcharr's own
  built-in profiles. An audio-only channel keeps its audio at 0, which is what the plugin declares
  for it.
- The id is kept in memory and added to the stream URL (`&output_profile=` on the proxy URL,
  `?output_profile=` on the Xtream URL). Playback only reads the stored id; it never calls
  Dispatcharr, so nothing is added in front of a tune (BUG-007). Until a refresh has resolved an
  id, channels play without it, as before.
- A profile with the name is used whoever created it, so an admin can create it by hand for an
  account that may not. One that is switched off in Dispatcharr is left alone.
- The lookup never retries: a 404 (no output profiles, older Dispatcharr) or a 403 (account may
  not create) is logged once and the setting does nothing. A 400 on create, normally a concurrent
  refresh that created it first, reads the list again.

## Consequences

- Dispatcharr runs one extra ffmpeg copy step per playing channel while the setting is on. It
  copies, so CPU cost is small, and Dispatcharr shares one step between all clients of a channel.
- The step adds start-up time in front of the first bytes. Measured on the maintainer's server
  when this was written (Emby 4.10, one channel, two transcodes each): first HLS playlist after
  2.7 to 2.9 s without the profile, 3.4 to 3.7 s with it, so about 0.7 s.
- Off by default, so nothing changes for users who don't have the problem. The trade-off is that
  users who do have it have to find the setting; the README's Troubleshooting entry quotes the
  exact error from the transcode log.
- The profile name is the lookup key. Renaming `VideoFirstOutputProfile.Name` strands profiles
  already created; changing its parameters only affects servers where it is created anew.
- Users without Dispatcharr are not affected: with no stream stats, Emby probes the stream and
  finds the real order itself (unless probing is turned off by hand).
