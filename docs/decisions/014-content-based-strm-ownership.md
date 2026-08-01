# ADR-014: Verify STRM Ownership by Content, Not by a Manifest

**Date**: 2026-07-31
**Status**: ACCEPTED
**Affects**: `StrmOwnership`, `StrmSyncService.CleanupOrphans` / `RemoveExcludedContent`, `XtreamTunerApi.DeleteContentFolder`

---

## Context

The plugin deletes files on the user's disk in three places: orphan cleanup, per-item exclusion
(ADR-012), and the "delete all content" button in config. All three ran against a library root that
the user may also be putting their own files into.

## Problem

None of the three could tell its own output from the user's.

**Orphan cleanup** treated every `.strm` under the library root as a candidate. A STRM the user wrote
themselves — pointing at their own NAS, never listed by the provider — is by definition not in
`validPaths`, so it looked exactly like an orphan and was deleted.

**Per-item exclusion** matched folders by title. ADR-012 already recognised that a title match is not
proof of ownership and added a guard: skip any matched folder containing no `.strm`. That protects a
user's own `Ben-Hur` folder, but it is coarse. Once a folder held at least one plugin `.strm`, the
pass deleted *every* `.strm` and `.nfo` in the tree. A user's `trailer.strm` or hand-written `.nfo`
sitting beside plugin output went with it, contradicting the code's own comment that "anything the
user put alongside it survives".

**Delete all content** was `Directory.Delete(root, true)` on the whole `Movies`/`Shows` root, followed
by recreating it. If `StrmLibraryPath` pointed at a shared library, one button press removed
everything in it. This is the same class of data loss as BUG-028, reached through a button rather than
a sync.

## Alternatives considered

### 1. A tracking manifest of every written path — REJECTED

The obvious answer, and what an outside review recommended. Persist every path the plugin writes and
delete only tracked paths.

Rejected as disproportionate. It needs new persisted state, a migration path, and a rebuild story for
libraries that already exist (the manifest starts empty, so on first run after upgrade nothing is
owned and cleanup silently stops). It also has to stay correct across renames, naming-version resyncs
(ADR-004) and folder-mode changes, each of which rewrites paths. That is a lot of moving parts guarding
a property the files already carry.

ADR-012 rejected a closely related idea — persisting written paths to infer manual deletions — for
overlapping reasons. Worth noting the rejection there was about *inferring user intent* from the
filesystem; this one is about cost and migration risk.

### 2. Marker file or extended attribute per folder — REJECTED

A `.emby-xtream` marker is easy to write but trivially lost: any user reorganisation, copy, or restore
from a backup that does not preserve dotfiles or xattrs silently unowns the folder. Extended attributes
do not survive most transfers and are filesystem-dependent.

### 3. Ownership by content prefix — ACCEPTED

The plugin's output already identifies itself. Every STRM it writes is the provider URL it was built
from:

```
{BaseUrl}/movie/{user}/{pass}/{id}.{ext}
{BaseUrl}/series/{user}/{pass}/{id}.{ext}
{DispatcharrUrl}/proxy/vod/movie/{uuid}?stream_id={id}     (multi-version entries)
```

A STRM whose content starts with one of the configured hosts is ours. No new state, no migration, and
it works retroactively on libraries that already exist.

## Decision

**`StrmOwnership.IsOwnedStrm` decides ownership by reading the file and matching its content against
`BaseUrl` or `DispatcharrUrl`.** Both hosts are checked: multi-version entries point at Dispatcharr,
so a `BaseUrl`-only check would classify them as foreign and stop cleaning them.

NFOs are matched structurally, because the plugin writes exactly two shapes: `{folderName}.nfo` beside
its matching `{folderName}.strm`, and `tvshow.nfo` at a series root. An NFO qualifies only if it fits
one of those relative to an owned STRM. Everything else — a user's `movie.nfo`, a hand-edited episode
NFO — is foreign.

All three delete paths go through this. `DeleteOwnedFiles` resolves ownership across the whole tree
*before* deleting anything, since the NFO rule is defined in terms of the STRM files that were there.

**Everything fails safe.** An unreadable, empty, or non-matching file is treated as foreign and kept.
A false negative costs an orphan that survives; a false positive destroys user data.

Ownership is checked only for files that are already deletion candidates, so orphan cleanup reads a
number of files proportional to the deletions it is about to make, not to library size.

## Consequences

- A user can keep their own content in the STRM library root and the plugin will not touch it. That
  was never safe before.
- Excluding a title removes that title's files and leaves anything the user put in the same folder,
  which is what ADR-012's remarks already claimed the behaviour was.
- "Delete all content" now reports how many folders it kept because they held foreign files. A user
  who expects a clean sweep and sees "kept 3 folders" gets a true statement instead of silent data
  loss.
- **Changing `BaseUrl` unowns every previously-written STRM.** They stop being cleaned rather than
  being deleted, which is the safe direction. ADR-004's naming-version resync already rewrites files
  after a configuration change of this kind, which re-establishes ownership.
- The orphan ratio is now taken over `validPaths.Count + ownedOrphans.Count` rather than every STRM on
  disk. Counting foreign files in the denominator would dilute the ratio and make the ADR-013 safety
  guard fire less often than intended.
- Orphan counts drop for anyone whose library contains foreign STRMs. That is the fix working, but it
  means an existing user may see the "Deleted" figure fall after upgrading.
- Existing orphan tests seeded placeholder file content (`"orphan"`, `"stream 1"`). Those fixtures were
  unrealistic — a real plugin-written orphan contains the provider URL — and were updated. The tests
  that deliberately seed foreign content to assert survival were already correct and are unchanged.
