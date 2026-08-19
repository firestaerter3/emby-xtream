# ADR-015: Decouple TMDB ID Resolution from Folder Naming

**Date**: 2026-08-19
**Status**: ACCEPTED
**Affects**: `StrmSyncService.SyncMoviesAsync` (movie loop), `StrmSyncService.RetryMovieItemAsync`, two new internal helpers `ResolveMovieTmdbIdAsync` and `ResolveMovieTmdbIdForRetryAsync`, `BuildMovieFolderName` (unchanged), `NfoWriter.WriteMovieNfo` (unchanged)

---

## Context

The movie sync loop has two independent consumers of a movie's TMDB ID:

1. **Folder naming** — `EnableTmdbFolderNaming` makes `BuildMovieFolderName` append `[tmdbid=…]` to the folder. Folder naming was added later and gated behind its own flag.
2. **NFO sidecar** — `EnableNfoFiles` makes `NfoWriter.WriteMovieNfo` write `<uniqueid type="tmdb">…</uniqueid>` into the sidecar.

The provider sometimes supplies the TMDB ID directly on `VodStreamInfo.TmdbId`. When it does not, the plugin can run a fallback lookup (`EnableTmdbFallbackLookup`) that hits TMDB by title and optional year.

## Problem

[Issue #63](https://github.com/firestaerter3/emby-xtream/issues/63) — *NFO files only written when "Enable Metadata ID in Folder Names" is also on*.

The whole TMDB resolution block was gated on `EnableTmdbFolderNaming`:

```csharp
string tmdbId = null;
if (config.EnableTmdbFolderNaming)
{
    if (IsValidTmdbId(movie.TmdbId)) tmdbId = movie.TmdbId.Trim();
    else if (config.EnableTmdbFallbackLookup) { /* lookup */ }
}
```

Two effects of that gate:

- A movie whose TMDB ID came from the provider (`movie.TmdbId == "603"`) but whose user had `EnableTmdbFolderNaming = false` never wrote an NFO with that ID — because the local `tmdbId` was `null`. The NFO writer silently skipped (see `NfoWriter.WriteMovieNfo` line 12: `if (string.IsNullOrEmpty(tmdbId)) return;`).
- The fallback lookup never ran for NFO-only configs, so even titles that *could* have been resolved never had an NFO written.

From the user's perspective the two flags are independent — "I want NFO files" has nothing to do with "I want `[tmdbid=…]` in the folder name". Coupling them meant turning off folder naming also turned off NFO content.

## Alternatives considered

1. **Make `NfoWriter` always look up its own TMDB ID.** Rejected — the writer is a static sidecar-writer with no TMDB client. Pushing the lookup there means duplicating the year-from-title parsing and swallowing the exceptions, and the writer still has to know whether the caller wants a lookup at all (the user's flag).
2. **Always resolve TMDB ID in the loop, ignore the folder-naming flag.** Rejected — the user explicitly disabled folder naming, so the lookup should not run if no other consumer wants it. Unconditional lookups on every movie would multiply TMDB calls for users who don't want any TMDB-derived output.
3. **Decouple: gate the resolution on the OR of the two flags.** Chosen.

## Decision

Two new helpers in `StrmSyncService`:

- `ResolveMovieTmdbIdAsync(VodStreamInfo movie, string cleanedName, PluginConfiguration config, …)` — runs the lookup when **either** `EnableTmdbFolderNaming` or `EnableNfoFiles` is set. Returns the provider-supplied ID when valid, else runs the fallback lookup, else null.
- `ResolveMovieTmdbIdForRetryAsync(FailedSyncItem item, …)` — same shape but for the retry path. Folder naming on retry is unconditional (it predates `EnableTmdbFolderNaming`), so this helper is only used by the NFO writer on retry; the folder name still uses the provider-supplied ID directly.

Callers gate the *consumption* of the ID on their own flag:

```csharp
// Main loop
string tmdbId = await ResolveMovieTmdbIdAsync(...);
var folderTmdbId = config.EnableTmdbFolderNaming ? tmdbId : null;
var folderName = BuildMovieFolderName(cleanedName, folderTmdbId);
// ...later...
if (config.EnableNfoFiles && !string.IsNullOrEmpty(tmdbId))
    NfoWriter.WriteMovieNfo(nfoPath, cleanedName, tmdbId, year);
```

```csharp
// Retry path
var folderTmdbId = IsValidTmdbId(item.TmdbId) ? item.TmdbId.Trim() : null;
var folderName = BuildMovieFolderName(cleanedName, folderTmdbId);
string tmdbId = folderTmdbId;
if (config.EnableNfoFiles && string.IsNullOrEmpty(tmdbId))
    tmdbId = await ResolveMovieTmdbIdForRetryAsync(...);
```

The retry-path split mirrors the same NFO/folder-naming independence, while preserving the legacy behaviour that folder naming on retry was unconditional.

## Consequences

- NFO files now contain a `<uniqueid type="tmdb">` whenever the TMDB ID can be resolved — independent of folder-naming flag. This is what issue #63 asked for.
- Folder names are unchanged when `EnableTmdbFolderNaming = false`. No `[tmdbid=…]` suffix is added.
- TMDB fallback lookup runs only when at least one consumer wants a TMDB ID. Users with both flags off pay zero lookup cost.
- The retry path keeps its original "use provider TMDB for folder name even when folder-naming flag is off" behaviour. The fallback lookup on retry is gated on `EnableNfoFiles`.
- Regression tests in `StrmSyncServiceTests`: 9 cases covering provider-ID short-circuit, both-flags-off, lookup exceptions, retry-path provider-ID short-circuit, retry-path no-fallback, and the end-to-end "NFO populates, folder does not get suffix" shape.

## Implementation references

- `Emby.Xtream.Plugin/Service/StrmSyncService.cs:496-512` (movie loop — resolver call + folder-name gate)
- `Emby.Xtream.Plugin/Service/StrmSyncService.cs:1444-1485` (retry path)
- `Emby.Xtream.Plugin/Service/StrmSyncService.cs:1602-1697` (new helpers — `ResolveMovieTmdbIdAsync` at 1614, `ResolveMovieTmdbIdForRetryAsync` at 1666)
- `Emby.Xtream.Plugin.Tests/StrmSyncServiceTests.cs:572-790` (regression tests — 9 cases)
