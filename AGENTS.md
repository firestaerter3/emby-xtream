# Emby Xtream Plugin — Development Notes

## See also

- `Jellyfin-Xtream-Library/` — shared Xtream plugin architecture / code patterns; primary Jellyfin variant
- `Dispatcharr/` — IPTV proxy this plugin consumes

## Emby Plugin Architecture

### Emby scans and directly instantiates public service classes via SimpleInjector

**Critical**: Emby's `ApplicationHost.CreateInstanceSafe` scans the plugin assembly and auto-registers ALL public classes that have a constructor matching known DI types (e.g. `ILogger`). It then instantiates these directly via SimpleInjector — **before** the `Plugin` constructor runs.

This means:
- `Plugin.Instance` is **null** when Emby creates these service classes
- `Plugin.Instance.Configuration` will throw (via SimpleInjector wrapping as `ActivationException`)
- **Never call `Plugin.Instance.*` in a service class constructor** (e.g. `StrmSyncService`, `LiveTvService`, `TmdbLookupService`)

**Safe pattern**: Access `Plugin.Instance.Configuration` only from methods called at runtime (not construction time). The `Plugin` constructor calls `new ServiceClass(logger)` itself, but Emby may also create the service independently beforehand.

### Plugin.Instance is set early but Configuration loading requires ApplicationPaths

`BasePlugin<T>.get_Configuration()` calls `Path.Combine(ApplicationPaths.PluginConfigurationsPath, ...)` internally. This path may not be fully initialized when Emby is scanning services, causing `ArgumentNullException: Value cannot be null. (Parameter 'path2')`.

### Delta sync timestamps survive restarts via PluginConfiguration

`PluginConfiguration` is serialized to XML by Emby automatically. Fields added to it persist across restarts without any extra work. Use this for: sync watermarks (`LastMovieSyncTimestamp`, `LastSeriesSyncTimestamp`), channel hashes (`LastChannelListHash`), and similar state.

### Guide grid empty — check browser localStorage

If the Emby guide grid shows no channels despite having channel data, check browser localStorage for a stale `guide-tagids` filter. The guide calls `/LiveTv/EPG?TagIds=<id>` and if the stored tag ID doesn't match any channel, the grid is empty. Fix: click the filter icon in the guide or run `localStorage.removeItem('guide-tagids')` in the browser console.

### Channel logos come from `stream_icon` only

`ChannelInfo.ImageUrl` is set exclusively from `LiveStreamInfo.StreamIcon` (`XtreamTunerHost.cs:493`) — the `stream_icon` field returned by `player_api.php?action=get_live_streams`. The plugin never sources channel logos from XMLTV, from `<channel><icon>` in EPG, or from any tvg-logo M3U attribute. XMLTV `<icon>` is only used for *program* images (`XmltvParser.cs:150` → `ProgramInfo.ImageUrl`).

The only other write path is `StampGracenoteLogosOntoChannels` (ADR-007), which fills `ImageUrl` from listings providers when (a) the channel has a Gracenote station ID, (b) `DeferEpgToGuideData` is on, and (c) `ImageUrl` is currently empty. It never overwrites a populated value — upstream-supplied logos always win.

**Implication for upstream integrations**: any Xtream-compatible source (Dispatcharr, M3U Editor, raw provider) is responsible for putting the desired logo in `stream_icon`. If a user sees an unexpected logo, the diagnostic is to curl `player_api.php?action=get_live_streams` directly and inspect the field — what's there is what Emby will show.

### SupportsGuideData controls whether Emby polls the tuner for EPG

When `SupportsGuideData()` returns `true`, Emby calls `GetProgramsInternal` on the tuner host for each channel. The `tunerChannelId` parameter is whatever was set in `ChannelInfo.TunerChannelId` — the Gracenote station ID (e.g. `"51529"`) when Dispatcharr is enabled and a station ID exists for the channel, or the raw stream ID (e.g. `"12345"`) otherwise. Use `_tunerChannelIdToStreamId` to translate either form back to a stream ID.

### Emby probes MediaSource.Path directly — disable for Dispatcharr

When `SupportsProbing = true` and `AnalyzeDurationMs > 0`, Emby runs ffprobe against `MediaSource.Path` **independently** of `GetChannelStream` / `ILiveStream`. For Dispatcharr proxy URLs this is destructive: the probe opens a short-lived HTTP connection (~0.1s, ~120KB), then closes it. Dispatcharr interprets the close as the last client leaving and tears down the channel. The real playback connection that follows immediately hits the teardown "channel stop signal" and fails — triggering a rapid retry storm visible in Dispatcharr logs as repeated `Fetchin channel with ID: <n>` → broken pipe cycles.

