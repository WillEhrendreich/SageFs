# Watch your agents from your phone

Roadmap item: `watch-your-agents-from-your-phone` (Later, Agents). Plan written 2026-10-26 against master 59a8692c.

## Work order

Written 2026-11-11 against master 8975e898 (0.6.945). Every `path:line` below was read that day. The plan under this section is older; where its numbers disagree with these, these win.

### 1. Outcome

A person opens one link on a phone that is on their tailnet and sees the dashboard read-only: sessions, eval output, the cohort panel with members, claims and the landing queue, live test state. No eval box, no stop, reset, create or tidy button, no settings. A mutating request sent to that listener is refused with a reason, whatever the page shows. Later (slice 2) a second kind of link, minted for one landing, shows that landing and one button (veto or resolve-veto) that acts once.

Outcome gate: a `--integration-browser` journey (`SageFs.Tests/Program.fs:374`, the `DashboardBrowserRunner` entry) that starts a daemon with the viewer listener on a free port and `SAGEFS_VIEWER_HOST=localhost`, loads the page in headless Chromium, and asserts over the matrix {view link, expired link, forged link} x {every viewer route, plus `POST /dashboard/eval`, `/dashboard/reset`, `/dashboard/session/stop/{id}` asked of the viewer port}: the page has no `data-on-click` that posts, no form and no input; a mutating POST is answered with the refusal; zero Datastar console errors. Proven on Linux only. A real phone browser (iOS Safari, Android Chrome) holding the SSE stream through `tailscale serve` is unproven everywhere; nobody on this project can test it until someone with a tailnet runs the "Measure first" check.

### 2. Where the code is today

- The dashboard is a second `WebApplication`: `startDashboardServer` at `SageFs/DaemonMode.fs:1671`, called at `:5301` with `dashboardEndpoints` (`:5271`) plus the hot reload proxy and live bindings endpoints. Loopback only through `LoopbackHost.listenUrl` (`SageFs.Core/SageFsConfig.fs:171`); `LoopbackHost.parse` (`:177`) refuses any other host with the text at `:185`.
- The origin guard: `HttpOriginGuard.isLoopbackHost` (`SageFs.Core/HttpOriginGuard.fs:93`) and `isLoopbackOrigin` (`:101`). A request that arrives through `tailscale serve` with a `*.ts.net` Host is refused by it today. Tests: `SageFs.Tests/HttpOriginGuardTests.fs`, `OriginGuardOutcomeTests.fs`.
- Mutating controls are in the markup. Lines carrying `Ds.post`, `Ds.onClick` or `Ds.onSubmit` (`grep -c`, so a floor): `SageFs/DashboardFragments.fs` 37, `SageFs/LiveBindingsDock.fs` 7, `SageFs/Dashboard.fs` 3, `SageFs/PaneMenuView.fs` 2, `SageFs/AppOutputView.fs` 0. The stream handler is `createStreamHandler` (`Dashboard.fs:1582`), mapped at `:3899`; the page's first `Ds.onInit` is at `:383`.
- Veto and resolve exist: `CohortCommand.VetoLanding` (`SageFs.Core/Cohort.fs:753`), `ResolveVeto` (`:771`). The cohort panel is `renderCohortPanelCore` (`DashboardFragments.fs:3317`) and `renderCohortPanelFor` (`:3551`).
- The credential item shipped slice 0, and that changes this plan's dependency line. `sfo_` operator credential (`SageFs.Core/EdgeCredential.fs:22`), `EdgeAccess = Open | OperatorOnly` (`:106`), `decide` (`:132`); `Endpoint.createOperatorOnly` (`SageFs.Core/EndpointContracts.fs:30`) and `POST /api/members/mint` (`:77`); the CLI verb `member` (`SageFs/Program.fs:164`, usage `:692`). `apiVersion` is 4 (`EndpointContracts.fs:38`), so the plan's "already 5" is wrong. There is no `sfv_` viewer credential, no `/auth/exchange`, no cookie exchange: the plan's section 3 still has to build them, but it can copy the mint route and the `member` verb instead of waiting for the full credential item.
- Ratchets that bite here: `routes open to every caller` budget 63 (`SageFs.Tests/EdgeAccessBudgetTests.fs:24`), so every viewer route must carry a non-`Open` access; file sizes `DashboardFragments.fs` 4459 and `Dashboard.fs` 4259 (`SageFs.Tests/RatchetBudgets.fs:343-344`) against 4457 and 4256 real lines, two and three lines of room.
- Not built: `ViewMode`, `mutating`, `ViewerRoutes.fs`, `ViewerLink.fs`, `startViewerServer`, `SAGEFS_VIEWER_HOST`, `sagefs link`. `[not verified]`: whether `tailscale serve` passes the Host header and `text/event-stream` through unchanged.

