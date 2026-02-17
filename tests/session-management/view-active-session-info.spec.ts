// seed: seed.spec.ts

import { test, expect } from '@playwright/test';

test.describe('Session Management', () => {
  test('view active session info', async ({ page }) => {
    // 1. Navigate to http://localhost:37750/dashboard
    await page.goto('http://localhost:37750/dashboard');

    // 2. Wait for the page to fully load and SSE to connect - verify Connection status shows '✅ Connected'
    await expect(page.getByText('✅ Connected')).toBeVisible();

    // 3. Locate the Sessions panel
    await expect(page.getByRole('heading', { name: 'Sessions' })).toBeVisible();
    
    // expect: The panel should show session status (e.g., 'Ready')
    await expect(page.getByText('Ready')).toBeVisible();
    
    // expect: It should display a session ID and show the number of loaded projects
    await expect(page.getByText(/Session: session-[\w-]+ \| Projects: \d+/)).toBeVisible();
  });
});
