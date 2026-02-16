import { test, expect } from '@playwright/test';

test.describe('Dashboard seed', () => {
  test('seed', async ({ page }) => {
    await page.goto('/dashboard');
    await expect(page.locator('h1')).toContainText('SageFs Dashboard');
  });
});
