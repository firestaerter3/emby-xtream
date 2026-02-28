/**
 * Shared helpers for Emby E2E tests.
 */

import { Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { execSync } from 'child_process';

/** Log in to Emby using credentials from .env. */
export async function login(page: Page): Promise<void> {
  const url = process.env.EMBY_URL || 'http://localhost:8096';
  const username = process.env.EMBY_USERNAME;
  const password = process.env.EMBY_PASSWORD;

  if (!username || !password) {
    throw new Error('EMBY_USERNAME and EMBY_PASSWORD must be set in tests/e2e/.env');
  }

  await page.goto(url);

  // Emby may show either:
  //   (a) a user-picker screen — tile buttons for each user, then a password field
  //   (b) a manual login form — username + password text inputs
  // Detect which one by waiting for the first element to appear.
  const pickerOrForm = await Promise.race([
    page.waitForSelector(`button:has-text("${username}")`, { timeout: 10_000 })
      .then(() => 'picker' as const),
    page.waitForSelector('#txtManualName, input[name="name"], input[type="text"]', { timeout: 10_000 })
      .then(() => 'form' as const),
  ]);

  if (pickerOrForm === 'picker') {
    // Click the user tile — Emby then shows only a password field.
    await page.click(`button:has-text("${username}")`);
    await page.waitForSelector('#txtManualPassword, input[name="password"], input[type="password"]', {
      timeout: 10_000,
    });
    await page.fill('#txtManualPassword, input[name="password"], input[type="password"]', password);
    await page.click('button[type="submit"], .btnSubmit, button:has-text("Sign in"), button:has-text("Login")');
  } else {
    // Standard text form with username + password inputs.
    await page.fill('#txtManualName, input[name="name"], input[type="text"]', username);
    const passwordInput = page.locator('#txtManualPassword, input[name="password"], input[type="password"]');
    if (await passwordInput.count() > 0) {
      await passwordInput.fill(password);
    }
    await page.click('button[type="submit"], .btnSubmit, button:has-text("Sign in"), button:has-text("Login")');
  }

  // Wait for the home page — not just any URL containing #! (which would match the
  // intermediate #!/startup/manuallogin.html page and return before login completes).
  await page.waitForURL(url => url.href.includes('#!/home'), { timeout: 15_000 });
}

/** Resolve the current plugin version for tagging result files. */
function resolveVersion(): string {
  if (process.env.PLUGIN_VERSION) return process.env.PLUGIN_VERSION;
  try {
    return execSync('git describe --tags --abbrev=0', { encoding: 'utf8' }).trim();
  } catch {
    return 'unknown';
  }
}

/** Resolve the current git commit hash (short). */
function resolveCommit(): string {
  try {
    return execSync('git rev-parse --short HEAD', { encoding: 'utf8' }).trim();
  } catch {
    return 'unknown';
  }
}

export interface TimingPair {
  infoTime: number;
  streamTime: number;
}

export interface StreamSession {
  playSessionId: string | null;
  openToken: string | null;
  mediaSourceId: string | null;
  itemId: string | null;
}

/**
 * Close an active Emby live stream via the server API.
 *
 * Mirrors the three-step `close_stream()` in tools/benchmark_livetv.py:
 *   1. LiveStreams/Close   — disposes the ILiveStream (drops Dispatcharr connection)
 *   2. Sessions/Playing/Stopped — releases tuner locks
 *   3. Videos/ActiveEncodings DELETE — kills transcoding processes for this browser device
 *
 * All calls are fire-and-forget; failures are silently ignored so the benchmark
 * continues even if Emby is in a bad state.
 */
export async function closeActiveStream(
  page: Page,
  session: StreamSession,
): Promise<void> {
  const apiKey = process.env.EMBY_API_KEY;
  const baseUrl = process.env.EMBY_URL || 'http://localhost:8096';
  if (!apiKey) return; // no API key → skip server-side cleanup

  if (session.openToken) {
    await page.request.post(
      `${baseUrl}/emby/LiveStreams/Close?api_key=${apiKey}`,
      { data: { LiveStreamId: session.openToken } },
    ).catch(() => {});
  }

  if (session.playSessionId) {
    await page.request.post(
      `${baseUrl}/emby/Sessions/Playing/Stopped?api_key=${apiKey}`,
      { data: {
        ItemId: session.itemId ?? '',
        MediaSourceId: session.mediaSourceId ?? '',
        PlaySessionId: session.playSessionId,
      } },
    ).catch(() => {});
  }

  // Belt-and-suspenders: kill all active encodings for this browser's DeviceId.
  const deviceId = await page.evaluate(
    () => localStorage.getItem('_deviceId2') ?? 'unknown',
  ).catch(() => 'unknown');
  await page.request.delete(
    `${baseUrl}/emby/Videos/ActiveEncodings?DeviceId=${deviceId}&api_key=${apiKey}`,
  ).catch(() => {});
}

export interface ChannelBenchmark {
  cold: TimingPair;
  warm: TimingPair[];
}

export interface BenchmarkData {
  channels: Record<string, ChannelBenchmark>;
}

/**
 * Write benchmark results to `results/benchmark-<mode>-<date>.json`.
 * Overwrites any existing file for the same mode+date, so re-runs replace stale data.
 */
export function saveBenchmarkResults(mode: string, data: BenchmarkData): void {
  const version = resolveVersion();
  const commit = resolveCommit();
  const date = new Date().toISOString().slice(0, 10);

  const resultsDir = path.resolve(__dirname, '..', 'results');
  fs.mkdirSync(resultsDir, { recursive: true });

  const filename = path.join(resultsDir, `benchmark-${mode}-${date}.json`);

  const payload = {
    mode,
    version,
    commit,
    date,
    ...data,
  };

  fs.writeFileSync(filename, JSON.stringify(payload, null, 2) + '\n');
  console.log(`Benchmark results saved to ${filename}`);
}

/**
 * Append test timing data to `results/<version>-<date>.json`.
 * Results directory is gitignored; kept locally for version-to-version comparison.
 */
export async function saveResults(data: Record<string, unknown>): Promise<void> {
  const version = resolveVersion();
  const commit = resolveCommit();
  const date = new Date().toISOString().slice(0, 10);

  const resultsDir = path.resolve(__dirname, '..', 'results');
  fs.mkdirSync(resultsDir, { recursive: true });

  const filename = path.join(resultsDir, `${version}-${date}.json`);

  // Read existing file (multiple specs write to the same file on the same day).
  let existing: Record<string, unknown> = {};
  if (fs.existsSync(filename)) {
    try {
      existing = JSON.parse(fs.readFileSync(filename, 'utf8'));
    } catch {
      // Ignore parse errors; start fresh.
    }
  }

  const merged = {
    version,
    commit,
    date,
    ...existing,
    ...data,
  };

  fs.writeFileSync(filename, JSON.stringify(merged, null, 2) + '\n');
  console.log(`Results saved to ${filename}`);
}
