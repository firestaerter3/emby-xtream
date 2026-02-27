# Contributing to Emby Xtream Plugin

## Architecture

### Emby DI / SimpleInjector — service class construction

Emby's `ApplicationHost.CreateInstanceSafe` scans the plugin assembly and auto-registers **all public classes** whose constructor matches known DI types (e.g. `ILogger`). It instantiates these via SimpleInjector **before** the `Plugin` constructor runs.

This means:
- `Plugin.Instance` is **null** when Emby creates service classes
- `Plugin.Instance.Configuration` will throw (wrapped as `ActivationException` by SimpleInjector)
- **Never call `Plugin.Instance.*` in a service class constructor** (`StrmSyncService`, `LiveTvService`, `TmdbLookupService`, etc.)

**Safe pattern**: access `Plugin.Instance.Configuration` only from methods called at runtime, not from constructors.

### Plugin configuration path requires ApplicationPaths

`BasePlugin<T>.get_Configuration()` calls `Path.Combine(ApplicationPaths.PluginConfigurationsPath, ...)` internally. This path may not be initialised when Emby is scanning services, causing `ArgumentNullException: Value cannot be null. (Parameter 'path2')`. Same rule applies — defer config access to runtime methods.

### Delta sync state via PluginConfiguration

`PluginConfiguration` is serialised to XML by Emby automatically. Fields added to it persist across restarts without any extra work. Use this for sync watermarks (`LastMovieSyncTimestamp`, `LastSeriesSyncTimestamp`), channel hashes (`LastChannelListHash`), and similar durable state.

### SupportsGuideData and EPG

When `SupportsGuideData()` returns `true`, Emby calls `GetProgramsInternal` on the tuner host for each channel. The `tunerChannelId` parameter is whatever was set in `ChannelInfo.TunerChannelId` — the Gracenote station ID (e.g. `"51529"`) when Dispatcharr is enabled and a station ID exists, or the raw stream ID (e.g. `"12345"`) otherwise. Use `_tunerChannelIdToStreamId` to translate either form back to a stream ID.

### Dispatcharr proxy — never enable probing

When `SupportsProbing = true` and `AnalyzeDurationMs > 0`, Emby runs ffprobe against `MediaSource.Path` independently of `GetChannelStream`. For Dispatcharr proxy URLs (`/proxy/ts/stream/{uuid}`) this is destructive: the probe opens a short-lived connection then closes it, Dispatcharr interprets the close as the last client leaving and tears down the channel, and the real playback connection that follows hits the "channel stop signal" — triggering a rapid retry storm.

**Rule**: always set `SupportsProbing = false` and `AnalyzeDurationMs = 0` for Dispatcharr proxy URLs. Direct Xtream URLs can still use probing when stream stats are absent.

### Guide grid empty after setup

If the Emby guide shows no channels despite having data, check browser localStorage for a stale `guide-tagids` filter. The guide calls `/LiveTv/EPG?TagIds=<id>` — if the stored tag ID doesn't match any channel the grid is empty. Fix: click the filter icon in the guide, or run `localStorage.removeItem('guide-tagids')` in the browser console.

---

## Architecture Decision Records (ADRs)

Significant decisions are recorded in `docs/decisions/NNN-title.md`. Create a new ADR when:
- Choosing between multiple viable approaches (especially after trying alternatives that failed)
- Making a change driven by a non-obvious root cause
- Reversing or replacing a previous approach

Each ADR should cover: Context, Problem, Alternatives considered, Decision, and Consequences. See `docs/decisions/001-bypass-dispatcharr-proxy.md` as the template.

Numbering: sequential, zero-padded to 3 digits (`001`, `002`, ...).

---

## Development Workflow

### One concern per branch

Unrelated fixes should live on separate short-lived branches and be merged to `main` independently. This makes each change revertable without touching unrelated code.

### Commit before switching context

Never leave changes in the working tree when starting unrelated work. An uncommitted change is easy to tangle with later work. Use a `WIP:` commit or `git stash` if the change isn't ready.

### Building

```bash
cd Emby.Xtream.Plugin
bash build.sh
```

Output: `Emby.Xtream.Plugin/out/Emby.Xtream.Plugin.dll`

Requires .NET SDK 6.0+.