### 3. Steps

1. **Pure.** RED `SageFs.Tests/DashboardViewModeTests.fs`, default tier, `[<Tests>]`: "WHY - a viewer render contains no control that posts". Render `renderMainContent` (`DashboardFragments.fs:2773`) with `ViewMode.Viewer` over a snapshot with a session, a vetoed landing and every panel shown, assert no `data-on-click` containing `@post(`, no `<form`, no `<input`. Fails to compile. Then new `SageFs/DashboardViewMode.fs` (`ViewMode`, `mutating`), listed in `SageFs/SageFs.fsproj` before `DashboardFragments.fs`; `renderMainContentIn` gains a `ViewMode` argument and `renderMainContent` passes `Operator`. Wrap call sites one file at a time (LiveBindingsDock, PaneMenuView, Dashboard, then Fragments). Add a ratchet table `postsOutsideMutating` in `RatchetBudgets.fs`, start at the real count, lower with `--ratchets --tighten`. Keep each wrap on one line: Fragments has two lines of budget.
2. **Pure, parallel with 1.** `SageFs.Core/ViewerLink.fs`: `LinkGrant`, `Spent`, an injected clock, `exchange` and `confirm` as a total function. Tests in `SageFs.Tests/ViewerLinkTests.fs` (property: at most one confirm per grant, none after `until`). Then `SageFs.Simulation/ViewerLinkSim.fs` with the twins the plan names, test file `ViewerLinkSimTests.fs`. Register all three in `SageFs.Tests/SageFs.Tests.fsproj` beside `CapabilitySimTests.fs` (`:912`).
3. **Edge.** `EdgeAccess` gains a `ViewerLink` case; `EdgeCredential.decide` refuses it on the loopback listeners. `EndpointContracts.fs` gains `viewerEndpoints`, each created with that access, so the open-route count stays 63. `SageFs/ViewerRoutes.fs` (the closed `ViewerRoute` list from the plan), `startViewerServer` beside `startDashboardServer`, started only when `SAGEFS_VIEWER_HOST` is set. The guard gets an allowed-host set for this listener only; test in `HttpOriginGuardTests.fs`. `DECISION:` the setting's type. `SettingValue` (`SageFs.Core/Settings.fs:287`) has `VBindHost of LoopbackHost` and no host-name case; add `VViewerHost` with a parser that accepts a DNS name and refuses anything with a scheme, path or port. Recommendation: add the case; a string setting is a magic string.
4. **Edge.** `sagefs link view --ttl 30m`: a `Link` verb in `Program.fs` after `Member` (`:164`), posting to a new `OperatorOnly` route `POST /api/viewer/link` that mints a grant (hash only, in the credential table) and prints `https://<SAGEFS_VIEWER_HOST>/dashboard#c=<code>`. `POST /auth/exchange` on the viewer listener spends the code and sets the `HttpOnly; SameSite=Strict` cookie. Wire change: `EndpointContracts.fs` is on the wire-surface list, so the commit carries `Plugin: n/a, a listener and a CLI verb no editor uses`.
5. **Edge.** The browser journey from section 1. That is the end of slice 1 (read-only view).
6. **Slice 2.** `sagefs link act --landing <id> --action veto|resolve-veto`, the action page (GET shows, POST acts once), running `VetoLanding` or `ResolveVeto` as the minting member. Needs a notification story to be useful; see "Not now".

### 4. How to check it

