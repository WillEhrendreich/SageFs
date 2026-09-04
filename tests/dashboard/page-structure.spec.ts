import { test, expect, type Page } from '@playwright/test';
import { openEvalArea, openNewSession } from '../helpers/dashboard';

test.describe('Dashboard page structure', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/dashboard');
    // Wait for Datastar SSE to connect and push initial state
    // Server status banner is hidden when connected; session-status becomes visible
    await expect(page.locator('#session-status')).toContainText('Ready', { timeout: 30000 });
  });

  test('should display page title with version', async ({ page }) => {
    // The shell has no <h1>; the daemon-health bar carries the product
    // identity + version (e.g. "🟢 Healthy · SageFs 0.6.444.0 · up 6m · 15MB").
    const health = page.locator('#daemon-health');
    await expect(health).toContainText('SageFs');
    await expect(health).toContainText(/v?\d+\.\d+\.\d+/);
  });

  test('should hide connection banner when connected', async ({ page }) => {
    const banner = page.locator('#server-status');
    await expect(banner).toBeHidden();
  });

  test('should render output panel with empty state', async ({ page }) => {
    const outputSection = page.locator('#output-section');
    await expect(outputSection).toBeVisible();
    await expect(outputSection.locator('h2')).toContainText('Output');

    const outputPanel = page.locator('#output-panel');
    await expect(outputPanel).toBeVisible();
  });

  test('should render evaluate section with textarea and buttons', async ({ page }) => {
    const evalSection = page.locator('#evaluate-section');
    await expect(evalSection).toBeVisible();
    await expect(evalSection).toContainText('Evaluate');

    // Evaluate is a <details class="eval-area"> collapsed by default — open it.
    await openEvalArea(page);

    // Textarea
    const textarea = page.locator('.eval-input').first();
    await expect(textarea).toBeVisible();
    await expect(textarea).toHaveAttribute('placeholder', /Enter F# code/);

    // Eval button
    await expect(page.getByRole('button', { name: 'Eval' })).toBeVisible();

    // Reset buttons
    await expect(page.getByRole('button', { name: /Reset/ }).first()).toBeVisible();
  });

  test('should render sessions panel', async ({ page }) => {
    const sessionsPanel = page.locator('#session-status');
    await expect(sessionsPanel).toBeVisible();
  });

  test('should hide server status banner when connected', async ({ page }) => {
    const banner = page.locator('#server-status');
    await expect(banner).toBeHidden();
  });

  test('should render eval stats panel', async ({ page }) => {
    const evalStats = page.locator('#eval-stats');
    await expect(evalStats).toBeAttached();
    // Stats text: "0 evals · avg 0ms · min 0ms · max 0ms" (fresh session) or
    // real counts after evals.
    await expect(evalStats).toContainText(/evals/);
  });

  test('should render create session section with inputs', async ({ page }) => {
    // "New Session" is a <details> collapsed by default — open it.
    await openNewSession(page);

    // Working directory input
    const dirInput = page.locator('input[placeholder*="path\\\\to\\\\project"]');
    await expect(dirInput).toBeVisible();

    // Discover button
    await expect(page.getByRole('button', { name: /Discover/ })).toBeVisible();

    // Manual projects input
    const manualInput = page.locator('input[placeholder*="MyProject.fsproj"]');
    await expect(manualInput).toBeVisible();

    // Create session button
    await expect(page.getByRole('button', { name: /Create/ }).first()).toBeVisible();
  });

  test('should have clear output button in panel header', async ({ page }) => {
    const clearBtn = page.locator('#output-section .panel-header-btn');
    await expect(clearBtn).toBeVisible();
    await expect(clearBtn).toContainText('CLEAR');
  });
});
