# A Content-Security-Policy for the dashboard

Written 2026-11-11 with the escaping fix (roast 2, threats finding H3). No policy ships yet: one that the page can live
with today would stop little, and one that stops what matters breaks the page. This is the finding and the work order.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Write the failing browser case in step 1 of the work order: the page under the policy below, zero `securitypolicyviolation` events, the Evaluate key, a navigation key and a completion click all working. |
| Decisions made | No policy ships until the four inline scripts are gone (2026-11-11), because a policy the page can carry today still needs `'unsafe-inline'` for script. |
| Surprises | Datastar evaluates every `data-*` expression with `Function(`, so no policy the page can live with stops an injected Datastar attribute. The escaping sinks are the wall for that. |

## What a policy would and would not stop here

The dashboard origin is in `OwnOrigins` (`SageFs/DaemonMode.fs`), so script running in the page may POST
`/dashboard/eval`. That is why the escaping fix routes every attribute and Datastar expression through
`SageFs/DashboardSinks.fs` and why `DashboardSinkTests` ratchets the raw calls to zero.

A policy is a second wall behind that one. What it can and cannot do on this page:

- Datastar evaluates every `data-on:*`, `data-show`, `data-signals:*`, `data-class:*`, `data-attr:*`, `data-effect` and
  `data-text` value with `Function(`. The page cannot work without `script-src 'unsafe-eval'`. So an injected Datastar
  attribute runs under any policy the page can carry. A policy does not cover the class of bug the escaping fix closed.
- What a policy would stop: an injected `<script>` element, an inline event handler (`onerror=`, `onclick=`) and a
  `javascript:` URL. Those need `'unsafe-inline'` in `script-src`, which the page has no need for once its own inline
  scripts are gone.

## What the page carries inline today

Measured from the source on 2026-11-11:

- Four inline `<script>` elements: `Dashboard.completionInsertScript`, `Dashboard.evaluatePopoverFocusScript`,
  `Dashboard.keyboardHandlerScript` (`SageFs/Dashboard.fs`, built with `sprintf` over `DomIds` constants and
  `KeyBindings`), and `LiveBindingsDock.flashScript` (`SageFs/LiveBindingsDock.fs`). `script-src 'self' 'unsafe-eval'`
  without a nonce or hash blocks all four: completion insertion, the Evaluate popover's focus, every keyboard shortcut
  and the live-value flash stop working.
- Two inline `<style>` elements: `fontFaceCss` in the shell head and the theme variables (`renderThemeVars`,
  `SageFs/DashboardFragments.fs`), which the stream re-sends on a theme change.
- About 330 `Attr.style` uses across the renderers, and Datastar's `data-attr:style` (the dock height). A `style-src`
  without `'unsafe-inline'` blocks style attributes, so style stays `'unsafe-inline'` until those move to classes.

## Work order

1. Move the four inline scripts into one served file, `/dashboard/shell.js`, with the `DomIds` and key-binding table it
   needs read from `data-*` attributes on `<body>` (written through `Sink.attr`), so the file is constant and can be
   cached and hashed. RED first: a browser case in the style of `DashboardNarrowLayoutBrowserTests` that loads the page
   with the policy below set by the in-memory route and asserts no `securitypolicyviolation` event fires, then presses
   the Evaluate key, a session-navigation key and a completion click. It fails today on all four.
2. Move `fontFaceCss` into `dashboard.css` (the font URLs are constants). The theme variables can stay inline under a
   per-response nonce, since the stream re-sends them: the nonce goes on the `<style>` the shell writes and on the one
   `renderThemeVars` patches.
3. Then send, from the dashboard listener only (never the MCP port):
   `Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-eval'; style-src 'self' 'unsafe-inline';
   img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none';
   form-action 'self'`.
   `img-src data:` is for the cohort panel's SVG data URIs. The header is a constant in one place, and a
   `DashboardCspTests` case reads the served header against it.
4. Later, not in this order: moving the 330 style attributes to classes so `style-src` can drop `'unsafe-inline'`. That
   is a design-system change and wants its own plan.

The gate for step 3 is the browser case from step 1 across the state matrix the dashboard taste rules name (no
sessions, sessions, explicit session, create from the picker, click into a session), with zero policy violations and
zero console errors.
