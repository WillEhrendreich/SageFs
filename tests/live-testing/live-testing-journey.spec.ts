// Live-testing dashboard journey: proves the enable -> state-change loop in a
// REAL browser against a REAL daemon — the dashboard's Live Testing panel is
// server state pushed over the SSE stream, so flipping it exercises the full
// round-trip (button POST -> daemon -> SSE morph -> panel updates).
//
// Prerequisites (scripts/e2e-dashboard.ps1): a daemon on PLAYWRIGHT_BASE_URL
// with a Ready session on the WebLive sample (the sample carries a test
// project, so enabling live testing also exercises discovery).
//
// The journey:
//   1. Open the dashboard and wait for the session to be Ready.
//   2. Assert the Live Testing panel renders in the OFF state with the
//      cost-hint copy ("run tests on every keystroke and file save").
//   3. Click Enable.
//   4. Assert the panel flips to the ON state (server round-trip via SSE).
//   5. Click Disable and assert it returns to OFF (idempotent round-trip).

import { test, expect } from '@playwright/test';

test.describe('Live testing dashboard journey', () => {
  test('live testing panel enables and disables through the SSE round-trip', async ({ page }) => {
    test.setTimeout(120_000);

    // 1. Dashboard with a Ready session.
    await page.goto('/dashboard');
    await expect(page.locator('#session-status')).toContainText('Ready', { timeout: 30_000 });

    // 2. The Live Testing panel renders in the OFF state with the cost hint.
    const panel = page.locator('#live-testing-panel');
    await expect(panel).toBeVisible();
    await expect(panel).toContainText('Live Testing: OFF', { timeout: 15_000 });
    await expect(panel).toContainText('keystroke');

    // 3. Enable.
    await panel.getByRole('button', { name: 'Enable' }).click();

    // 4. The panel flips ON via the SSE round-trip. Enabling on a real sample
    //    starts discovery, so the panel may show "Discovering tests..." or a
    //    live status label — the OFF state must be gone and ON present.
    await expect(panel).toContainText('Live Testing: ON', { timeout: 30_000 });

    // 5. Disable and confirm the round-trip returns to OFF.
    await panel.getByRole('button', { name: 'Disable' }).click();
    await expect(panel).toContainText('Live Testing: OFF', { timeout: 30_000 });
  });
});
