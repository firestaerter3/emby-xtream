# Per-Item Content Filtering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers-extended-cc:subagent-driven-development (recommended) or superpowers-extended-cc:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users exclude individual movies and series from STRM sync by expanding a category in plugin config and unticking titles, with excluded titles removed from disk on the next sync.

**Architecture:** Two `int[]` exclusion lists on `PluginConfiguration` (`ExcludedVodStreamIds`, `ExcludedSeriesIds`) persisted by Emby's XML config serializer. `StrmSyncService` partitions the fetched catalogue into included/excluded before the write loop; included items sync unchanged, excluded items get their on-disk folder deleted by a targeted removal pass that runs independently of `CleanupOrphans` and `OrphanSafetyThreshold`. Two new read-only API routes list the titles in a category so the config UI can render per-title checkboxes lazily on expand.

**Tech Stack:** C# netstandard2.0 (Emby plugin), xunit tests on net10.0, plain ES5-style JS in `Configuration/Web/config.js` (Emby `view`-based plugin config page).

---

## Context and constraints

Read before starting:

- **GitHub issue:** https://github.com/firestaerter3/emby-xtream/issues/57 (reporter: andyj682). The issue is filed under Area "Live TV / EPG" but is really about VOD/Series STRM sync. It also claims the Emby plugin already has per-channel Live TV selection — **it does not**; that is the Jellyfin plugin. Live TV is out of scope for this plan.
- **Reference implementation:** `../Jellyfin-Xtream-Library/Jellyfin.Xtream.Library/Service/ContentExclusionFilter.cs` and `Configuration/Web/config.js:1380-1600`. The C# concepts port; the JS does **not** port verbatim (Jellyfin uses a `self.`-object page controller, Emby uses `view`-scoped plain functions).
- **SimpleInjector hazard** (project `CLAUDE.md`): Emby auto-instantiates public classes with DI-shaped constructors before `Plugin.Instance` exists. `ContentExclusionFilter` MUST be `internal static` — no instance service, no constructor.
- **Divergence from Jellyfin (deliberate):** Jellyfin leans on orphan cleanup to remove newly-excluded items. Emby ships `CleanupOrphans = false` by default with a 20% `OrphanSafetyThreshold`, so that would silently do nothing for most users. This plan deletes excluded folders directly. An exclusion is a deliberate user action, not a provider anomaly, so the safety bound (which exists to survive a provider returning a truncated catalogue) does not apply to it.
- **Scope:** the per-title UI lives in the flat category list, which only renders in **Single Folder** mode. The sync-time filter and removal pass honour the exclusion lists in every folder mode; only the UI for editing them is single-mode-only. Series granularity is per-series, not per-episode.

## File Structure

| File | Responsibility |
|---|---|
| `Emby.Xtream.Plugin/PluginConfiguration.cs` | Two new `int[]` exclusion properties. |
| `Emby.Xtream.Plugin/Service/ContentExclusionFilter.cs` (new) | Null-safe set construction + membership test. |
| `Emby.Xtream.Plugin/Service/StrmSyncService.cs` | Partition catalogue; `RemoveExcludedContent` targeted deletion pass; wire into both sync methods. |
| `Emby.Xtream.Plugin/Api/XtreamTunerApi.cs` | Two GET routes returning `{Id, Name}` per category. |
| `Emby.Xtream.Plugin/Configuration/Web/config.html` | Hint text under the VOD/Series category lists. |
| `Emby.Xtream.Plugin/Configuration/Web/config.js` | Expander UI, lazy item fetch, exclusion state, load/save wiring. |
| `Emby.Xtream.Plugin.Tests/ContentExclusionFilterTests.cs` (new) | Unit tests for the filter helper. |
| `Emby.Xtream.Plugin.Tests/SyncMoviesIntegrationTests.cs` | Exclusion + removal integration tests. |
| `Emby.Xtream.Plugin.Tests/SyncSeriesIntegrationTests.cs` | Exclusion + removal integration tests. |
| `docs/decisions/012-per-item-content-exclusion.md` (new) | ADR recording the design fork. |
| `CLAUDE.md` | Architecture note on exclusion semantics. |

---

### Task 1: Exclusion config fields and filter helper

**Goal:** Persisted exclusion lists plus a tested, null-safe helper for testing membership.

**Files:**
- Modify: `Emby.Xtream.Plugin/PluginConfiguration.cs` (after line 57 and after line 63)
- Create: `Emby.Xtream.Plugin/Service/ContentExclusionFilter.cs`
- Test: `Emby.Xtream.Plugin.Tests/ContentExclusionFilterTests.cs`

