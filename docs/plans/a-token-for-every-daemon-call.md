# A token for every daemon call

Roadmap item: `a-token-for-every-daemon-call` (Later, Platform). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | L |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: a route with no declared access cannot be added. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

With the daemon's credential policy set to required, a request to either listener (MCP port 37749, dashboard port 37750) that carries no valid credential gets a 401 with the reason and the way to get one, except a short named list of probe routes that discovery needs. Three kinds of credential exist: the operator credential (the person and their tools: CLI, bridge, editors, hook), a member token (an agent run, exactly as built), and a viewer token (read-only, used by `watch-your-agents-from-your-phone`). A member token cannot call an operator-only HTTP route, so the "way out" sentence in the confirmation refusal is true only for an operator and the refusal stops pointing agents at it. The outcome gate runs a real daemon over the matrix: credential kind (none, operator, member per role, viewer, revoked, expired, malformed) by surface (MCP tool call, MCP `tools/list`, each HTTP route class on 37749, each on 37750) and asserts admit or refuse with the named reason in every cell.

## What exists today

Verified:

- Both listeners bind loopback only (`LoopbackHost.parse`, `SageFs.Core/SageFsConfig.fs`; the error text says "has no authentication"). The MCP listener is configured at `SageFs/McpServer.fs:4374`; the dashboard is a second `WebApplication` in `startDashboardServer` (`SageFs/DaemonMode.fs:1644`) that adds `HttpOriginGuard` and Falco endpoints and nothing else.
- `SageFs.Core/HttpOriginGuard.fs` is the only gate on HTTP. Its own threat model says local processes "are inside the trust boundary" and pass. It defends against browser pages.
- Member tokens: `Capability.fs` (pure reducer, `TokenHash`, `Token.prefix = "sfm_"`, `CapabilityTransport` with `headerName = "X-SageFs-Member-Token"`, `metaKey = "sagefs/memberToken"`, `bridgeEnvVar`, `IdentityPolicy.ConnectionsAllowed | TokenRequired`), `SageFs/CapabilityStore.fs` (in-memory table behind one lock), `SageFs/McpCapability.fs` (mint, revoke, `principalOfHeader`, `presentationFrom`, `presentToken`).
- Where a token is read: `useMemberToken` (`McpServer.fs:833`) turns the header into a principal carrying the token hash claim; the call filter, `createToolListFilter` (`McpServer.fs:781`) and `createReadResourceFilter` read it. Only MCP requests are affected. The doc says so: "The HTTP API and the MCP resources (`sessions://list`, `cohort://status`) do not read the token" (`docs/mcp-tools.md`, "What the route does not stop").
- `IdentityPolicy.TokenRequired` already refuses a token-less MCP caller except status tools and the conductor (`admitTokenless`). The conductor is whichever connection joined first, with `Authority.Conductor`; nothing a client presents proves it.
- `mint_member` forbids an unbound route (`MintRefusal.UnboundNotMintable`), so the conductor's own authority is explicitly not mintable. An operator credential cannot be a member token.
- 64 contracted endpoints in `SageFs.Core/EndpointContracts.fs` (`Endpoint.create` entries; record has `method`, `path`, `description`, `category`). `SageFs.Tests/EndpointContractTests.fs` has a "Route scan" (`RouteScan.routesIn`) that compares the source's `MapGet`, `MapPost` and Falco routes to the table, so a route that is added without being listed already fails a test.
- Callers that would need a credential: the stdio bridge (`SageFs/McpStdioBridge.fs:592` already sends the member header), `SageFs/DaemonClient.fs` and the CLI verbs in `Program.fs`, `sagefs-vscode` (`SageFsClient.fs`, `http-helpers.js` `httpPostHeaders`), `sagefs.nvim` (`lua/sagefs/member_token.lua` already sends the header; `daemon.lua`, `daemon_port.lua`), the hook (`tools/agent-hooks/Hook.fs` posts `/api/lease/request` and `/api/hatch/record`), the dashboard in a browser, and about 30 files under `scripts/` (install, gate, reconnect, demos, lemmings).
- The confirmation refusal: `ConfirmationRefusal.wayOut` and `unattendedRoute` (`McpServer.fs:348` to `:370`) tell a caller with no one to answer to call the same job over HTTP "without asking". A memory note in this repo says the bypass stays until per-call daemon tokens ship, and this item is that.
- SDK: `Directory.Packages.props:78` pins `ModelContextProtocol` and `ModelContextProtocol.AspNetCore` 1.0.0-rc.1. 2.3.0 is out (2026-10-20). A branch `sdk-2-upgrade` in `~/scratch/sdk2` has an uncommitted diff that pins 2.3.0 and sets `opts.SessionMode <- HttpServerSessionMode.Stateful`; it has not been built (the note says it waited for the release gate).
- From the research note `mcp-tool-list-by-harness-2026-10-26.md` (sections 1, 6, 7; its claims about the SDK come from the SDK's own docs and were not run): revision 2026-07-28 has no protocol-level session and moves identity to a credential on each request; SDK 2.x rebuilds `HttpContext.User` on every Streamable HTTP request; a tool can take a `ClaimsPrincipal`; `AddAuthorizationFilters()` makes `[Authorize(Roles = ...)]` apply to tools and removes inaccessible items from list results; `ElicitAsync` throws in stateless mode.

## Design

One principle: a single authentication scheme at the HTTP edge of both listeners that turns a request into a `Principal`, and everything after it (cohort, gates, confirmation) reads the principal. That is the shape SDK 2.x expects (`HttpContext.User` per request), so the work lands once.

```fsharp
[<RequireQualifiedAccess>]
type Presented =
  | Nothing
  | Operator of OperatorHash
  | Member of TokenHash
  | Viewer of ViewerHash
  | Unreadable of reason: string

[<RequireQualifiedAccess>]
type EdgeAccess =                 // one per route, on Endpoint
  | Probe                         // /health, /version, /api/daemon-info: no credential
  | AnyCredential                 // read routes a member or viewer may use
  | MemberRoute of RouteKind      // what a member may reach, held to its Route (Capability.RouteKind)
  | OperatorOnly                  // stop, reset, shutdown, local-data clear, mint

[<RequireQualifiedAccess>]
type EdgeRefusal =
  | NoCredential of route: string
  | CredentialNotAllowed of kind: CredentialKind * route: string
  | TokenRefused of PresentRefusal
```

Header: `Authorization: Bearer <credential>` is the standard header and the one MCP clients already know how to set (Claude Code `headers`, Cursor, Copilot). Prefixes make a leaked value recognizable and select the lookup: `sfo_` operator, `sfm_` member (existing), `sfv_` viewer. The `X-SageFs-Member-Token` header and the `_meta["sagefs/memberToken"]` key stay for the per-call case (`a-token-per-subagent-in-claude-code`); the header is replaced by `Authorization` in one commit with every in-repo client, per the standing rule that the wire contract is ours and gets no compat shim. (See Decisions.)

Pure core, new `SageFs.Core/EdgeCredential.fs` (no ASP.NET): `Presented.ofHeaders`, `EdgeAccess` per route, `decide : EdgePolicy -> Presented -> EdgeAccess -> Result<Principal, EdgeRefusal>`. `EdgePolicy` replaces the meaning of `IdentityPolicy` at the edge: `Open` (today: no credential needed, local callers are trusted) and `Required`. `IdentityPolicy.TokenRequired` keeps its cohort meaning.

Operator credential: 32 random bytes drawn at first start, written to `<data dir>/operator.token` with owner-only permissions (0600 on Unix; an ACL on Windows), next to the daemon info file (`DaemonOwnership.DaemonInfoFile`). Only its SHA-256 is held in memory. It survives a restart because the person's editors and CLI would otherwise break on every daemon update. `sagefs token rotate` replaces it. `SAGEFS_DATA_DIR` isolation already gives each test daemon its own file.

Edge: a real ASP.NET authentication handler (`AddAuthentication().AddScheme<,>`), registered on both `WebApplication`s, producing a `ClaimsPrincipal` with a credential-kind claim and the token-hash claim. It replaces `useMemberToken` and the hand-built `principalOfHeader` identity. It goes before `MapMcp()`. For HTTP routes, an endpoint filter reads `EdgeAccess` from the registry and refuses with a 401/403 JSON body naming the reason and the fix.

MCP side: keep `createToolListFilter` and `ToolClass` as the only role table. Do not annotate 72 tools with `[Authorize(Roles = ...)]`; that would be a second copy of the table that `MemberRouteGateTests` and `CapabilityTests` guard today. Use the SDK only for the principal. Whether `AddAuthorizationFilters()` can read a role derived from our token table and keep a stable order is an open question (below), and if it can, the filter can shrink to one line later.

Operator identity in the cohort: today the conductor is a connection. The operator credential needs a member id. Add `MemberId.Operator` to `MemberTable.MemberId` (`SageFs.Core/MemberTable.fs:13`). Every exhaustive match then breaks the build until it decides, which is the intent: `ConfirmationPolicy.decide` (a session the operator created is the person's own work, so step 4 decides whether an operator-created session still asks), `authorityOfMember`, display. Seven sites match `MemberId.Mcp` today.

Confirmation refusal: `ConfirmationRefusal.describe` takes the caller's `CredentialKind`. `wayOut` is printed only for `Operator` (and for `Open` policy, as today). For a member it says the HTTP route is operator-only and the person must confirm. Dropping the sentence for the default policy is a decision for Will (below).

Dashboard (browser): a page cannot read `operator.token`. `sagefs dashboard` (new CLI verb) opens `http://localhost:37750/dashboard#t=<one-time>`; the page posts the one-time code to `/auth/exchange`, which sets an `HttpOnly; SameSite=Strict` cookie holding a session credential and rejects the code a second time. The same exchange serves viewer links. Staged: MCP port and HTTP API first, dashboard second.

Stateless: in `Required` policy every request carries a credential, so nothing depends on `ctx.Server.SessionId` for identity. The `MemberId.Mcp` path remains for `Open`. This is why the work and the SDK 2.x move are one piece.

Wire and clients: the route table gains `access` per endpoint (`Endpoint.create` takes it), `/auth/exchange` and the 401 shape are new, `apiVersion` moves to 5 (clients must send a credential under `Required`; VS Code requires an exact match, so the extension and plugin are updated in the same stretch). `sagefs-vscode`: read `operator.token` via the data dir it already discovers (`DaemonDiscovery.fs`) and send `Authorization`. `sagefs.nvim`: `member_token.lua` becomes `credential.lua` (operator file first, member token option second); `compat.lua` `api_range` gains 5. Hook: read the file, send `Authorization` on its two posts. `Plugin: done` trailers per commit. No new MCP tool; `mint_member`'s description gains one sentence. `docs/mcp-tools.md` and `skills/sagefs/troubleshooting.md` get a "401 Unauthorized" entry.

Honest limit, which goes in the docs in these words: the operator file is readable by every process running as the same user, so against a hostile same-user process this adds nothing. What it stops is a caller that can reach loopback and cannot read your home directory: a sandboxed agent (bwrap, a container with host networking), another OS user on a shared machine, a forwarded dev-container port. That is the case the confirmation "way out" matters for.

## Tests

RED first, in order:

1. Default tier, `SageFs.Tests/EdgeCredentialTests.fs`: `WHY - a route with no declared access cannot be added`: scans `Endpoint.create` lines and asserts each carries an `EdgeAccess`. Fails to compile today.
2. `WHY - a member token is refused on an operator-only route and the refusal names the operator credential`. Property: for every `EdgeAccess` and every `Presented`, `decide` is total and never yields a `Principal` for `Nothing` under `Required` unless the access is `Probe`.
3. `WHY - the refusal text for a member never contains an unattended route`: renders `ConfirmationRefusal.describe` for each `CredentialKind` and `ConfirmationRefusal` case.
4. Property: a `Viewer` principal is never admitted to any route whose method is POST (a pure function of the table).
5. Simulation `SageFs.Simulation/EdgeCredentialSim.fs`, in the style of `CapabilitySim.fs`: seeded events (`Mint`, `Rotate`, `Present`, `PassMinutes`, `Revoke`, `Exchange` a one-time code twice) folded through the real `decide` and `Capability.decide`. Invariants: a revoked or rotated-out credential is never admitted afterwards; a one-time code is admitted at most once; a viewer is never admitted to an `OperatorOnly` route. Twins, one each: `RotateKeepsOldTwin` (old operator hash still resolves), `ExchangeReusableTwin`, `ViewerFallsToOperatorTwin` (an unknown prefix resolves as operator), `ProbeLeakTwin` (marks every route a probe). Each must fail its invariant or the invariant has no teeth.
6. Integration tier (`--integration-host`), extending `CapabilityHttpIntegrationTests.fs`: the outcome matrix above against a spawned daemon with its own data dir. Each cell asserts status code and the named reason. No cell is skipped; a missing prerequisite fails the tier.
7. Ratchet: a `Ratchet.table` counting `Endpoint.create` entries marked `OperatorOnly` or `AnyCredential` versus `Probe`; `Probe` has a budget that only goes down.
8. Client contract tests: `EndpointContractTests` "Neovim contract" and "VS Code contract" gain the `Authorization` header; the golden `.mjs` harness for `SageFsClient` asserts the header is sent.

Waits: none on wall clocks. Token expiry and lapse run on the injected `now` the reducer already takes. The integration daemon waits on its readiness event as the existing harness does.

## Steps

1. Spike the SDK upgrade on `sdk-2-upgrade` (not this plan's code, but its prerequisite): build with `SessionMode = Stateful`, list compile breaks and `MCP9xxx` warnings, run `--integration-host`. Gate for everything below; if it is red, steps 2 and 3 still proceed on 1.0.0-rc.1 because the pure core does not touch the SDK.
2. RED tests 1 to 4 and `EdgeCredential.fs`; `EdgeAccess` on `Endpoint`. Owns `SageFs.Core/EdgeCredential.fs`, `EndpointContracts.fs`, `EdgeCredentialTests.fs`.
3. Simulation (test 5). Owns `SageFs.Simulation/EdgeCredentialSim.fs` and its registration. Parallel with 2 once the types exist.
4. `MemberId.Operator`: owns `MemberTable.fs` and the exhaustive matches it breaks (`ConfirmationPolicy.fs`, `Affordances.fs`, `McpTools.fs` sites).
5. The operator file and `OperatorStore` (new `SageFs/OperatorStore.fs`): create, read, rotate, permissions test on Unix.
6. The authentication handler and endpoint filter on the MCP listener (replaces `useMemberToken`; owns the `useMemberToken` region of `McpServer.fs`). Integration test 6 for the MCP port.
7. Clients in one commit: bridge, VS Code, nvim (separate repo, in lockstep), hook, `scripts/*` helpers (extract one shared `scripts/DaemonAuth.fs` first so thirty files do not each learn it). File-disjoint per client, so these split across sub-agents.
8. Dashboard listener, the exchange, `sagefs dashboard`. Test 6 for port 37750.
9. Confirmation refusal text per credential (`McpServer.fs` `ConfirmationRefusal`), docs, `apiVersion` 5.
10. Default flip of `EdgePolicy` to `Required`, after a week of dogfooding with `Open` plus a log line counting credential-less calls.

## Measure first

- Does `AddAuthorizationFilters()` in 2.3.0 accept a role computed from our token table, and keep a stable order? Experiment: in the spike branch register it, give a principal a `Role` claim from `ToolClass`, and compare its `tools/list` to `createToolListFilter`'s for each preset. Equal and stable: schedule deleting our filter. Different or reordered: keep ours.
- Does the Claude Code and Cursor and Copilot `headers` setting send `Authorization` on the MCP port after a `401`, and does Claude Code's `headersHelper` re-run on 401 (the MCP docs say it does)? Test with a throwaway server.
- The cost of the handler: per-request hash and a table read. Expected microseconds, but the SSE stream and `/events` are long-lived; confirm the handler runs once per request, not per event.
- Stateful versus hybrid mode for `Required`: with `StatefulForInitializeClients`, does a credential header on the `initialize` request persist its identity on the session, or does each later request need it? The SDK identity doc says the principal is rebuilt per request, so each request must carry it; confirm in the spike.

## Risks and dependencies

- A client left out of step 7 breaks only under `Required`; `Open` stays the default until step 10, which is the rollback.
- Thirty scripts under `scripts/` including the lemmings harness call the daemon; missing one fails a ship. The shared helper is the mitigation.
- The operator file on Windows needs an ACL test I cannot run here.
- Depends on: the SDK 2.x spike (outside this group). Feeds: `tokens-survive-a-restart`, `watch-your-agents-from-your-phone`, `a-token-per-subagent-in-claude-code` (phase 2), `confirmation-gates-in-every-agent-host`, `check-the-scope-before-the-write`, `drive-real-agents-as-members`, and the tool-list profiles in the harness research note, which want the list keyed by what the caller presents.

## Size

L. Ten steps across the daemon, two editors, a hook and thirty scripts; the daemon half alone is M.

## Decisions for Will

- Replace `X-SageFs-Member-Token` with `Authorization: Bearer` in one commit with no shim? Recommendation: yes, matching the standing wire rule, because every client in the repo is ours.
- Persist the operator credential in the data dir? Recommendation: yes. A per-boot secret would break every editor on every daemon update.
- When does the confirmation "way out" go? Recommendation: print it only to an operator now, and flip the default to `Required` only after the dogfood week. For an agent that can read your home directory the prompt stays advisory either way, and the docs should say so.
- `MemberId.Operator` as a new case, or map the operator to an existing id? Recommendation: a new case, so the compiler finds every decision.
