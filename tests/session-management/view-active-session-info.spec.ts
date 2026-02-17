// seed: seed.spec.ts

import { test, expect } from '@playwright/test';

test.describe('Session Management', () => {
  test('view active session info', async ({ page }) => {
    // 1. Navigate to http://localhost:37750/dashboard
    await page.goto('http://localhost:37750/dashboard');

    // 2. Wait for the page to fully load and SSE to connect
    // Server status banner should be hidden when connected
    await expect(page.locator('#server-status')).toBeHidden({ timeout: 10000 });

    // 3. Session status should be visible with session info
    const sessionStatus = page.locator('#session-status');
    await expect(sessionStatus).toBeVisible({ timeout: 10000 });
    await expect(sessionStatus).toContainText('Ready', { timeout: 30000 });

    // 4. Sessions panel in sidebar should exist
    await expect(page.getByRole('heading', { name: 'Sessions' })).toBeVisible();
  });
});
