// seed: seed.spec.ts

import { test, expect } from '@playwright/test';

test.describe('Diagnostics Panel', () => {
  test('view diagnostics panel when empty', async ({ page }) => {
    // 1. Navigate to http://localhost:37750/dashboard
    await page.goto('http://localhost:37750/dashboard');

    // 2. Locate the Diagnostics panel - expect: The panel should have heading 'Diagnostics'
    await expect(page.getByRole('heading', { name: 'Diagnostics' })).toBeVisible();

    // expect: The panel should show 'No diagnostics' when there are no errors
    await expect(page.getByText('No diagnostics')).toBeVisible();
  });
});