**Acceptance Criteria:**
- [ ] `PluginConfiguration.ExcludedVodStreamIds` and `ExcludedSeriesIds` default to empty `int[]`, never null
- [ ] `ContentExclusionFilter` is `internal static` (no constructor for Emby's SimpleInjector scan to latch onto)
- [ ] `BuildSet(null)` returns an empty set rather than throwing
- [ ] `IsExcluded` on an empty set returns false for every ID

**Verify:** `dotnet test Emby.Xtream.Plugin.Tests/ --filter ContentExclusionFilterTests -v minimal` → all tests pass

**Steps:**

- [ ] **Step 1: Write the failing tests**

Create `Emby.Xtream.Plugin.Tests/ContentExclusionFilterTests.cs`:

```csharp
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class ContentExclusionFilterTests
    {
        [Fact]
        public void BuildSet_Null_ReturnsEmptySet()
        {
            var set = ContentExclusionFilter.BuildSet(null);
            Assert.Empty(set);
        }

        [Fact]
        public void BuildSet_Empty_ReturnsEmptySet()
        {
            var set = ContentExclusionFilter.BuildSet(new int[0]);
            Assert.Empty(set);
        }

        [Fact]
        public void BuildSet_Duplicates_Deduplicated()
        {
            var set = ContentExclusionFilter.BuildSet(new[] { 7, 7, 9 });
            Assert.Equal(2, set.Count);
        }

        [Fact]
        public void IsExcluded_EmptySet_AlwaysFalse()
        {
            var set = ContentExclusionFilter.BuildSet(null);
            Assert.False(ContentExclusionFilter.IsExcluded(set, 1));
            Assert.False(ContentExclusionFilter.IsExcluded(set, 0));
        }

        [Fact]
        public void IsExcluded_MemberAndNonMember()
        {
            var set = ContentExclusionFilter.BuildSet(new[] { 42 });
            Assert.True(ContentExclusionFilter.IsExcluded(set, 42));
            Assert.False(ContentExclusionFilter.IsExcluded(set, 43));
        }

        [Fact]
        public void Configuration_ExclusionLists_DefaultEmpty()
        {
            var config = new PluginConfiguration();
            Assert.NotNull(config.ExcludedVodStreamIds);
            Assert.Empty(config.ExcludedVodStreamIds);
            Assert.NotNull(config.ExcludedSeriesIds);
            Assert.Empty(config.ExcludedSeriesIds);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Emby.Xtream.Plugin.Tests/ --filter ContentExclusionFilterTests -v minimal`
Expected: build failure — `ContentExclusionFilter` and the two config properties do not exist.

- [ ] **Step 3: Add the config properties**

In `Emby.Xtream.Plugin/PluginConfiguration.cs`, add after `public string MovieFolderMappings { get; set; } = string.Empty;` (line 59):

```csharp

        /// <summary>
        /// Xtream stream IDs the user has explicitly excluded from movie sync.
        /// Excluded items are never written, and their existing folder is deleted
        /// on the next sync (independent of <see cref="CleanupOrphans"/>).
        /// </summary>
        public int[] ExcludedVodStreamIds { get; set; } = new int[0];
```

And after `public string SeriesFolderMappings { get; set; } = string.Empty;` (line 65):

```csharp

        /// <summary>
        /// Xtream series IDs the user has explicitly excluded from series sync.
        /// Granularity is per-series, not per-episode.
        /// </summary>
        public int[] ExcludedSeriesIds { get; set; } = new int[0];
```

- [ ] **Step 4: Create the filter helper**

Create `Emby.Xtream.Plugin/Service/ContentExclusionFilter.cs`:

```csharp
using System.Collections.Generic;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Filters individual VOD/series items out of a sync run using the configured
    /// exclusion lists (<see cref="PluginConfiguration.ExcludedVodStreamIds"/> and
    /// <see cref="PluginConfiguration.ExcludedSeriesIds"/>).
    ///
    /// Deliberately a static class with no constructor: Emby's SimpleInjector scan
    /// instantiates public service classes before Plugin.Instance exists, so anything
    /// with a DI-shaped constructor risks being built too early (see CLAUDE.md).
    /// </summary>
    internal static class ContentExclusionFilter
    {
        /// <summary>
        /// Builds a lookup set from an exclusion ID array. Null or empty input yields an empty set.
        /// </summary>
        public static HashSet<int> BuildSet(int[] excludedIds)
        {
            if (excludedIds == null || excludedIds.Length == 0)
            {
                return new HashSet<int>();
            }

            return new HashSet<int>(excludedIds);
        }

        /// <summary>
        /// Returns true if the given item ID is present in the exclusion set.
        /// An empty set always returns false (nothing excluded).
        /// </summary>
        public static bool IsExcluded(HashSet<int> excludedSet, int itemId)
        {
            return excludedSet != null && excludedSet.Count != 0 && excludedSet.Contains(itemId);
        }
    }
}
```

- [ ] **Step 5: Make the test project see the internal type**

`ContentExclusionFilter` is `internal`, so the test project needs friend access. Add to `Emby.Xtream.Plugin/Emby.Xtream.Plugin.csproj` inside the existing top-level `<Project>` element (check whether an `InternalsVisibleTo` is already present first — `grep -n InternalsVisibleTo Emby.Xtream.Plugin/Emby.Xtream.Plugin.csproj`; if it is, skip this step):

```xml
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>Emby.Xtream.Plugin.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test Emby.Xtream.Plugin.Tests/ --filter ContentExclusionFilterTests -v minimal`
Expected: PASS, 6 tests.

- [ ] **Step 7: Commit**

```bash
git add Emby.Xtream.Plugin/PluginConfiguration.cs \
        Emby.Xtream.Plugin/Service/ContentExclusionFilter.cs \
        Emby.Xtream.Plugin/Emby.Xtream.Plugin.csproj \
        Emby.Xtream.Plugin.Tests/ContentExclusionFilterTests.cs
git commit -m "feat: add per-item exclusion config fields and filter helper"
```

---

### Task 2: Targeted removal of excluded item folders

**Goal:** A reusable `RemoveExcludedContent` pass in `StrmSyncService` that deletes the on-disk folder for an excluded item, matching the folder regardless of a `[tmdbid=…]` / `[tvdbid=…]` suffix.

**Files:**
- Modify: `Emby.Xtream.Plugin/Service/StrmSyncService.cs` (add private method next to `CleanupOrphans` at line 1682)

**Acceptance Criteria:**
- [ ] Deletes `{StrmLibraryPath}/{subFolder}/{SanitizeFileName(cleanedName)}` and any `… [tmdbid=N]` / `… [tvdbid=N]` variant of it
- [ ] Runs regardless of `CleanupOrphans` and ignores `OrphanSafetyThreshold`
- [ ] Reads each subfolder's directory listing at most once per call
- [ ] Returns the number of folders removed; per-item failures are logged and do not abort the pass
- [ ] Items whose category is unmapped in custom folder mode (`BuildContentFolderPath` returns null) are skipped

**Verify:** `dotnet test Emby.Xtream.Plugin.Tests/ -v minimal` → still green (this task adds no behaviour change on its own; Tasks 3 and 4 exercise it)

**Steps:**

- [ ] **Step 1: Add the removal method**

In `Emby.Xtream.Plugin/Service/StrmSyncService.cs`, insert immediately **before** `private int CleanupOrphans(` (line 1682):

```csharp
        /// <summary>
        /// Deletes the on-disk folders of items the user has explicitly excluded.
        /// </summary>
        /// <remarks>
        /// Runs independently of <see cref="PluginConfiguration.CleanupOrphans"/> and ignores
        /// <see cref="PluginConfiguration.OrphanSafetyThreshold"/>. That threshold exists to survive
        /// a provider returning a truncated catalogue; an exclusion is a deliberate user action, so
        /// suppressing the delete would just look like the filter doing nothing.
        ///
        /// Folder matching strips any metadata-ID suffix, so an excluded title is found whether it
        /// was written as "Some Movie", "Some Movie [tmdbid=123]" or "Some Show [tvdbid=456]".
        /// </remarks>
        /// <param name="config">Active plugin configuration (supplies the library root).</param>
        /// <param name="excludedItems">Cleaned display name + category ID for each excluded item.</param>
        /// <param name="folderMode">"single", "multiple" or "custom".</param>
        /// <param name="categoryNames">Category ID → name, used by "multiple" mode.</param>
        /// <param name="folderMappings">Category ID → folder, used by "custom" mode.</param>
        /// <param name="rootFolder">"Movies" or "Shows".</param>
        /// <returns>The number of folders deleted.</returns>
        private int RemoveExcludedContent(
            PluginConfiguration config,
            List<Tuple<string, int?>> excludedItems,
            string folderMode,
            Dictionary<int, string> categoryNames,
            Dictionary<int, string> folderMappings,
            string rootFolder)
        {
            if (excludedItems == null || excludedItems.Count == 0)
            {
                return 0;
            }

            var removed = 0;

            // subFolder → { folderNameWithoutIdSuffix → fullPath }. One readdir per subfolder.
            var dirIndexCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in excludedItems)
            {
                var sanitized = SanitizeFileName(item.Item1);
                if (string.IsNullOrWhiteSpace(sanitized))
                {
                    continue;
                }

                var subFolder = BuildContentFolderPath(
                    folderMode, item.Item2, categoryNames, folderMappings, rootFolder);
                if (subFolder == null)
                {
                    continue;
                }

                Dictionary<string, string> dirIndex;
                if (!dirIndexCache.TryGetValue(subFolder, out dirIndex))
                {
                    dirIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var fullPath = Path.Combine(config.StrmLibraryPath, subFolder);
                    if (Directory.Exists(fullPath))
                    {
                        foreach (var dir in Directory.GetDirectories(fullPath))
                        {
                            var stripped = StripFolderIdSuffix(Path.GetFileName(dir));
                            if (!string.IsNullOrEmpty(stripped) && !dirIndex.ContainsKey(stripped))
                            {
                                dirIndex[stripped] = dir;
                            }
                        }
                    }

                    dirIndexCache[subFolder] = dirIndex;
                }

                string existingDir;
                if (!dirIndex.TryGetValue(sanitized, out existingDir))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(existingDir, true);
                    dirIndex.Remove(sanitized);
                    removed++;
                    _logger.Info("Removed excluded item folder: {0}", existingDir);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Failed to remove excluded item folder '{0}': {1}", existingDir, ex.Message);
                }
            }

            if (removed > 0)
            {
                _logger.Info("Removed {0} folder(s) for explicitly excluded items under {1}", removed, rootFolder);
            }

            return removed;
        }

```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build Emby.Xtream.Plugin/Emby.Xtream.Plugin.csproj -v minimal`
Expected: Build succeeded. If `Tuple`, `Path`, `Directory` or `StringComparer` are unresolved, confirm `System`, `System.Collections.Generic` and `System.IO` are in the file's using block (they are, at the top of `StrmSyncService.cs`).

- [ ] **Step 3: Run the existing suite to confirm nothing regressed**

Run: `dotnet test Emby.Xtream.Plugin.Tests/ -v minimal`
Expected: PASS, same test count as before the change.

- [ ] **Step 4: Commit**

```bash
git add Emby.Xtream.Plugin/Service/StrmSyncService.cs
git commit -m "feat: add targeted removal pass for excluded content folders"
```

---

### Task 3: Apply exclusions in the movie sync

**Goal:** Excluded movies are never written, their existing folders are deleted, and the delta watermark stays accurate.

**Files:**
- Modify: `Emby.Xtream.Plugin/Service/StrmSyncService.cs:325` (fetch), `:573-589` (cleanup + watermark)
- Test: `Emby.Xtream.Plugin.Tests/SyncMoviesIntegrationTests.cs`

**Acceptance Criteria:**
- [ ] A movie whose `stream_id` is in `ExcludedVodStreamIds` gets no STRM file written
- [ ] An excluded movie's pre-existing folder is deleted even with `CleanupOrphans = false`
- [ ] An excluded movie's folder is deleted even when it carries a `[tmdbid=N]` suffix
- [ ] Non-excluded movies in the same run are unaffected
- [ ] `LastMovieSyncTimestamp` still advances to the max `added` across the **unfiltered** catalogue

**Verify:** `dotnet test Emby.Xtream.Plugin.Tests/ --filter SyncMoviesIntegrationTests -v minimal` → all tests pass

**Steps:**

- [ ] **Step 1: Write the failing tests**

Append to `Emby.Xtream.Plugin.Tests/SyncMoviesIntegrationTests.cs`, inside the class before the closing brace:

```csharp
        // -----------------------------------------------------------------
        // Per-item exclusion (issue #57)
        // -----------------------------------------------------------------

        [Fact]
        public async Task ExcludedMovie_NotWritten_OthersUnaffected()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Keep Me", added: 1000),
                VodStream(streamId: 2, name: "Drop Me", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(MovieStrmPath("Keep Me")));
            Assert.False(File.Exists(MovieStrmPath("Drop Me")));
        }

        [Fact]
        public async Task ExcludedMovie_ExistingFolderDeleted_WithoutOrphanCleanup()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = false;
            config.ExcludedVodStreamIds = new[] { 2 };

            // Simulate a previous sync having written "Drop Me"
            var staleStrm = MovieStrmPath("Drop Me");
            Directory.CreateDirectory(Path.GetDirectoryName(staleStrm));
            File.WriteAllText(staleStrm, "http://fake-xtream/movie/user/pass/2.mkv");

            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Keep Me", added: 1000),
                VodStream(streamId: 2, name: "Drop Me", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Movies", "Drop Me")));
            Assert.True(File.Exists(MovieStrmPath("Keep Me")));
        }

        [Fact]
        public async Task ExcludedMovie_FolderWithTmdbSuffix_Deleted()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };

            var staleDir = Path.Combine(TempDir.Path, "Movies", "Drop Me [tmdbid=550]");
            Directory.CreateDirectory(staleDir);
            File.WriteAllText(Path.Combine(staleDir, "Drop Me [tmdbid=550].strm"), "http://old");

            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 2, name: "Drop Me", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(staleDir));
        }

        [Fact]
        public async Task ExcludedMovie_DoesNotStallDeltaWatermark()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Keep Me", added: 1000),
                VodStream(streamId: 2, name: "Drop Me", added: 5000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(5000, config.LastMovieSyncTimestamp);
        }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Emby.Xtream.Plugin.Tests/ --filter SyncMoviesIntegrationTests -v minimal`
Expected: the four new tests FAIL (excluded movie is written; stale folder survives; watermark asserts may pass incidentally).

- [ ] **Step 3: Partition the fetched catalogue**

In `Emby.Xtream.Plugin/Service/StrmSyncService.cs`, replace line 325:

```csharp
                var allStreams = await FetchVodStreamsAsync(config.SelectedVodCategoryIds, config, cancellationToken).ConfigureAwait(false);
