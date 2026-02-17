// seed: seed.spec.ts

import { test, expect } from '@playwright/test';

test.describe('Code Evaluation', () => {
  test('evaluate simple expression', async ({ page }) => {
    // 1. Navigate to http://localhost:37750/dashboard
    await page.goto('http://localhost:37750/dashboard');

    // 2. Click on the F# code textarea to focus it
    await page.getByRole('textbox', { name: 'Enter F# code... (Ctrl+Enter' }).click();

    // 3. Type 'let x = 1 + 1;;' into the textarea
    await page.getByRole('textbox', { name: 'Enter F# code... (Ctrl+Enter' }).fill('let x = 1 + 1;;');

    // 4. Click the '▶ Eval' button
    await page.getByRole('button', { name: '▶ Eval' }).click();

    // expect: The result 'val x: int = 2' should appear below the textarea
    await expect(page.getByText('val x: int = 2')).toBeVisible();

    // expect: The textarea should be cleared after eval
    await expect(page.getByRole('textbox', { name: 'Enter F# code... (Ctrl+Enter' })).toHaveValue('');
  });
});
