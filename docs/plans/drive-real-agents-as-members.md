# Drive real coding agents as cohort members

Roadmap item: `drive-real-agents-as-members` (Exploring, Agents). Plan written 2026-10-26 against master 59a8692c. Work order added 2026-11-11 against master 8975e898.

## Work order

### 1. Outcome

Today a person who wants two coding agents in one cohort starts each by hand, mints each a token, pastes it into each agent's MCP config, and nothing answers the agents' permission prompts from what they hold. After this item, `sagefs drive --agent <vendor> --role Implementer --task "<text>"` is one process that makes a worktree, mints a token bound to it, starts the agent over the Agent Client Protocol with `sagefs mcp` as its MCP server under that token, answers each `session/request_permission` from the member's grant and claims, and revokes the member when the run ends. `get_cohort_status` shows the agent as `cap:<id>` with its own claims and landings.

Outcome gate: a stub ACP agent (a test process in `SageFs.Tests`, no model, no network) run through the real driver against an isolated daemon, over the matrix {edit inside the claim, edit in scope but unclaimed, edit outside scope, delete, shell command, read outside the checkout, `fs/write_text_file`} x {Implementer, Verifier, Observer} x {turn ends, cancelled, agent dies, driver killed}. Each cell asserts the permission reply the stub received, the cohort ledger afterwards, and that the token is revoked or expired and the worktree reported. Linux only; a real agent per vendor is a manual tier on the desktop. Windows and macOS process handling for the agent subprocess is unproven.

### 2. Where the code is today

Verified by reading on 2026-11-11.

- The roadmap sentence "it has to join the cohort before it mints anything" is stale. `POST /api/members/mint` mints with the operator credential and no seat (`SageFs.Core/EndpointContracts.fs:77`, `SageFs/McpMintRoute.fs`; `operator.token`, `sfo_` prefix, `SageFs.Core/EdgeCredential.fs:9-22`). Its reply is JSON, `McpMintRoute.MintReply` (`:125`), built from `McpCapability.MintedMember` by `replyOf` (`:135`), so the token never goes through prose. The CLI is `sagefs member mint --role R (--dir D | --session ID) [--scope S] [--ttl N] [--print-env VAR]` (`SageFs/MemberMintCommand.fs:16`). The driver mints through this route.
- The MCP `mint_member` reply is still prose with the token on an indented line and still ends `revoke_member member=...` while the parameter is `member_id` (`SageFs/McpCapability.fs:445` against `SageFs/McpTools.fs:3138`). A one-word fix.
- "I could not find the command a person uses to restore a conductor" is stale. `sagefs cohort seat [<member id> [--replace <holder id>]] [--dir D]` (`SageFs/Program.fs:694`, `SageFs/CohortSeatCommand.fs`), the operator-only routes `GET`/`POST /api/cohort/seat` (`EndpointContracts.fs:78-79`), and the MCP tool `seat_conductor` (`docs/mcp-tools.md:698`) fill a `Vacant` seat.
- `revoke_member` is an MCP tool, conductor-only (`McpTools.fs:3138-3152`, `McpCapability.revokeMemberIn`). There is no HTTP revoke route. A driver that only holds the operator credential can mint but cannot revoke; see the DECISION.
- An MCP-over-HTTP client already lives in the product: `SageFs/McpCallCommand.fs` (`clientFor` `:56`, `callTool` `:63`, `converse` `:75`, `run` `:106`) is what `sagefs call` uses (`docs/mcp-tools.md:195`). `AGENTS.md` forbids a hand-rolled MCP client; this one is the house client. The test project also uses the SDK's `HttpClientTransport` with `AdditionalHeaders` (`SageFs.Tests/CapabilityHttpIntegrationTests.fs:36-38`).
- Grants and roles: `Grant` (`SageFs.Core/Capability.fs:619`), `RolePreset` (`:213`), `admitClaim` (`:898`), `RouteBinding` (`:332`), `CheckoutRoot` (`:300`), `ClaimScope.overlaps` (`SageFs.Core/Cohort.fs:175`). Identity policy `TokenRequired` (`Capability.fs:919`).
- Seat states: `ConductorBinding.NeverBound` and `Vacant` (`Cohort.fs:314-319`).
- Worktrees: `Features.CohortGit.addWorktree repoDir worktreePath branch baseRef` (`SageFs/CohortGit.fs:518`, used at `SageFs/McpCohortIntegration.fs:303`); `Checkout.classify` (`SageFs.Core/Checkout.fs:40`); `tidy_workspace`.
- The CLI verb pattern to copy: `SageFs/CohortExportCommand.fs`, `SageFs/CohortSeatCommand.fs` (pure `parseArgs`, `UsageRefusal.describe`, `usage`, dispatched from `Program.fs:845`). `CliCommand` is at `Program.fs:125`.
- JSON: `SageFs.Json` profiles only (`SageFs.Core/Json.fs`); `JsonCentralizationTests` is the ratchet.
- No ACP package is in `Directory.Packages.props`. No `AcpWire.fs`, `AcpDriver.fs` or `AcpDriverCommand.fs` exists.
- Tool list budget: `RatchetBudgets.toolList` is 38,286 characters for the full catalog and 5,395 for the starter list (`SageFs.Tests/RatchetBudgets.fs:462`). This item adds no tool.
- ACP facts in "What ACP specifies" are from the protocol site read 2026-10-26, and the `initialize` capabilities page was not read. Still unverified.