```

with:

```csharp
                var fetchedStreams = await FetchVodStreamsAsync(config.SelectedVodCategoryIds, config, cancellationToken).ConfigureAwait(false);

                // Per-item exclusions (issue #57): split the catalogue before anything else reads it.
                // The excluded half is kept so its on-disk folders can be removed below.
                var excludedVodSet = ContentExclusionFilter.BuildSet(config.ExcludedVodStreamIds);
                var excludedMovies = new List<Tuple<string, int?>>();
                var allStreams = fetchedStreams;
                if (excludedVodSet.Count > 0)
                {
                    allStreams = new List<VodStreamInfo>();
                    foreach (var s in fetchedStreams)
                    {
                        if (ContentExclusionFilter.IsExcluded(excludedVodSet, s.StreamId))
                        {
                            var excludedName = config.EnableContentNameCleaning
                                ? ContentNameCleaner.CleanContentName(s.Name, config.ContentRemoveTerms)
                                : s.Name;
                            excludedMovies.Add(Tuple.Create(excludedName, s.CategoryId));
                        }
                        else
                        {
                            allStreams.Add(s);
                        }
                    }

                    _logger.Info("Per-item exclusions: skipping {0} of {1} movies",
                        excludedMovies.Count, fetchedStreams.Count);
                }
```

- [ ] **Step 4: Run the removal pass and fix the watermark**

Still in `SyncMoviesAsync`, replace the block at lines 572-589 (from `// Cleanup orphans` through the closing brace of the timestamp block):

```csharp
                // Cleanup orphans
                if (config.CleanupOrphans)
                {
                    _movieProgress.Phase = "Cleaning up orphaned files";
                    var moviesRoot = Path.Combine(config.StrmLibraryPath, "Movies");
                    _movieProgress.Deleted = CleanupOrphans(moviesRoot, writtenPaths, config.OrphanSafetyThreshold);
                }

                // Persist the highest Added timestamp seen so next sync can delta from here
                if (allStreams.Count > 0)
                {
                    var maxAdded = allStreams.Max(m => m.Added);
                    if (maxAdded > config.LastMovieSyncTimestamp)
                    {
                        config.LastMovieSyncTimestamp = maxAdded;
                        saveConfig?.Invoke();
                    }
                }
```

with:

