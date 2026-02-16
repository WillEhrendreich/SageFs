import { test, expect, type Page } from '@playwright/test';

test.describe('Dashboard page structure', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/dashboard');
    // Wait for Datastar to connect and push initial state
    await expect(page.locator('#server-status')).toContainText('Connected', { timeout: 10000 });
  });

  test('should display page title with version', async ({ page }) => {
    await expect(page.locator('h1')).toContainText('SageFs Dashboard');
    await expect(page.locator('h1')).toContainText('v');
  });

  test('should show connection banner as connected', async ({ page }) => {
    const banner = page.locator('#server-status');
    await expect(banner).toHaveClass(/conn-connected/);
    await expect(banner).toContainText('Connected');
  });

  test('should render output panel with empty state', async ({ page }) => {
    const outputSection = page.locator('#output-section');
    await expect(outputSection).toBeVisible();
    await expect(outputSection.locator('h2')).toContainText('Output');

    const outputPanel = page.locator('#output-panel');
    await expect(outputPanel).toBeVisible();
    await expect(outputPanel).toHaveClass(/log-box/);
  });

  test('should render evaluate section with textarea and buttons', async ({ page }) => {
    const evalSection = page.locator('#evaluate-section');
    await expect(evalSection).toBeVisible();
    await expect(evalSection.locator('h2')).toContainText('Evaluate');

    // Textarea
    const textarea = page.locator('.eval-input').first();
    await expect(textarea).toBeVisible();
    await expect(textarea).toHaveAttribute('placeholder', /Enter F# code/);

    // Eval button
    await expect(page.getByRole('button', { name: 'Eval' })).toBeVisible();

    // Reset buttons
    await expect(page.getByRole('button', { name: '↻ Reset' })).toBeVisible();
    await expect(page.getByRole('button', { name: '⟳ Hard Reset' })).toBeVisible();
  });

  test('should render sessions panel', async ({ page }) => {
    const sessionsPanel = page.locator('#session-status');
    await expect(sessionsPanel).toBeVisible();
  });

  test('should render diagnostics panel with empty state', async ({ page }) => {
    const diagPanel = page.locator('#diagnostics-panel');
    await expect(diagPanel).toBeVisible();
    await expect(diagPanel).toHaveClass(/log-box/);
  });

  test('should render eval stats panel', async ({ page }) => {
    const evalStats = page.locator('#eval-stats');
    await expect(evalStats).toBeVisible();
  });

  test('should render create session section with inputs', async ({ page }) => {
    // Working directory input
    const dirInput = page.locator('input[placeholder*="path\\\\to\\\\project"]');
    await expect(dirInput).toBeVisible();

    // Discover button
    await expect(page.getByRole('button', { name: '🔍 Discover' })).toBeVisible();

    // Manual projects input
    const manualInput = page.locator('input[placeholder*="MyProject.fsproj"]');
    await expect(manualInput).toBeVisible();

    // Create session button
    await expect(page.getByRole('button', { name: '➕ Create Session' })).toBeVisible();
  });

  test('should have clear output button in panel header', async ({ page }) => {
    const clearBtn = page.locator('#output-section .panel-header-btn');
    await expect(clearBtn).toBeVisible();
    await expect(clearBtn).toContainText('Clear');
  });
});