### 3. Steps

1. RED, pure. `SageFs.Tests/AcpWireTests.fs`, list `AcpWire`: "WHY - a frame with an embedded newline is refused" and a round-trip property over the request, response, notification and error shapes. Register `<Compile Include="AcpWireTests.fs" />` in `SageFs.Tests/SageFs.Tests.fsproj` before `Program.fs`. Commit red.
2. GREEN, pure. `SageFs.Core/Features/AcpWire.fs`: the message DU, `encode`/`decode` through a `SageFs.Json` profile, newline framing as a pure function over a string buffer. Add to `SageFs.Core.fsproj` after `Capability.fs`.
3. RED, pure. `SageFs.Tests/AcpDriverTests.fs`, list `AcpDriver`: tests 1 to 5 under Tests (`OutsideCheckout`, `OutsideScope`, `NotClaimed`, allowed under a held claim; the agreement property with `admitClaim`; roles; `Execute` rejected; `allow_always` never chosen). Commit red.
4. GREEN, pure. `SageFs.Core/Features/AcpDriver.fs`: `HeldScope`, `PermissionVerdict`, `RejectReason`, `decide`, as in Design.
5. Measure (needs steps 1 and 2, no product code). A logging stub ACP client as an `.fsx` over `AcpWire` (`scripts/acp-probe.fsx`, thin glue only): advertises `fs` and `terminal`, approves everything, logs every request. Run each installed agent adapter through one edit-and-test task; record per vendor how many writes arrive as `fs/write_text_file` against edits made by the agent's own tools, and whether the `mcpServers` stdio entry with `SAGEFS_MEMBER_TOKEN` in `env` makes `get_cohort_status` show `cap:<id>`. Write the counts into "Measure first". If most writes bypass the client, rewrite the Outcome paragraph: the driver is lifecycle plus claims, and the permission answers are advisory.
6. RED then GREEN, pure. Test 6 and the `session/new` payload builder: the token appears only in the stdio entry's `env` or the HTTP entry's `headers`, never in `args`, logs or the run record (property over generated tokens).
7. Edge. Mint and revoke. The driver mints with `POST /api/members/mint` under `SAGEFS_OPERATOR_TOKEN`. DECISION below decides revoke. If a route is added: `POST /api/members/revoke`, operator-only, in `memberEndpoints`, with the pure half in a new `SageFs/McpRevokeRoute.fs` mirroring `McpMintRoute.fs`. `EndpointContracts.fs` commit carries `Plugin: n/a, an operator route the driver calls`.
8. Edge. The stub agent (`SageFs.Tests/AcpStubAgent.fs`, started as a child of the test process the way `DeltaProgram.fs` is), the run state machine (pure, `AcpDriver.Run`: Started, Prompting, AwaitingPermission, Cancelling, Ended, Crashed), then `SageFs/AcpDriverCommand.fs` and the `Drive of args` case in `CliCommand`. Help text in `Program.fs`.
9. Simulation `SageFs.Simulation/AcpDriverSim.fs` with the invariants and the `allow_always` twin under Tests; registered in both fsproj files.
10. Fix the `member=` hint (`McpCapability.fs:445`). Trailer `Plugin: n/a, a word in a reply`.
11. Outcome gate `SageFs.Tests/AcpDriverOutcomeTests.fs` as an `Integration.hostList`, isolated daemon via `CohortMcpToolsIntegrationTests.startIsolatedDaemonWith [ CapabilityTransport.policyEnvVar, "TokenRequired" ]` with an `operator.token` in its data dir. Then `docs/agents.md`, one subsection per vendor with the adapter and its install line.

