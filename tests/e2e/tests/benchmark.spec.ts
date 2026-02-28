/**
 * Channel-switch benchmark across three Emby configurations.
 *
 * Run with BENCHMARK_MODE set to one of: baseline | with-stats | no-stats
 *
 *   BENCHMARK_MODE=baseline    npm run benchmark
 *   BENCHMARK_MODE=with-stats  npm run benchmark
 *   BENCHMARK_MODE=no-stats    npm run benchmark
 *
 * Measurements per channel:
 *   infoTime   — click channel tile → play dialog visible
 *   streamTime — click Play → video reaches readyState >= 2
 *
 * Structure: 1 cold pass, 5s pause, 3 warm passes.
 *   Between every two measurements: 5s gap for stream teardown (BETWEEN_STREAMS_MS).
 * No assertions — purely observational data for the report.
 *
 * Navigation: uses the Channels tab (non-virtualized grid) rather than the Guide
 * timeline, so channels like BBC ONE and CNN are always reachable via
 * scrollIntoViewIfNeeded regardless of their position in the list.
 */

import { test, type Page } from '@playwright/test';
import { login, saveBenchmarkResults, ChannelBenchmark, TimingPair, StreamSession, closeActiveStream } from './helpers';

const VALID_MODES = ['baseline', 'with-stats', 'no-stats'] as const;
type BenchmarkMode = typeof VALID_MODES[number];

const BENCHMARK_MODE = process.env.BENCHMARK_MODE as BenchmarkMode | undefined;
const BENCHMARK_CHANNELS = ['NPO 1', 'BBC ONE', 'CNN'];

const CHANNELS_GRID = '.card, [data-type="Channel"], .channelCard, .gridItem';

/**
 * Mandatory gap between stopping one stream and starting the next.
 * Gives Dispatcharr (and the upstream IPTV source) time to fully tear down the
 * previous connection before a new play request arrives.  Without this, the
 * next channel hits the source while the previous one is still closing, which
 * causes "No compatible streams" errors and inflated stream-start times.
 */
const BETWEEN_STREAMS_MS = 5_000;

test.setTimeout(600_000);

/**
 * Cached URL of the Channels tab page.
 * Set on the first successful navigation; reused on all subsequent calls so we
 * never have to click through the sidebar → tab sequence again (which fails when
 * the tab bar is scrolled out of view after video playback).
 */
let channelsUrl: string | null = null;

/**
 * Emby session tokens captured from the most recent PlaybackInfo response.
 * Updated by the response interceptor in the test body; consumed and reset by
 * measureChannel (and by returnToChannels as a safety net) via closeActiveStream.
 */
let lastSession: StreamSession = {
  playSessionId: null,
  openToken: null,
  mediaSourceId: null,
  itemId: null,
};

function emptySession(): StreamSession {
  return { playSessionId: null, openToken: null, mediaSourceId: null, itemId: null };
}

/** Navigate to the Live TV Channels tab (non-virtualized grid). */
async function goToChannels(page: Page): Promise<void> {
  if (channelsUrl) {
    // Direct navigation — works regardless of scroll position or current page.
    await page.goto(channelsUrl);
    // Wait for the SPA router to finish routing to the channels tab.
    // Without this, waitForSelector can fire on home-page .card elements if the
    // SPA briefly renders the home page before the channels tab content loads.
    await page.waitForURL(url => url.href.includes('tab=channels'), { timeout: 15_000 });
    await page.waitForSelector(CHANNELS_GRID, { state: 'attached', timeout: 30_000 });
    // If Emby's SPA hash-navigation left a channel info dialog open, close it.
    const playBtn = page.getByRole('button', { name: 'Play', exact: true });
    if (await playBtn.count() > 0) {
      await page.keyboard.press('Escape');
      await page.waitForTimeout(500);
    }
    return;
  }
  // First call: navigate via the nav drawer sidebar then click the Channels tab.
  // Using the nav drawer button avoids ambiguity with the "Live TV" tile card that
  // also appears on the home page.
  await page.getByRole('button', { name: ' Live TV' }).first().click();
  await page.getByRole('button', { name: 'Channels', exact: true }).click({ timeout: 10_000 });
  await page.waitForSelector(CHANNELS_GRID, { state: 'attached', timeout: 30_000 });
  // Cache the URL so subsequent calls bypass the sidebar entirely.
  channelsUrl = page.url();
}

/**
 * Close any open overlay (video player or channel-info dialog).
 *
 * Emby's video player is a persistent SPA overlay — it does NOT close
 * automatically when the hash/route changes. We must explicitly dismiss it
 * before navigating, otherwise it stays on top of the Channels grid and
 * findChannelButton searches inside the player instead of the grid.
 *
 * Strategy: press Escape once, then wait for the <video> element to disappear
 * from the DOM (Emby removes it when the player fully closes). Repeat up to
 * maxAttempts times if needed. Also handles the channel-info dialog.
 *
 * NOTE: we deliberately do NOT check for the player transport chrome
 * ("Play on another device" button) here. Pressing Escape when the chrome is
 * showing (but the <video> element is already gone) triggers Emby's SPA
 * back-navigation to the home page. The residual chrome is instead handled
 * after goToChannels() via page.reload() in returnToChannels().
 */