```csharp
                // Remove folders for explicitly excluded movies. Deliberately before orphan
                // cleanup and independent of it — see RemoveExcludedContent remarks.
                if (excludedMovies.Count > 0)
                {
                    _movieProgress.Phase = "Removing excluded movies";
                    _movieProgress.Deleted += RemoveExcludedContent(
                        config, excludedMovies, config.MovieFolderMode, categoryNames, folderMappings, "Movies");
                }

                // Cleanup orphans
                if (config.CleanupOrphans)
                {
                    _movieProgress.Phase = "Cleaning up orphaned files";
                    var moviesRoot = Path.Combine(config.StrmLibraryPath, "Movies");
                    _movieProgress.Deleted += CleanupOrphans(moviesRoot, writtenPaths, config.OrphanSafetyThreshold);
                }

                // Persist the highest Added timestamp seen so next sync can delta from here.
                // Computed over the UNFILTERED catalogue: if the newest movie happens to be
                // excluded, the watermark must still advance past it or every later sync
                // re-processes everything after it.
                if (fetchedStreams.Count > 0)
                {
                    var maxAdded = fetchedStreams.Max(m => m.Added);
                    if (maxAdded > config.LastMovieSyncTimestamp)
                    {
                        config.LastMovieSyncTimestamp = maxAdded;
                        saveConfig?.Invoke();
                    }
                }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Emby.Xtream.Plugin.Tests/ --filter SyncMoviesIntegrationTests -v minimal`
Expected: PASS, all tests including the four new ones.

- [ ] **Step 6: Commit**

```bash
git add Emby.Xtream.Plugin/Service/StrmSyncService.cs \
        Emby.Xtream.Plugin.Tests/SyncMoviesIntegrationTests.cs
git commit -m "feat: honour per-movie exclusions during STRM sync"
```

---

### Task 4: Apply exclusions in the series sync

**Goal:** Excluded series are never written, their existing folders are deleted, and the series delta watermark stays accurate.

**Files:**
- Modify: `Emby.Xtream.Plugin/Service/StrmSyncService.cs:673` (fetch), `:676-691` (watermark), `:1033-1037` (cleanup)
- Test: `Emby.Xtream.Plugin.Tests/SyncSeriesIntegrationTests.cs`

**Acceptance Criteria:**
- [ ] A series whose `series_id` is in `ExcludedSeriesIds` produces no folder and triggers no `get_series_info` call
- [ ] An excluded series' pre-existing folder (including all season subfolders) is deleted with `CleanupOrphans = false`
- [ ] Non-excluded series in the same run are unaffected
- [ ] `LastSeriesSyncTimestamp` still accounts for excluded series' `last_modified`

**Verify:** `dotnet test Emby.Xtream.Plugin.Tests/ --filter SyncSeriesIntegrationTests -v minimal` → all tests pass

**Steps:**

- [ ] **Step 1: Write the failing tests**

The helpers below already exist — `SeriesListJson` / `Series` / `SeriesDetailJson` in `SyncTestBase.cs`, and the `Handler.RespondWith("action=get_series", …)` registration style in `SyncSeriesIntegrationTests.cs`. Note that response matching is on the **full query fragment** (`"action=get_series_info&series_id=1"`), and that `"action=get_series"` is a prefix of `"action=get_series_info"` — register the more specific detail responses so each series ID gets its own entry.

Append to `Emby.Xtream.Plugin.Tests/SyncSeriesIntegrationTests.cs`, inside the class before the closing brace:

```csharp
        // -----------------------------------------------------------------
        // Per-item exclusion (issue #57)
        // -----------------------------------------------------------------

        [Fact]
        public async Task ExcludedSeries_NotWritten_OthersUnaffected()
        {
            var config = DefaultConfig();
            config.ExcludedSeriesIds = new[] { 2 };

            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Keep Show", lastModified: "1000"),
                Series(seriesId: 2, name: "Drop Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));
            Handler.RespondWith("action=get_series_info&series_id=2", SeriesDetailJson(seriesId: 2));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Keep Show")));
            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Drop Show")));
        }

        [Fact]
        public async Task ExcludedSeries_ExistingFolderDeleted_WithoutOrphanCleanup()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = false;
            config.ExcludedSeriesIds = new[] { 2 };

            var staleSeasonDir = Path.Combine(TempDir.Path, "Shows", "Drop Show", "Season 01");
            Directory.CreateDirectory(staleSeasonDir);
            File.WriteAllText(Path.Combine(staleSeasonDir, "Drop Show - S01E01.strm"), "http://old");

            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 2, name: "Drop Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=2", SeriesDetailJson(seriesId: 2));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Drop Show")));
        }

        [Fact]
        public async Task ExcludedSeries_DoesNotStallDeltaWatermark()
        {
            var config = DefaultConfig();
            config.ExcludedSeriesIds = new[] { 2 };

            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Keep Show", lastModified: "1000"),
                Series(seriesId: 2, name: "Drop Show", lastModified: "5000")));
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));
            Handler.RespondWith("action=get_series_info&series_id=2", SeriesDetailJson(seriesId: 2));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.Equal(5000, config.LastSeriesSyncTimestamp);
        }
```

Note: `SeriesDetailJson` hardcodes `name = "Test Show"` in its `info` block, but the folder name comes from the **series list** entry, not the detail — so "Keep Show" is the folder that gets created. If a run shows a `Test Show` folder instead, the sync is reading the wrong name field and that is a real bug, not a test defect.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Emby.Xtream.Plugin.Tests/ --filter SyncSeriesIntegrationTests -v minimal`
Expected: the three new tests FAIL — "Drop Show" gets created, and the stale folder survives.

- [ ] **Step 3: Partition the fetched series list**

In `Emby.Xtream.Plugin/Service/StrmSyncService.cs`, replace line 673:

```csharp
                var allSeries = await FetchSeriesListAsync(config.SelectedSeriesCategoryIds, config, cancellationToken).ConfigureAwait(false);
```

with:

```csharp
                var fetchedSeries = await FetchSeriesListAsync(config.SelectedSeriesCategoryIds, config, cancellationToken).ConfigureAwait(false);

                // Per-item exclusions (issue #57) — see the matching block in SyncMoviesAsync.
                var excludedSeriesSet = ContentExclusionFilter.BuildSet(config.ExcludedSeriesIds);
                var excludedSeriesItems = new List<Tuple<string, int?>>();
                var excludedSeriesRaw = new List<SeriesInfo>();
                var allSeries = fetchedSeries;
                if (excludedSeriesSet.Count > 0)
                {
                    allSeries = new List<SeriesInfo>();
                    foreach (var s in fetchedSeries)
                    {
                        if (ContentExclusionFilter.IsExcluded(excludedSeriesSet, s.SeriesId))
                        {
                            var excludedName = config.EnableContentNameCleaning
                                ? ContentNameCleaner.CleanContentName(s.Name, config.ContentRemoveTerms)
                                : s.Name;
                            excludedSeriesItems.Add(Tuple.Create(excludedName, s.CategoryId));
                            excludedSeriesRaw.Add(s);
                        }
                        else
                        {
                            allSeries.Add(s);
                        }
                    }

                    _logger.Info("Per-item exclusions: skipping {0} of {1} series",
                        excludedSeriesItems.Count, fetchedSeries.Count);
                }
```

- [ ] **Step 4: Fold excluded series into the delta watermark**

`maxSeriesTs` is advanced inside the per-series loop, which excluded series never enter. Immediately after the `saveConfig?.Invoke();` on line 691 (the end of the naming-flag reset block), add:

```csharp

                // Excluded series never enter the loop below, so fold their timestamps in here —
                // otherwise the watermark stalls behind an excluded-but-recent title.
                foreach (var s in excludedSeriesRaw)
                {
                    long excludedLm;
                    if (long.TryParse(s.LastModified, NumberStyles.None, CultureInfo.InvariantCulture, out excludedLm)
                        && excludedLm > maxSeriesTs)
                    {
                        maxSeriesTs = excludedLm;
                    }
                }