DECISION: how the driver revokes. Options: (a) add operator-only `POST /api/members/revoke` (recommended: small, mirrors mint, keeps the driver off the MCP conductor seat); (b) the driver joins the cohort as conductor over MCP and calls `revoke_member` (ties every run to one conductor connection and dies with it); (c) rely on `ttl_minutes` only (a killed agent keeps a live token for up to the TTL).
DECISION: `Execute` default rejected with a per-run opt-in (recommended) or allowed inside the checkout.
DECISION: frame ACP ourselves on `SageFs.Json` (recommended, about six methods) or take an unofficial NuGet package (needs discussion, none is official).
DECISION: whether step 5's counts, if they show agents bypass the client, downgrade this item to "lifecycle and claims" before any edge code is written. Recommendation: yes, and say so in the Outcome.

### 4. How to check it

- Inner loop: a session on `SageFs.Tests`, `send_fsharp_code` the `decide` cases and the codec before writing files.
- `dotnet build SageFs.Tests -c Release`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets` (JSON centralization, tool list, analyzer budgets).
- Filtered: `... --summary --filter-test-list "AcpDriver"` and `--filter-test-list "AcpWire"`.
- Acceptance: `--summary` unfiltered (TRUST `Trusted`), then `--integration-host --summary` for the outcome gate. The real-vendor runs are by hand on the desktop, one adapter at a time, and their counts go in the plan.

### 5. Traps in this repo

- SAGE-005: `decide` takes `HeldScope` and a `PermissionRequest` record, never a path string and a scope string side by side. The payload builder takes a `MemberToken` single-case type, never `string`.
- `SageFs.Json` only: a `JsonSerializerOptions` at a call site fails `JsonCentralizationTests`.
- Child processes in tests go through `SpawnEnvironment.apply` so the agent subprocess inherits a clean environment; the stub agent starts with it.
- No timing: every wait in the gate is on a protocol message (the stub's stop reason, the daemon's ready event). A driver-killed cell asserts token expiry through the simulation's clock, never a real TTL.
- Budgets only go down: `RatchetBudgets.fileSize` has a budget of zero for a file not in its table, and the threshold is 1,500 lines; keep each new file under it.
- `EndpointContracts.fs`, `McpTools.fs` and `McpServer.fs` are on the wire-surface list: every commit that touches them carries a `Plugin:` trailer, and `scripts/ship.fsx` refuses a release without one.
- No new NuGet package without discussion.
- Linux only: the stub agent is a .NET process, so the gate runs anywhere .NET runs, but the real adapters are Node packages that have not been installed on this machine.

### 6. Size and order

Steps 1 to 4: two days. Step 5: one day per installed vendor, and it can change the plan. Steps 6 to 8: three to four days. 9 to 11: three days. Total two to three weeks for one person. Order: 1 to 4, then 5 before any edge code. 1-2 and 3-4 are file-disjoint and can run in parallel. First commit: `test(acp): RED a frame with an embedded newline is refused` with `AcpWireTests.fs` and the fsproj line.

### 7. Not now

- Tokens that survive a daemon restart (`tokens-survive-a-restart`): until then a restart ends every run, and the driver says so.
- Driving across machines (`a-cohort-across-machines`).
- A write boundary for agents that edit with their own tools: that is `check-the-scope-before-the-write`, and the driver's `fs/write_text_file` path should call its `WriteScope.decide` once it exists, not a second one.
- `plan-a-cohort-as-code` (`calls` output): the driver takes a task string first; the plan file comes when that item lands.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | L |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: an edit at `../outside.fs` is rejected `OutsideCheckout`. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

`sagefs drive` is one process that holds the conductor seat and runs coding agents (Claude, Copilot, OpenCode, Pi) as members. For each run it creates a worktree, mints a member token bound to that worktree, starts the agent over the Agent Client Protocol with SageFs configured as its MCP server under that token, and answers the agent's permission requests from what the member actually holds: its role, its scope prefix and its claims. When the run ends, or the driver stops, it revokes the member and reports the worktree and branch for tidying. `get_cohort_status` shows each agent as its own member with its own claims and landings.

The outcome gate is a run of a scripted ACP agent (a stub speaking the protocol, so the gate needs no model and no network) and, as a manual tier, one real agent per vendor. Matrix: {edit inside the claim, edit inside the scope but unclaimed, edit outside the scope, delete, shell command, read outside the checkout, write through the client's `fs/write_text_file`} x {Implementer, Verifier, Observer grant} x {agent ends the turn, agent is cancelled, agent process dies, driver is killed}. Each cell asserts the permission answer the agent received, the ledger afterwards, and that no token or worktree is left behind.

## What ACP specifies (read 2026-10-26 from agentclientprotocol.com; the page I could not read is marked)

- JSON-RPC 2.0. The stdio transport is the implemented one: the client launches the agent as a subprocess and the two exchange UTF-8 messages delimited by newlines, with no embedded newlines. Streamable HTTP is a draft proposal. Custom transports are allowed.
- Agent methods: `initialize`, `authenticate`, `session/new`, `session/prompt`; optional `session/load`, `session/set_mode`; notification `session/cancel`. Client methods: `session/request_permission` (baseline), and optional `fs/read_text_file`, `fs/write_text_file`, the `terminal/*` family and `elicitation/create`, each gated on a capability the client advertises. `session/update` is the agent's notification stream (message chunks, tool calls and updates, plans).
- `session/new` takes an absolute `cwd` and `mcpServers`. A stdio entry is `{ name; command (absolute path); args; env: [{ name; value }] }`, always supported. An HTTP entry is `{ type = "http"; name; url; headers: [{ name; value }] }` and needs the agent's `mcpCapabilities.http`.
- `session/request_permission`: params `sessionId`, `toolCall` (`toolCallId`, `title`, optional `kind` in `read | edit | delete | move | search | execute | think | fetch | switch_mode | other`, `locations: [{ path; line? }]`, `rawInput`), and `options: [{ optionId; name; kind }]` with kinds `allow_once | allow_always | reject_once | reject_always`. The reply is `{ outcome: "selected", optionId }` or `{ outcome: "cancelled" }`, and a cancelled prompt turn must be answered `cancelled`. The `name` field on a tool call is informational and grants no authority.
- Adapters: Claude through `claude-agent-acp` (Zed's, not Anthropic's), Codex through `codex-acp`, Pi through `pi-acp` (svkozak, third party). Copilot CLI, OpenCode and Gemini CLI are listed as native; Copilot's ACP support is a public preview.
- .NET: I found only unofficial SDKs (`AgentClientProtocol` by nuskey8 0.1.5 on net8/net9, `LibAcp` 0.1.0, `AcpSdk` 1.2.0 on StreamJsonRpc). No official one.
- I could not read the pages for `initialize`'s `clientCapabilities` or authentication, and I did not read any reference implementation's source. The permission semantics when an agent keeps its own tools (a built-in shell, its own file tools) beyond what it asks the client to do are not stated on the pages I read. That gap decides the design and is the first measurement.

## What exists today

- Tokens: `mint_member role scope ttl_minutes working_directory | session_id` is conductor-only, shows the token once, seats the member (`SageFs/McpCapability.fs`, `Capability.fs`). A grant is `{ Preset; Scope; Route; NotAfter }`; `RolePreset` has Observer, Analysis, Verifier, Implementer. `revoke_member member_id=cap:<id>` departs the seat and orphans its claims.
- Presenting: `X-SageFs-Member-Token` header per connection, `_meta["sagefs/memberToken"]` per call, and `SAGEFS_MEMBER_TOKEN` read by the stdio bridge `sagefs mcp` (`Capability.CapabilityTransport`). The stdio bridge is the route `AGENTS.md` prefers, because it owns the handshake.
- A client library already in the repo: the MCP SDK's `McpClient` with `HttpClientTransport` and `AdditionalHeaders`, used in `SageFs.Tests/CapabilityHttpIntegrationTests.fs`. `AGENTS.md` forbids hand-rolling an MCP client over HTTP.
- The first MCP mint needs a seat. A connection that has not joined is `Anonymous`, and `Capability.decide` refuses a mint by anyone but the conductor. The first joiner of a repository's cohort is its conductor (`Cohort.decide`'s `Join` from `NeverBound`). Since 2026-11-02 the operator credential mints without a seat through `POST /api/members/mint` and `sagefs member mint` (`SageFs/McpMintRoute.fs`, `SageFs/MemberMintCommand.fs`), and that is the route the driver takes.
- Conductor loss: a `Vacant` seat is never refilled by a join. Since the plan was written, `sagefs cohort seat <member id>` (`SageFs/CohortSeatCommand.fs`), the operator-only `POST /api/cohort/seat` and the MCP tool `seat_conductor` fill it (seen 2026-11-11). Tokens still die with the daemon (`Capability` table is in memory).
- Scope is policy: `Capability.fs`'s header says a prefix "refuses claims, an eval routed by the daemon and a landing outside it", and "does not stop a process from" writing the files. `check-the-scope-before-the-write` (roadmap, Exploring) is the item about making it a boundary.
- The MCP `mint_member` reply is prose with the token on an indented line after a `TOKEN (shown once...)` heading, and it ends "Cut one off with revoke_member member=cap:...", while the tool's parameter is `member_id`. The text and the parameter disagree (`McpCapability.fs:445` against `McpTools.fs:3138`, seen 2026-11-11). The HTTP mint route already answers with a JSON `MintReply` (`McpMintRoute.fs:125`).
- Worktrees: `Checkout.classify` and the worktree routing boundary, `tidy_workspace`, and the rule in `AGENTS.md` that a sub-agent brief reports its worktree path and branch.
- `docs/agents.md` covers Claude Code and a paragraph for "other agents"; nothing drives an agent.

## Design

Vertical slice: pure core in `SageFs.Core/Features/AcpDriver.fs`, edge in `SageFs/AcpDriverCommand.fs` (the `sagefs drive` verb, built like `CohortExportCommand`: pure `parseArgs`, every failure a DU case with one `describe`).

```fsharp
type HeldScope = { Grant: Grant; HeldClaims: ClaimScope list; Checkout: CheckoutRoot }

type AcpToolKind = Read | Edit | Delete | Move | Search | Execute | Think | Fetch | SwitchMode | Other

type PermissionVerdict =
  | Allow of ToolRisk                       // picks the allow_once option
  | Reject of RejectReason                  // picks reject_once; the reason is kept for the run record
  | Cancelled                               // the prompt turn was cancelled

type RejectReason =
  | OutsideCheckout of path: string
  | OutsideScope of path: string * ScopePrefix
  | NotClaimed of path: string
  | RoleLacks of AcpToolKind * RolePreset
  | NeedsAShell of command: string          // execute: refused unless the run says otherwise
  | UnknownKind

AcpDriver.decide : HeldScope -> PermissionRequest -> PermissionVerdict    // total, pure
```

The mapping uses `Capability.admitClaim` for the path arithmetic and checks the path against the held claims with `ClaimScope.overlaps`, so the driver never has a second idea of "inside". A path comes from `locations`; a request with an `edit`, `delete` or `move` kind and no location is rejected (`UnknownKind` for the shape, never a guess from `title`). `allow_always` and `reject_always` options are never chosen: the driver answers each request on its own merits, because a remembered allow outlives a revoked grant.

The driver's own flow, as a state machine in the pure core and effects at the edge:

1. Join the cohort as conductor through `McpClient` first, then mint. Under `TokenRequired` the conductor may act without a token.
2. Per run: `git worktree add` (through `CohortGit.addWorktree`), `mint_member` with `working_directory` set to it, parse the token, start the agent.
3. `session/new` with `cwd` the worktree and one `mcpServers` entry. Preferred: the stdio entry `{ command = <absolute path of sagefs>; args = ["mcp"]; env = [SAGEFS_MEMBER_TOKEN = token] }`, which keeps the token out of tool arguments and lets the bridge own the handshake. The HTTP entry with the header is the fallback for agents without stdio MCP.
4. `session/prompt` with the task text; answer `session/request_permission` through `decide`; forward `session/cancel` on a stop signal.
5. On end, cancellation, agent exit or driver exit: `revoke_member`, record the worktree and branch in the run record, and leave removal to `tidy_workspace`. A killed driver cannot revoke, so every token has a short `ttl_minutes`, and tokens lapse unused after the lease window anyway.

If the client advertises `fs` and `terminal` capabilities, the driver implements `fs/write_text_file` with the same `decide` before it writes, and runs `terminal/create` only inside the checkout. That turns the permission answer into a real write boundary for every agent that routes its writes through the client. An agent that edits through its own tools is covered only by its permission requests, and by the claims and landing checks; say so in the docs.

Dependencies and wire: no new tool. Preferred JSON-RPC is a small newline-delimited framing in the driver using `SageFs.Json` profiles, since the protocol surface needed is about six methods; the alternative is an unofficial NuGet SDK, which `AGENTS.md` says needs discussion first. One wire request: make `mint_member` return a structured block (member id, token) beside the prose so the driver does not parse an indented line, and fix the `member=` hint to `member_id=`. That touches `McpCapability.fs`, so it carries a `Plugin:` trailer, and `apiVersion` moves only if the prose reply changes shape. Token cost: zero new tools; the structured block is a few tokens in one reply.

## Tests

RED first, `SageFs.Tests/AcpDriverTests.fs` (default suite):

1. `decide` rejects an `Edit` at `../outside.fs` with `OutsideCheckout`, an `Edit` inside the checkout but outside the prefix with `OutsideScope`, an `Edit` inside the prefix with no claim over it with `NotClaimed`, and allows one under a `Held` claim.
2. Property: for every generated grant, claim set and path, `decide` allows exactly when `Capability.admitClaim` is `Ok` and some held claim overlaps. The driver and the cohort cannot disagree.
3. A Verifier or Observer grant is rejected for `Edit`, `Delete`, `Move`; `Read` and `Search` inside the checkout are allowed for every role.
4. `Execute` is rejected by default with `NeedsAShell`.
5. `allow_always` is never selected, whatever options the agent offers (property over option lists).
6. The `session/new` payload builder puts the token only in `env` of the stdio entry (or only in `headers` of the HTTP entry) and in no `args`, no log line and no run record (property over generated tokens).
7. A frame codec round-trips every message shape the driver sends and reads, and a line with an embedded newline is refused.

Simulation, `SageFs.Simulation/AcpDriverSim.fs`: the run lifecycle as a state machine (Started, Prompting, AwaitingPermission, Cancelling, Ended, Crashed) with seeded events (permission requests, agent crash, driver kill, token expiry, daemon restart) and `FaultInjection` for duplicated and dropped messages. Invariants: every minted token is revoked or expired by the end of the schedule; a `cancelled` turn answers every pending permission request `cancelled`; no `Allow` is issued after the grant expires; no worktree is unreported. Twin: a driver that selects `allow_always`, caught by a revoked-then-reused case.

Outcome gate: `SageFs.Tests/AcpDriverOutcomeTests.fs`, `--integration-host` tier, with a stub ACP agent (a test process in the same project) that performs scripted tool calls, against a real daemon. It asserts, per matrix cell, the permission answer, the cohort status, the claims, and that the worktree and token are accounted for. Every wait is on the protocol: the stub's turn ends with a stop reason, and the test waits for that response.

Manual tier: one real agent per vendor against a throwaway repo, run on the desktop, checked in as a recorded ledger (`cohort-export`) under `SageFs.Tests/cohorts/` only if anonymised.

## Steps

1. Tests 1 to 5 and `AcpDriver.decide`. New file and test file only.
2. Frame codec and message types (`AcpWire.fs`), test 7. Parallel with step 1 (disjoint files).
3. Measurement 1 below, with a logging stub client. No product code.
4. Test 6 and the `session/new` builder.
5. The stub agent, the run state machine, simulation, then the edge: `AcpDriverCommand.fs`, `Program.fs` verb, help text.
6. Structured mint reply and the hint fix (`McpCapability.fs`, `McpTools.fs`), with a `Plugin:` trailer.
7. Outcome gate, then `docs/agents.md` section per vendor with the adapter that each needs.

## Measure first

1. Where do real agents write? Write a stub ACP client that advertises `fs.readTextFile`, `fs.writeTextFile` and `terminal`, logs every request it receives, and approves everything. Run each of Claude (through `claude-agent-acp`), Copilot, OpenCode and Pi through one task: edit a file, run the tests. Count writes that arrive as `fs/write_text_file` against edits the agent made itself, and shell commands that arrive as `terminal/create` against its own. If most go through the client, the driver is a real write boundary and the plan stands. If most bypass the client, the driver's permission answers are advisory and the value is the lifecycle (token, worktree, revoke) and the claims, which is still worth building but should be described that way. Record the counts per vendor.
2. Does each agent accept the `mcpServers` entry, and does it pass the token through? Start each with the stdio entry and call `get_cohort_status` from inside; confirm the member id the cohort shows is `cap:<id>` and not the driver's. Note which need `mcpCapabilities.http`.
3. Do permission requests arrive for the actions that matter? Count requests per task per vendor. An agent configured to auto-approve inside its own settings never asks, and the driver sees nothing; the run needs the agent's own settings pinned for the run (a config file in the worktree), which is vendor-specific work.
4. Token hygiene: grep each agent's session log directory for the token after a run. Anything found is a leak the docs must name.

## Risks and dependencies

- The conductor seat. A driver that dies and a daemon that restarts leaves a vacant seat, and no tool fills it. Long-lived driving needs a "restore the conductor" verb, which belongs to the conductor-seat work and not to this plan; until it exists the driver documents a daemon restart as ending a run, and refuses to start against a vacant seat with the reason.
- Tokens do not survive a daemon restart, so a restart ends every run. `tokens-survive-a-restart` would change that.
- Vendor drift: ACP is young, Copilot's support is a public preview, and two of the adapters are third-party. The pure `decide` is vendor-free; adapters sit at the edge and each gets its own manual-tier note.
- Depends on `plan-a-cohort-as-code` (its `calls` output is what the driver executes per member), `check-the-scope-before-the-write` (same boundary question), and `each-agent-its-own-member`/`tokens-bound-to-a-session` (built or planned; the driver relies on them). `a-cohort-across-machines` would let a driver run remotely.

## Size

L. The pure decision is S; the protocol framing, the lifecycle simulation, the stub agent and four vendor adapters are where the time goes, and measurement 1 can shrink or grow it.

## Decisions for Will

1. Frame ACP ourselves with `SageFs.Json` (about six methods) or take an unofficial NuGet SDK? Recommendation: frame it ourselves, to avoid a new dependency on a 0.1.x package with no official owner.
2. Default `Execute` to rejected? Recommendation: yes, with an explicit per-run opt-in, because a shell cannot be confined by a path prefix.
3. Make `mint_member` return a structured block? Recommendation: yes. Parsing prose for a secret is how a token ends up in a log.
4. Is the missing "restore the conductor" verb in scope for this item or its own item? Recommendation: its own item, scheduled before this one goes past the stub-agent tier.