- REPL first: a session on `SageFs.Tests`, `send_fsharp_code` the render of step 1 and read the markup before you write the test.
- `dotnet build SageFs.Tests -c Release`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets` (the open-route, file-size and new `postsOutsideMutating` budgets live there).
- Inner loop: `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --summary --filter-test-list "viewer"`; read the TRUST line's `ran=`.
- Acceptance: the unfiltered default suite (`--summary`), then `--integration-browser` for the journey. Both must be Trusted.

### 5. Traps in this repo

- Open-route budget 63 (`EdgeAccessBudgetTests.fs:24`): a viewer route created with `Endpoint.create` fails the ratchet. Use the new access case.
- File-size budgets with two and three lines of room on the two files you must touch most. Put every new function in `DashboardViewMode.fs`, never in Fragments or Dashboard.
- SAGE-005: `startViewerServer` will want `(port: int) (host: string)` beside the dashboard's `(port: int)`; take a `ViewerListener` record, not two primitives.
- `EndpointContracts.fs` is wire surface: `Plugin:` trailer on every commit that touches it, and `scripts/ship.fsx` refuses without one.
- Linux-only machine: the phone and `tailscale serve` cells are manual, by someone with a tailnet. Say so in the docs; do not mark them proven.
- No new NuGet package. Cookies and the one-time code are ASP.NET Core and `RandomNumberGenerator`.

### 6. Size and order

Slice 1 (read-only): about a week for one person. Steps 1 and 2 run in parallel (disjoint files); 3 after 1; 4 after 2 and 3; 5 last. First commit: `test(dashboard): a viewer render contains no control that posts` with the RED test and `DashboardViewMode.fs`. Slice 2: another week, after the measurement below.

Measure first, by whoever has a tailnet, before slice 2 and ideally before step 3: run `tailscale serve --bg --https=443 http://127.0.0.1:37750`, open `https://<machine>.<tailnet>.ts.net/dashboard` from a phone, and record (a) whether the guard refused it (then the Host arrives unchanged, which is what step 3 assumes), (b) how often the connection banner reconnects over an hour with the screen locked between looks.

### 7. Not now

- Binding the tailnet address directly: `LoopbackHost.parse` says why (`SageFsConfig.fs:185`), and `tailscale serve` gives TLS for free.
- Push notifications to the phone: without one the action link needs the person to be looking already. Separate decision.
- Persisting link grants across a restart: `tokens-survive-a-restart` does not cover them, on purpose.
- Hiding eval output from a viewer: a setting, after the first person asks.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | L, M for the read-only view |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: the viewer listener maps no route that mutates. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

