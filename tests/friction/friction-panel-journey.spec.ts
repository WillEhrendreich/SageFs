// Friction dashboard journey: proves the local friction review surface renders
// in a REAL browser against a REAL daemon.
//
// Privacy model under test: the panel shows ONLY the user's local friction
// telemetry. With zero recorded events (a fresh session) the panel renders the
// empty state and NO send form — the endpoint/token/Send surface appears only
// once MCP tool use records friction events (covered by the daemon-side
// friction flow tests). This spec pins the honest 0-event browser state.
//
// Prerequisites (scripts/e2e-dashboard.ps1): a daemon on PLAYWRIGHT_BASE_URL
// with a Ready session on the WebLive sample.
//
// The journey:
//   1. Open the dashboard and wait for the session to be Ready (SSE live).
//   2. Open the friction panel (<details id="friction-panel">).
//   3. Assert the panel title carries the counts ("Friction (0 events...").
//   4. Assert the empty-state privacy copy renders.
//   5. Assert NO send form is present when there is nothing to send.

import { test, expect } from '@playwright/test';

test.describe('Friction review panel journey', () => {
  test('friction panel renders the honest empty state with no send surface', async ({ page }) => {
    test.setTimeout(90_000);

    // 1. Dashboard with a Ready session.
    await page.goto('/dashboard');
    await expect(page.locator('#session-status')).toContainText('Ready', { timeout: 30_000 });

    // 2. The friction panel is a <details> — open it if collapsed.
    const panel = page.locator('#friction-panel');
    await expect(panel).toBeVisible();
    const summary = panel.locator('summary');
    await expect(summary).toContainText('Friction', { timeout: 15_000 });
    const isOpen = await panel.evaluate((el: HTMLElement) => (el as HTMLDetailsElement).open);
    if (!isOpen) {
      await summary.click();
    }

    // 3. Counts in the title: a fresh session has 0 events / 0 feedback.
    await expect(summary).toContainText('0 events');
    await expect(summary).toContainText('0 feedback');

    // 4. Empty-state privacy copy.
    await expect(panel).toContainText('No local friction recorded yet');

    // 5. Honest 0-event state: no send form (endpoint input / Send button).
    await expect(panel.getByPlaceholder(/your-worker\.example\.workers\.dev/)).toHaveCount(0);
    await expect(panel.getByRole('button', { name: /Send Report/ })).toHaveCount(0);
  });
});
