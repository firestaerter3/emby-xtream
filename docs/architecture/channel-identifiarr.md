# Channel Identifiarr

A web-based TV channel lineup search and Dispatcharr/Emby integration tool. It bridges a local Gracenote SQLite database with Dispatcharr and Emby, automating the process of matching IPTV channels to Gracenote station IDs.

- **GitHub**: [egyptiangio/channelidentifiarr](https://github.com/egyptiangio/channelidentifiarr)
- **Version**: v0.6.5 (as of March 2026)
- **Runs as**: Docker container on port 9192

## What It Does

### Station Matching

Users search the local Gracenote database by channel name, call sign, or lineup. Channel Identifiarr uses fuzzy matching (name similarity, call sign, resolution detection, logo presence) to suggest Gracenote stations for each channel. Users can apply a match to an existing Dispatcharr channel or create a new one.

### Two Integration Points

**Role A — Dispatcharr enrichment**: When a user applies a Gracenote match to a Dispatcharr channel, Channel Identifiarr writes the station ID to Dispatcharr's `tvc_guide_stationid` field via `PATCH /api/channels/channels/{id}/`. It can also update `tvg_id`, call sign, channel name, and upload logos.

**Role B — Emby listing providers**: The "Scan Missing Listings" feature adds Gracenote lineups directly to Emby so that Emby has the guide data source available. Without this step, Emby has no Gracenote data to query even if station IDs are correctly set.

## Emby Integration Endpoints

| Endpoint | Method | Purpose |
|---|---|---|
| `/api/emby/test` | POST | Test Emby server connection |
| `/api/emby/channels` | POST | Get all Emby Live TV channels |
| `/api/emby/scan-missing-listings` | POST | Add Gracenote lineups for channels missing guide data |
| `/api/emby/delete-logos` | POST | Bulk-delete channel logos from Emby |
| `/api/emby/clear-channel-numbers` | POST | Clear channel numbers from Emby |

## How "Scan Missing Listings" Works

This is the most relevant feature for the Xtream Tuner Plugin. The algorithm:

1. **Fetch Emby channels** — `GET /emby/LiveTv/Manage/Channels?Fields=ManagementId,ListingsId,Name,ChannelNumber,Id`
2. **Filter** — keep only channels where `ListingsId` is empty/null
3. **Extract station IDs** — for each missing channel, determine its Gracenote station ID
4. **Query Gracenote DB** — for each station ID, find all lineups that contain it
5. **Greedy set-cover** — select the minimum set of lineups that cover all stations, prioritizing by user's country/ZIP and lineup type (OTA > Cable > Satellite > VMVPD)
6. **Add providers** — `POST /emby/LiveTv/ListingProviders` for each selected lineup with type `embygn`

### The ManagementId Problem

Step 3 originally extracted station IDs by parsing Emby's `ManagementId`:

```python
mgmt_id = ch.get('ManagementId', '')
if mgmt_id and '_' in mgmt_id:
    station_id = mgmt_id.split('_')[-1]
    if station_id.isdigit() and len(station_id) >= 4:
        station_ids.add(station_id)
```

This works for **M3U tuner** channels where the ManagementId format includes `tvg-id` (which IS the Gracenote station ID):

| Tuner | ManagementId | Last segment | Is Gracenote ID? |
|---|---|---|---|
| M3U | `abc123_m3u_51529` | `51529` | Yes |
| Xtream | `abc123_xtream-tuner_14035` | `14035` | No (stream ID) |

For custom tuners like Xtream, the last segment is an internal stream ID. Since both stream IDs and Gracenote station IDs are large integers, collisions occur — the set-cover algorithm then selects dozens of irrelevant regional lineups.

See [ADR-006](../decisions/006-channel-identifiarr-managementid-bug.md) for the fix.

### Interaction with DetachListingProviders

The Xtream Tuner Plugin's `DetachListingProviders()` removes all listing provider associations from the Xtream tuner. This means **every** Xtream channel will always have `ListingsId` empty in Emby. Consequently, "Scan Missing Listings" processes all channels on every run, not just new ones.

This is a known limitation. When adding new channels with Gracenote IDs, the practical workflow is: clear existing Emby EPG sources, then re-run "Scan Missing Listings" to re-add the correct minimal set.

## Other Features

- **Clone Lineup** — import entire real-world lineups (DirecTV, Dish, cable) from the Gracenote database into Dispatcharr with pre-populated metadata
- **Logo Management** — upload logos from Gracenote to Dispatcharr, delete cached logos from Emby
- **Stream Assignment** — search and assign streams to channels with drag-and-drop ordering and confidence scoring
- **Database Updates** — check for and download updated Gracenote databases from remote sources

## Settings

Channel Identifiarr stores settings in a JSON file (server-side). Relevant settings:

- `dispatcharr.url`, `dispatcharr.username`, `dispatcharr.password` — Dispatcharr API credentials
- `emby.url`, `emby.username`, `emby.password` — Emby API credentials
- Database path (defaults to `/data/channelidentifiarr.db`)
