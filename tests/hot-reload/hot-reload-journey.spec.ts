// Hot-reload browser journey: proves the FULL loop a user experiences —
// edit a source file on disk, save, and the open browser page updates by
// itself (worker re-eval -> DevReload SSE -> injected script reloads the
// page) against a REAL daemon and a REAL browser.
//
// Prerequisites (set up by scripts/e2e-dashboard.ps1):
//   - a SageFs daemon on PLAYWRIGHT_BASE_URL (dashboard port) with a WebLive
//     session on the SageFs.Samples.WebappDatastar sample project
//   - the sample's .SageFs/init.fsx started the app and wrote
//     .SageFs/init-ok.txt containing "started at <url>"
//
// The journey:
//   1. Open the sample app's "/" page in a real browser. The DevReload
//      middleware (auto-injected by the host) serves the HTML with an
//      injected <script> that opens an SSE connection to the worker's
//      /__sagefs__/reload endpoint.
//   2. Assert the v1 h1 is visible.
//   3. Edit the h1 text in Program.fs on disk.
//   4. The worker's file watcher re-evals the source; the running app serves
//      the new page; the injected script receives the reload event and the
//      browser page updates to show the v2 h1 — no manual refresh.
//   5. Restore the file so the journey is re-runnable.

import { test, expect, type Page } from '@playwright/test';
import { readFileSync, writeFileSync } from 'fs';
import { join } from 'path';

const SAMPLE_DIR = join(__dirname, '..', '..', 'samples', 'demos', 'SageFs.Samples.WebappDatastar');
const PROGRAM_FS = join(SAMPLE_DIR, 'Program.fs');
const INIT_OK = join(SAMPLE_DIR, '.SageFs', 'init-ok.txt');

const V1_H1 = '✅ Todo — powered by SageFs + Falco.Datastar';
const V2_H1 = '✅ Todo — hot reload verified live';

function readAppBaseUrl(): string {
  const content = readFileSync(INIT_OK, 'utf8');
  const match = content.match(/started at (http:\/\/127\.0\.0\.1:\d+)/);
  if (!match) {
    throw new Error(`Could not find app URL in ${INIT_OK}: ${content}`);
  }
  return match[1];
}

test.describe('Hot reload browser journey', () => {
  test('an open browser page updates itself after a source edit + save', async ({ page }) => {
    test.setTimeout(180_000);

    const appUrl = readAppBaseUrl();

    // 1. Open the app in a real browser.
    await page.goto(`${appUrl}/`, { waitUntil: 'domcontentloaded' });

    // 2. The v1 h1 must be visible.
    const h1 = page.locator('h1');
    await expect(h1).toContainText('Todo', { timeout: 30_000 });

    // 3. Edit the h1 text on disk (the hot-reload demo target).
    const original = readFileSync(PROGRAM_FS, 'utf8');
    expect(original).toContain(V1_H1);
    const edited = original.replace(V1_H1, V2_H1);
    writeFileSync(PROGRAM_FS, edited, 'utf8');

    try {
      // 4. The browser page must update itself to the v2 h1 — the worker
      //    re-evals the saved file and the injected DevReload script reloads
      //    the page. No manual refresh, no restart.
      await expect(h1).toContainText('hot reload verified live', { timeout: 90_000 });
    } finally {
      // 5. Restore the source.
      writeFileSync(PROGRAM_FS, original, 'utf8');
    }
  });
});
