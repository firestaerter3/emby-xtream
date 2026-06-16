# ADR-010: Tolerant Deserialization for Provider Responses

**Date**: 2026-06-13 (updated 2026-06-16)
**Status**: Accepted
**Affects**: `Client/Models/TolerantStringConverter`, `Client/Models/TolerantNullableIntConverter`, `Client/Models/SeriesInfo`, `Client/Models/StreamStatsInfo`, `StrmSyncService.JsonOptions`, `XtreamTunerApi` series-list options

---

## Context

Xtream provider responses are not a strict schema. The same field is typed differently across
providers, and even across endpoints on the same provider. A field documented and usually
returned as a string (e.g. `info.releasedate`, `rating`, `tmdb`) can arrive as a bare JSON
number, a boolean, `null`, or an empty array.

The plugin deserializes these responses with `System.Text.Json`. The model properties that
back these fields are typed `string`. The deserialization options already set
`NumberHandling = JsonNumberHandling.AllowReadingFromString`, which lets a quoted numeric
string deserialize into a numeric property — but that only covers the string → number
direction.

## Problem

`System.Text.Json` does not coerce a JSON number (or boolean/array) into a `string` property.
It throws:

```
The JSON value could not be converted to System.String. Path: $.info.releasedate
```

Because the series importer deserializes the whole `get_series_info` payload in one call
(`StrmSyncService.FetchSeriesDetailAsync`), a single off-type field aborts the entire parse.
The exception surfaces out of the sync, so **every** series download fails — not just the one
field, and not just the one series. Reported as GitHub #32 (reporter on Emby-Server for
Windows): every series failed with the error above.

## Alternatives Considered

### 1. Change the property type (e.g. `object`, or `JsonElement`)

Store the raw value and interpret it at the call site.

**Rejected.** Pushes the type-juggling into every consumer, and the rest of the codebase
expects a plain `string` (for filenames, NFO fields, TMDB lookups). It would spread the
problem rather than contain it.

### 2. Annotate only the known-fragile fields with a converter

Put `[JsonConverter(...)]` on `ReleaseDate` (and a few others) individually.

**Rejected as the primary mechanism.** It only protects fields we have already seen misbehave.
Providers vary every string field, so the next bug report would be `rating`, then `genre`,
then `cast`. Whack-a-mole.

### 3. A tolerant converter registered at the options level

Coerce any JSON token into a string for every `string` property in the provider models.

**Chosen.** One registration protects the whole model graph (series, seasons, episodes, VOD)
against the entire class of off-type-string bugs, current and future.

## Decision

Generalise the pre-existing `StringOrNumberAsStringConverter` (which handled Number/String/Null
and threw on anything else, used only for Dispatcharr `audio_channels`) into a reusable
`TolerantStringConverter`:

- Number → invariant-culture string
- `true`/`false` → `"true"`/`"false"`
- `null` → `null`
- object/array → skipped (`reader.Skip()`) and treated as `null`, so a structured value where a
  scalar was expected can never throw

Register it on the deserialization options that parse provider content:

- `StrmSyncService.JsonOptions` — series detail, series lists, and VOD/movie lists
- the local series-list options in `XtreamTunerApi` — the config-UI "load series" path

`StreamStatsInfo.AudioChannels` now references the shared converter, removing the duplicate
inline class.

## Consequences

- **A malformed string field can no longer break a sync.** The worst case is a `null` where a
  value was hoped for, which the downstream code already tolerates.
- **The pattern is now centralised.** New provider models inherit the protection by being
  deserialized with the same options; no per-field annotation needed.
- **Do not "simplify" string fields back to the default converter.** The converter is load-bearing
  for provider interop, not boilerplate.
- **Live-channel and EPG parse paths were intentionally left unchanged** (`XtreamTunerHost.JsonOptions`,
  `LiveTvService.JsonOptions`). They have not exhibited this failure. If the same quirk surfaces
  there, register the same converter on those options.
- Covered by `TolerantStringConverterTests` (releasedate as number, decimal, null, array, object,
  boolean, string; multi-field numeric payload; full payload whose episodes still parse).

## Update (2026-06-16): the same quirk in integer fields

The reporter from GitHub #32 updated to the fix above and hit the mirror-image failure on a
different field:

```
The JSON value could not be converted to System.Nullable`1[System.Int32]. Path: $.info.category_id
```

`SeriesInfo.CategoryId` is typed `int?`. `NumberHandling = AllowReadingFromString` parses a
strictly-numeric quoted string, but the provider was sending something it could not parse — an
empty string, a non-numeric string, or a structured value. Same root cause (inconsistent
provider typing), same blast radius (one field aborts the whole `get_series_info` parse, so every
series fails), same chosen mechanism (a tolerant converter at the options level rather than
per-field annotation or whack-a-mole).

Added `TolerantNullableIntConverter`, the integer sibling of `TolerantStringConverter`:

- Number → `int` (decimal numbers truncate)
- Numeric string → parsed `int` (decimal strings truncate)
- Empty / non-numeric string → `null`
- `null` → `null`
- object/array → skipped (`reader.Skip()`) and treated as `null`

Registered on the same two option sets (`StrmSyncService.JsonOptions` and the `XtreamTunerApi`
series-list options). `null` is the correct degraded value: `category_id` is already optional and
is only used for folder grouping, so a missing value falls back to the uncategorised bucket. The
converter only covers nullable ints — non-nullable provider IDs (`series_id`, `stream_id`) are
deliberately left strict, because silently defaulting a missing primary ID to `0` would
manufacture broken library items rather than skip a cosmetic grouping.

Covered by `TolerantNullableIntConverterTests` (category_id as number, numeric string, empty
string, non-numeric string, null, array, object, decimal string; full payload whose episodes
still parse; serialize roundtrip preserves value and null).