From a phone on the tailnet, a link opens the dashboard in a read-only view: sessions, eval output, the cohort panel with members, claims and the landing queue, live test state. No eval box, no stop or reset button, no settings. A second kind of link, minted by the person for one landing, opens a page that shows the landing and offers one button, veto or resolve-veto, which acts once and then the link is dead. Both links expire. Nothing is reachable from the tailnet except the viewer listener, and nothing on the existing loopback listeners changes meaning. The outcome gate drives a real daemon with a real headless browser (Playwright.NET, as the other dashboard tests do) over the matrix: link kind (view, action, expired, used, revoked, forged) by route (every route on the viewer listener, plus a sample of the full dashboard's mutating routes asked of the viewer listener), asserting the page shows no mutating control, a mutating request is refused with the reason, and an action link acts exactly once.

This item cannot start before `a-token-for-every-daemon-call` ships its credential kinds and the one-time-code exchange.

## What exists today

- The dashboard is a second `WebApplication` started by `startDashboardServer` (`SageFs/DaemonMode.fs:1671` on 2026-11-11) on `mcpPort + 1` (37750), bound through `LoopbackHost.listenUrl`. It carries `useOriginGuard` and the Falco endpoints from `Dashboard.fs`, nothing else. There is no authentication.
- The routes it serves include mutating ones in the same table as the reads: `/dashboard/eval`, `/dashboard/eval-file`, `/dashboard/hard-reset`, `/dashboard/reset`, `/dashboard/session/stop/{id}`, `/dashboard/session/purge/{id}`, `/dashboard/session/create`, `/dashboard/run-app/{id}`, `/dashboard/hygiene/tidy/{plan}`, `/dashboard/config/*`, `/dashboard/settings/*`, `/dashboard/layout`, `/api/shutdown`, `/api/dispatch`. Reads: `GET /dashboard`, `/dashboard/stream/{clientId}` (the SSE morph), `/dashboard/dashboard.css`, `/dashboard/datastar.js`, `/dashboard/inspect/{kind}/{id}`, `/dashboard/app-output/stream`. (From `grep` over `SageFs/Dashboard.fs`; the full list is in `dashboardEndpoints`, `DaemonMode.fs:5271`.)
- The page is one server-rendered shell that is morphed whole on every push (`#main`). It contains the mutating controls in the markup: on 2026-11-11 `DashboardFragments.fs` has 37 lines with `Ds.post`, `Ds.onClick` or `Ds.onSubmit`, `LiveBindingsDock.fs` 7, `Dashboard.fs` 3 and `PaneMenuView.fs` 2. The taste record for this repo insists on one authoritative whole-page render path and no per-panel fragmenting, so a read-only mode must come from a parameter to that path.
- Landing veto and resolve exist as cohort commands (`Cohort.CohortCommand.VetoLanding of by * LandingId * reason * scope`, `ResolveVeto of by * LandingId * scope`, `SageFs.Core/Cohort.fs:753` and `:771`) and as tools `veto_landing` (a seated Implementer, Verifier or the conductor) and `resolve_veto` (conductor only). `LandingState.Blocked(LandingBlocker.VetoedBy(by, reason), _)` is the state the conductor acts on, and `renderCohortPanelCore` (`DashboardFragments.fs:3317`) already prints it.
- The bind policy: `LoopbackHost.parse` in `SageFs.Core/SageFsConfig.fs` has no non-loopback case on purpose, and the error says SageFs "has no authentication, so it only listens on loopback". Exposing a listener on a tailnet address breaks that rule. Tailscale's own documentation says `tailscale serve` proxies to a local backend (only `http://127.0.0.1` is supported as a target) and terminates HTTPS with an automatically provisioned certificate, so the daemon can keep its rule and let Tailscale do the exposure. This was read from tailscale.com/kb/1242 on 2026-10-26. That page does not say whether `serve` adds identity headers; I did not verify that and the plan does not rely on it.
- `HttpOriginGuard` rejects any request whose Host is not loopback, as DNS rebinding. A request that arrives through `tailscale serve` carries the tailnet host name, so the guard would refuse every phone request unless the viewer listener has its own allowed-host set.
- Credential kinds and the one-time exchange: slice 0 of `docs/plans/a-token-for-every-daemon-call.md` shipped the operator credential (`sfo_`, `SageFs.Core/EdgeCredential.fs`), `EdgeAccess.OperatorOnly` routes and `sagefs member mint`. The `sfv_` viewer prefix, `/auth/exchange` and the `HttpOnly; SameSite=Strict` cookie are still designed only.

## Design

Three pieces, each small.

### 1. A third listener, loopback only, exposed by Tailscale

`startViewerServer` beside `startDashboardServer`, a third `WebApplication` on `mcpPort + 2` (37751) bound to loopback through `LoopbackHost.listenUrl`. It serves only the viewer route set, a closed list built in a new `SageFs/ViewerRoutes.fs`:

```fsharp
[<RequireQualifiedAccess>]
type ViewerRoute =
  | Page                       // GET /dashboard
  | Stream                     // GET /dashboard/stream/{clientId}
  | Asset of path: string      // css, datastar.js, favicon
  | Inspect                    // GET /dashboard/inspect/...
  | Exchange                   // POST /auth/exchange (one-time code to cookie)
  | ActionPage of LandingId    // GET: shows the landing and one button
  | ActionConfirm of LandingId // POST: the single use
```

Nothing else is mapped, so a mutating dashboard route on this listener is a 404 by construction. The tailnet exposure is `tailscale serve --https=443 http://127.0.0.1:37751`, run by the person (and printed by `sagefs link --help`). The daemon never touches a non-loopback address, and `LoopbackHost` is unchanged. The viewer listener's origin guard takes an explicit `AllowedHosts` set from one setting, `SAGEFS_VIEWER_HOST` (the machine's `*.ts.net` name); with it unset the listener is not started, so the default install opens nothing. A setting that is present and not a plausible host name fails the daemon's start with the reason, as `SAGEFS_BIND_HOST` does.

### 2. A read-only render

