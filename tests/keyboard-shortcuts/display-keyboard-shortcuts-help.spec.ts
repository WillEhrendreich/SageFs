// spec: Keyboard Shortcuts - display keyboard shortcuts help
// seed: seed.spec.ts

import { test, expect } from '@playwright/test';

test.describe('Keyboard Shortcuts', () => {
  test('display keyboard shortcuts help', async ({ page }) => {
    // 1. Navigate to http://localhost:37750/dashboard
    await page.goto('http://localhost:37750/dashboard');

    // 2. Click the '⌨ Help' button
    await page.getByRole('button', { name: '⌨ Help' }).click();

    // expect: A keyboard shortcuts panel should appear
    await expect(page.getByRole('table')).toBeVisible();

    // expect: It should list shortcuts like Ctrl+Enter, Ctrl+L, F1, Tab
    await expect(page.getByText('Ctrl+Enter')).toBeVisible();
    await expect(page.getByText('Ctrl+L')).toBeVisible();
    await expect(page.getByText('Tab')).toBeVisible();

    // 3. Click the '⌨ Help' button again (or press F1)
    await page.getByRole('button', { name: '⌨ Help' }).click();

    // expect: The keyboard shortcuts panel should be hidden/toggled off
    await expect(page.getByRole('table')).not.toBeVisible();
  });
});
