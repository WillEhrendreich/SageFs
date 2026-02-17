// spec: Code Evaluation - evaluate with keyboard shortcut
// seed: seed.spec.ts

import { test, expect } from '@playwright/test';

test.describe('Code Evaluation', () => {
  test('evaluate with keyboard shortcut', async ({ page }) => {
    // 1. Navigate to http://localhost:37750/dashboard
    await page.goto('http://localhost:37750/dashboard');

    // 2. Click on the F# code textarea and type 'printfn "Hello, World!"'
    const codeTextarea = page.getByRole('textbox', { name: 'Enter F# code... (Ctrl+Enter' });
    await codeTextarea.click();
    await codeTextarea.fill('printfn "Hello, World!"');
    
    // expect: The code should appear in the textarea
    await expect(codeTextarea).toHaveValue('printfn "Hello, World!"');

    // 3. Press Ctrl+Enter
    await page.keyboard.press('Control+Enter');
    
    // expect: The code should be evaluated
    // expect: A result should appear below the textarea
    await expect(page.getByText('val it: unit = ()')).toBeVisible();
    
    // expect: The textarea should be cleared
    await expect(codeTextarea).toHaveValue('');
  });
});
