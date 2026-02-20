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

  // Emby login form — selectors are stable across recent Emby versions.
  await page.waitForSelector('#txtManualName, input[name="name"], input[type="text"]', {
    timeout: 10_000,
  });
  await page.fill('#txtManualName, input[name="name"], input[type="text"]', username);

  const passwordInput = page.locator('#txtManualPassword, input[name="password"], input[type="password"]');
  if (await passwordInput.count() > 0) {
    await passwordInput.fill(password);
  }

  await page.click('button[type="submit"], .btnSubmit, button:has-text("Sign in"), button:has-text("Login")');

  // Wait for the dashboard to confirm successful login.
  await page.waitForURL(/#!/, { timeout: 15_000 });
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
