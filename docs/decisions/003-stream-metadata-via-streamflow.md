# ADR 003 — Stream metadata routed through Streamflow rather than probed by the plugin

## Context

Emby uses `MediaSourceInfo.MediaStreams` to decide whether a live TV stream can be
direct-played or must be transcoded. The relevant fields are:

- **Video**: `Profile`, `Level`, `BitDepth`, `RefFrames` — used to check client HW decoder limits
- **Audio**: `Language`, `ChannelLayout`, `SampleRate` — used for track labelling and auto-selection
- **Timing**: `WallClockStart`, `ContainerStartTimeTicks`, `StreamStartTimeTicks` — used for
  scrubber positioning and DVR buffer alignment

An M3U tuner gets all of these by probing the stream via ffprobe at stream-open time.
The Xtream plugin serves channels through a Dispatcharr proxy (`/proxy/ts/stream/<uuid>`),
which cannot be probed safely.

## Problem

Probing the Dispatcharr proxy URL causes a CLOSE-WAIT stuck state (see ADR 001). A probe
opens a short-lived HTTP connection (~0.1s), then closes it. Dispatcharr interprets the
close as the last client leaving and tears down the channel. The real playback connection
then fails immediately. Recovery requires restarting the Dispatcharr worker processes.

This rules out probing at plugin level at stream-open time, which is how M3U tuners
obtain their metadata.

## Alternatives considered

**1. Plugin probes the Dispatcharr proxy at scheduled intervals (not at open time)**
Avoids the teardown race but still leaves orphaned Redis state and consumes a stream slot
during the probe window. Dispatcharr's 1-stream limit means a background probe would block
real playback. Rejected.

**2. Plugin probes the source URL directly**
The plugin does not have access to the raw source URL — only the Dispatcharr proxy UUID.
Dispatcharr selects which underlying stream to serve dynamically. Not feasible.

**3. Plugin returns dummy defaults and lets Emby probe**
Setting `SupportsProbing = true` delegates the problem to Emby's ffprobe, which probes
the proxy URL at startup. This is exactly the scenario that causes the CLOSE-WAIT storm
documented in ADR 001. Rejected.

**4. Streamflow adds ffprobe call during its background scan and stores results in Dispatcharr**
Streamflow already runs ffmpeg against source URLs in the background on a schedule
(`stream_last_measured_days` cycle, default 1 day). Adding a fast ffprobe JSON call
(`~2-3s`) to each scan pass gives structured metadata (profile, level, bit depth, refs,
language, sample rate, channel layout) without touching the Dispatcharr proxy. Results
are stored in Dispatcharr's `stream_stats` JSON field and read by the plugin at startup
via the existing channel data API call. Accepted.

## Decision

Structured stream metadata is extracted by **Streamflow** during its background scan using
`ffprobe -print_format json -show_streams` against the raw source URL. Results are PATCHed
into `stream_stats` on Dispatcharr. The plugin reads `stream_stats` at startup and maps
fields into `MediaStreamInfo` properties without any network call to the stream at playback
time.

Implementation: [krinkuto11/streamflow#334](https://github.com/krinkuto11/streamflow/pull/334)
(pending merge; volume-mounted patch applied in the interim).

Fields now available: `video_profile`, `video_level`, `video_bit_depth`, `video_ref_frames`,
`audio_language`, `sample_rate`, `audio_channels` (normalised channel layout).

## Consequences

**Solved**
- Emby can make correct direct-play vs transcode decisions for HEVC 10-bit, high-level
  H.264, and high-ref-count streams.
- Audio track picker shows language ("Dutch AC3 5.1" instead of "AC3 5.1").
- Emby preferred-language auto-selection works for live TV.

**Remaining gaps** (require live stream probing — not safely doable)

| Field | Effect of absence |
|---|---|
| `WallClockStart` | Emby cannot anchor the scrubber to real clock time. DVR buffer window is unanchored. Contributes to "rocky until stable" startup behaviour. Partial mitigation: set to `DateTime.UtcNow` at stream open (approximate but better than nothing). |
| `ContainerStartTimeTicks` / `StreamStartTimeTicks` / `TimeBase` | Emby must infer PTS alignment on the fly during first seconds of playback. Root cause of remaining startup instability. Not fixable without probing. |
| Subtitle streams | EIA 608 closed captions and other embedded subtitle tracks are invisible to Emby. Not fixable without probing. |

**Architectural dependency**
The plugin now depends on Streamflow having scanned a channel before its metadata is
available. Channels not yet scanned fall back to the dummy H264/AAC defaults. Stats
become stale if the source changes codec (e.g. provider upgrades from H.264 to HEVC)
between scan cycles.

**Dispatcharr stream selection**
`stream_stats` are stored per-stream. Dispatcharr may fail over to a different stream at
playback time, and the plugin will show metadata for the primary/scanned stream rather
than the one actually being served. For profile/level/bit depth this is usually harmless
(same provider, same codec family). For language it could be wrong if backup streams have
different audio.
