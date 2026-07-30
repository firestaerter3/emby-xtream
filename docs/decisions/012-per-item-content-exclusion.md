# ADR-012: Per-Item Content Exclusion via Explicit Lists

**Date**: 2026-07-31
**Status**: ACCEPTED
**Affects**: `PluginConfiguration`, `StrmSyncService.SyncMoviesAsync` / `SyncSeriesAsync`, `ContentExclusionFilter`, `XtreamTunerApi`, plugin config UI

---

## Context

Category-level filtering is the only content filter the plugin has ever had. Users select whole VOD
or series categories and get everything inside them. A provider category like "Action" routinely
carries hundreds of titles, so a single unwanted title means either accepting it or dropping the
entire category.

[Issue #57](https://github.com/firestaerter3/emby-xtream/issues/57) asked for the per-item selection
the sibling Jellyfin plugin already ships: expand a category in config, untick individual titles.

Note the issue is filed under Area "Live TV / EPG" and states the Emby plugin already has per-channel
Live TV selection. It does not — that is the Jellyfin plugin. This change is scoped to VOD and series
STRM sync only.

## Problem

The reporter asked for two different things in one issue:

1. Per-title selection, matching Jellyfin.
2. *"Even just an option to not add back folders that I've manually deleted would be enough."*

These need different mechanisms. (1) is a declared list of unwanted IDs. (2) infers intent from the
filesystem — the sync would have to remember every path it wrote and treat *written-before, now-missing*
as an implicit exclusion.

A second problem sits underneath both: how does an already-synced folder actually leave the library?

## Alternatives considered

### 1. Auto-detect manual deletions — REJECTED

Persist every written path; on the next sync, treat any path that existed before and is now gone as
user-deleted and never recreate it.

Closest to the literal ask, and it needs no UI. Rejected because the inference is unsafe. A transient
mount failure, an unmounted library path, or a container started before its volume attached all present
identically to "the user deleted everything". The plugin would then permanently suppress the entire
library. This is precisely the failure mode `OrphanSafetyThreshold` exists to bound, and unlike orphan
cleanup the suppression is silent and only recoverable by re-ticking every title by hand.

### 2. Explicit exclusion lists — CHOSEN

Two `int[]` fields on `PluginConfiguration`. Deterministic, visible in the config UI, trivially
reversible, and impossible to trigger by accident.

### 3. Rely on orphan cleanup for removal (Jellyfin's approach) — REJECTED FOR EMBY

Jellyfin filters excluded items out at fetch time and lets the orphan pass delete the leftover folders.
That does not transfer: on Emby `CleanupOrphans` defaults to `false`, and when it is on, the 20%
`OrphanSafetyThreshold` blocks any sizeable deselect. The common case would be a user unticking thirty
movies, running a sync, seeing all thirty folders still present, and reporting the filter as broken.

## Decision

Add `ExcludedVodStreamIds` and `ExcludedSeriesIds` to `PluginConfiguration` (Emby's XML serializer
persists them for free).

`ContentExclusionFilter` is an `internal static` class with no constructor. Emby's SimpleInjector scan
instantiates public service classes before `Plugin.Instance` exists, so anything with a DI-shaped
constructor risks being built too early.

Both sync methods partition the fetched catalogue into included and excluded before the write loop.
Included items sync exactly as before. Excluded items are handed to `RemoveExcludedContent`, which
deletes their folders directly — **ignoring both `CleanupOrphans` and `OrphanSafetyThreshold`**. The
threshold guards against a provider returning a truncated catalogue; an exclusion is a deliberate user
action, so suppressing the delete would just read as the filter doing nothing.

Folder matching reuses the existing `StripFolderIdSuffix`, so a title is found whether it was written
as `Some Movie`, `Some Movie [tmdbid=123]` or `Some Show [tvdbid=456]`. This keeps removal correct
across changes to the metadata-ID naming settings.

Two read-only endpoints (`GET /XtreamTuner/Items/Vod` and `/XtreamTuner/Items/Series`) return
`{Id, Name}` for one category. The config UI calls them lazily when a category row is expanded.

## Consequences

- **Re-inclusion works without a forced re-sync.** Both smart-skip paths already guard on the folder
  or its STRM files existing, so a re-ticked title is recreated on the next sync. No delta watermark
  reset is needed, and none was added.
- **Delta watermarks derive from the unfiltered catalogue.** If the newest movie or series happens to
  be excluded, `LastMovieSyncTimestamp` / `LastSeriesSyncTimestamp` must still advance past it, or
  every later sync re-processes everything after it. Excluded series never enter the per-series loop
  that normally advances the watermark, so their `last_modified` values are folded in separately.
- **Excluded series cost nothing.** They skip the loop entirely, so no `get_series_info` call is made
  for them — a real saving on providers with slow detail endpoints.
- **Granularity is per-series, not per-episode.** Excluding a show removes all of it.
- **The editing UI only appears in Single Folder mode**, where the flat category list lives. The
  exclusion lists are still honoured at sync time in every folder mode; Multiple Folders mode just has
  no UI for editing them.
- **Two extra provider calls per category expand**, made lazily so page load is unaffected. They use
  the tolerant JSON converters ([ADR-010](010-tolerant-provider-deserialization.md)) — one off-type
  provider field would otherwise blank an entire category listing.
- **The literal "don't re-add what I deleted on disk" ask is not covered.** Users must untick the title
  in config. This is a deliberate trade against the silent-suppression risk in Alternative 1.
