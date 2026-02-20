# Bug Logs

Received user logs are stored here, organized by bug slug. Directory structure is committed; log files are gitignored.

## Structure

```
logs/
├── README.md                              (this file — committed)
├── apple-tv-range-probe/                  (bug slug directories — committed)
│   ├── joe-v1.4.6-2026-02-20.log         (log files — gitignored)
│   └── ...
├── channel-switch-delay/
├── dispatcharr-api-repeat/
├── dispatcharr-probe-storm/
├── dispatcharr-uuid-mapping/
└── ...
```

## Naming convention

Log files: `<reporter>-v<version>-<date>.log`

Example: `scottrobertson-v1.4.0-2026-01-15.log`

## Known bug slugs

| Slug | Description | Versions affected |
|------|-------------|-------------------|
| `dispatcharr-probe-storm` | FFprobe teardown — Emby probed Dispatcharr URLs, causing channel teardown and reconnect storms | v1.4.0 |
| `dispatcharr-api-repeat` | Repeated Dispatcharr API calls per stream session (BUG-007) | v1.4.0–v1.4.4 |
| `dispatcharr-uuid-mapping` | UUID map keyed by wrong ID for URL-based stream sources | v1.4.2–v1.4.4 |
| `apple-tv-range-probe` | AppleCoreMedia sends `Range: bytes=0-1` probe causing Dispatcharr teardown | v1.4.6 |
| `channel-switch-delay` | `EnsureStatsLoadedAsync` first-time delay on channel switch | v1.4.6 |

## Adding a new bug slug

1. `mkdir logs/<slug>`
2. `touch logs/<slug>/.gitkeep`
3. Add a row to the table above
4. Commit the directory
5. Place received log files in the directory (they are auto-gitignored)