async function dismissOverlays(page: Page): Promise<void> {
  for (let attempt = 0; attempt < 6; attempt++) {
    const hasVideo = (await page.locator('video').count()) > 0;
    const hasDialog = (await page.getByRole('button', { name: 'Play', exact: true }).count()) > 0;
    if (!hasVideo && !hasDialog) return;
    await page.keyboard.press('Escape');
    // Give Emby time to animate the overlay closed and remove the video element.
    await page.waitForTimeout(700);
  }
}

/** Stop any active video and return to the Channels grid. */
async function returnToChannels(page: Page): Promise<void> {
  // Safety net: close any server-side stream that measureChannel may have missed.
  await closeActiveStream(page, lastSession);
  lastSession = emptySession();

  await dismissOverlays(page);
  await goToChannels(page);

  // After SPA hash-navigation the player transport chrome (seek bar, "Play on
  // another device" button) can survive as a persistent DOM overlay. Pressing
  // Escape to dismiss it triggers a SPA back-navigation to the home page, so we
  // use page.reload() instead, which forces a full SPA reinitialisation at the
  // channels URL and removes all overlay state without touching the history stack.
  if (await page.getByRole('button', { name: 'Play on another device' }).count() > 0) {
    await page.reload();
    await page.waitForSelector(CHANNELS_GRID, { state: 'attached', timeout: 30_000 });
    // Wait for Emby to fully initialise — it can auto-resume the last stream from
    // stored state, which would re-show the player chrome. We need to detect this
    // before proceeding, otherwise mouse.wheel below would land on the chrome overlay.
    await page.waitForTimeout(1_500);
    if (await page.getByRole('button', { name: 'Play on another device' }).count() > 0) {
      // Emby auto-resumed from stored state — clear storage and reload a second time.
      await page.evaluate(() => {
        sessionStorage.clear();
        const keysToRemove = Object.keys(localStorage).filter(k =>
          k.includes('player') || k.includes('Player') || k.includes('playback') || k.includes('Playback'),
        );
        keysToRemove.forEach(k => localStorage.removeItem(k));
      });
      await page.reload();
      await page.waitForSelector(CHANNELS_GRID, { state: 'attached', timeout: 30_000 });
    }
  }

  // Scroll the channels virtual-scroll container back to position 0.
  // We use mouse.wheel here (not window.scrollTo) because the channels list is
  // a separate scrollable container — window.scrollTo only moves the page frame.
  // At this point the player chrome has been cleared (above), so the wheel event
  // safely targets the channels grid rather than a player overlay.
  await page.mouse.move(640, 400);
  await page.mouse.wheel(0, -99999);
  await page.waitForTimeout(300);

  // Wait for the previous stream to fully tear down before the next measurement.
  // This prevents back-to-back connections from colliding in Dispatcharr / the
  // upstream IPTV source, which would cause playback errors or skewed timings.
  console.log(`  [gap] waiting ${BETWEEN_STREAMS_MS / 1000}s for stream teardown…`);
  await page.waitForTimeout(BETWEEN_STREAMS_MS);
}

/**
 * Scroll the channels grid until the button for `channel` appears in the DOM.
 * The Channels tab uses virtual scrolling, so channels far down the list are
 * not rendered until the user scrolls past them.
 *
 * Channel buttons have accessible names like "109 BBC ONE" or "101 NPO 1".
 * We match the name ending with the channel string (word-boundary anchor) to
 * avoid false substring matches — e.g. "NPO 1" must NOT match "NPO 1 EXTRA 71".
 */
async function findChannelButton(page: Page, channel: string) {
  const escaped = channel.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const btn = () => page.getByRole('button', { name: new RegExp(`\\b${escaped}$`) }).first();

  // Position mouse in content area then wheel-scroll down.
  // Using mouse.wheel avoids having to find the exact scroll container element.
  await page.mouse.move(640, 400);
  for (let i = 0; i < 60; i++) {
    if (await btn().count() > 0) return btn();
    await page.mouse.wheel(0, 800);
    await page.waitForTimeout(150);
  }
  throw new Error(`Channel button for "${channel}" not found after scrolling`);
}

