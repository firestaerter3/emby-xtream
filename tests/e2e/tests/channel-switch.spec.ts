/**
 * Channel switch performance test.
 *
 * Measures three timings per channel:
 *
 *   infoTime    — click channel in guide → play/record dialog appears.
 *                 Catches: slow GetChannelStreamMediaSources, EnsureStatsLoadedAsync delay,
 *                 or repeated Dispatcharr API calls (BUG-007).
 *
 *   streamTime  — click play → video element reaches HAVE_CURRENT_DATA (readyState >= 2).
 *                 Catches: probe storm / teardown issues (FFprobe, Range: bytes=0-1).
 *
 * Thresholds (seconds):
 *   infoTime   < 3s   (dialog should appear quickly; > 3s suggests extra API round-trips)
 *   streamTime < 5s   (video data should arrive; > 5s suggests teardown / probe issue)
 *
 * Dispatcharr call count:
 *   After the first channel is fully playing, subsequent channel switches should
 *   not make more than 2 Dispatcharr API calls. More than that indicates BUG-007
 *   (repeated /api/channels/ fetches per session).
 *
 * Configuration:
 *   Set EMBY_CHANNELS in .env as a comma-separated list of channel names to test.
 *   Example: EMBY_CHANNELS=NPO 1,RTL 4,SBS 6
 *   If not set, the test skips with a warning.
 */

import { test, expect, type Page } from '@playwright/test';
import { login, saveResults } from './helpers';

const CHANNELS_ENV = process.env.EMBY_CHANNELS;
const channels = CHANNELS_ENV
  ? CHANNELS_ENV.split(',').map(c => c.trim()).filter(Boolean)
  : [];

const INFO_TIME_THRESHOLD_S = 3;
const STREAM_TIME_THRESHOLD_S = 5;

const GUIDE_CELL = '.channelCell:not(.settingsChannelCell), .guideChannelText';

/**
 * Dismiss any active video player overlay and channel-info dialog.
 *
 * Presses Escape up to 6 times (same pattern as benchmark.spec.ts dismissOverlays).
 * Emby removes the <video> element from the DOM when the player fully closes.
 */
async function dismissOverlays(page: Page): Promise<void> {
  for (let attempt = 0; attempt < 6; attempt++) {
    const hasVideo  = (await page.locator('video').count()) > 0;
    const hasDialog = (await page.getByRole('button', { name: 'Play', exact: true }).count()) > 0;
    if (!hasVideo && !hasDialog) return;
    await page.keyboard.press('Escape');
    await page.waitForTimeout(700);
  }
}

/**
 * Return to the guide after playing a channel.
 *
 * Mirrors the benchmark.spec.ts returnToChannels pattern:
 *   1. Escape to close the video player (removes <video> from DOM).
 *   2. Hard-navigate to the guide URL (same origin, preserves auth token).
 *   3. If Emby auto-resumes from stored state, clear player-related storage and
 *      reload a second time to start clean.
 *   4. Wait for guide channel cells to be visible.
 */
async function returnToGuide(page: Page, guideUrl: string): Promise<void> {
  await dismissOverlays(page);
  await page.goto(guideUrl, { waitUntil: 'domcontentloaded', timeout: 20_000 });

  // After a hard navigation Emby can auto-resume the last stream from stored state,
  // which re-shows the player overlay and hides the guide grid. Wait briefly, then
  // check for the "Play on another device" button that signals an active player chrome.
  await page.waitForTimeout(1_500);
  if ((await page.getByRole('button', { name: 'Play on another device' }).count()) > 0) {
    // Clear stored player/playback session keys (same keys as benchmark.spec.ts).
    await page.evaluate(() => {
      sessionStorage.clear();
      Object.keys(localStorage)
        .filter(k => k.includes('player') || k.includes('Player') ||
                     k.includes('playback') || k.includes('Playback'))
        .forEach(k => localStorage.removeItem(k));
    });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(500);
  }

  await page.evaluate(() => { localStorage.removeItem('guide-tagids'); });
  await page.waitForSelector(GUIDE_CELL, { state: 'visible', timeout: 15_000 });
}

