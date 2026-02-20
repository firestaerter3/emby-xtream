# ADR-001: Bypass Dispatcharr Proxy — Use Xtream Emulation URL

**Date**: 2026-02-20
**Status**: REVERTED — see Outcome section
**Affects**: `XtreamTunerHost.BuildStreamUrl()`

---

## ⚠️ THIS CHANGE WAS FULLY REVERTED

**The code was reverted to use the original proxy URL (`/proxy/ts/stream/{uuid}`).**
**The premise of this ADR was wrong. Do not re-implement this change.**

See the Outcome section at the bottom for the full explanation.

---

## Original Context

Dispatcharr offers two streaming endpoints:

1. **Proxy** (`/proxy/ts/stream/{uuid}`) — buffered proxy with Redis-backed client tracking
2. **Xtream emulation** (`/live/{user}/{pass}/{id}.ts`) — direct passthrough using Xtream API conventions

Both route through Dispatcharr's load balancer. The plugin originally used the proxy endpoint.

## Original Problem Statement (subsequently disproven)

At the time, the proxy endpoint appeared fundamentally unreliable:

- **HTTP 503**: Proxy returned `Server returned 5XX Server Error reply` — ffmpeg failed immediately
- **Orphaned Redis state**: Interrupted connections left Redis keys blocking ALL subsequent proxy connections
- **CLOSE-WAIT cascades**: Each failed attempt worsened the stuck state
- **0-byte race condition**: Even when it returned 200, the buffer appeared empty

These were diagnosed as architectural issues — stemming from the proxy's client-tracking model.

## What We Tried

### Attempt 1: Replace proxy URL with Xtream emulation URL

Changed `BuildStreamUrl()` to use `/live/{user}/{pass}/{id}.ts` instead of `/proxy/ts/stream/{uuid}`.

**Result**: Failed. The Xtream emulation endpoint routes through the **same** proxy infrastructure
(`ts_proxy.views` → `stream_manager` → Redis buffer). It is not a separate code path. The
assumption that it bypassed the proxy was incorrect.

### Attempt 2: Modify Dispatcharr source — fix `chunk_available` event

Identified two bugs in Dispatcharr's streaming pipeline:
- `stream_buffer.py`: `chunk_available.set()` immediately followed by `chunk_available.clear()` —
  dead notification, no consumer could catch it
- `stream_generator.py`: polling backoff capped at 1.0s, combined with ~1.5s cold-start and
  ffmpeg's 3s analyzeduration, total exceeds Emby's 10s kill timeout

Changed `stream_buffer.py` to remove the immediate `clear()` and changed `stream_generator.py`
to use `wait(timeout=0.05)` instead of `sleep()`.

**Result**: Failed. Introduced a new failure mode: "Detected channel stop signal, terminating
stream" at 0.01s. Dispatcharr sets a `ts_proxy:channel:{id}:stopping` key (30s TTL) after each
failed attempt. Emby's retry within 10s always hit this key → instant fail → key refreshed →
infinite loop. **Both Dispatcharr files were reverted via `git checkout`.**

### Attempt 3: `RequiresOpening=true` with fallback

The `Open()` call creates a short-lived HTTP connection to validate the stream. This was the
same connection pattern that triggered orphaned state in Dispatcharr. **Abandoned.**

## Outcome: Root Cause Was Installation State Corruption

After exhausting all code-level workarounds, a **full reinstall of Dispatcharr** (drop database,
flush Redis, fresh clone from `main`) was performed.

**After the reinstall, the proxy endpoint worked immediately and reliably.**

This proves the original symptoms (503 errors, orphaned channels, CLOSE-WAIT, 0-byte buffers)
were caused by **accumulated corruption in the old Dispatcharr installation** — stale Redis keys,
orphaned DB state, and lingering CLOSE-WAIT connections from months of interrupted connections —
**not by any architectural flaw in the proxy endpoint itself.**

The proxy endpoint is sound. Other users in the community had zero issues with it throughout
this entire debugging session, which was consistent with this conclusion.

## Final Decision

**Revert all changes. Use the original proxy URL.**

`BuildStreamUrl()` was restored to its original form using `/proxy/ts/stream/{uuid}`.
The Dispatcharr source files (`stream_buffer.py`, `stream_generator.py`) were restored to
upstream v0.19.0 via `git checkout`.

## Recovery Procedure (for future reference)

If the proxy endpoint stops working again (503, orphaned channels, CLOSE-WAIT), the fix is:

```bash
# On the Dispatcharr host:
# 1. Stop services
systemctl stop dispatcharr dispatcharr-celery dispatcharr-celerybeat dispatcharr-daphne

# 2. Drop and recreate the database
sudo -u postgres psql -c "DROP DATABASE dispatcharr_db;"
sudo -u postgres psql -c "CREATE DATABASE dispatcharr_db OWNER dispatcharr_usr;"

# 3. Flush Redis
redis-cli FLUSHALL

# 4. Re-run migrations
cd /opt/dispatcharr && python manage.py migrate

# 5. Restart
systemctl start dispatcharr dispatcharr-celery dispatcharr-celerybeat dispatcharr-daphne
```

A targeted Redis flush (`FLUSHALL` + restart) may be sufficient for minor stuck-state issues
without needing the full DB reset.
