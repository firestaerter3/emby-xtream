# ADR-025: One Episode Version per Dispatcharr Provider

**Date**: 2026-11-10
**Status**: Accepted
**Affects**: `StrmSyncService.SyncSeriesCoreAsync()`, `EpisodeVersions`,
`DispatcharrClient.GetSeriesEpisodeProvidersAsync()`, `PluginConfiguration.DispatcharrEpisodeVersions`

---

## Context

Issue #111. Movies already get one `.strm` per provider when Dispatcharr has a movie from more
than one provider, so Emby offers them as versions. Series do not: the series sync reads the
episode list from Dispatcharr's Xtream output, which lists each episode once. Behind that single
URL Dispatcharr picks the provider with the highest account priority. Users want to choose, for
example a regional dub, or a version without Dolby Vision for a 4K player that cannot play it.

## Problem

The Xtream output has no way to name another provider. The plugin needs the providers per
episode and a URL that plays a chosen one, without asking Dispatcharr (or worse, the provider)
something for every episode.

## Alternatives Considered

### 1. One request per episode

**Rejected**: a large library has tens of thousands of episodes. The user who asked was already
worried about rate limits after another tool caused them.

### 2. Always on when Dispatcharr is enabled, like movies

**Rejected**: it adds files to libraries that already exist, and Emby's grouping of episode
versions is less dependable than its grouping of movie versions (it needs the same series and a
known season). Off by default, with a setting on the Series tab.

### 3. Label every version, including the first

**Rejected**: renaming the file every episode already has means a delete and a new file for each
one. With many episodes that easily passes the orphan safety limit, cleanup then refuses, and the
old file stays behind as a third version.

## Decision

- Setting *Series → One version per Dispatcharr provider* (`DispatcharrEpisodeVersions`), off by
  default.
- Per written series, two requests to Dispatcharr's own API, both answered from its database:
  `/api/vod/episodes/{id}/` with any episode id from the Xtream output (which is Dispatcharr's own
  episode id) to find the series, then `/api/vod/series/{id}/episodes/`, which returns every
  episode with its UUID and providers.
- An episode with two or more providers gets one file per provider, at most 8 (Emby's limit),
  each playing `/proxy/vod/episode/{uuid}?stream_id={provider stream}`. Order follows Dispatcharr's
  own choice for the plain URL: account priority, then relation id. The first keeps the episode's
  usual file name, so it plays what it played before; the others add ` - {account name}`.
- An episode with one provider keeps its single Xtream URL, unchanged.
- If the lookup fails, episodes get their single file and the version files already on disk are
  counted as in use, so a Dispatcharr hiccup does not make cleanup delete them (ADR-018).
- Changing the setting forces a full series pass, like the folder naming settings do. Otherwise
  series the provider has not touched are skipped before their episodes are read.

## Consequences

- Version files hold a Dispatcharr URL, which already counts as the plugin's own (ADR-014), so
  cleanup and exclusions treat them like any other episode file.
- A provider added in Dispatcharr later shows up when the series is written again: when the
  provider changes the series, or on the full pass after toggling the setting.
- Turning the setting off removes the extra files through orphan cleanup, which still applies the
  safety limit. A library where many episodes had versions may need that limit raised once.
- Not tested against a real multi-provider series by the maintainer, whose Dispatcharr has one
  provider per series. Released as a beta for the reporter to test.
