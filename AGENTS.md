# Emby Xtream Plugin — Development Notes

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

`PluginConfiguration` is serialized to XML by Emby automatically. Fields added to it persist across restarts without any extra work. Use this for: sync watermarks (`LastMovieSyncTimestamp`, `LastSeriesSyncTimestamp`), and similar state.

### Guide grid empty — check browser localStorage

If the Emby guide grid shows no channels despite having channel data, check browser localStorage for a stale `guide-tagids` filter. The guide calls `/LiveTv/EPG?TagIds=<id>` and if the stored tag ID doesn't match any channel, the grid is empty. Fix: click the filter icon in the guide or run `localStorage.removeItem('guide-tagids')` in the browser console.

### Channel logos come from `stream_icon` only

`ChannelInfo.ImageUrl` is set exclusively from `LiveStreamInfo.StreamIcon` (`XtreamTunerHost.cs:493`) — the `stream_icon` field returned by `player_api.php?action=get_live_streams`. The plugin never sources channel logos from XMLTV, from `<channel><icon>` in EPG, or from any tvg-logo M3U attribute. XMLTV `<icon>` is only used for *program* images (`XmltvParser.cs:150` → `ProgramInfo.ImageUrl`).

The only other write path is `StampGracenoteLogosOntoChannels` (ADR-007), which fills `ImageUrl` from listings providers when (a) the channel has a Gracenote station ID, (b) `DeferEpgToGuideData` is on, and (c) `ImageUrl` is currently empty. It never overwrites a populated value — upstream-supplied logos always win.

**Implication for upstream integrations**: any Xtream-compatible source (Dispatcharr, M3U Editor, raw provider) is responsible for putting the desired logo in `stream_icon`. If a user sees an unexpected logo, the diagnostic is to curl `player_api.php?action=get_live_streams` directly and inspect the field — what's there is what Emby will show.

### One DLL for Emby 4.9 and 4.10

The shipped DLL is compiled against the 4.9 SDK and loads on 4.10 because `XtreamLiveStream`
always carries `AddConsumer(string)` / `RemoveConsumer(string)`; the runtime matches them to
4.10's `ILiveStream` by name and signature. **Never put them back behind `#if EMBY_4_10`**: that
is exactly what made the 4.9 DLL fail on 4.10 with `Method 'AddConsumer' ... does not have an
implementation`. The `Release_4_10` configuration is kept only as a compile check, and
`scripts/sdk-load-check` loads the built DLL against both `lib/` SDKs in CI and before every
release. See [ADR-017](docs/decisions/017-single-dll-for-emby-4-9-and-4-10.md).

### SupportsGuideData controls whether Emby polls the tuner for EPG

When `SupportsGuideData()` returns `true`, Emby calls `GetProgramsInternal` on the tuner host for each channel. The `tunerChannelId` parameter is whatever was set in `ChannelInfo.TunerChannelId` — the Gracenote station ID (e.g. `"51529"`) when Dispatcharr is enabled and a station ID exists for the channel, or the raw stream ID (e.g. `"12345"`) otherwise. Use `_tunerChannelIdToStreamId` to translate either form back to a stream ID.

### Emby probes MediaSource.Path directly — disable for Dispatcharr

When `SupportsProbing = true` and `AnalyzeDurationMs > 0`, Emby runs ffprobe against `MediaSource.Path` **independently** of `GetChannelStream` / `ILiveStream`. For Dispatcharr proxy URLs this is destructive: the probe opens a short-lived HTTP connection (~0.1s, ~120KB), then closes it. Dispatcharr interprets the close as the last client leaving and tears down the channel. The real playback connection that follows immediately hits the teardown "channel stop signal" and fails — triggering a rapid retry storm visible in Dispatcharr logs as repeated `Fetchin channel with ID: <n>` → broken pipe cycles.

**Rule**: Always set `SupportsProbing = false` and `AnalyzeDurationMs = 0` for Dispatcharr proxy URLs (`/proxy/ts/stream/{uuid}`), regardless of whether stream stats are available. Direct Xtream URLs (no Dispatcharr) can still use probing when stats are absent.

### The declared video codec comes from the Dispatcharr stream profile

`stream_stats.video_codec` is the codec Dispatcharr **ingested** from the provider, not the one
it **emits**. On a stream profile that transcodes (HEVC source, H.264 output) declaring it makes
Emby force the wrong decoder and the channel dies with `Invalid NAL unit` before playback starts.