The shell takes a `ViewMode` and every mutating control is produced through one function:

```fsharp
[<RequireQualifiedAccess>]
type ViewMode =
  | Operator
  | Viewer of LinkGrant

let mutating (mode: ViewMode) (control: XmlNode) : XmlNode list =
  match mode with
  | ViewMode.Operator -> [ control ]
  | ViewMode.Viewer _ -> []
```

Every mutating site changes to go through `mutating`. A test fails when a `Ds.post` appears in the dashboard sources outside a `mutating` call (a source scan with a budget table, counted down to zero, in the style of `BlockingReaderRatchetTests`). The stream handler for the viewer listener renders with `ViewMode.Viewer`; the whole-page morph and its no-change suppression stay as they are, so payload economy stays on the transport (Brotli) and the render path does not fragment.

### 3. Links

```fsharp
[<RequireQualifiedAccess>]
type LinkGrant =
  | View of until: DateTime
  | Act of landing: LandingId * action: LandingAction * until: DateTime   // single use
and LandingAction = Veto | ResolveVeto
```

Minted outside the model and outside MCP, by the new CLI verb `sagefs link view --ttl 30m` and `sagefs link act --landing <id> --action resolve-veto --ttl 10m`, which call a daemon route with the operator credential and print a URL `https://<SAGEFS_VIEWER_HOST>/dashboard#c=<one-time code>`. The fragment never reaches a server log or a proxy. The page posts the code to `/auth/exchange`, gets the cookie, and the code is spent. The `Act` cookie is bound to one landing and one action.

Why a button and not the link itself: a chat app or a link previewer fetches a URL before the person taps it, so an action must never happen on a GET. The action page shows the landing's state and reason and one POST button; the POST runs `Cohort.CohortCommand.VetoLanding` or `ResolveVeto` as the member who minted the link (the conductor), marks the grant spent, and the page says what happened. A landing whose state changed since the link was made shows the new state and no button, with the reason.

The daemon holds link grants in the same in-memory credential table as other tokens (hash only), so they are revoked by `revoke_member`-style removal and by a restart. They are not persisted (`tokens-survive-a-restart` does not extend to them).

No new MCP tool. The catalog does not grow. `sagefs link` is a CLI verb and the routes are HTTP, so the token cost is zero.

Wire and clients: new HTTP routes on the viewer listener, one daemon route for minting links, and the `ViewMode` parameter, which is internal. `EndpointContracts.fs` gains a `viewerEndpoints` list; `apiVersion` is 4 today (`EndpointContracts.fs:38`), and whether a new listener bumps it is a call for whoever lands step 3. `sagefs-vscode` and `sagefs.nvim` are unaffected; trailer `Plugin: n/a, a listener and a CLI verb that no editor uses`. Persistence: none.

Honest limits. A link is a bearer credential: anyone who has it can use it until it expires, and the tailnet is the only network boundary. A viewer sees eval output, which can contain secrets that the dashboard already shows to anyone at the keyboard. The first version offers no push: nothing tells the phone a landing needs the conductor, so the person must already be looking. A notification channel is a separate decision.

## Tests

RED first.

