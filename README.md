# Xtream Tuner Plugin for Emby

An Emby Server plugin that integrates Xtream-compatible IPTV services, providing Live TV with EPG, VOD movie libraries, and TV series — all managed through a built-in configuration dashboard.

## Features

- **Live TV & EPG** — M3U playlist generation with XMLTV electronic program guide, configurable cache intervals, and category-based channel filtering
- **Catch-up / Timeshift** — Access previously aired content on supported channels
- **VOD Movie Sync** — Generates STRM files for on-demand movies with folder organization (single, multi-category, or custom mapping)
- **Series Sync** — STRM generation for TV series with season/episode structure
- **TMDB Integration** — Automatic folder naming with TMDB/TVDb IDs for rich metadata matching
- **Dispatcharr Support** — Optional integration with [Dispatcharr](https://github.com/Dispatcharr/Dispatcharr) for stream management
- **Content Name Cleaning** — Configurable term removal for cleaner library names
- **Smart Sync** — Skip existing content, parallel fetching, orphan cleanup

## Requirements

- Emby Server 4.8+
- .NET SDK (for building)
- An Xtream-compatible IPTV provider

## Building

```bash
cd Emby.Xtream.Plugin
bash build.sh
```

The compiled plugin DLL will be at `Emby.Xtream.Plugin/out/Emby.Xtream.Plugin.dll`.

## Installation

Copy the DLL to your Emby plugins directory and restart Emby:

```bash
docker cp Emby.Xtream.Plugin/out/Emby.Xtream.Plugin.dll <container>:/config/plugins/
docker restart <container>
```

Then configure the plugin from the Emby dashboard under **Plugins > Xtream Tuner**.

## Configuration

The plugin settings page provides tabs for:

- **Settings** — Server URL, credentials, Dispatcharr integration
- **Live TV** — Category selection, channel filtering, EPG settings
- **Movies** — VOD sync with folder mode selection and category mapping
- **Series** — Series sync with folder mode and metadata ID options
- **Dashboard** — Sync status, history, and library statistics

## License

MIT
