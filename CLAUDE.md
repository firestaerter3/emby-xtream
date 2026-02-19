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

`PluginConfiguration` is serialized to XML by Emby automatically. Fields added to it persist across restarts without any extra work. Use this for: sync watermarks (`LastMovieSyncTimestamp`, `LastSeriesSyncTimestamp`), channel hashes (`LastChannelListHash`), and similar state.

### Guide grid empty — check browser localStorage

If the Emby guide grid shows no channels despite having channel data, check browser localStorage for a stale `guide-tagids` filter. The guide calls `/LiveTv/EPG?TagIds=<id>` and if the stored tag ID doesn't match any channel, the grid is empty. Fix: click the filter icon in the guide or run `localStorage.removeItem('guide-tagids')` in the browser console.

### SupportsGuideData controls whether Emby polls the tuner for EPG

When `SupportsGuideData()` returns `true`, Emby calls `GetProgramsInternal` on the tuner host for each channel. The `tunerChannelId` parameter is the raw stream ID (e.g. `"12345"`), not the Emby-prefixed form.

### Emby probes MediaSource.Path directly — disable for Dispatcharr

When `SupportsProbing = true` and `AnalyzeDurationMs > 0`, Emby runs ffprobe against `MediaSource.Path` **independently** of `GetChannelStream` / `ILiveStream`. For Dispatcharr proxy URLs this is destructive: the probe opens a short-lived HTTP connection (~0.1s, ~120KB), then closes it. Dispatcharr interprets the close as the last client leaving and tears down the channel. The real playback connection that follows immediately hits the teardown "channel stop signal" and fails — triggering a rapid retry storm visible in Dispatcharr logs as repeated `Fetchin channel with ID: <n>` → broken pipe cycles.

**Rule**: Always set `SupportsProbing = false` and `AnalyzeDurationMs = 0` for Dispatcharr proxy URLs (`/proxy/ts/stream/{uuid}`), regardless of whether stream stats are available. Direct Xtream URLs (no Dispatcharr) can still use probing when stats are absent.

## Git Workflow

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
