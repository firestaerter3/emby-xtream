# ADR-005: Detach Listing Providers for Gracenote EPG Control

**Date**: 2026-03-22
**Status**: ADOPTED (v1.4.61), REVISED (v1.4.69)
**Affects**: `XtreamTunerHost.DetachListingProviders()`, `GetChannelsInternal()`, `ClearWrongChannelArtwork()`

---

## Context

Users with Dispatcharr and Channel Identifiarr can assign Gracenote station IDs (`tvc_guide_stationid`) to their IPTV channels. When "Use Emby Guide Data" is enabled, the plugin should use Gracenote EPG for channels that have station IDs and Xtream EPG for channels that don't.

The challenge is Emby's auto-mapping behaviour: when a listing provider (Gracenote lineup) covers a tuner, Emby automatically maps every channel to the closest Gracenote station using name, call sign, and channel number heuristics. These heuristic matches are frequently wrong — e.g. CBS channel 116 gets mapped to VICE (Gracenote station 18822) because VICE happens to be channel 116 in the Gracenote lineup.

## Problem

Setting `ListingsChannelId` and `CallSign` on `ChannelInfo` was not enough to override Emby's auto-mapper. Emby re-runs its mapping logic and overrides explicit plugin-provided IDs with its own heuristic matches.

## Alternatives Considered

### 1. `ILiveTvManager.SetChannelMapping()` (v1.4.59-60)

Programmatically called `SetChannelMapping()` after channel refresh to explicitly map each channel to its correct Gracenote station, and mapped channels without station IDs to empty strings.

**Result**: Failed. Emby's auto-mapper re-ran on subsequent refreshes and overrode the explicit mappings. Channels without Gracenote IDs either got wrong heuristic matches or lost all EPG (including Xtream EPG). Also processed far too many channels (3000+ instead of the user's 237) because it iterated across all listing providers.

### 2. Map to empty `providerChannelNumber` (v1.4.60)

For channels without Gracenote IDs, explicitly mapped them to an empty `providerChannelNumber` to prevent auto-mapping.

**Result**: Failed. Emby still auto-mapped these channels on the next refresh cycle. The empty mapping didn't persist.

### 3. Detach listing providers + direct fetch (v1.4.61) — ADOPTED

Remove the Xtream tuner from all listing provider associations entirely, then fetch Gracenote programs directly from within `GetProgramsInternal()`.

## Decision

Adopted approach 3. The implementation has three parts:

### DetachListingProviders()

Resolves `IConfigurationManager` via DI, reads `LiveTvOptions`, and for each listing provider:
- If `EnableAllTuners` is true: sets it to false and populates `EnabledTuners` with all non-Xtream tuner IDs
- If the Xtream tuner ID is in `EnabledTuners`: removes it

Saves the modified configuration. Runs automatically after every channel refresh.

### FetchGracenotePrograms() in GetProgramsInternal

When a channel has a station ID in `_stationIdMap` and `DeferEpgToGuideData` is true:
1. Resolves `ILiveTvManager` and iterates its `ListingProviders`
2. Calls `IListingsProvider.GetProgramsAsync(providerInfo, stationId, startDate, endDate)`
3. Re-assigns `ChannelId` on each returned `ProgramInfo` to the tuner's channel ID
4. Returns the programs to Emby

If no Gracenote data is returned, falls back to Xtream EPG.

### ClearWrongChannelArtwork() (v1.4.62, revised v1.4.69)

Emby downloads artwork from listing providers during auto-mapping. Even though we detach the providers, artwork cached during the brief auto-mapping window persists. This method:
1. Resolves `ILibraryManager` via DI
2. Queries all `LiveTvChannel` items belonging to the Xtream tuner (via reflection)
3. Clears `ImageInfos` on **all** Xtream channels (including Gracenote-matched ones, since Emby's auto-mapper can assign wrong artwork to those too) and calls `UpdateItem` with `ItemUpdateType.ImageUpdate`

As of v1.4.69, `ClearWrongChannelArtwork()` only runs when `DetachListingProviders()` actually modifies config (i.e. the first time it detaches). Previously it ran unconditionally on every guide refresh, which wiped channel logos repeatedly and caused them to disappear between refresh cycles.

## Consequences

**Positive**:
- Plugin has full control over which channels get Gracenote EPG
- No more wrong heuristic matches from Emby's auto-mapper
- Channels without Gracenote IDs correctly show Xtream EPG
- Stale artwork is cleaned up once when providers are first detached

**Negative**:
- Channel Identifiarr's "Scan Missing Listings" sees all Xtream channels as missing `ListingsId` (because no listing provider is associated with the tuner). Every run re-processes all channels. See [ADR-006](006-channel-identifiarr-managementid-bug.md).
- Depends on Emby internals accessed via reflection (`LiveTvChannel`, `InternalItemsQuery`, `ItemUpdateType`), which could break with Emby updates.
- The circular call pattern (plugin → Emby's IListingsProvider → Gracenote Cloud → back to plugin) is non-obvious and harder to debug.

## Emby Guide Refresh Paths

Emby has two user-facing ways to refresh guide data, and they trigger different internal code paths:

| Button | Location | Calls `GetChannelsInternal`? | Calls `GetProgramsInternal`? |
|---|---|---|---|
| Refresh Guide | Scheduled Tasks page | Yes | Yes |
| Refresh Guide Data | Live TV management page | Not always | Yes |

"Refresh Guide" runs the full pipeline: channel scan (`GetChannelsInternal`) followed by per-channel EPG fetch (`GetProgramsInternal`). Since `DetachListingProviders()` is called from `GetChannelsInternal`, this path reliably triggers the detach and artwork clear.

"Refresh Guide Data" may use a lighter code path that skips the channel scan phase entirely. Prior to v1.4.69, the plugin had a fallback `DetachListingProviders()` call inside `GetProgramsInternal` to cover this case. This caused duplicate execution during normal guide refreshes (detach + artwork clear fired twice). Since artwork clearing only matters on the initial detach (a one-time config change), the fallback was removed — the small risk of missing the detach on the lighter path is acceptable because it will fire on the next full refresh.

## Related

- [Gracenote EPG Architecture](../architecture/gracenote-epg-chain.md)
- [ADR-006: Channel Identifiarr ManagementId Bug](006-channel-identifiarr-managementid-bug.md)
