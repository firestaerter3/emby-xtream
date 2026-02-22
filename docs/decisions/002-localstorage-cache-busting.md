# ADR-002: localStorage-Based Cache Busting for Plugin Config Pages

**Date**: 2026-02-22
**Status**: ACTIVE
**Affects**: `config.js` (loadDashboard callback), `XtreamTunerApi.cs` (DashboardResult), `config.html` (version label)

---

## Context

Emby serves plugin configuration pages (`config.html`, `config.js`) with `Cache-Control: public`
but no `max-age` directive. Browsers apply heuristic freshness and skip revalidation — meaning
after a plugin DLL update, users can get stale HTML/JS until they manually hard-refresh.

The `?v=4.9.3.0` query parameter that Emby appends is tied to the **Emby server version**, not the
plugin version. It only changes when Emby itself is upgraded, which is rare compared to plugin
releases.

## Problem

After deploying a new plugin DLL and restarting Emby, users opening the plugin config page may
see the old UI (missing new fields, stale JS logic) because the browser serves cached resources.
This leads to confusing bug reports ("I updated but nothing changed") and potential data issues
if old JS writes config fields the new backend doesn't expect.

## Alternatives Considered

### 1. Rename page registration on each release

Rename the HTML/JS page name (e.g. `xtreamconfig` → `xtreamconfig2`) so the browser fetches a
completely new URL with no cache history.

**Tried and failed.** Emby's SPA uses hash-based navigation (`#!/configurationpage?name=...`).
Changing the page name broke navigation — the old cached HTML still referenced the old name,
and bookmarks/links stopped working. Had to be reverted across two commits (`4f62452` →
`8fb3214`). Not viable without controlling Emby's routing layer.

### 2. Build-time version stamp (`_BUILD_VERSION`)

Have `build.sh` use `sed` to replace a `var _BUILD_VERSION = 'dev'` sentinel in `config.js`
with the real version (e.g. `'1.4.15'`) before `dotnet publish`, then restore the file. At
runtime, compare the stamped version against `data.PluginVersion` from the Dashboard API.

**Pros**: JS knows exactly what version it is — can detect staleness even on first visit.
**Cons**: Build script complexity (sed, .bak file, trap/restore). Fragile if build is
interrupted — working tree left dirty. Two files to coordinate. The `'dev'` sentinel means
it's inert during development, which is both a pro and a con.

Rejected because the runtime mechanism (fetch pre-warm + reload) is identical to Solution 3,
and the only advantage — detecting staleness on first-ever visit — is too rare a scenario to
justify the build complexity.

### 3. localStorage version tracking (chosen)

After the Dashboard API responds, compare `data.PluginVersion` against
`localStorage['xtream-plugin-version']`. On mismatch (and a previous version was stored),
pre-warm the cache with `fetch({ cache: 'reload' })` and reload the page.

## Decision

**Use localStorage version tracking (Solution 3).**

### How It Works

1. The Dashboard API (`/emby/xtream/dashboard`) now returns `PluginVersion` — the assembly
   version read from `typeof(Plugin).Assembly.GetName().Version`.

2. On every dashboard load, `config.js` compares `data.PluginVersion` against
   `localStorage.getItem('xtream-plugin-version')`.

3. **Version match** (or first visit): store the version, continue normally.

4. **Version mismatch** (update detected):
   - Set `sessionStorage['xtream-cache-bust'] = '1'` (reload-loop guard)
   - Fetch both `configurationpage?name=xtreamconfig&v=...` and
     `configurationpage?name=xtreamconfigjs&v=...` with `{ cache: 'reload' }`
   - Call `location.reload()` after both fetches complete
   - After reload: versions now match, `sessionStorage` guard is cleared

5. The `sessionStorage` guard ensures at most **one** reload per browser tab per session.
   If the fetch fails or the version still mismatches after reload, no infinite loop occurs.

### Files Changed

- **`XtreamTunerApi.cs`**: Added `PluginVersion` property to `DashboardResult` class;
  populated from `typeof(Plugin).Assembly.GetName().Version?.ToString()`.

- **`config.js`**: 18 lines in the `loadDashboard` callback — localStorage check, fetch
  pre-warm with `{ cache: 'reload' }`, sessionStorage reload guard. Also renders the version
  string in the health bar via `renderDashboardStatus`.

- **`config.html`**: Moved `.pluginVersion` span inside the health bar for layout consistency.

## Known Limitations and Edge Cases

### First-ever visit with stale cache

If a user has **never** visited the plugin page before (no localStorage entry), but their
browser has a stale cached copy (e.g. from a CDN or shared cache), the logic stores the
current version but cannot detect staleness. The user sees whatever the cache has.

**Why this is acceptable**: A user who has never visited the page has no expectations about
what it should look like. And the next update after this visit *will* trigger the bust.

### localStorage cleared by user

If a user clears localStorage (or uses private browsing), the next visit just re-stores the
version without busting. However, clearing localStorage also typically clears the browser
cache, so the resources are likely fresh anyway.

### Multiple tabs

If two tabs are open during an update, both will independently detect the mismatch and reload.
The `sessionStorage` guard is per-tab, so each tab reloads exactly once. This is harmless.

### `PluginVersion` unavailable

If the Dashboard API fails to return `PluginVersion` (e.g. older DLL without the field),
the check is skipped entirely. No reload, no error. The feature is inert.

### Assembly version vs. tag version

`Assembly.GetName().Version` returns the four-part version from the `.csproj` `<Version>`
property (e.g. `1.4.15.0`), which is set by `build.sh` from the latest git tag. During
local development without tags, this may be `1.0.0.0`. The localStorage comparison is a
simple string equality check — any difference triggers a bust.

## How to Revert

If users report issues (infinite reloads, broken page after update, etc.):

1. **Quick disable** — remove or comment out the 18-line block in `config.js` between
   `// Auto-bust browser cache when plugin was updated.` and
   `sessionStorage.removeItem('xtream-cache-bust');` (inclusive). The `PluginVersion` field
   in the API response is harmless and can stay.

2. **Full revert** — also remove `PluginVersion` from `DashboardResult` and the assembly
   version read in `GetDashboard()`, and move `.pluginVersion` back above the health bar
   in `config.html`.

3. **User-side recovery** — if a single user is stuck in a reload loop (should not happen
   due to sessionStorage guard, but just in case):
   ```
   // In browser console:
   sessionStorage.removeItem('xtream-cache-bust');
   localStorage.removeItem('xtream-plugin-version');
   ```

## Consequences

- Every plugin update now triggers one automatic page reload on each user's first visit —
  transparent to the user but ensures they always see the correct UI.
- No build script changes required. The mechanism is entirely runtime.
- The Dashboard API response grows by one small string field (`PluginVersion`).
- If this approach proves insufficient (e.g. the first-visit edge case matters), Solution 2
  (build-time stamp) can be layered on top without conflict — it replaces the localStorage
  check with an embedded version check but uses the same fetch + reload mechanism.
