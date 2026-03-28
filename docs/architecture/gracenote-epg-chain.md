# Gracenote EPG Architecture

How Gracenote guide data flows from a local station database through five applications and into Emby's TV Guide.

![Architecture Diagram](../../assets/complete-gracenote-architecture-v10.png)

## Components

### Gracenote Database (SQLite, local)

A proprietary SQLite database containing Gracenote station metadata: station IDs, names, call signs, logos, and lineup definitions (which stations belong to which cable/satellite/OTA package). This database is **not** provided by any of the tools — users obtain it from community sources or build their own.

Used **only at setup time** by Channel Identifiarr for matching channel names to station IDs. Not used at runtime for EPG data.

Key tables:
- `stations` — `station_id`, `name`, `call_sign`, `language`, `logo_uri`
- `station_lineups` — `station_id` → `lineup_id`, `channel_number`, `video_type`
- `lineups` — `lineup_id`, `name`, `type` (OTA/Cable/Satellite/VMVPD), `location`

### Channel Identifiarr (Web App)

A Docker-based web application ([GitHub](https://github.com/egyptiangio/channelidentifiarr)) that bridges the Gracenote database with Dispatcharr and Emby. It has **two distinct integration points**:

**Role A — Enriches Dispatcharr**: Writes `tvc_guide_stationid` (the Gracenote station ID) to each Dispatcharr channel via `PATCH /api/channels/channels/{id}/`. This is the per-channel matching — "ESPN2 HD is Gracenote station 45507."

**Role B — Adds listing providers to Emby**: Calls `POST /emby/LiveTv/ListingProviders` to register Gracenote lineups (type `embygn`) directly in Emby. Without this step, Emby has no Gracenote data source to query. The `scan-missing-listings` feature uses a greedy set-cover algorithm to find the minimum set of lineups that cover all stations.

See [channel-identifiarr.md](channel-identifiarr.md) for detailed documentation.

### Dispatcharr (IPTV Middleware)

Stores per-channel metadata including `tvc_guide_stationid` (Gracenote station ID), `tvg_id`, and `channel_number`. Also proxies IPTV streams, but that role is outside the scope of EPG architecture.

The plugin reads channel data from Dispatcharr's API at runtime to build its `_stationIdMap` (stream ID → Gracenote station ID).

### Xtream Tuner Plugin (Emby Plugin, C# DLL)

The plugin is the orchestrator. At runtime it:

1. Reads station IDs from Dispatcharr and builds `_stationIdMap`
2. Registers as a tuner with Emby, providing `ChannelInfo` with `ListingsChannelId` set to the Gracenote station ID where available
3. When Emby asks for EPG via `GetProgramsInternal`, decides per-channel whether to fetch Gracenote or Xtream EPG
4. Runs `DetachListingProviders()` to prevent Emby's auto-mapper from incorrectly mapping Gracenote to all channels (see [ADR-005](../decisions/005-detach-listing-providers.md))

### Emby Server

The media server that displays the TV Guide. Contains:

- **Listing providers** — Gracenote lineups (type `embygn`) registered by Channel Identifiarr
- **Premiere subscription** — Required paid subscription that authenticates with Gracenote's cloud servers. Without Premiere, `embygn` providers cannot fetch data.
- **`IListingsProvider`** — Internal Emby interface. The Gracenote implementation (`embygn`) exposes `GetProgramsAsync(providerInfo, stationId, startDate, endDate)`.

## Setup vs Runtime

### Setup (one-time)

```
Gracenote DB  →  Channel Identifiarr  →  (A) Dispatcharr: tvc_guide_stationid per channel
                                       →  (B) Emby: adds Gracenote lineup providers
```

### Runtime (every guide refresh)

```
Emby asks plugin: "what's on channel X?"
    ↓
Plugin checks _stationIdMap
    ↓
┌─ Has Gracenote station ID? ─────────────────────────────────────┐
│                                                                  │
│  YES: FetchGracenotePrograms(stationId)                         │
│       → calls Emby's IListingsProvider internally                │
│       → Premiere authenticates with Gracenote Cloud              │
│       ← returns curated EPG: episodes, seasons, ratings,        │
│         official artwork, full descriptions                      │
│                                                                  │
│  NO:  Fetches from Xtream Codes API                              │
│       → calls IPTV provider's external API                       │
│       ← returns provider-supplied EPG: titles, times,            │
│         descriptions, artwork, genres (quality varies)            │
│                                                                  │
└──────────────────────────────────────────────────────────────────┘
    ↓
Plugin returns ProgramInfo to Emby Guide
```

## The Circular Gracenote Call

The most non-obvious part of the architecture: the plugin receives a request **from** Emby, then calls **back into** Emby's internal `IListingsProvider` to get Gracenote data, then returns that data **to** Emby.

```
Emby → Plugin (GetProgramsInternal)
         → Emby internal (IListingsProvider.GetProgramsAsync)
           → Premiere auth
             → Gracenote Cloud
             ← program data
           ← List<ProgramInfo>
         ← ProgramInfo (re-assigned to tuner's ChannelId)
       ← Emby Guide displays it
```

This works because the plugin resolves `IListingsProvider` via Emby's DI container and calls it as an internal API, not an HTTP request.

## Key Identifiers

| Identifier | Example | Where it lives | What it means |
|---|---|---|---|
| `station_id` (Gracenote) | `45507` | Gracenote DB, Dispatcharr (`tvc_guide_stationid`), Plugin (`_stationIdMap`) | A specific Gracenote station (e.g. ESPN2 HD) |
| `lineup_id` | `USA-CT06404-X` | Gracenote DB, Emby (listing provider `ListingsId`) | A Gracenote lineup/package (e.g. Comcast Hartford CT) |
| `stream_id` (Xtream) | `14035` | Xtream provider, Plugin (`_tunerChannelIdToStreamId`) | An IPTV stream in the Xtream Codes provider |
| `ManagementId` (Emby) | `abc_xtream-tuner_14035` | Emby internal | Emby's internal composite ID for a channel |
| `ListingsChannelId` | `45507` | `ChannelInfo` property | Links a channel to a Gracenote station for EPG |

## Prerequisites

For Gracenote EPG to work, all of the following must be in place:

1. Active **Emby Premiere** subscription
2. **Channel Identifiarr** has matched channels and added lineup providers to Emby
3. **Dispatcharr** channels have `tvc_guide_stationid` populated
4. Plugin config: **Use Emby Guide Data** enabled (`DeferEpgToGuideData = true`)
5. Plugin has run `DetachListingProviders()` to prevent auto-mapping
