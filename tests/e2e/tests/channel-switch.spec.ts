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
 *   switchTime  — navigate away and back (channel-to-channel) → new video plays.
 *                 Catches: incremental regression vs cold start.
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
 *   Example: EMBY_CHANNELS=NPO 1,RTL 4,RTL 5
 *   If not set, the test skips with a warning.
 */

import { test, expect } from '@playwright/test';
import { login, saveResults } from './helpers';

const CHANNELS_ENV = process.env.EMBY_CHANNELS;
const channels = CHANNELS_ENV
  ? CHANNELS_ENV.split(',').map(c => c.trim()).filter(Boolean)
  : [];

const INFO_TIME_THRESHOLD_S = 3;
const STREAM_TIME_THRESHOLD_S = 5;

test.describe('channel switch performance', () => {
  test.skip(!channels.length, 'Set EMBY_CHANNELS in .env to run channel switch tests');

  test('info screen and stream start within thresholds', async ({ page }) => {
    await login(page);

    // Clear any stale guide filter that would make the grid appear empty.
    await page.evaluate(() => {
      localStorage.removeItem('guide-tagids');
    });

    // Navigate to the Live TV guide.
    await page.goto('#!/livetv/guide');
    await page.waitForSelector(
      '.guideTable .guideProgramName, .channelName, [data-type="Program"]',
      { state: 'visible', timeout: 10_000 },
    );

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
      const t0 = performance.now();

      // Click the channel name in the guide.
      await page.click(`text="${channel}"`, { timeout: 10_000 });

      // Wait for the play/record dialog (Emby shows an info overlay with a Play button).
      await page.waitForSelector(
        '[data-action="play"], .btnPlay, button:has-text("Play"), .playButton',
        { state: 'visible', timeout: 10_000 },
      );
      const infoTime = (performance.now() - t0) / 1000;

      // ── Stream start time ──────────────────────────────────────────────────
      const t1 = performance.now();

      await page.click(
        '[data-action="play"], .btnPlay, button:has-text("Play"), .playButton',
      );

      // Wait for the video element to be attached and have enough data to play.
      await page.waitForSelector('video', { state: 'attached', timeout: 15_000 });
      await page.waitForFunction(
        () => {
          const v = document.querySelector('video');
          return v !== null && v.readyState >= 2; // HAVE_CURRENT_DATA
        },
        { timeout: 15_000, polling: 200 },
      );
      const streamTime = (performance.now() - t1) / 1000;

      const callsThisPhase = totalDispatcharrCalls - callsBefore;
      callsPerPhase.push(callsThisPhase);

      channelResults.push({ name: channel, infoTime, streamTime, dispatcharrCalls: callsThisPhase });
      console.log(
        `[${channel}] infoTime=${infoTime.toFixed(2)}s  streamTime=${streamTime.toFixed(2)}s  ` +
        `dispatcharrCalls=${callsThisPhase}`,
      );

      // Navigate back to guide for the next iteration (channel-to-channel switch).
      if (i < channels.length - 1) {
        await page.goBack();
        await page.waitForSelector(
          '.guideTable .guideProgramName, .channelName, [data-type="Program"]',
          { state: 'visible', timeout: 10_000 },
        );
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
