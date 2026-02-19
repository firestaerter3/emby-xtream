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
