// seed: seed.spec.ts

import { test, expect } from '@playwright/test';

test.describe('Code Evaluation', () => {
  test('consecutive evaluations', async ({ page }) => {
    // 1. Navigate to http://localhost:37750/dashboard
    await page.goto('http://localhost:37750/dashboard');

    // 2. Type 'let x = 5;;' and click '▶ Eval'
    await page.getByRole('textbox', { name: 'Enter F# code... (Ctrl+Enter' }).fill('let x = 5;;');
    await page.getByRole('button', { name: '▶ Eval' }).click();

    // - expect: The result 'val x: int = 5' should appear
    await expect(page.getByText('val x: int = 5')).toBeVisible();

    // 3. Type 'let y = x + 3;;' and click '▶ Eval'
    await page.getByRole('textbox', { name: 'Enter F# code... (Ctrl+Enter' }).fill('let y = x + 3;;');
    await page.getByRole('button', { name: '▶ Eval' }).click();

    // - expect: The result 'val y: int = 8' should appear
    await expect(page.getByText('val y: int = 8')).toBeVisible();
    // - expect: Variable 'x' should still be in scope (verified by successful evaluation)

    // 4. Type 'x + y;;' and click '▶ Eval'
    await page.getByRole('textbox', { name: 'Enter F# code... (Ctrl+Enter' }).fill('x + y;;');
    await page.getByRole('button', { name: '▶ Eval' }).click();

    // - expect: The result 'val it: int = 13' should appear
    await expect(page.getByText('val it: int = 13')).toBeVisible();
  });
});