test.describe('channel switch performance', () => {
  test.skip(!channels.length, 'Set EMBY_CHANNELS in .env to run channel switch tests');

  test('info screen and stream start within thresholds', async ({ page }) => {
    test.setTimeout(120_000);
    await login(page);

    // Clear any stale guide filter that would make the grid appear empty.
    await page.evaluate(() => { localStorage.removeItem('guide-tagids'); });

    // Navigate to the Guide tab.
    await page.click('a:has-text("Guide"), button:has-text("Guide")');
    await page.waitForSelector(GUIDE_CELL, { state: 'visible', timeout: 10_000 });

    // Capture the guide URL so returnToGuide can hard-navigate back to it.
    const guideUrl = page.url();

    // Track Dispatcharr API call counts.
    let totalDispatcharrCalls = 0;
    const callsPerPhase: number[] = [];
    page.on('request', req => {
      if (req.url().includes('/api/channels/')) totalDispatcharrCalls++;
    });

    const channelResults: {
      name: string;
      infoTime: number;
      streamTime: number;
      dispatcharrCalls: number;
    }[] = [];

    for (let i = 0; i < channels.length; i++) {
      const channel = channels[i];
      const callsBefore = totalDispatcharrCalls;

      // ── Info screen time ───────────────────────────────────────────────────

      // The guide uses virtual scrolling — off-screen channels are not rendered.
      // Scroll the channel column until the target button appears in the DOM.
      const channelBtn = page.getByRole('button', { name: channel }).first();
      // Locate the first guide channel cell for mouse-wheel scroll positioning.
      const firstCell = page.locator('.channelCell:not(.settingsChannelCell)').first();
      const cellBox = await firstCell.boundingBox().catch(() => null);
      const scrollX = cellBox ? cellBox.x + cellBox.width / 2 : 300;
      const scrollY = cellBox ? cellBox.y + cellBox.height / 2 : 450;
      await page.mouse.move(scrollX, scrollY);
      // Scroll to the top of the channel column first so the search always starts
      // from position 0, regardless of where a previous iteration left the list.
      await page.mouse.wheel(0, -99999);
      await page.waitForTimeout(300);
      // Scroll downward until the target channel button becomes visible.
      for (let s = 0; s < 40; s++) {
        if (await channelBtn.isVisible({ timeout: 100 }).catch(() => false)) break;
        await page.mouse.wheel(0, 300);
        await page.waitForTimeout(150);
      }

      // Start timer only after channel is visible (scroll time excluded from infoTime).
      const t0 = performance.now();
      await channelBtn.click({ timeout: 10_000 });

      // Wait for the play/record dialog (exact name match avoids "Play on another device").
      const playBtn = page.getByRole('button', { name: 'Play', exact: true });
      await playBtn.waitFor({ state: 'visible', timeout: 10_000 });
      const infoTime = (performance.now() - t0) / 1000;

      // ── Stream start time ──────────────────────────────────────────────────
      const t1 = performance.now();

      await playBtn.click();

      // Race: wait for video to reach HAVE_CURRENT_DATA OR a "Playback Error" dialog.
      // The error dialog (Got It button) indicates Dispatcharr has no compatible stream.
      const videoOk = await Promise.race([
        page.waitForFunction(
          () => {
            const v = document.querySelector('video');
            return v !== null && v.readyState >= 2; // HAVE_CURRENT_DATA
          },
          { timeout: 20_000, polling: 200 },
        ).then(() => true).catch(() => false),
        page.waitForSelector('button:has-text("Got It")', { state: 'visible', timeout: 20_000 })
          .then(() => false).catch(() => false),
      ]);

      const streamTime = videoOk ? (performance.now() - t1) / 1000 : Infinity;

      if (!videoOk) {
        // Dismiss any error dialog before continuing to the next channel.
        const gotIt = page.getByRole('button', { name: 'Got It', exact: true });
        const visible = await gotIt.isVisible({ timeout: 1_000 }).catch(() => false);
        if (visible) await gotIt.click();
      }

      const callsThisPhase = totalDispatcharrCalls - callsBefore;
      callsPerPhase.push(callsThisPhase);

      channelResults.push({ name: channel, infoTime, streamTime, dispatcharrCalls: callsThisPhase });
      const streamLabel = isFinite(streamTime) ? streamTime.toFixed(2) + 's' : 'FAILED';
      console.log(
        `[${channel}] infoTime=${infoTime.toFixed(2)}s  streamTime=${streamLabel}  ` +
        `dispatcharrCalls=${callsThisPhase}`,
      );

      // ── Return to guide for the next channel ───────────────────────────────
      if (i < channels.length - 1) {
        if (videoOk) {
          await returnToGuide(page, guideUrl);
        } else {
          // Error dialog was already dismissed — still on the guide, no navigation needed.
          await page.evaluate(() => { localStorage.removeItem('guide-tagids'); });
        }
      }
    }

    // ── Assertions ─────────────────────────────────────────────────────────

    for (const r of channelResults) {
      expect(
        r.infoTime,
        `[${r.name}] infoTime should be < ${INFO_TIME_THRESHOLD_S}s`,
      ).toBeLessThan(INFO_TIME_THRESHOLD_S);

      expect(
        r.streamTime,
        `[${r.name}] streamTime should be < ${STREAM_TIME_THRESHOLD_S}s`,
      ).toBeLessThan(STREAM_TIME_THRESHOLD_S);
    }

    // After the first channel warms up, subsequent channels should not spam
    // the Dispatcharr API (BUG-007: repeated /api/channels/ calls per session).
    for (let i = 1; i < callsPerPhase.length; i++) {
      expect(
        callsPerPhase[i],
        `[${channels[i]}] should make ≤ 2 Dispatcharr API calls after warm-up (BUG-007 guard)`,
      ).toBeLessThanOrEqual(2);
    }

    await saveResults({ channels: channelResults, totalDispatcharrCalls });
  });
});