`XtreamTunerHost.ResolveVideoCodec` therefore asks the channel's stream profile first
(`StreamProfileCodec` parses `-c:v` and friends out of the profile's ffmpeg parameters), falls
back to `stream_stats.video_codec` when the profile passes video through or cannot be read, and
falls back again to `h264`. **No path may declare a null codec**: Emby's
`RecordingRequiresEncoding` dereferences `MediaStream.Codec`, so a null turns a recording into a
`NullReferenceException`.

Profile data is fetched on the cached refresh path only (`BuildProfileCodecMapAsync`). Dispatcharr
lookups at playback time are what BUG-007 was about. `Profile`, `Level`, `BitDepth` and
`RefFrames` are only declared when the declared codec is the reported one, since they describe the
ingested codec. `DispatcharrVideoCodecSource` (`auto` / `stats` / `h264` / `hevc`) overrides the
whole thing. See [ADR-016](docs/decisions/016-dispatcharr-stats-codec-trust.md).

### Dispatcharr channels can play from the Xtream URL, opt-in

*Dispatcharr → Stream URL* = Xtream makes a channel with a UUID play from
`{BaseUrl}/live/{user}/{pass}/{id}.ts` so Dispatcharr shows the right user. It is only used when
`BaseUrl` is the Dispatcharr server (same scheme, host, port and path); otherwise the proxy URL is kept, so
the setting never routes around Dispatcharr. It still counts as a Dispatcharr stream: probing off,
profile codec wins. See [ADR-022](docs/decisions/022-dispatcharr-xtream-stream-url-opt-in.md).

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

### Recordings retry until the stream is live; live viewing does not

Emby records through `CopyToAsync(Stream, …)` (its `DirectRecorder`, chosen because
`SupportsCopyTo = true`) and plays live TV through `CopyToAsync(PipeWriter, …)`. Only the
recording overload retries: it keeps trying every 10 s until Emby cancels the token at the end of
the recording window, holds back the first 64 KB of each attempt so error packets never reach the
file, and reconnects and appends when a live stream drops.

**Never let a timeout escape that overload as `OperationCanceledException`.** Emby reads any OCE
from the recorder as "Recording stopped" and marks it Completed without retrying, which is
how event recordings that started before the event ended up empty. Only the recording token's own
cancellation may escape. See [ADR-021](docs/decisions/021-recording-retries-until-the-stream-is-live.md).

### DVB subtitles are declared statically, not probed

Because the plugin disables stream probing (`SupportsProbing = false`, `AnalyzeDurationMs = 0`) to keep channel switching fast and to avoid the Dispatcharr teardown storm, Emby never discovers DVB subtitle tracks embedded in MPEG-TS by itself. The optional `DeclareDvbSubtitles` config flag tells `CreateMediaSourceInfo` to append two `dvb_subtitle` `MediaStream` entries (regular + hearing impaired) to every live channel. ffmpeg silently drops them on sources that don't carry subtitle PIDs, so the cost is two unused entries in the player menu on non-DVB channels. See [ADR-009](docs/decisions/009-dvb-subtitle-static-declaration.md).

A diagnostic endpoint `GET /XtreamTuner/StreamStats` exposes the cached Dispatcharr stream stats for all known streams (codecs, resolution, bitrate, audio language) and is useful when reasoning about what metadata is or isn't reaching the plugin.

### Provider fields go through tolerant converters

Xtream providers type the same field differently across (and within) servers. A nominally-string field like `info.releasedate`, `rating`, or `tmdb` can arrive as a bare number, a boolean, `null`, or an empty array; a nominally-integer field like `info.category_id` can arrive as an empty string, a non-numeric string, or an array. The default `System.Text.Json` readers throw (`"could not be converted to System.String"` / `"...System.Nullable\`1[System.Int32]"`) on any of these, and because a whole `get_series_info` payload is parsed in one call, one off-type field aborts the entire series/VOD sync.

Two converters handle this, both registered on `StrmSyncService.JsonOptions` and the `XtreamTunerApi` series-list options:
- `Client/Models/TolerantStringConverter` coerces any JSON token into a string (or `null` for structured values).
- `Client/Models/TolerantNullableIntConverter` coerces numbers and numeric strings into `int`, and everything else (empty/non-numeric strings, arrays, objects, `null`) into `null`. Only nullable ints are tolerant — non-nullable provider IDs (`series_id`, `stream_id`) stay strict so a missing primary ID fails loudly instead of defaulting to `0`.

**Don't replace these provider-model properties with the default converters** — the tolerant ones are load-bearing for provider interop. `NumberHandling = AllowReadingFromString` only covers the string → number direction (and only for strictly-numeric strings), not number → string or empty/garbage → int. See [ADR-010](docs/decisions/010-tolerant-provider-deserialization.md).

### Per-item exclusions delete folders directly, not via orphan cleanup

`ExcludedVodStreamIds` / `ExcludedSeriesIds` remove an item's files in a targeted pass (`StrmSyncService.RemoveExcludedContent`) that ignores both `CleanupOrphans` and `OrphanSafetyThreshold`. That threshold guards against a provider returning a truncated catalogue; an exclusion is a deliberate user action, so suppressing the delete would just read as the filter doing nothing. Folder matching strips `[tmdbid=…]`/`[tvdbid=…]` suffixes, so a title is found regardless of the metadata-ID naming settings in force when it was written.

**Never recursively delete a matched folder.** Matching is by title only, so a match is not proof of ownership. The first implementation used `Directory.Delete(dir, recursive: true)` and could destroy user data (BUG-028).

Ownership is decided by file content, not by extension or a manifest — see `StrmOwnership` and [ADR-014](docs/decisions/014-content-based-strm-ownership.md). Every `.strm` the plugin writes holds the provider URL it was built from, so a `.strm` starting with `BaseUrl` (or `DispatcharrUrl`, for multi-version entries) is ours and nothing else is. NFOs match structurally against the only two shapes the plugin writes: `{folderName}.nfo` beside its `.strm`, and `tvshow.nfo` at a series root.

All three delete paths go through this — orphan cleanup, per-item exclusion, and the "delete all content" button. A user's own `Ben-Hur` survives excluding the provider's `Ben-Hur`, and so does a `trailer.strm` or hand-written `.nfo` sitting in a folder alongside plugin output. Everything fails safe: unreadable, empty or non-matching means foreign means kept.

**Never delete a folder the current run wrote into.** Exclusion is by stream ID but the folder match is by cleaned name, ignoring case, so a kept twin ("Twin Title" vs "twin title") shares the excluded one's folder. `RemoveExcludedContent` takes `writtenPaths` and skips those folders. The pass is postponed when a category fails to load; a failed item instead records its existing files in `writtenPaths` from its catch block, because gating on item failures would block exclusions for as long as the item stays failed. See [ADR-018](docs/decisions/018-exclusion-never-deletes-what-the-sync-wrote.md).

Both sync methods compute their delta watermark from the **unfiltered** catalogue — if the newest title happens to be excluded, the watermark must still advance past it or every later sync re-processes everything after it. Re-ticking a title needs no watermark reset: both smart-skip paths already guard on the folder existing. See [ADR-012](docs/decisions/012-per-item-content-exclusion.md).

### Absence of provider data is never evidence of deletion

Orphan cleanup works by difference, so it is only correct when the run saw the whole catalogue. A per-category request that fails must not flatten into an empty list: that is indistinguishable from an empty category, and one failing category among many stays well under `OrphanSafetyThreshold`, so the ratio guard never fires. `CatalogueFetchResult<T>` carries `FailedCategoryCount` and gates cleanup on it.

**Gate cleanup on completeness, never the watermark.** Freezing the watermark on a partial failure makes every later sync re-process the categories that succeeded. Completeness is required to delete, not to record progress on what was actually seen.

An empty `get_series_info` payload for a series that already has files on disk is treated as a transient failure: the existing paths are preserved and `Failed` is incremented, which trips the cleanup guard that already exists. This matters because ADR-010's tolerant converters deliberately turn malformed values into empty rather than throwing, so garbage arrives here looking like "no episodes".

The `existingStrms.Length > 10` gate on the ratio check stays. Removing it means a one-file library has ratio 1.0 and never gets cleaned. The separate `validPaths.Count == 0` guard is what actually closes the data-loss path at small N. See [ADR-013](docs/decisions/013-partial-catalogue-cleanup-guard.md).

### The sync's delete code lives in its own file for mutation testing

`RemoveExcludedContent`, `CleanupOrphans` and their helpers are in `Service/StrmSyncService.Cleanup.cs` (a partial of `StrmSyncService`). Stryker mutates that whole file; it cannot compile its changes to the rest of `StrmSyncService.cs`, and a line range into that file went stale whenever code was added above it (issue #75). `scripts/check-delete-sites.py` fails CI if a delete call of the sync service appears anywhere else. Run Stryker locally with `DOTNET_ROOT=<dotnet libexec> DOTNET_ROLL_FORWARD=Major ~/.dotnet/tools/dotnet-stryker --config-file ../stryker-config.json` from `Emby.Xtream.Plugin.Tests` (it ships as net8.0).

### One sync of each kind at a time, enforced in the service

`MovieProgress.IsRunning` is a fast path for callers, not a lock. The API endpoints and the scheduled tasks both check it and then call, so two runs can pass the check, share `writtenPaths` and the progress object, and have the first to finish clear `IsRunning` while the second is still writing. `StrmSyncService` holds a `SemaphoreSlim` for the whole operation and returns `false` when it declined to run; callers report "already running" from that, not from their own check. `RetryFailedAsync` takes the movie gate because it writes movie files and reuses `_movieProgress`.

Series in the failed list are forced through the next series sync (both skip paths bypassed), because the watermark has already moved past them. `RetryFailedAsync` retries series by calling `SyncSeriesCoreAsync`, never by writing episodes itself: a separate writer drifted into its own layout (`Season 1`, `S1E01.strm`) and duplicated shows. Movies are retried through `WriteMovieAsync`, the same per-movie writer the movie sync uses, with the same folder mappings and Dispatcharr client (and category names in the old `multiple` mode); a separate retry writer never wrote Dispatcharr's extra versions and wrote nothing in `multiple` mode. Only a movie that was actually written leaves the failed list; one that cannot be placed stays there for the next sync.

Note for tests: `FakeHttpHandler` responds synchronously, so a caller runs to completion before returning its `Task` and two syncs can never overlap. Set `Handler.Gate` to hold requests in flight, or a concurrency test passes while testing nothing.

### Every plugin route is admin-only

Emby (4.10.1, `ServiceController.RegisterService`) gives a route with no attribute a plain
`[Authenticated]`: a login is required, but no role is checked, so any signed-in user could
delete content or restart the server. Every request DTO in `Api/XtreamTunerApi.cs` carries
`[Authenticated(Roles = "Admin")]` and `AuthenticatedRoutesTests` fails on one that doesn't.
Never use `AllowLocal`: it skips authentication entirely for requests from the local network.

## Tests

### `FakeHttpHandler` responses are single-shot and match by substring in registration order

`RespondWith(urlSubstring, body)` queues **one** response. A test that runs the same sync twice starves on the second call and throws `no registered response for URL: …`. For a multi-phase test use `RespondWithSequence(urlSubstring, new[] { body, body, body })` — one body per expected call.

Matching walks the rules in registration order and takes the first whose substring is contained in the URL *and* still has a queued response. That makes registration order load-bearing whenever one pattern is a prefix of another: `"action=get_series"` is a prefix of `"action=get_series_info"`, so the **detail rule must be registered first**. Single-shot registrations happen to survive this by draining, which is why the existing tests get away with the opposite order — sequences do not.

### Every plugin route is admin-only

Emby (4.10.1, `ServiceController.RegisterService`) gives a route with no attribute a plain
`[Authenticated]`: a login is required, but no role is checked, so any signed-in user could
delete content or restart the server. Every request DTO in `Api/XtreamTunerApi.cs` carries
`[Authenticated(Roles = "Admin")]` and `AuthenticatedRoutesTests` fails on one that doesn't.
Never use `AllowLocal`: it skips authentication entirely for requests from the local network.

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

### The README changes in the same PR as the setting

A PR that adds, renames or removes a user-facing setting, changes a default, or changes what a
button does, updates `README.md` in the same PR. The README is what new users read first, and
it drifted when this was left for later: by v1.4.112 it listed a folder mode that no longer
existed, the wrong default for orphan cleanup and the wrong .NET SDK. Describe settings by the
label the config page shows, and keep defaults out of the README unless a user has to know them.

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