1. Default tier, `SageFs.Tests/ViewerRoutesTests.fs`: `WHY - the viewer listener maps no route that mutates`. Takes the viewer route list and asserts none appears in a set of the mutating dashboard routes scanned from `Dashboard.fs`. Fails to compile today.
2. `WHY - a viewer render contains no control that posts`: renders the full shell with `ViewMode.Viewer` over a rich snapshot (sessions, a vetoed landing, hot reload, friction) and asserts the markup has no `data-on-click` post, no form, no input for code. The matrix covers each panel, so a panel added later is in it by enumeration of `OptionalPanel`.
3. Ratchet table `dashboardPostsOutsideMutating`: budget counts down from the current number to 0.
4. `WHY - an Act link is single use`: the pure link state machine (`LinkGrant`, `Spent`) in `SageFs.Core/ViewerLink.fs`. Property: for any sequence of exchange and confirm events, at most one confirm succeeds per grant, and none succeeds after `until`.
5. Simulation `SageFs.Simulation/ViewerLinkSim.fs`, in the style of `CohortVetoSpec.fs`: seeded events (`Mint`, `Exchange`, `Confirm`, `LandingChanges`, `PassMinutes`, `Restart`) over the real cohort `decide` and the link machine. Invariants: a confirm acts only if the landing is still in the state the link was made for; a spent or expired grant never acts; a view grant never reaches a confirm route. Twins: `ConfirmOnGetTwin` (acts on the page request), `ReusableCodeTwin`, `IgnoresLandingStateTwin`, `ViewActsTwin`. Each has to trip its invariant.
6. Integration tier (the `--integration-browser` tier, Playwright.NET, F# only): the outcome matrix above against a daemon with the viewer listener started on a free port with `SAGEFS_VIEWER_HOST=localhost` as a stand-in host, asserting visible state in the page (no button elements, text of the landing state) and the status and reason of refused requests. Zero Datastar console errors across the states (the repo's standing check for the dashboard).
7. Host guard test in `HttpOriginGuardTests.fs`: a request with the configured tailnet host is admitted on the viewer listener and refused on the loopback ones; an unlisted host is refused on both.

No wall-clock waits: expiry runs on an injected clock in tests 4 and 5; the browser tier waits on the SSE morph's first render event, as the other dashboard browser tests do.

## Steps

1. RED tests 1 to 3 and the `ViewMode` plumbing: `mutating` and the call sites (up to 37, since some `Ds.onClick` sites are view-only and stay). Owns `SageFs/DashboardFragments.fs`, `Dashboard.fs`; run alone because those files are large and others touch them.
2. `ViewerLink.fs` (pure machine) and tests 4 and 5. Owns `SageFs.Core/ViewerLink.fs`, `SageFs.Simulation/ViewerLinkSim.fs`. Parallel with 1.
3. `ViewerRoutes.fs`, `startViewerServer`, the host guard (test 7), `SAGEFS_VIEWER_HOST` in `SageFsConfig.fs`. Owns `SageFs/ViewerRoutes.fs` and the `DaemonMode.fs` start-up region.
4. `sagefs link` verb and the mint-link route (after the credential item's exchange exists).
5. The action page and the veto and resolve-veto confirm path.
6. Browser tier test 6, docs (`docs/architecture.md`, a short "phone" page), and the Tailscale recipe.

## Measure first

- Whether the phone's browser holds the Datastar SSE stream open through `tailscale serve` for hours (idle timeouts, mobile suspend). Run the existing dashboard behind `tailscale serve` for a day from a phone, with the daemon's own reconnect banner (`DashboardConnection`) visible, and record reconnect counts. If reconnects are frequent, the page needs the existing hard and soft banners to work on resume.
- Frame size per push with the full shell on a cellular link: the morph is Brotli compressed (`startDashboardServer` configures it); measure bytes per push for a typical session to see whether a phone-sized render is needed.
- Whether `tailscale serve` passes a stable `Host` header and the SSE `Content-Type` through unchanged. Run it once.
- Whether Tailscale's identity headers are present on served requests. If they are, the viewer listener can also require a named tailnet user, which would make a leaked link useless to anyone else. Unverified.

## Risks and dependencies

- Fixing the render sites touches the two biggest files (`DashboardFragments.fs` 4,457 lines against a budget of 4,459, `Dashboard.fs` 4,256 against 4,259, on 2026-11-11), under file-size ratchets; the `mutating` change should not add lines net.
- A mutating control missed in the render lands as a dead button on the phone. Test 2 enumerates panels to catch it, and the interface review lens (no dead controls) is the manual check.
- Depends on `a-token-for-every-daemon-call` (viewer credential, exchange), and for the Act link on `delegate_conductor` and the cohort's veto rules (done). Feeds nothing.

## Size

L. Read-only view alone is M; the action link and the simulation make it L.

## Decisions for Will

- Expose through `tailscale serve` to a loopback port, or bind the tailnet address directly (the roadmap's wording)? Recommendation: `tailscale serve`. It keeps the rule that SageFs never listens past loopback, and TLS comes with it.
- Ship the read-only view first and the action link later? Recommendation: yes. The view needs only the credential item; the action link needs a notification story to be useful.
- Should a viewer see eval output? Recommendation: yes by default, with a setting to hide eval and app output, because the phone is the least controlled screen you own.