**Rule**: Always set `SupportsProbing = false` and `AnalyzeDurationMs = 0` for Dispatcharr proxy URLs (`/proxy/ts/stream/{uuid}`), regardless of whether stream stats are available. Direct Xtream URLs (no Dispatcharr) can still use probing when stats are absent.

### SupportsDirectStream controls whether native clients bypass server-side segmentation

`SupportsDirectStream = true` in `MediaSourceInfo` tells Emby the stream can be forwarded directly to the client without transcoding. Native Emby clients (Apple TV, iOS, Android) prefer this path when available — they receive raw MPEG-TS or the proxy URL and play it directly. This skips Emby's HLS segmentation pipeline entirely.

**Consequence for timeshift**: Emby's DVR/timeshift buffer is built from HLS segments produced by the transcoding pipeline. Direct stream bypasses that pipeline, so no buffer is created and timeshift does not work.

| Client | Default path | Timeshift? |
|--------|-------------|------------|
| Browser | Always HLS transcode (browsers can't play raw MPEG-TS) | Works |
| Apple TV / iOS / Android | Direct stream when `SupportsDirectStream = true` | Broken |
| Native + "playback correction" | Forced HLS transcode | Works (same as browser) |

**"Playback correction"** is a client-side setting in the Emby Apple TV / iOS / Android apps. It forces the client into the transcoding path, restoring timeshift. This is the correct workaround — not a plugin bug.

Setting `SupportsDirectStream = false` globally would force transcoding everywhere (fixing timeshift on all clients) but increases CPU load for every stream. `ForceAudioTranscode` already does this selectively for AC3/EAC3 streams.

This behaviour applies to all tuner types (M3U, Xtream, HDHomeRun) — it is not plugin-specific. Emby has acknowledged native client timeshift as a known limitation (as of early 2025).

### DVB subtitles are declared statically, not probed

Because the plugin disables stream probing (`SupportsProbing = false`, `AnalyzeDurationMs = 0`) to keep channel switching fast and to avoid the Dispatcharr teardown storm, Emby never discovers DVB subtitle tracks embedded in MPEG-TS by itself. The optional `DeclareDvbSubtitles` config flag tells `CreateMediaSourceInfo` to append two `dvb_subtitle` `MediaStream` entries (regular + hearing impaired) to every live channel. ffmpeg silently drops them on sources that don't carry subtitle PIDs, so the cost is two unused entries in the player menu on non-DVB channels. See [ADR-009](docs/decisions/009-dvb-subtitle-static-declaration.md).

A diagnostic endpoint `GET /XtreamTuner/StreamStats` exposes the cached Dispatcharr stream stats for all known streams (codecs, resolution, bitrate, audio language) and is useful when reasoning about what metadata is or isn't reaching the plugin.

### Provider string fields go through a tolerant converter

Xtream providers type the same field differently across (and within) servers: a nominally-string field like `info.releasedate`, `rating`, or `tmdb` can arrive as a bare number, a boolean, `null`, or an empty array. The default `System.Text.Json` string reader throws `"The JSON value could not be converted to System.String"` on any of these, and because a whole `get_series_info` payload is parsed in one call, one off-type field aborts the entire series/VOD sync.

`Client/Models/TolerantStringConverter` coerces any JSON token into a string (or `null` for structured values) and is registered on `StrmSyncService.JsonOptions` and the `XtreamTunerApi` series-list options. **Don't replace string properties on the provider models with the default converter** — the tolerant one is load-bearing for provider interop. `NumberHandling = AllowReadingFromString` only covers the string → number direction, not number → string. See [ADR-010](docs/decisions/010-tolerant-provider-deserialization.md).

### Per-item exclusions delete folders directly, not via orphan cleanup

`ExcludedVodStreamIds` / `ExcludedSeriesIds` remove an item's files in a targeted pass (`StrmSyncService.RemoveExcludedContent`) that ignores both `CleanupOrphans` and `OrphanSafetyThreshold`. That threshold guards against a provider returning a truncated catalogue; an exclusion is a deliberate user action, so suppressing the delete would just read as the filter doing nothing. Folder matching strips `[tmdbid=…]`/`[tvdbid=…]` suffixes, so a title is found regardless of the metadata-ID naming settings in force when it was written.

**Never recursively delete a matched folder.** Matching is by title only, so a match is not proof of ownership. The pass skips any matched folder containing no `.strm` (it wasn't written by us — a user's own `Ben-Hur` must survive excluding the provider's `Ben-Hur`), deletes only `.strm`/`.nfo`, then prunes what that emptied. Same contract as `CleanupOrphans`. The first implementation used `Directory.Delete(dir, recursive: true)` and could destroy user data.

Both sync methods compute their delta watermark from the **unfiltered** catalogue — if the newest title happens to be excluded, the watermark must still advance past it or every later sync re-processes everything after it. Re-ticking a title needs no watermark reset: both smart-skip paths already guard on the folder existing. See [ADR-012](docs/decisions/012-per-item-content-exclusion.md).

## Tests

### `FakeHttpHandler` responses are single-shot and match by substring in registration order

`RespondWith(urlSubstring, body)` queues **one** response. A test that runs the same sync twice starves on the second call and throws `no registered response for URL: …`. For a multi-phase test use `RespondWithSequence(urlSubstring, new[] { body, body, body })` — one body per expected call.

Matching walks the rules in registration order and takes the first whose substring is contained in the URL *and* still has a queued response. That makes registration order load-bearing whenever one pattern is a prefix of another: `"action=get_series"` is a prefix of `"action=get_series_info"`, so the **detail rule must be registered first**. Single-shot registrations happen to survive this by draining, which is why the existing tests get away with the opposite order — sequences do not.

## Architecture Decision Records (ADRs)

Significant decisions are recorded in `docs/decisions/NNN-title.md`. Create a new ADR when:
- Choosing between multiple viable approaches (especially after trying alternatives that failed)
- Making a change driven by a non-obvious root cause
- Reversing or replacing a previous approach

Format: see `docs/decisions/001-bypass-dispatcharr-proxy.md` as the template. Each ADR should include Context, Problem, Alternatives considered, Decision, and Consequences.

Numbering: sequential, zero-padded to 3 digits (`001`, `002`, ...).

## Git Workflow

### Never create a GitHub release without explicit user approval

Tag the commit and push the tag, then stop and ask: "Ready to create the GitHub release for vX.Y.Z — shall I proceed?" Do not run `gh release create` until the user says yes.

### Commit before switching context

Never leave changes in the working tree when starting unrelated work or ending a session. An uncommitted change is invisible and easy to tangle with later work. Use a `WIP:` commit or `git stash` if the change isn't ready.

### One concern per branch

Unrelated fixes should live on separate short-lived branches (e.g. `fix/audio-codec-passthrough`, `fix/dispatcharr-probe-storm`) and be merged to `main` independently. This makes each change revertable without touching unrelated code.

### Check `git status` at the start of every session

The git status shown at conversation start reflects the state of the working tree. A modified file there means something is already in flight — address it before starting new work.

### Release notes must credit bug reporters

When editing or creating GitHub release notes, each bug fix entry should include the reporter in brackets:

```
- Fix Dispatcharr reconnect storm by disabling stream probing (reported by scottrobertson)
```

Use the reporter name from `BUGS.md`. If a bug has multiple reporters, list all of them. Internal/self-discovered fixes need no reporter credit. Auto-generated release notes from GitHub never include this — always edit them manually after tagging.

### Release notes must be written for users, not developers

Release notes are read by non-technical users deciding whether to update. Write them from the user's perspective:

- **Lead with what the user experiences**, not what changed in the code. "Channels were failing to play" beats "UUID lookup key was incorrect".
- **Explain the symptom, then the cause, then the fix** — in that order. Users need to recognise their own problem before they care about the solution.
- **Bug fixes**: describe what the user saw (the error message or behaviour), why it happened in plain terms, and what is now different. Credit the reporter at the end of the section.
- **New features**: describe what the user can now do and where to find it. Include the config path if there's a UI setting involved (e.g. *Plugin Config → Settings → STRM Sync Settings*).
- **Avoid commit-log language**: phrases like `feat:`, `fix:`, `refactor:`, or "add X via Y" belong in git history, not release notes.
- Use a `## Bug Fix` or `## What's New` top-level heading, then a `### Short symptom-focused title` subheading per item.

**Example — bad:**
```
- feat: fix Dispatcharr UUID mapping for URL-based stream sources
```

**Example — good:**
```
### "Dispatcharr Proxy Unavailable" for Some Channels (reported by Joe 🇺🇸)

Some channels were failing to play with a *Dispatcharr proxy unavailable* error even
though Dispatcharr itself was running fine and other channels worked normally. ...
```
