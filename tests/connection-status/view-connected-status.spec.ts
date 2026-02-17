// spec: specs/plan.md
// seed: seed.spec.ts

import { test, expect } from '@playwright/test';

test.describe('Connection Status', () => {
  test('view connected status', async ({ page }) => {
    // 1. Navigate to http://localhost:37750/dashboard
    await page.goto('http://localhost:37750/dashboard');

    // 2. Wait for the page to fully load
    await page.getByText("✅ Connected").first().waitFor({ state: 'visible' });

    // 3. Check the connection status banner at the top of the page
    // - expect: The banner should show '✅ Connected'
    await expect(page.getByText('✅ Connected')).toBeVisible();

    // - expect: The banner should have a green background color or green styling
    const connectionStatus = page.getByText('✅ Connected');
    const backgroundColor = await connectionStatus.evaluate((element) => {
      const styles = window.getComputedStyle(element);
      return styles.backgroundColor;
    });
    
    expect(backgroundColor).toBe('rgb(63, 185, 80)');
  });
});
