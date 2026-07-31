# ADR-013: Incomplete Provider Data Must Never Drive Deletion

**Date**: 2026-07-31
**Status**: ACCEPTED
**Affects**: `StrmSyncService.FetchVodStreamsAsync` / `FetchSeriesListAsync` / `SyncMoviesAsync` / `SyncSeriesAsync` / `CleanupOrphans`, `CatalogueFetchResult<T>`

---

## Context

Orphan cleanup decides what to delete by difference: every `.strm` under the library root that is not
in the set of paths this run wrote or preserved is an orphan, and orphans get deleted. That is correct
only if the run saw the *whole* catalogue. `OrphanSafetyThreshold` (default 20%) exists to catch the
case where it did not, by refusing to delete when the orphan ratio is implausibly high.

## Problem

Three paths fed cleanup a catalogue that looked complete but was not. None of them tripped the
threshold.

**1. A failed category request returned an empty list.** `FetchVodStreamsAsync` and
`FetchSeriesListAsync` fan out one HTTP request per selected category and wrapped each in
`catch { return new List<T>(); }`. An empty list from a 502 is indistinguishable from a category that
is genuinely empty. Every title in the failed category became an orphan and was deleted. With many
categories selected, one failing category is a small fraction of the library, so the ratio stayed
comfortably under 20% and the guard never fired.

**2. An empty `get_series_info` payload looked like a show that lost every episode.** The check
`detail == null || detail.Episodes == null || detail.Episodes.Count == 0` returned early without
recording anything, so all of that show's existing episode STRMs became orphans. This compounds with
ADR-010: the tolerant converters deliberately turn malformed provider values into empty/null rather
than throwing, which is right for parsing but means a garbage payload arrives here as "no episodes".

**3. The ratio guard did not apply below 11 files.** `existingStrms.Length > 10` gated the whole
check, because a ratio over a handful of files is statistically meaningless. The effect was that
small libraries had no protection at all: a transient `[]` response deleted everything.

The common shape is that *absence of data was read as data*. The plugin could not distinguish "the
provider says this is gone" from "the provider did not tell us about this".

## Alternatives considered

### 1. Lower the safety threshold — REJECTED

Does not address it. The threshold is a ratio, and the failure produces a plausible ratio. Lowering it
enough to catch a single failed category among many would block legitimate cleanup constantly.

### 2. Abort the entire sync when any category fails — REJECTED

Correct on the deletion axis but too blunt. A provider with one persistently flaky category would
never sync anything again. Writing the categories that succeeded is useful and safe; only *deleting*
requires complete information.

### 3. Freeze the delta watermark on any failure — REJECTED

Proposed as part of the same fix, and it is wrong. If category 7 fails, freezing the watermark makes
every later sync re-process categories 1-6 from scratch. That is the exact re-processing failure the
unfiltered-watermark note in ADR-012 exists to prevent. Completeness is required for deletion, not for
recording progress on what was actually seen.

## Decision

**Separate "what we fetched" from "did we fetch all of it", and gate only deletion on the latter.**

`CatalogueFetchResult<T>` carries the items plus `FailedCategoryCount`. A category that throws is
counted, not silently flattened into an empty result.

- **Cleanup** is skipped when `HadFailures` is true, alongside the existing `Failed == 0` guard.
- **The watermark still advances**, computed over the items that did arrive. A partial fetch records
  real progress on the categories that answered.
- **An empty episode payload for a series that already has files on disk** preserves those paths and
  increments `Failed`, which reuses the existing cleanup guard rather than adding a parallel flag. A
  show that genuinely lost every episode and owns nothing on disk is not a failure, so it does not
  block cleanup for unrelated titles.
- **`validPaths.Count == 0` with files on disk refuses cleanup outright.** A run that produced nothing
  is never evidence that the user's entire library should be deleted.

The `> 10` ratio gate is **kept**. Removing it looked tempting but breaks legitimate small-library
cleanup: with one file and one orphan the ratio is 1.0, so cleanup would be skipped forever and the
user would get a warning on every sync. The empty-catalogue guard closes the actual data-loss path at
small N without that side effect.

## Consequences

- One flaky category no longer costs the user that category's library. Cleanup resumes automatically
  on the next run that returns cleanly, so no manual intervention is needed.
- A provider that fails a category on *every* run means cleanup never runs. Orphans accumulate rather
  than files being destroyed, which is the correct direction to fail. The warning names the failed
  category count so the cause is visible in the log.
- If a user genuinely empties their entire provider selection, cleanup will refuse and they must use
  the delete-content button. Acceptable: that button exists for exactly this, and the alternative is
  a transient blip wiping a library.
- Deletion counts drop in partial-failure runs. That is the point, but it means the dashboard's
  "Deleted" figure is no longer a reliable signal that cleanup ran; the log is.
- Regression tests all start with files already on disk. A cleanup test seeded from an empty directory
  passes trivially, which is precisely why the pre-existing empty-response test missed this.
