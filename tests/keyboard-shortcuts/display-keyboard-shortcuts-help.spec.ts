// spec: Keyboard Shortcuts - display keyboard shortcuts help
// seed: seed.spec.ts

import { test, expect } from '@playwright/test';
import { openEvalArea } from '../helpers/dashboard';

test.describe('Keyboard Shortcuts', () => {
  test('display keyboard shortcuts help', async ({ page }) => {
    // 1. Navigate to the dashboard
    await page.goto('/dashboard');

    // 2. The ⌨ Help toggle lives inside the collapsed Evaluate area — open it.
    await openEvalArea(page);

    // 3. Click the '⌨' help toggle button
    const helpButton = page.locator('#evaluate-section .panel-header-btn').first();
    await helpButton.click();

    // expect: A keyboard shortcuts panel should appear
    await expect(page.getByRole('table')).toBeVisible();

    // expect: It should list shortcuts like Ctrl+Enter, Ctrl+L, Tab
    await expect(page.getByText('Ctrl+Enter')).toBeVisible();
    await expect(page.getByText('Ctrl+L')).toBeVisible();
    await expect(page.getByText('Tab')).toBeVisible();

    // 4. Click the '⌨' help toggle button again
    await helpButton.click();

    // expect: The keyboard shortcuts panel should be hidden/toggled off
    await expect(page.getByRole('table')).not.toBeVisible();
  });
});
