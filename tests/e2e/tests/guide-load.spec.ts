/**
 * Guide load performance test.
 *
 * Measures:
 * - Time from navigation to #!/livetv/guide until channel rows are visible
 * - Number of Dispatcharr API calls made during the load
 *
 * Threshold: channels must appear within 10 seconds.
 */

import { test, expect } from '@playwright/test';
import { login, saveResults } from './helpers';

test.describe('guide load', () => {
  test('channels appear within threshold', async ({ page }) => {
    await login(page);

    let dispatcharrCalls = 0;
    page.on('request', req => {
      if (req.url().includes('/api/channels/')) dispatcharrCalls++;
    });

    const t0 = performance.now();
    await page.goto('#!/livetv/guide');

    // Wait for at least one channel row to appear in the guide grid.
    // The guide renders a table/grid; rows carry data-id or similar attributes.
    // Adjust the selector if the Emby guide structure differs.
    await page.waitForSelector(
      '.guideTable .guideProgramName, .channelName, [data-type="Program"]',
      { state: 'visible', timeout: 10_000 },
    );
    const guideLoadTime = (performance.now() - t0) / 1000;

    console.log(`Guide load time: ${guideLoadTime.toFixed(2)}s`);
    console.log(`Dispatcharr API calls during load: ${dispatcharrCalls}`);

    expect(guideLoadTime, 'guide should load within 10s').toBeLessThan(10);

    await saveResults({ guideLoadTime, dispatcharrCallsDuringLoad: dispatcharrCalls });
  });
});
