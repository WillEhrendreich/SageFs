import { defineConfig } from '@playwright/test';

// SageFs dashboard E2E harness.
//
// The dashboard is served by a running SageFs daemon (started manually or by
// CI) — this config targets it over HTTP and does NOT auto-spawn it, because
// a daemon carries real session state. Point PLAYWRIGHT_BASE_URL at the
// dashboard port (default: the local daemon's dashboard on 37750).
export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  expect: { timeout: 30_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: process.env.PLAYWRIGHT_BASE_URL ?? 'http://127.0.0.1:37750',
    viewport: { width: 1440, height: 1000 },
    trace: 'retain-on-failure',
  },
  projects: [
    {
      name: 'dashboard',
      testMatch: /tests\/(dashboard|code-evaluation|session-management|connection-status|keyboard-shortcuts|hot-reload|friction)\/.*\.spec\.ts/,
    },
  ],
});
