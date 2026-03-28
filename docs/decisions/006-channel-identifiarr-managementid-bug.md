# ADR-006: Channel Identifiarr ManagementId Bug and Fix

**Date**: 2026-03-25
**Status**: FIX PUSHED (PR #7 updated, awaiting merge)
**Affects**: Channel Identifiarr `backend/app.py` (`scan_emby_missing_listings`)
**Issue**: [Pharaoh-Labs/channelidentifiarr#6](https://github.com/Pharaoh-Labs/channelidentifiarr/issues/6)
**PR**: [Pharaoh-Labs/channelidentifiarr#7](https://github.com/Pharaoh-Labs/channelidentifiarr/pull/7)

---

## Context

Channel Identifiarr's "Scan Missing Listings" feature scans Emby channels that are missing guide data (`ListingsId` is empty) and adds the Gracenote lineup providers needed to cover those channels. To determine which Gracenote station each channel corresponds to, it parses Emby's `ManagementId`:

```python
mgmt_id = ch.get('ManagementId', '')
if mgmt_id and '_' in mgmt_id:
    station_id = mgmt_id.split('_')[-1]
    if station_id.isdigit() and len(station_id) >= 4:
        station_ids.add(station_id)
```

## Problem

The `ManagementId` format varies by tuner type:

| Tuner | ManagementId example | Last segment | Is Gracenote ID? |
|---|---|---|---|
| M3U | `abc123_m3u_51529` | `51529` | Yes (from `tvg-id`) |
| Xtream | `abc123_xtream-tuner_14035` | `14035` | No (internal stream ID) |

For the Xtream Tuner Plugin (and any custom tuner), the last segment is an internal identifier, not a Gracenote station ID. Since both stream IDs and Gracenote station IDs are large integers, collisions occur — the set-cover algorithm finds dozens of irrelevant regional lineups to "cover" phantom stations.

**Observed**: 44 Gracenote lineup providers added for a setup with only 12 channels that actually have Gracenote IDs.

Combined with [ADR-005](005-detach-listing-providers.md) (`DetachListingProviders`), this problem is amplified: since the Xtream tuner is detached from all listing providers, **every** Xtream channel shows as missing `ListingsId`, so every run processes the full channel list.

## Decision

Filed issue [#6](https://github.com/Pharaoh-Labs/channelidentifiarr/issues/6) and PR [#7](https://github.com/Pharaoh-Labs/channelidentifiarr/pull/7) on the Channel Identifiarr repository. The fix adds a new helper `_load_dispatcharr_station_ids()` that:

1. Loads Dispatcharr settings from the settings manager
2. Fetches all Dispatcharr channels (with pagination)
3. Builds a lookup by channel name and number to `tvc_guide_stationid`

For each Emby channel missing `ListingsId`, the code first checks the Dispatcharr lookup. If a match is found and it has a `tvc_guide_stationid`, that value is used as the station ID. The ManagementId parsing is kept as a fallback for setups without Dispatcharr.

## Key Learning from Testing

Moonshine tested the initial PR and found that the **per-channel fallback was still broken**: when a channel didn't match by name/number in the Dispatcharr lookup (due to slight naming differences), it fell through to the ManagementId parsing — triggering the exact same bug.

**The fix**: when Dispatcharr is configured and returns data, the ManagementId fallback must be **completely disabled** — not evaluated on a per-channel basis. The two approaches are mutually exclusive:

```python
dispatcharr_station_ids = _load_dispatcharr_station_ids()
dispatcharr_available = len(dispatcharr_station_ids) > 0

for ch in missing_channels:
    if dispatcharr_available:
        # Dispatcharr is the source of truth — no ManagementId fallback
        ch_name = (ch.get('Name') or '').strip().lower()
        ch_number = str(ch.get('ChannelNumber') or '').strip()
        matched = dispatcharr_station_ids.get(('name', ch_name)) \
            or dispatcharr_station_ids.get(('number', ch_number))
        if matched:
            station_ids.add(matched)
    else:
        # No Dispatcharr — fall back to ManagementId (M3U tuners only)
        mgmt_id = ch.get('ManagementId', '')
        if mgmt_id and '_' in mgmt_id:
            station_id = mgmt_id.split('_')[-1]
            if station_id.isdigit() and len(station_id) >= 4:
                station_ids.add(station_id)
```

Moonshine's alternative workaround (hardcoding `'xtream-tuner' not in mgmt_id`) works but is tuner-specific. The mutual-exclusion approach is generic.

## Consequences

- Setups with Dispatcharr: only channels with `tvc_guide_stationid` get station IDs. Stream ID collisions are eliminated.
- Setups without Dispatcharr: unchanged behaviour (ManagementId parsing, works for M3U tuners).
- Name/number matching between Emby and Dispatcharr may miss channels with slightly different names. Those channels simply don't get a Gracenote lineup — they fall back to Xtream EPG via the plugin. This is acceptable.
- PR #7 has been updated with the mutual-exclusion fix (commit `84c8183`). Awaiting merge by upstream maintainer.

## Rejected Alternative

[PR #21 on emby-xtream](https://github.com/firestaerter3/emby-xtream/pull/21) proposed absorbing Channel Identifiarr's entire Gracenote matching engine (SQLite reader, fuzzy matching, ~600 lines of C#) into the plugin itself. This was rejected because:

- It added a `Microsoft.Data.Sqlite` native dependency, significantly increasing DLL size
- It duplicated Channel Identifiarr's matching algorithm (imperfectly — used LCS instead of Python's `SequenceMatcher`)
- It introduced a hard regression: Dispatcharr-sourced station IDs were discarded, breaking existing setups
- The root cause belongs in Channel Identifiarr, not the plugin

## Related

- [Gracenote EPG Architecture](../architecture/gracenote-epg-chain.md)
- [Channel Identifiarr Reference](../architecture/channel-identifiarr.md)
- [ADR-005: Detach Listing Providers](005-detach-listing-providers.md)