async function measureChannel(page: Page, channel: string): Promise<TimingPair> {
  // ── Info screen time ──────────────────────────────────────────────────────
  const t0 = performance.now();

  // Scroll the virtual-scroll grid until the channel button is in the DOM, then click.
  const channelBtn = await findChannelButton(page, channel);
  await channelBtn.scrollIntoViewIfNeeded({ timeout: 10_000 });
  await channelBtn.click({ timeout: 10_000 });

  // The channel info dialog contains a "Play" button. Use role+name to avoid
  // false matches against other buttons earlier in DOM order.
  const playBtn = page.getByRole('button', { name: 'Play', exact: true });
  await playBtn.waitFor({ state: 'visible', timeout: 60_000 });

  const infoTime = (performance.now() - t0) / 1000;

  // ── Stream start time ─────────────────────────────────────────────────────
  const t1 = performance.now();

  await playBtn.click();

  // Wait for the video to reach HAVE_CURRENT_DATA (readyState ≥ 2).
  //
  // Note on waitForFunction call signature:
  //   page.waitForFunction(fn, arg, options) — arg must be passed explicitly so
  //   that { timeout, polling } lands in the options slot, not the arg slot.
  //   Passing undefined as arg is safe because the fn ignores its argument.
  //
  // If the stream fails (Playback Error dialog), we catch the timeout, dismiss
  // the dialog, and record streamTime = -1 so the benchmark run continues.
  let streamTime: number;
  try {
    await page.waitForSelector('video', { state: 'attached', timeout: 15_000 });
    await page.waitForFunction(
      () => {
        const v = document.querySelector('video');
        return v !== null && (v as HTMLVideoElement).readyState >= 2; // HAVE_CURRENT_DATA
      },
      undefined, // no arg passed to the pageFunction
      { timeout: 30_000, polling: 200 },
    );
    streamTime = (performance.now() - t1) / 1000;
  } catch {
    // Stream failed — dismiss any error dialog so the benchmark can continue.
    const gotIt = page.getByRole('button', { name: 'Got It' });
    if (await gotIt.count() > 0) await gotIt.click();
    streamTime = -1;
    console.log(`  [WARN] stream failed for ${channel} — recorded streamTime = -1`);
  }

  // Close the server-side live stream regardless of success or failure.
  // This prevents Dispatcharr connection slots from being consumed by phantom clients.
  await closeActiveStream(page, lastSession);
  lastSession = emptySession();

  return { infoTime, streamTime };
}

test.describe('channel switch benchmark', () => {
  test.skip(
    !BENCHMARK_MODE || !VALID_MODES.includes(BENCHMARK_MODE),
    `Set BENCHMARK_MODE to one of: ${VALID_MODES.join(', ')}`,
  );

  test(`benchmark [${BENCHMARK_MODE ?? 'unset'}]`, async ({ page }) => {
    const mode = BENCHMARK_MODE!;

    // Capture session tokens from PlaybackInfo responses so closeActiveStream()
    // can tear down each stream properly via the Emby API (not just the UI).
    page.on('response', async (response) => {
      if (response.url().includes('/PlaybackInfo') && response.ok()) {
        try {
          const json = await response.json();
          const urlMatch = response.url().match(/\/Items\/([^/?]+)\/PlaybackInfo/);
          lastSession = {
            playSessionId: json.PlaySessionId ?? null,
            openToken: json.MediaSources?.[0]?.OpenToken ?? null,
            mediaSourceId: json.MediaSources?.[0]?.Id ?? null,
            itemId: urlMatch?.[1] ?? null,
          };
        } catch { /* ignore parse errors */ }
      }
    });

    await login(page);
    await goToChannels(page);

    const results: Record<string, ChannelBenchmark> = {};
    for (const ch of BENCHMARK_CHANNELS) {
      results[ch] = { cold: { infoTime: 0, streamTime: 0 }, warm: [] };
    }

    // ── Cold pass ─────────────────────────────────────────────────────────────
    console.log('\n=== Cold pass ===');
    for (let i = 0; i < BENCHMARK_CHANNELS.length; i++) {
      const ch = BENCHMARK_CHANNELS[i];
      const timing = await measureChannel(page, ch);
      results[ch].cold = timing;
      console.log(
        `[cold][${ch}] info=${timing.infoTime.toFixed(2)}s  stream=${timing.streamTime.toFixed(2)}s` +
        `  total=${(timing.infoTime + timing.streamTime).toFixed(2)}s`,
      );
      if (i < BENCHMARK_CHANNELS.length - 1) {
        await returnToChannels(page);
      }
    }

    // ── 5s pause between cold and warm ───────────────────────────────────────
    console.log('\nWaiting 5s before warm passes…');
    await page.waitForTimeout(5_000);

    // ── Warm passes (3×) ─────────────────────────────────────────────────────
    for (let pass = 1; pass <= 3; pass++) {
      console.log(`\n=== Warm pass ${pass}/3 ===`);

      await returnToChannels(page);

      for (let i = 0; i < BENCHMARK_CHANNELS.length; i++) {
        const ch = BENCHMARK_CHANNELS[i];
        const timing = await measureChannel(page, ch);
        results[ch].warm.push(timing);
        console.log(
          `[warm ${pass}][${ch}] info=${timing.infoTime.toFixed(2)}s  stream=${timing.streamTime.toFixed(2)}s` +
          `  total=${(timing.infoTime + timing.streamTime).toFixed(2)}s`,
        );
        if (i < BENCHMARK_CHANNELS.length - 1) {
          await returnToChannels(page);
        }
      }
    }

    saveBenchmarkResults(mode, { channels: results });
  });
});
