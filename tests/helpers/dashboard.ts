// Shared dashboard helpers for Playwright journey specs.

import { expect, type Page } from '@playwright/test';

// The dashboard's Evaluate section is a <details id="evaluate-section"
// class="eval-area"> collapsed by default (accordion). Every code-evaluation
// journey must open it before interacting with the textarea / Eval button.
export async function openEvalArea(page: Page): Promise<void> {
  const section = page.locator('#evaluate-section');
  await expect(section).toBeVisible();
  const isOpen = await section.evaluate((el: HTMLElement) => (el as HTMLDetailsElement).open);
  if (!isOpen) {
    await section.locator('summary').first().click();
  }
  await expect(page.locator('.eval-input').first()).toBeVisible();
}

// The dashboard's New Session form is a <details> collapsed by default.
export async function openNewSession(page: Page): Promise<void> {
  const newSession = page.getByText('New Session').first();
  await newSession.click();
}
