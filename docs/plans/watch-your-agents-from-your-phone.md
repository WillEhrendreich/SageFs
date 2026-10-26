# Watch your agents from your phone

Roadmap item: `watch-your-agents-from-your-phone` (Later, Agents). Plan written 2026-10-26 against master 59a8692c.

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

- The dashboard is a second `WebApplication` started by `startDashboardServer` (`SageFs/DaemonMode.fs:1644`) on `mcpPort + 1` (37750), bound through `LoopbackHost.listenUrl`. It carries `useOriginGuard` and the Falco endpoints from `Dashboard.fs`, nothing else. There is no authentication.
- The routes it serves include mutating ones in the same table as the reads: `/dashboard/eval`, `/dashboard/eval-file`, `/dashboard/hard-reset`, `/dashboard/reset`, `/dashboard/session/stop/{id}`, `/dashboard/session/purge/{id}`, `/dashboard/session/create`, `/dashboard/run-app/{id}`, `/dashboard/hygiene/tidy/{plan}`, `/dashboard/config/*`, `/dashboard/settings/*`, `/api/shutdown`, `/api/dispatch`. Reads: `GET /dashboard`, `/dashboard/stream/{clientId}` (the SSE morph), `/dashboard/dashboard.css`, `/dashboard/datastar.js`, `/dashboard/inspect/{kind}/{id}`, `/dashboard/app-output/stream`. (From `grep` over `SageFs/Dashboard.fs`; the full list is in `dashboardEndpoints`, `DaemonMode.fs:4560`.)
- The page is one server-rendered shell that is morphed whole on every push (`#main`). It contains the mutating controls in the markup: `DashboardFragments.fs` has 34 `Ds.post`, `Ds.onClick` or `Ds.onSubmit` sites and `Dashboard.fs` 3. The taste record for this repo insists on one authoritative whole-page render path and no per-panel fragmenting, so a read-only mode must come from a parameter to that path.
- Landing veto and resolve exist as cohort commands (`Cohort.CohortCommand.VetoLanding of by * LandingId * reason * scope`, `ResolveVeto of by * LandingId * scope`, `SageFs.Core/Cohort.fs:652` and `:670`) and as tools `veto_landing` (a seated Implementer, Verifier or the conductor) and `resolve_veto` (conductor only). `LandingState.Blocked(LandingBlocker.VetoedBy(by, reason), _)` is the state the conductor acts on, and `renderCohortPanel` (`DashboardFragments.fs:2980`) already prints it.
- The bind policy: `LoopbackHost.parse` in `SageFs.Core/SageFsConfig.fs` has no non-loopback case on purpose, and the error says SageFs "has no authentication, so it only listens on loopback". Exposing a listener on a tailnet address breaks that rule. Tailscale's own documentation says `tailscale serve` proxies to a local backend (only `http://127.0.0.1` is supported as a target) and terminates HTTPS with an automatically provisioned certificate, so the daemon can keep its rule and let Tailscale do the exposure. This was read from tailscale.com/kb/1242 on 2026-10-26. That page does not say whether `serve` adds identity headers; I did not verify that and the plan does not rely on it.
- `HttpOriginGuard` rejects any request whose Host is not loopback, as DNS rebinding. A request that arrives through `tailscale serve` carries the tailnet host name, so the guard would refuse every phone request unless the viewer listener has its own allowed-host set.
- Credential kinds and the one-time exchange do not exist yet; they are designed in `docs/plans/a-token-for-every-daemon-call.md` (`sfv_` viewer prefix, `/auth/exchange`, an `HttpOnly; SameSite=Strict` cookie).

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

Wire and clients: new HTTP routes on the viewer listener, one daemon route for minting links, and the `ViewMode` parameter, which is internal. `EndpointContracts.fs` gains a `viewerEndpoints` list; `apiVersion` is already 5 from the credential item. `sagefs-vscode` and `sagefs.nvim` are unaffected; trailer `Plugin: n/a, a listener and a CLI verb that no editor uses`. Persistence: none.

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

- Fixing the render sites touches the two biggest files (`DashboardFragments.fs` 3,996 lines, `Dashboard.fs` 3,816), under file-size ratchets; the `mutating` change should not add lines net.
- A mutating control missed in the render lands as a dead button on the phone. Test 2 enumerates panels to catch it, and the interface review lens (no dead controls) is the manual check.
- Depends on `a-token-for-every-daemon-call` (viewer credential, exchange), and for the Act link on `delegate_conductor` and the cohort's veto rules (done). Feeds nothing.

## Size

L. Read-only view alone is M; the action link and the simulation make it L.

## Decisions for Will

- Expose through `tailscale serve` to a loopback port, or bind the tailnet address directly (the roadmap's wording)? Recommendation: `tailscale serve`. It keeps the rule that SageFs never listens past loopback, and TLS comes with it.
- Ship the read-only view first and the action link later? Recommendation: yes. The view needs only the credential item; the action link needs a notification story to be useful.
- Should a viewer see eval output? Recommendation: yes by default, with a setting to hide eval and app output, because the phone is the least controlled screen you own.
