import { test, expect } from '@playwright/test';

test.describe('Connection Banner Independence', () => {
  // The connection banner must NOT rely on Datastar signals (data-show attribute)
  // because when the server dies, it can't push signal updates to hide/show the banner.
  // Instead, the banner should be controlled by pure JS that intercepts the SSE stream lifecycle.
  //
  // Design principle: the banner is ONLY for problems. When connected, it is invisible.
  // Users should not see "Connected" — that's the assumed happy state.

  test('server-status banner does not have data-show attribute', async ({ page }) => {
    // Navigate to dashboard — inspect raw HTML before SSE connects
    await page.goto('/dashboard');

    const banner = page.locator('#server-status');
    await expect(banner).toBeAttached();

    // The banner must NOT have a data-show attribute (Datastar signal binding)
    // If it does, the banner can't respond to server death (no signal push when server is down)
    const dataShow = await banner.getAttribute('data-show');
    expect(dataShow, 'Banner should not use data-show (Datastar signal). Use JS lifecycle instead.').toBeNull();
  });

  test('server-status banner is invisible when connected', async ({ page }) => {
    await page.goto('/dashboard');

    const banner = page.locator('#server-status');
    // After SSE connects, banner should be completely invisible — no "Connected" message
    await expect(banner).toBeHidden({ timeout: 15000 });

    // Verify there's no visible "Connected" text anywhere in the banner area
    await expect(banner).not.toContainText('Connected');
  });

  test('server-status banner shows when SSE cannot connect', async ({ page }) => {
    // Block the SSE stream BEFORE navigating so we observe the disconnected state
    await page.route('**/dashboard/stream', route => route.abort());
    await page.goto('/dashboard');

    const banner = page.locator('#server-status');
    // Banner should be visible with a problem indicator when SSE cannot establish
    // Current implementation: Ds.show "!$serverConnected" controls visibility via Datastar signal,
    // which means banner visibility depends on signal initialization, not actual connection state.
    // After fix: JS controls visibility based on actual SSE stream lifecycle.
    await expect(banner).toBeVisible({ timeout: 5000 });
  });

  test('reconnection polls /api/daemon-info when SSE fails', async ({ page }) => {
    // Track requests to /api/daemon-info
    const daemonInfoRequests: string[] = [];
    page.on('request', req => {
      if (req.url().includes('/api/daemon-info')) {
        daemonInfoRequests.push(req.url());
      }
    });

    // Block SSE stream from the start to simulate server being down
    await page.route('**/dashboard/stream', route => route.abort());
    await page.goto('/dashboard');

    // Wait for reconnection poller to start hitting /api/daemon-info
    // The architecture specifies polling every 2 seconds
    await page.waitForTimeout(6000);

    expect(
      daemonInfoRequests.length,
      'Should poll /api/daemon-info for reconnection when SSE stream fails'
    ).toBeGreaterThan(0);
  });
});
