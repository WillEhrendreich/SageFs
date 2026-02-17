// spec: Code Evaluation - evaluate multiline code
// seed: seed.spec.ts

import { test, expect } from '@playwright/test';

test.describe('Code Evaluation', () => {
  test('evaluate multiline code', async ({ page }) => {
    // 1. Navigate to http://localhost:37750/dashboard
    await page.goto('http://localhost:37750/dashboard');

    // 2. Type multi-line F# code into the textarea: 'let add x y =\n  x + y\nadd 5 3;;'
    const codeTextbox = page.getByRole('textbox', { name: 'Enter F# code... (Ctrl+Enter' });
    await codeTextbox.fill('let add x y =\n  x + y\nadd 5 3;;');
    
    // expect: The code should appear on multiple lines in the textarea
    await expect(codeTextbox).toHaveValue('let add x y =\n  x + y\nadd 5 3;;');

    // 3. Click the '▶ Eval' button
    await page.getByRole('button', { name: '▶ Eval' }).click();
    
    // expect: The code should evaluate successfully
    // expect: A result should appear below the textarea
    await expect(page.getByText('val add: x: int -> y: int -> int val it: int = 8')).toBeVisible();
  });
});