```

- [ ] **Step 5: Run the removal pass**

Replace the orphan-cleanup block at lines 1033-1038:

```csharp
                if (config.CleanupOrphans)
                {
                    _seriesProgress.Phase = "Cleaning up orphaned files";
                    var showsRoot = Path.Combine(config.StrmLibraryPath, "Shows");
                    var deletedEpisodes = CleanupOrphans(showsRoot, writtenPaths, config.OrphanSafetyThreshold);
```

Insert **before** it:

```csharp
                if (excludedSeriesItems.Count > 0)
                {
                    _seriesProgress.Phase = "Removing excluded series";
                    _seriesProgress.Deleted += RemoveExcludedContent(
                        config, excludedSeriesItems, config.SeriesFolderMode, categoryNames, folderMappings, "Shows");
                }

```

Then read the lines that follow the `CleanupOrphans` call (`sed -n '1033,1050p'`) and make sure the existing assignment of `deletedEpisodes` into `_seriesProgress.Deleted` uses `+=` rather than `=`, so the excluded-removal count is not overwritten.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test Emby.Xtream.Plugin.Tests/ -v minimal`
Expected: PASS, whole suite green.

- [ ] **Step 7: Commit**

```bash
git add Emby.Xtream.Plugin/Service/StrmSyncService.cs \
        Emby.Xtream.Plugin.Tests/SyncSeriesIntegrationTests.cs
git commit -m "feat: honour per-series exclusions during STRM sync"
```

---

### Task 5: API routes listing titles in a category

**Goal:** Two read-only GET endpoints the config UI calls when a category row is expanded.

**Files:**
- Modify: `Emby.Xtream.Plugin/Api/XtreamTunerApi.cs` (route DTOs near line 46, result DTO near line 190, handlers after `Get(GetSeriesCategories)`)

**Acceptance Criteria:**
- [ ] `GET /XtreamTuner/Items/Vod?CategoryId=N` returns `[{Id, Name}, …]` sorted by name
- [ ] `GET /XtreamTuner/Items/Series?CategoryId=N` returns the same shape
- [ ] Both return an empty list (not a 500) when connection settings are missing or the provider call fails
- [ ] Both use the tolerant JSON converters, so an off-type provider field cannot break the listing

**Verify:** `dotnet build Emby.Xtream.Plugin/Emby.Xtream.Plugin.csproj -v minimal` → Build succeeded; then manually `curl -s "http://<emby-host>/emby/XtreamTuner/Items/Vod?CategoryId=<id>&api_key=<key>" | head -c 400` after deploying → JSON array of `{"Id":…,"Name":"…"}`

**Steps:**

- [ ] **Step 1: Add the route DTOs**

In `Emby.Xtream.Plugin/Api/XtreamTunerApi.cs`, after the `GetSeriesCategories` class (line 46):

```csharp

    [Route("/XtreamTuner/Items/Vod", "GET", Summary = "Gets VOD movie titles for one category")]
    public class GetVodItems : IReturn<List<ContentItemSummary>>
    {
        public int CategoryId { get; set; }
    }

    [Route("/XtreamTuner/Items/Series", "GET", Summary = "Gets series titles for one category")]
    public class GetSeriesItems : IReturn<List<ContentItemSummary>>
    {
        public int CategoryId { get; set; }
    }
```

- [ ] **Step 2: Add the result DTO**

After the `BrowsePathResult` class (line 182):

```csharp

    /// <summary>Minimal title projection for the per-item selection UI — a category can hold
    /// thousands of titles, so only the ID and display name cross the wire.</summary>
    public class ContentItemSummary
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }
```

- [ ] **Step 3: Add the handlers**

In `XtreamTunerApi`, immediately after the closing brace of `Get(GetSeriesCategories)`:

```csharp

        public async Task<object> Get(GetVodItems request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.BaseUrl) ||
                string.IsNullOrEmpty(config.Username) || string.IsNullOrEmpty(config.Password))
            {
                return new List<ContentItemSummary>();
            }

            var url = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_vod_streams&category_id={3}",
                config.BaseUrl, Uri.EscapeDataString(config.Username), Uri.EscapeDataString(config.Password),
                request.CategoryId);

            try
            {
                using (var httpClient = Plugin.CreateHttpClient())
                {
                    var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                    var streams = System.Text.Json.JsonSerializer.Deserialize<List<VodStreamInfo>>(json, ItemListJsonOptions)
                        ?? new List<VodStreamInfo>();

                    return streams
                        .Select(s => new ContentItemSummary { Id = s.StreamId, Name = s.Name })
                        .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
            catch
            {
                return new List<ContentItemSummary>();
            }
        }

        public async Task<object> Get(GetSeriesItems request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.BaseUrl) ||
                string.IsNullOrEmpty(config.Username) || string.IsNullOrEmpty(config.Password))
            {
                return new List<ContentItemSummary>();
            }

            var url = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_series&category_id={3}",
                config.BaseUrl, Uri.EscapeDataString(config.Username), Uri.EscapeDataString(config.Password),
                request.CategoryId);

            try
            {
                using (var httpClient = Plugin.CreateHttpClient())
                {
                    var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                    var series = System.Text.Json.JsonSerializer.Deserialize<List<SeriesInfo>>(json, ItemListJsonOptions)
                        ?? new List<SeriesInfo>();

                    return series
                        .Select(s => new ContentItemSummary { Id = s.SeriesId, Name = s.Name })
                        .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
            catch
            {
                return new List<ContentItemSummary>();
            }
        }
```

- [ ] **Step 4: Add the shared JSON options**

Provider fields arrive off-type across servers (ADR-010) and one bad field would blank the whole listing. Add as a private static field on `XtreamTunerApi`, just inside the class opening brace (before `Get(GetEpgXml)`):

```csharp
        /// <summary>
        /// Shared options for the per-item listing endpoints. Uses the tolerant converters
        /// so a single off-type provider field can't blank an entire category listing (ADR-010).
        /// </summary>
        private static readonly System.Text.Json.JsonSerializerOptions ItemListJsonOptions =
            new System.Text.Json.JsonSerializerOptions
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
                PropertyNameCaseInsensitive = true,
                Converters =
                {
                    new Client.Models.TolerantStringConverter(),
                    new Client.Models.TolerantNullableIntConverter(),
                },
            };

```

- [ ] **Step 5: Verify it builds**

Run: `dotnet build Emby.Xtream.Plugin/Emby.Xtream.Plugin.csproj -v minimal`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add Emby.Xtream.Plugin/Api/XtreamTunerApi.cs
git commit -m "feat: add per-category title listing endpoints"
```

---

### Task 6: Config UI for per-title selection

**Goal:** Each VOD and series category row in Single Folder mode gets a `▸` expander that lazily loads its titles and renders a checkbox per title, with the unticked ones written to the exclusion lists on save.

**Files:**
- Modify: `Emby.Xtream.Plugin/Configuration/Web/config.html:774-776` and `:900-910` (hint text)
- Modify: `Emby.Xtream.Plugin/Configuration/Web/config.js` — state (line 26), load (lines 393/401), save (lines 506/512), `renderCategoryList` (line 1185), `loadVodCategories` (line 1292), `loadSeriesCategories` (line 1413), `toggleAllVodCategories`/`toggleAllSeriesCategories` (lines 1314/1435), plus new functions and one delegated click handler

**Acceptance Criteria:**
- [ ] Every category row in the VOD and Series single-mode lists shows a `▸` expander
- [ ] Expanding fetches titles once and caches them for the page session; collapsing and re-expanding does not refetch
- [ ] Unticking a title adds its ID to the exclusion list; reticking removes it
- [ ] "Select All" / "Deselect All" on categories clears that type's exclusion list, so no stale exclusions survive a wholesale change
- [ ] Exclusions round-trip: save, reload the config page, re-expand → previously unticked titles are still unticked
- [ ] The existing category filter box still hides whole rows (expander panel included)

**Verify:** Deploy the DLL, open Plugin Config → Movies, expand a category, untick a title, save, reload the page, re-expand → the title is still unticked. Then run a movie sync and confirm the title's folder is gone from the library.

**Steps:**

- [ ] **Step 1: Add page state**

In `Emby.Xtream.Plugin/Configuration/Web/config.js`, after line 26 (`this.selectedSeriesCategoryIds = [];`):

```js
        this.excludedVodStreamIds = [];
        this.excludedSeriesIds = [];
        this.contentItemsByCategory = { vod: {}, series: {} };
        this.expandedContentCategories = { vod: {}, series: {} };
```

- [ ] **Step 2: Load and save the exclusion lists**

After line 393 (`instance.selectedVodCategoryIds = config.SelectedVodCategoryIds || [];`):

```js
            instance.excludedVodStreamIds = config.ExcludedVodStreamIds || [];
```

After line 401 (`instance.selectedSeriesCategoryIds = config.SelectedSeriesCategoryIds || [];`):

```js
            instance.excludedSeriesIds = config.ExcludedSeriesIds || [];
```

After line 506 (`config.SelectedVodCategoryIds = getSelectedVodCategoryIds(instance);`):

```js
            config.ExcludedVodStreamIds = instance.excludedVodStreamIds.slice();
```

After line 512 (`config.SelectedSeriesCategoryIds = getSelectedSeriesCategoryIds(instance);`):

```js
            config.ExcludedSeriesIds = instance.excludedSeriesIds.slice();
```

- [ ] **Step 3: Teach `renderCategoryList` to draw expanders**

Replace `renderCategoryList` (lines 1185-1200) with:

```js
    // contentType: 'vod' | 'series' to draw a per-title expander, or null/undefined for
    // Live TV (per-channel selection is not implemented for Live TV in this plugin).
    function renderCategoryList(view, listSelector, categories, checkboxClass, selectedIds, contentType) {
        var listEl = view.querySelector(listSelector);
        if (!listEl) return;
        var html = '';
        for (var i = 0; i < categories.length; i++) {
            var cat = categories[i];
            var checked = selectedIds.indexOf(cat.CategoryId) >= 0 ? ' checked' : '';
            html += '<div class="checkboxContainer" style="margin:0.15em 0;">';
            html += '<div style="display:flex; align-items:center;">';
            if (contentType) {
                html += '<button type="button" class="contentItemToggle" data-content-type="' + contentType + '"';
                html += ' data-cat-id="' + cat.CategoryId + '" title="Show titles in this category"';
                html += ' style="background:none; border:none; color:inherit; cursor:pointer; opacity:0.6; padding:0 0.4em 0 0; font-size:0.95em;">&#9656;</button>';
            }
            html += '<label style="display:flex; align-items:center; cursor:pointer; flex:1;">';
            html += '<input type="checkbox" class="' + checkboxClass + '" data-category-id="' + cat.CategoryId + '"' + checked + ' style="margin-right:0.5em;" />';
            html += '<span>' + escapeHtml(cat.CategoryName) + '</span>';
            html += '</label>';
            html += '</div>';
            if (contentType) {
                html += '<div class="contentItemList" data-content-type="' + contentType + '" data-cat-id="' + cat.CategoryId + '"';
                html += ' style="display:none; margin:0.2em 0 0.5em 1.6em; padding-left:0.6em; border-left:2px solid rgba(128,128,128,0.25);"></div>';
            }
            html += '</div>';
        }
        listEl.innerHTML = html;
    }
```

- [ ] **Step 4: Route the cached-category load through the new parameter**

Line 1123 becomes:

```js
                    renderCategoryList(view, '.vodCategoriesList', vodCats, 'vodCategoryCheckbox', instance.selectedVodCategoryIds, 'vod');
```

Line 1150 becomes:

```js
                    renderCategoryList(view, '.seriesCategoriesList', seriesCats, 'seriesCategoryCheckbox', instance.selectedSeriesCategoryIds, 'series');
```

Line 1175 (Live TV) stays as-is — no sixth argument, so no expander.

- [ ] **Step 5: Deduplicate the refresh-path rendering**

`loadVodCategories` and `loadSeriesCategories` each hand-roll the same markup `renderCategoryList` produces. Replace the inline loops so both paths draw identical rows.

In `loadVodCategories`, replace lines 1292-1303 (from `var html = '';` through `listEl.innerHTML = html;`) with:

```js
            renderCategoryList(view, '.vodCategoriesList', categories, 'vodCategoryCheckbox', instance.selectedVodCategoryIds, 'vod');
```

In `loadSeriesCategories`, replace lines 1413-1424 (same span) with:

```js
            renderCategoryList(view, '.seriesCategoriesList', categories, 'seriesCategoryCheckbox', instance.selectedSeriesCategoryIds, 'series');
```

Both functions already declare `var view = instance.view;` and `var listEl = …` at the top; leave those alone — `listEl` is still used for the loading/error branches.

- [ ] **Step 6: Add the expand/fetch/render functions**

Add after `renderCategoryList` (before the `// ---- Live TV Categories ----` comment):

```js
    // ---- Per-title selection (issue #57) ----

    function toggleContentItemPanel(instance, contentType, categoryId, btn) {
        var view = instance.view;
        var panel = view.querySelector('.contentItemList[data-content-type="' + contentType + '"][data-cat-id="' + categoryId + '"]');
        if (!panel) return;

        var expanded = !!instance.expandedContentCategories[contentType][categoryId];
        if (expanded) {
            instance.expandedContentCategories[contentType][categoryId] = false;
            panel.style.display = 'none';
            if (btn) btn.innerHTML = '&#9656;';
            return;
        }

        instance.expandedContentCategories[contentType][categoryId] = true;
        panel.style.display = '';
        if (btn) btn.innerHTML = '&#9662;';

        if (instance.contentItemsByCategory[contentType][categoryId]) {
            renderContentItems(instance, contentType, categoryId);
        } else {
            fetchContentItems(instance, contentType, categoryId);
        }
    }

    function fetchContentItems(instance, contentType, categoryId) {
        var view = instance.view;
        var panel = view.querySelector('.contentItemList[data-content-type="' + contentType + '"][data-cat-id="' + categoryId + '"]');
        if (!panel) return;

        var label = contentType === 'vod' ? 'movies' : 'series';
        panel.innerHTML = '<div style="opacity:0.5; padding:0.25em 0;">Loading ' + label + '...</div>';

        // Query string appended by hand: every other call in this file uses the
        // single-argument form of getUrl, so don't introduce a second convention here.
        var endpoint = contentType === 'vod' ? 'XtreamTuner/Items/Vod' : 'XtreamTuner/Items/Series';
        var apiUrl = ApiClient.getUrl(endpoint) + '?CategoryId=' + encodeURIComponent(categoryId);

        ApiClient.getJSON(apiUrl).then(function (items) {
            instance.contentItemsByCategory[contentType][categoryId] = items || [];
            renderContentItems(instance, contentType, categoryId);
        }).catch(function () {
            panel.innerHTML = '<div style="color:#cc0000; padding:0.25em 0;">Failed to load ' + label +
                '. Save your connection settings first, then try again.</div>';
        });
    }

    function renderContentItems(instance, contentType, categoryId) {
        var view = instance.view;
        var panel = view.querySelector('.contentItemList[data-content-type="' + contentType + '"][data-cat-id="' + categoryId + '"]');
        if (!panel) return;

        var items = instance.contentItemsByCategory[contentType][categoryId] || [];
        var label = contentType === 'vod' ? 'movies' : 'series';
        if (items.length === 0) {
            panel.innerHTML = '<div style="opacity:0.5; padding:0.25em 0;">No ' + label + ' in this category.</div>';
            return;
        }

        var excluded = {};
        var list = contentType === 'vod' ? instance.excludedVodStreamIds : instance.excludedSeriesIds;
        for (var e = 0; e < list.length; e++) {
            excluded[list[e]] = true;
        }

        // Plain styled buttons, NOT is="emby-button": Emby's custom-element upgrade does not
        // run on markup injected via innerHTML, so an emby-button here renders unstyled.
        var btnStyle = 'background:none; border:1px solid rgba(128,128,128,0.35); color:inherit; ' +
            'cursor:pointer; padding:0.15em 0.6em; border-radius:3px; font-size:0.9em;';

        var html = '<div style="margin:0.25em 0 0.4em;">';
        html += '<button type="button" class="contentItemSelectAll"';
        html += ' data-content-type="' + contentType + '" data-cat-id="' + categoryId + '"';
        html += ' style="' + btnStyle + ' margin-right:0.4em;">Select All</button>';
        html += '<button type="button" class="contentItemDeselectAll"';
        html += ' data-content-type="' + contentType + '" data-cat-id="' + categoryId + '"';
        html += ' style="' + btnStyle + '">Deselect All</button>';
        html += '<span style="opacity:0.5; margin-left:0.6em;">' + items.length + ' ' + label + '</span>';
        html += '</div>';

        for (var i = 0; i < items.length; i++) {
            var item = items[i];
            var checked = excluded[item.Id] ? '' : ' checked';
            html += '<div style="margin:0.1em 0;">';
            html += '<label style="display:flex; align-items:center; cursor:pointer;">';
            html += '<input type="checkbox" class="contentItemCheckbox" data-content-type="' + contentType + '"';
            html += ' data-item-id="' + item.Id + '"' + checked + ' style="margin-right:0.5em;" />';
            html += '<span>' + escapeHtml(item.Name || '(unnamed)') + '</span>';
            html += '</label>';
            html += '</div>';
        }

        panel.innerHTML = html;
    }

    function setContentExclusion(instance, contentType, itemId, shouldBeExcluded) {
        var list = contentType === 'vod' ? instance.excludedVodStreamIds : instance.excludedSeriesIds;
        var idx = list.indexOf(itemId);
        if (shouldBeExcluded && idx === -1) {
            list.push(itemId);
        } else if (!shouldBeExcluded && idx !== -1) {
            list.splice(idx, 1);
        }
    }

    function toggleAllContentItems(instance, contentType, categoryId, checked) {
        var view = instance.view;
        var panel = view.querySelector('.contentItemList[data-content-type="' + contentType + '"][data-cat-id="' + categoryId + '"]');
        if (!panel) return;
        var boxes = panel.querySelectorAll('.contentItemCheckbox');
        for (var i = 0; i < boxes.length; i++) {
            boxes[i].checked = checked;
            setContentExclusion(instance, contentType, parseInt(boxes[i].getAttribute('data-item-id'), 10), !checked);
        }
    }

    // Wholesale category changes make a stale per-title exclusion list actively confusing
    // (a category the user just ticked would come back with holes in it), so reset it and
    // redraw any panel that is currently open.
    function clearContentExclusions(instance, contentType) {
        if (contentType === 'vod') {
            instance.excludedVodStreamIds = [];
        } else {
            instance.excludedSeriesIds = [];
        }

        var expanded = instance.expandedContentCategories[contentType] || {};
        for (var categoryId in expanded) {
            if (expanded[categoryId] && instance.contentItemsByCategory[contentType][categoryId]) {
                renderContentItems(instance, contentType, parseInt(categoryId, 10));
            }
        }
    }
```

- [ ] **Step 7: Bind one delegated click/change handler**

Panels are re-rendered from `innerHTML`, so per-element listeners would be lost. Bind once on the two list containers. Add in the constructor, immediately after the `.btnDeselectAllSeriesCategories` listener (line ~188):

```js
        // Per-title selection: delegated so re-rendered panels stay live
        ['.vodCategoriesList', '.seriesCategoriesList'].forEach(function (selector) {
            var listEl = view.querySelector(selector);
            if (!listEl) return;

            listEl.addEventListener('click', function (e) {
                var toggle = e.target.closest('.contentItemToggle');
                if (toggle) {
                    e.preventDefault();
                    toggleContentItemPanel(
                        self,
                        toggle.getAttribute('data-content-type'),
                        parseInt(toggle.getAttribute('data-cat-id'), 10),
                        toggle);
                    return;
                }

                var selectAll = e.target.closest('.contentItemSelectAll');
                if (selectAll) {
                    e.preventDefault();
                    toggleAllContentItems(
                        self,
                        selectAll.getAttribute('data-content-type'),
                        parseInt(selectAll.getAttribute('data-cat-id'), 10),
                        true);
                    return;
                }

                var deselectAll = e.target.closest('.contentItemDeselectAll');
                if (deselectAll) {
                    e.preventDefault();
                    toggleAllContentItems(
                        self,
                        deselectAll.getAttribute('data-content-type'),
                        parseInt(deselectAll.getAttribute('data-cat-id'), 10),
                        false);
                }
            });

            listEl.addEventListener('change', function (e) {
                var cb = e.target.closest('.contentItemCheckbox');
                if (!cb) return;
                setContentExclusion(
                    self,
                    cb.getAttribute('data-content-type'),
                    parseInt(cb.getAttribute('data-item-id'), 10),
                    !cb.checked);
            });
        });
```

- [ ] **Step 8: Clear exclusions on wholesale category toggles**

Replace `toggleAllVodCategories` (line 1314) and `toggleAllSeriesCategories` (line 1435) so they take the instance and reset exclusions:

```js
    function toggleAllVodCategories(instance, checked) {
        var view = instance.view;
        var checkboxes = view.querySelectorAll('.vodCategoryCheckbox');
        for (var i = 0; i < checkboxes.length; i++) {
            checkboxes[i].checked = checked;
        }
        clearContentExclusions(instance, 'vod');
        updateCategoryCountBadge(view, 'vod');
    }
```

```js
    function toggleAllSeriesCategories(instance, checked) {
        var view = instance.view;
        var checkboxes = view.querySelectorAll('.seriesCategoryCheckbox');
        for (var i = 0; i < checkboxes.length; i++) {
            checkboxes[i].checked = checked;
        }
        clearContentExclusions(instance, 'series');
        updateCategoryCountBadge(view, 'series');
    }
```

Update the four call sites (lines 165, 169, 182, 186) from `toggleAllVodCategories(view, true)` to `toggleAllVodCategories(self, true)`, and likewise for the `false` and series variants. Then confirm no other callers remain:

```bash
grep -n "toggleAllVodCategories\|toggleAllSeriesCategories" Emby.Xtream.Plugin/Configuration/Web/config.js
```

- [ ] **Step 9: Add the hint text**

In `Emby.Xtream.Plugin/Configuration/Web/config.html`, replace lines 774-776:

```html
                            <div class="fieldDescription" style="margin-bottom:0.5em;">
                                Select which VOD categories to sync. Leave all unchecked to include all movies.
                            </div>
```

with:

```html
                            <div class="fieldDescription" style="margin-bottom:0.5em;">
                                Select which VOD categories to sync. Leave all unchecked to include all movies.
                                Click the arrow next to a category to pick individual titles. Unticking a title
                                stops it syncing and deletes its folder on the next sync.
                            </div>
```

Find the equivalent series description (around line 900, above `.seriesCategoriesList`) with `sed -n '885,910p' Emby.Xtream.Plugin/Configuration/Web/config.html` and extend it the same way, substituting "series categories" / "series".

- [ ] **Step 10: Build and deploy**

```bash
bash Emby.Xtream.Plugin/build.sh
```
Expected: tests pass, `Emby.Xtream.Plugin/out/Emby.Xtream.Plugin.dll` written.

Deploy using the commands in `CLAUDE.local.md` → Deployment (scp the DLL, restart the `emby` container).

- [ ] **Step 11: Verify in the browser**

1. Open Plugin Config → Movies. Hard-refresh (the page JS is cached).
2. Confirm each category row has a `▸`.
3. Expand one, wait for titles, untick one.
4. Save, reload the page, re-expand the same category → the title is still unticked.
5. Check the browser console for errors — expected: none.

- [ ] **Step 12: Commit**

```bash
git add Emby.Xtream.Plugin/Configuration/Web/config.js \
        Emby.Xtream.Plugin/Configuration/Web/config.html
git commit -m "feat: add per-title selection UI to VOD and series category lists"
```

---

### Task 7: ADR and documentation

**Goal:** Record the design fork (explicit exclusion list vs. auto-detecting user deletions) and the divergence from Jellyfin's orphan-cleanup-based removal.

**Files:**
- Create: `docs/decisions/012-per-item-content-exclusion.md`
- Modify: `CLAUDE.md` (add a subsection under "Emby Plugin Architecture")

**Acceptance Criteria:**
- [ ] ADR follows the Context / Problem / Alternatives / Decision / Consequences structure of `docs/decisions/001-bypass-dispatcharr-proxy.md`
- [ ] ADR records why auto-detecting manual deletions was rejected
- [ ] ADR records why removal bypasses `CleanupOrphans` and `OrphanSafetyThreshold`
- [ ] `CLAUDE.md` gains a short note so future work does not re-litigate the removal semantics

**Verify:** `ls docs/decisions/012-per-item-content-exclusion.md && grep -n "per-item" CLAUDE.md` → file exists and the note is present

**Steps:**

- [ ] **Step 1: Read the ADR template**

Run: `cat docs/decisions/001-bypass-dispatcharr-proxy.md`

Match its heading structure and tone.

- [ ] **Step 2: Write the ADR**

Create `docs/decisions/012-per-item-content-exclusion.md` covering:

- **Context** — issue #57. Category-level filters are too coarse; users want specific titles gone permanently. The Jellyfin sibling plugin already has per-item selection.
- **Problem** — the reporter asked for two different things: per-title selection (Jellyfin parity) and "don't re-add folders I deleted by hand". These need different mechanisms.
- **Alternatives considered**
  1. *Auto-detect manual deletions* — persist every written path, treat written-before-now-missing as an implicit exclusion. **Rejected**: a transient mount failure or an unmounted library path presents as "the user deleted everything", which is exactly the failure mode `OrphanSafetyThreshold` exists to bound. Silent, and unrecoverable without re-ticking every title.
  2. *Explicit exclusion list only* (chosen) — deterministic, visible in config, reversible.
  3. *Rely on orphan cleanup for removal* (Jellyfin's approach) — **rejected for Emby**: `CleanupOrphans` defaults to false here and the 20% threshold blocks a large deselect, so unticking titles would appear to do nothing.
- **Decision** — `ExcludedVodStreamIds` / `ExcludedSeriesIds` on `PluginConfiguration`; partition the catalogue in `StrmSyncService` before the write loop; delete excluded folders in a targeted `RemoveExcludedContent` pass that ignores `CleanupOrphans` and `OrphanSafetyThreshold`, because an exclusion is a deliberate user action rather than a provider anomaly. Folder matching strips `[tmdbid=…]`/`[tvdbid=…]` suffixes via the existing `StripFolderIdSuffix`.
- **Consequences**
  - Re-ticking a title re-creates it on the next sync: both smart-skip paths already guard on the folder existing, so no forced full re-sync is needed.
  - Delta watermarks are computed over the unfiltered catalogue so an excluded-but-recent title cannot stall them.
  - Series granularity is per-series, not per-episode.
  - The editing UI only appears in Single Folder mode; the lists are still honoured at sync time in every folder mode.
  - Two extra provider calls per category expand, made lazily so page load is unaffected.
  - The literal "don't re-add what I deleted on disk" ask is not covered; users must untick the title in config.

- [ ] **Step 3: Add the CLAUDE.md note**

Append to `CLAUDE.md` under the "Emby Plugin Architecture" section, before "## Architecture Decision Records (ADRs)":

```markdown
### Per-item exclusions delete folders directly, not via orphan cleanup

`ExcludedVodStreamIds` / `ExcludedSeriesIds` remove an item's folder in a targeted pass
(`StrmSyncService.RemoveExcludedContent`) that ignores both `CleanupOrphans` and
`OrphanSafetyThreshold`. That threshold guards against a provider returning a truncated
catalogue; an exclusion is a deliberate user action, so suppressing the delete would just
read as the filter doing nothing. Folder matching strips `[tmdbid=…]`/`[tvdbid=…]` suffixes,
so a title is found regardless of the metadata-ID naming settings in force when it was written.
See [ADR-012](docs/decisions/012-per-item-content-exclusion.md).
```

- [ ] **Step 4: Commit**

```bash
git add docs/decisions/012-per-item-content-exclusion.md CLAUDE.md
git commit -m "docs: add ADR-012 for per-item content exclusion"
```

---

### Task 8: Full verification and branch wrap-up

**Goal:** Whole suite green, plugin builds, and the change is ready for a PR.

**Files:** none (verification only)

**Acceptance Criteria:**
- [ ] `bash Emby.Xtream.Plugin/build.sh` completes: tests pass and the DLL is produced
- [ ] No uncommitted changes remain
- [ ] Branch is pushed

**Verify:** `bash Emby.Xtream.Plugin/build.sh` → "DLL ready at: …" and `git status --short` → empty

**Steps:**

- [ ] **Step 1: Run the full build**

```bash
bash Emby.Xtream.Plugin/build.sh
```
Expected: `=== Running Tests ===` with 0 failures, then `DLL ready at: .../out/Emby.Xtream.Plugin.dll`.

If any test fails, fix it before continuing — do not proceed with a red suite.

- [ ] **Step 2: Confirm a clean tree**

```bash
git status --short
```
Expected: no output.

- [ ] **Step 3: Push the branch**

```bash
git push -u origin feat/per-item-content-filtering
```

- [ ] **Step 4: Stop and hand back**

Do not open the PR or comment on issue #57 without showing the drafts first (project convention: `CLAUDE.md` → Git Workflow, and global rule "always draft, never auto-post").
