// spec: Code Evaluation
// seed: seed.spec.ts

import { test, expect } from '@playwright/test';

test.describe('Code Evaluation', () => {
  test('evaluate code with errors', async ({ page }) => {
    // 1. Navigate to http://localhost:37750/dashboard
    await page.goto('http://localhost:37750/dashboard');

    // 2. Type 'let x = undefinedVariable;;' into the textarea
    await page.getByRole('textbox', { name: 'Enter F# code... (Ctrl+Enter' }).fill('let x = undefinedVariable;;');
    
    // expect: The code should appear in the textarea
    await expect(page.getByRole('textbox', { name: 'Enter F# code... (Ctrl+Enter' })).toHaveValue('let x = undefinedVariable;;');

    // 3. Click the '▶ Eval' button
    await page.getByRole('button', { name: '▶ Eval' }).click();
    
    // expect: An error message should appear below the textarea
    await expect(page.getByText('Error: Operation could not be completed due to earlier error')).toBeVisible();
  });
});
