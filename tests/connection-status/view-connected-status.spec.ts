// spec: specs/plan.md
// seed: seed.spec.ts

import { test, expect } from '@playwright/test';

test.describe('Connection Status', () => {
  test('view connected status', async ({ page }) => {
    // 1. Navigate to http://localhost:37750/dashboard
    await page.goto('http://localhost:37750/dashboard');

    // 2. Wait for the SSE stream to connect (server pushes serverConnected signal)
    // The server-status banner should become hidden when connected
    const banner = page.locator('#server-status');
    await expect(banner).toBeHidden({ timeout: 10000 });

    // 3. Session status should be visible with session info
    const sessionStatus = page.locator('#session-status');
    await expect(sessionStatus).toBeVisible({ timeout: 10000 });
  });
});
