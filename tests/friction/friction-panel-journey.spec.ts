// Friction dashboard journey: proves the local friction review + send surface
// renders and functions in a REAL browser against a REAL daemon.
//
// Privacy model under test: the panel shows ONLY the user's local friction
// telemetry and the send path is server-authoritative (the client never
// assembles the payload; the endpoint/token are user-supplied signals).
//
// Prerequisites (scripts/e2e-dashboard.ps1): a daemon on PLAYWRIGHT_BASE_URL
// with a Ready session on the WebLive sample.
//
// The journey:
//   1. Open the dashboard and wait for the session to be Ready (SSE live).
//   2. Open the friction panel (<details id="friction-panel">).
//   3. Assert the privacy-model copy renders ("Report is sanitized locally
//      before send" or the empty-state text).
//   4. Assert the send form exists: endpoint input + ingest-token input +
//      Send Report button (the client-side send affordance).
//   5. Type an endpoint into the bound signal input and confirm the send
//      button is present and enabled — the structural send path.

import { test, expect } from '@playwright/test';

test.describe('Friction review panel journey', () => {
  test('friction panel renders the local review + send surface', async ({ page }) => {
    test.setTimeout(90_000);

    // 1. Dashboard with a Ready session.
    await page.goto('/dashboard');
    await expect(page.locator('#session-status')).toContainText('Ready', { timeout: 30_000 });

    // 2. The friction panel is a <details> — open it if collapsed.
    const panel = page.locator('#friction-panel');
    await expect(panel).toBeVisible();
    const summary = panel.locator('summary');
    await expect(summary).toContainText('Friction', { timeout: 15_000 });
    // Open the details if it is not already expanded.
    const isOpen = await panel.evaluate((el: HTMLElement) => (el as HTMLDetailsElement).open);
    if (!isOpen) {
      await summary.click();
    }

    // 3. Privacy/empty-state copy: either the empty-state text (no local
    //    friction yet) or the sanitize-before-send text must be present.
    const bodyText = await page.locator('body').innerText();
    const hasPrivacyCopy =
      bodyText.includes('No local friction recorded yet') ||
      bodyText.includes('Report is sanitized locally before send');
    expect(hasPrivacyCopy, 'privacy-model copy must render').toBeTruthy();

    // 4. The send surface: endpoint input, optional token input, Send button.
    await expect(page.getByPlaceholder(/your-worker\.example\.workers\.dev/)).toBeVisible();
    await expect(page.getByPlaceholder('token if your receiver requires one')).toBeVisible();
    await expect(page.getByRole('button', { name: /Send Report/ })).toBeVisible();

    // 5. The endpoint input is a live Datastar-bound signal — typing must not
    //    error and the send button must remain enabled.
    const endpoint = page.getByPlaceholder(/your-worker\.example\.workers\.dev/);
    await endpoint.fill('http://127.0.0.1:39999/friction');
    await expect(page.getByRole('button', { name: /Send Report/ })).toBeEnabled();
  });
});
