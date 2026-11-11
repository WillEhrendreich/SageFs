# Confirmation gates in every agent host

Roadmap item: `confirmation-gates-in-every-agent-host` (Exploring, Agents). Plan written 2026-10-26 against master 59a8692c. Work order added 2026-11-11 against master 8975e898.

## Work order

This item shrank on 2026-11-07. The confirm tier is one tool, `manage_local_data`, and it is conductor-only. Everything in the original plan about six tools, session ownership and a `{ tool, sessionId, caller }` preview route describes code that no longer exists. The work order below is the re-scoped item. Its first step is a measurement that can end it.

### 1. Outcome

Today the daemon asks a person before `manage_local_data` runs, through MCP elicitation (`McpServer.fs:414-452`). A client that never advertised elicitation is `ClientCannotAsk` and the call runs with no prompt. After this item, on a host that can prompt from a hook (Claude Code `PreToolUse` with `permissionDecision: "ask"`, Cursor `beforeMCPExecution`, Copilot CLI `preToolUse`), the person is asked there instead, with the daemon's own loss sentence, and never asked twice. On a host with no way to ask, the docs say so by name.

Outcome gate: for each installed host, a scripted non-interactive run that calls `mcp__sagefs__manage_local_data` against an isolated daemon, read from files after the process exits: the host's approval record or the hook's deny line is present, and the daemon's local stores are unchanged. A second run calling a non-tier tool asserts nothing was asked. Linux only. None of the hosts has been run yet, so every host cell is unproven.

### 2. Where the code is today

Verified by reading on 2026-11-11.

- `ConfirmationPolicy.decide : ToolName -> Confirmation` (`SageFs.Core/ConfirmationPolicy.fs:25`), with `Confirmation = NotRequired | Required of loss` (`:18`). It reads only `Affordances.confirmationLosses` (`SageFs.Core/Affordances.fs:1321`), which holds one entry, `ToolName.ManageLocalData`. `singleSessionConfirmTools`, `SessionCreator` and `SessionActivity` are gone from the tree (grep).
- `destructiveTools` (`Affordances.fs:1242`) is information only, published as `destructiveHint` on `tools/list`. The comment above `confirmationLosses` (`:1276-1320`) is the record of why each destructive tool is out of the tier.
- The prompt path: `ConfirmationRefusal` (`SageFs/McpServer.fs:309`), `wayOut` (`:339`), `confirmationStep` (`:414`, `ClientCannotAsk` when `caps.Elicitation` is null at `:421`), `confirmWithin` (`:442`), called at `:486` with `SageFs.Timeouts.confirmationAnswer`. `unattendedRoute` no longer exists: the unattended path is `sagefs data clear` over the operator-only `POST /api/local-data/clear` (`SageFs/LocalDataCommand.fs`).
- `SageFs.Simulation/ConfirmationGateSim.fs` (registered at `SageFs.Simulation.fsproj:186`) and `SageFs.Tests/ConfirmationGateSimTests.fs` (`SageFs.Tests.fsproj:356`) already fold creates, person-creates, stops, restarts and calls over the real `decide` and assert that only `manage_local_data` asks. `ConfirmationPolicyTests.fs` (`:108`) covers the pure function.
- The hook project `tools/agent-hooks/` is Claude Code only and Bash only (`Hook.fs:122`). `HostAdapter` does not exist; `repl-guard-for-other-agents` (Not started) would add it.
- Host facts in the table below are from docs read 2026-10-26, summarised by a small model. Not one has been confirmed by a run.
- Operator routes and `EdgeAccess` exist (`SageFs.Core/EdgeCredential.fs:106`), so a new route can be operator-only from day one.

### 3. Steps

1. Measure (half a day, no product code). For each host installed here: start an isolated daemon with debug logging, connect the host, call `manage_local_data`, and read (a) whether the daemon saw `Elicitation` on the client's capabilities (add one debug log line at `McpServer.fs:421` if there is none; that is the only code this step may touch), (b) whether a prompt appeared, (c) whether the host prompts for MCP tools on its own (Claude Code asks before an MCP tool call unless the tool is on its allow list). Write a four-column table into "Measure first": host, advertises elicitation, host-native MCP prompt, hook can ask. If every installed host either elicits or prompts natively, go to the DECISION and recommend Stopped.
2. DECISION (Will): with one conductor-only tool in the tier, is a per-host shell worth building? Options: (a) `Stopped: the server prompt and the hosts' own MCP tool prompts cover the one tool` and the item stays on the roadmap with that sentence; (b) build the shell as a 30-line extension of the repl-guard hook once `HostAdapter` lands; (c) build it now inside `Hook.fs` for Claude Code only. Recommendation: (a) unless step 1 finds an installed host with no prompt of any kind, then (b), because the capture harness that item builds is the same one this item needs.
3. If (b) or (c). RED, pure. `SageFs.Tests/HostPromptTests.fs`, list `HostPrompt`: "WHY - every tool in confirmationLosses is HostMustAsk for a host that cannot elicit and ServerWillAsk for one that can; every other tool is NotInTier", a property over `ToolName.all` and both `HostCanElicit` values. Register in `SageFs.Tests.fsproj` next to `ConfirmationPolicyTests.fs` (line 108). Commit red.
4. GREEN, pure. `SageFs.Core/HostPrompt.fs` with `HostCanElicit`, `PromptDuty` (drop `HostMustAskUnlessOwn`: no own-session exemption exists), `duty`. It depends on `Affordances` only, so it can be linked into the hook project. Add after `ConfirmationPolicy.fs` (`SageFs.Core.fsproj:505`).
5. Edge. The shell reads the tier from the daemon so it never holds the name: `GET /api/confirmation/tier` answering `[{ tool, loss }]` from `confirmationLosses`, open access, in a new `confirmationEndpoints` list in `EndpointContracts.fs` (trailer `Plugin: n/a, a route for agent hooks`). The hook calls it with its 800 ms limit for a `mcp__sagefs__*` tool name only, and emits `ask` with the loss sentence when the tool is listed; daemon down means `ask`. Fixture tests from step 1's recorded stdin JSON.
6. Edge. `sagefs hooks install --host claude --ask` prints a `permissions.ask` entry per tier tool; a test compares the output with `confirmationLosses` byte for byte.
7. Docs: `docs/agents.md` gets the host table from step 1 and the sentence per host that cannot ask. `docs/mcp-tools.md:106-116` (the elicitation paragraphs) gets one line pointing at the hook.
8. Extend `ConfirmationGateSim` with a `Hook` caller kind and the `HookHoldsItsOwnListTwin` (a shell with a hard-coded tool name misses a tool added to the tier). Outcome gate per host as an `Integration.hostList` case over the recorded fixtures; the real-host runs are by hand.

### 4. How to check it

- Inner loop: a session on `SageFs.Tests`; `send_fsharp_code` the `duty` property before writing the file.
- `dotnet build SageFs.Tests -c Release`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`.
- Filtered: `... --summary --filter-test-list "HostPrompt"`; `--filter-test-list "ConfirmationGateSim"`.
- Acceptance: unfiltered `--summary` (TRUST `Trusted`), then `--integration-host --summary`.
- Hook: `dotnet fsi tools/agent-hooks/publish.fsx`, then pipe a recorded `PreToolUse` JSON for `mcp__sagefs__manage_local_data` into the binary with the daemon up and with it down.

### 5. Traps in this repo

- Budgets only go down. A tool added to `confirmationLosses` must also be in `destructiveTools` (`McpToolGateTests`).
- `EndpointContracts.fs` is on the wire-surface list; the route commit carries a `Plugin:` trailer.
- SAGE-005: the route handler and `duty` take `ToolName`, never a `string` tool name beside another string.
- No timing-sensitive tests: the daemon-down case is a fake reader, never a timeout. The host's own hook timeout is a config value the installer writes, not something a test waits for.
- Asking twice trains people to click through: a host that elicits or prompts natively must be `ServerWillAsk`, and step 1 decides that per host from a run, never from docs.
- Commit `.fsproj` files whole.
- Linux only.

### 6. Size and order

Step 1: half a day. Steps 3 to 8 if built: three to four days for Claude Code; one day per further host, each needing a real run. Step 1 and the DECISION first; nothing else starts before them. First commit: either the "Stopped" status line in "Resume here" with the measured table, or `test(host-prompt): RED every tier tool is HostMustAsk where the host cannot elicit` with `HostPromptTests.fs` and the fsproj line.

### 7. Not now

- Prompts for the 17 destructive tools outside the tier: Will chose on 2026-11-07 that they run unasked; a generated `permissions.ask` list for them is at most an opt-in flag.
- Caller identity from a hook (own-session exemption): the exemption no longer exists in the policy, so there is nothing to carry.
- SDK 2.x stateless mode for HTTP (`ask-before-a-destructive-tool-runs`): if elicitation stops working over Streamable HTTP, this item's shells become the only prompt, and the DECISION above should be revisited then.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: each destructive tool is `HostMustAsk` or `ServerWillAsk`. Then take the steps below in order. |
| Decisions made | 2026-11-07, Will: stopping and resetting sessions are normal everyday MCP use and never prompt, for any session. The server's confirm tier is `manage_local_data` alone (`Affordances.confirmationLosses`), and the self-created-session skip (`singleSessionConfirmTools`, `SessionCreator`, `SessionActivity`, `SessionEvaluators`) is deleted. Everything below that lists six tools, a per-session `decide`, or a preview route over `{ tool, sessionId, caller }` describes the code before that change. Re-scope this item to the one tool that asks before building any of it (the other open ones are under "Decisions for Will"). |
| Surprises | none yet |

## What done looks like

In every host where a person can be asked, `stop_session`, `reset_fsi_session`, `hard_reset_fsi_session`, `switch_workflow`, `manage_local_data` and `stop_app` stop and ask before they run, with the sentence the server already writes ("stop_session will take away the REPL definitions loaded in this session. Allow it?"), and a session the calling agent made itself still runs without a prompt where the host can say who is calling. Where the server can already ask (a client that advertises MCP elicitation) the person is asked once, not twice. Where a host has no way to ask, the plan says so for that host and the gate does not pretend. The outcome gate runs each installed host for real over the matrix: host by tier tool (six) by session ownership (agent's own, someone else's, unknown) by daemon reachability (up, down), asserting the host's prompt appeared or the call was refused, and for a non-tier tool that nothing was asked.

## What exists today

- The server side is built. `Affordances.destructiveTools` (`SageFs.Core/Affordances.fs:1242`, seen 2026-11-11) is 18 tools published as MCP `destructiveHint: true` on `tools/list` (`applyDestructiveHints`, `McpServer.fs`). `Affordances.confirmationLosses` (`:1321`) is the confirm tier, one tool since 2026-11-07, with the words for what it takes away. `SageFs.Core/ConfirmationPolicy.fs` is the pure `decide : ToolName -> Confirmation` (`Confirmation = NotRequired | Required of loss`, `:18-25`). The six-tool tier, `singleSessionConfirmTools` and the per-session `decide` this paragraph used to describe are gone.
- The prompt path is `confirmationStep` and `confirmWithin` (`McpServer.fs:414`, `:442`): `NotInTier | ClientCannotAsk | Ask`. A client that never advertised elicitation is `ClientCannotAsk` and the call runs, because "the four gates ahead of this one already ran". `ConfirmationRefusal` (`:309`) has typed cases and `wayOut` (`:339`) names `sagefs data clear`, which goes over the operator-only `POST /api/local-data/clear`; `unattendedRoute` no longer exists.
- So a host that does not elicit gets no prompt at all. The roadmap names that gap. I could not find, in the repo or the roadmap, which four hosts the item means. `docs/agents.md` names Claude Code, Codex, Copilot, Cursor and OpenCode.
- The hook project `tools/agent-hooks/` has the pure/edge split (`ReplGuard.fs`, `Verdict.fs`, `Hook.fs`) and links a pure Core file by `<Compile Include=... Link=...>` (`HatchUse.fs`). `repl-guard-for-other-agents` (this group) plans a `HostAdapter` layer there.
- Host facts, from each host's docs fetched 2026-10-26 (summarised by a small model, so confirm by running, which is Step 1):

| Host | Can a hook or rule ask before an MCP tool call? | Notes |
|:--|:--|:--|
| Claude Code | Yes, twice over. `permissions.ask` rules take full tool names (`mcp__sagefs__stop_session`) and `mcp__sagefs__*` style globs; `PreToolUse` can return `permissionDecision: "ask"`; matcher `mcp__sagefs__.*`. The docs say a matching ask rule prompts even when a hook returned allow, and an ask rule on Bash still prompts in auto mode. | Claude Code also advertises elicitation, I believe; not verified in this session. If so the server's own prompt already covers the six, and a client-side rule on the same six would ask twice. |
| Cursor | `beforeMCPExecution` takes `allow`, `deny` or `ask` and receives `tool_name`, `tool_input`, `mcp_server_name`, and the server URL or command. | `preToolUse` accepts `ask` in its schema but "is not enforced today". |
| Copilot CLI | `preToolUse` returns `allow`, `deny` or `ask` (`permissionDecisionReason` required for deny). | In the cloud agent and in CLI autopilot `ask` becomes `deny`. Whether MCP calls trigger `preToolUse` is not stated on the page. |
| Codex CLI | `PreToolUse` and `PermissionRequest` both cover "MCP tools" (matcher `mcp__server__.*`). | `ask` is parsed but unsupported and the tool call continues, so a gate can deny or stay silent. `PermissionRequest` returns allow or deny only, and with no matching hook "the normal approval prompt continues". |
| OpenCode | A plugin's `tool.execute.before` can throw to block. | The page does not say whether it fires for MCP tools and documents no ask; `permission.asked` and `permission.replied` exist as events. |

- Not verified anywhere: which hosts act on `destructiveHint` themselves, which advertise elicitation, and how any of them behave for a sub-agent's call.

## Design

The policy stays in the daemon and the shells stay thin. A shell never holds the destructive list.

Pure policy, new `SageFs.Core/HostPrompt.fs` (no dependency past FSharp.Core and `Affordances`, linkable into the hook project like `HatchUse.fs`):

```fsharp
[<RequireQualifiedAccess>]
type HostCanElicit =          // what the host's own server-side prompt can reach
  | Yes
  | No

[<RequireQualifiedAccess>]
type PromptDuty =
  | NotInTier                           // no opinion, the shell stays silent
  | ServerWillAsk                       // the server's elicitation covers it, do not double-prompt
  | HostMustAsk of message: string      // the sentence from confirmationLosses
  | HostMustAskUnlessOwn of message: string * ConfirmationReason   // daemon decides

let duty (canElicit: HostCanElicit) (tool: ToolName) : PromptDuty
```

`duty` is one exhaustive function over `ToolName`, built from `confirmationLosses` and `singleSessionConfirmTools`, so a tool that joins the confirm tier changes every shell with no edit to a shell. The own-session exemption needs facts only the daemon has (who made the session), so a shell asks the daemon:

`POST /api/confirmation/preview` with `{ tool, sessionId?, caller? }` returns `{ confirmation: "required" | "notRequired", reason, message }`, computed by `ConfirmationPolicy.decide` with the same `sessionOwners` lookup the MCP path uses. A new HTTP route in `EndpointContracts.fs` (`apiVersion` stays 4 until the credential item moves it; an added route breaks no client) and `McpServer.fs`. The shell's whole job: map the host's event to a tool name and arguments, call the route with an 800 ms limit (the existing hook's limit), turn the reply into the host's ask or deny, and exit.

Caller identity is the hard part. A hook runs outside the MCP connection, so it cannot name the MCP connection that made a session. With a member token in its environment (`SAGEFS_MEMBER_TOKEN`) it can send the token, and the daemon can compare with `SessionCreator.Recorded (MemberId.Capability _)`. Without one the caller is `CallerNotBound` and the policy answers `Required`: the shell asks. That is the safe direction, and it means connection-identity agents get a prompt for their own sessions on hosts where the shell is the only gate. The server-side prompt still has the exemption for the connection case. This asymmetry goes in the docs.

Fail closed: if the daemon does not answer, the shell asks (for a tier tool), because a tool that would also need the daemon will fail anyway and asking costs one click. For a non-tier tool the shell never calls the daemon and exits at once; that keeps latency off the other 66 tools.

Host adapters (reusing `HostAdapter` from `repl-guard-for-other-agents`, extended with a tool-call parse):

| Host | Shell | Ask form |
|:--|:--|:--|
| Claude Code | none needed when the server elicits; otherwise a `PreToolUse` hook, matcher `mcp__sagefs__.*`, or generated `permissions.ask` entries | `ask` |
| Cursor | `beforeMCPExecution` hook | `permission: ask` |
| Copilot CLI | `preToolUse` hook | `ask` (denied in cloud and autopilot, which is correct: no one to ask) |
| Codex | `PermissionRequest` hook for the six: silent so Codex's own prompt continues, or `deny` with the message when its approval policy is `never` | no ask exists |
| OpenCode | plugin shell | throw with the message, no prompt unless Step 1 finds a permission hook |

The generated-config path for Claude Code is the thinnest shell there is: `sagefs hooks install --host claude --ask` prints a `permissions.ask` array built from `confirmationLosses`. It is regenerated by the same command whenever the tier changes, and a test compares the generator's output with the tier. Whether to use it or the server's elicitation for Claude Code is decided by the measurement below.

Double prompt avoidance: `duty` takes `HostCanElicit`. For a host that advertises elicitation the six are `ServerWillAsk` and the shell is silent. The host-side value is configured per host in the installer and recorded in Step 1.

Wire and client impact: one new HTTP route. `sagefs.nvim` and `sagefs-vscode` do not call it (`Plugin: n/a, a route the agent hooks call`). The `destructiveTools` list and the elicitation text are unchanged. No MCP tool. No catalog cost.

Persistence: none.

## Tests

RED first.

1. `SageFs.Tests/HostPromptTests.fs` (new): `WHY - every tool in confirmationLosses is HostMustAsk for a host that cannot elicit, and ServerWillAsk for one that can`. A property over `ToolName.all` and both `HostCanElicit` values; a tool outside the tier is always `NotInTier`. Fails to compile today.
2. `WHY - adding a tool to confirmationLosses changes every host shell's behaviour with no shell edit`: build a modified loss map, run `duty`, expect the new tool asks. The twin is a shell that carries its own copy of the six names; the test fails against the twin.
3. `WHY - the preview route answers exactly what ConfirmationPolicy.decide answers`: a property over tool, target, creator and caller, comparing the route handler's pure core to `decide`. Includes the unbound caller (always required).
4. Fixture tests per host: recorded `tool_name`/`tool_input` JSON from Step 1 parse to the right `ToolName` (host-qualified names like `mcp__sagefs__stop_session` map through one `unqualify` function with a property test against `ToolName.toToolName`).
5. Fail-closed tests: the daemon down yields the host's ask for a tier tool and silence for others; a malformed daemon reply yields ask.
6. Generated Claude `permissions.ask` equals the set of `mcp__sagefs__<tool>` for the tier, byte-for-byte.
7. Simulation: `SageFs.Simulation/ConfirmationGateSim.fs` (with `SageFs.Tests/ConfirmationGateSimTests.fs`) already folds seeded `Creates`, `PersonCreates`, `Stops`, `Restarts` and `Calls` over the real `ConfirmationPolicy.decide` and asks "was a person asked". Extend it with a caller kind `Hook` (a shell asking the preview route, with or without a token) and an op `PreviewThenCall`, so the invariant covers the hook path: the preview route's answer equals the answer the MCP gate would give for the same caller identity, and an unbound hook caller is always asked. Add a twin `HookExemptsEveryCallerTwin` (the preview route answers `NotRequired` for any recorded creator regardless of caller). The restart op already models `sessionOwners` being forgotten, which is the case that matters here.
8. Outcome gate in the local `--integration-hosts` tier from `repl-guard-for-other-agents`: for each installed host, scripted non-interactive runs that attempt each tier tool against a test daemon. A run's prompt shows up as the host's recorded approval event or as a refusal in the transcript file; absence of both is a failure. Hosts whose headless mode auto-denies `ask` (Copilot autopilot, cloud) are recorded as "denied" and that is the expected cell for them. Missing host binary fails the tier.

No wall-clock waits: hosts run to completion; the daemon's readiness event gates the start.

## Steps

1. Reuse the capture harness from `repl-guard-for-other-agents` Step 1, adding MCP tool calls: record the hook JSON for `mcp__sagefs__stop_session` in each host, whether the host advertises elicitation to the daemon (read it from the daemon's `ClientCapabilities` log line), and whether it honours `destructiveHint`. Fill the table in this plan with facts.
2. Test 1 and `HostPrompt.fs`. Owns `SageFs.Core/HostPrompt.fs`, `HostPromptTests.fs`, `SageFs.Core.fsproj`.
3. The preview route and tests 3 and 5 (daemon half). Owns the route region in `McpServer.fs`, `EndpointContracts.fs`.
4. First shell: whichever host Step 1 shows has no server-side prompt and a hook that asks (Copilot CLI or Cursor), through `HostAdapter`. Owns the host's adapter branch and its fixtures.
5. Remaining shells, one per commit, file-disjoint (one adapter file per host).
6. `sagefs hooks install --host <h> --ask`, the Claude generator and test 6, docs in `docs/agents.md` and `docs/mcp-tools.md`.
7. Test 7 and the outcome tier cells.

Steps 2 and 3 are parallel after the types exist; 4 onward need 1 to 3.

## Measure first

- Does each host advertise elicitation, and does it show the server's prompt? For Claude Code the server's own prompt may already cover everything; the plan's value for it is only the generated ask rules for the 12 destructive tools outside the tier, which the owner chose not to ask about. If Claude Code is fully covered, drop its shell.
- Which hosts act on `destructiveHint` by themselves. If one does, its shell would double up with the host's native prompt.
- Whether `ask` from a hook, in each host, blocks the call until the person answers in an interactive session and what a headless run does. The Copilot docs say headless denies; confirm for Cursor and Claude with `-p`.
- Whether MCP tool calls reach Copilot CLI's `preToolUse` and OpenCode's `tool.execute.before`. The docs read did not say. If not, those two hosts have no gate and the plan lists them as uncovered.
- Hook latency on the tier tools only; expected well under 800 ms plus one local HTTP call.

## Risks and dependencies

- A host that fails open on a hook timeout (Copilot's command hooks do) turns a slow daemon into no prompt. The shell's daemon limit must be shorter than the host's hook timeout, and the install step sets both.
- Asking twice teaches people to click through, which defeats the feature. `ServerWillAsk` is the guard; Step 1 must classify each host honestly.
- Depends on `repl-guard-for-other-agents` (adapter layer and capture), on `a-token-for-every-daemon-call` for caller identity from a token and for authenticating the preview route (it reads which sessions someone made), and on `ask-before-a-destructive-tool-runs` (done, the server half). The server half moves to MRTR under SDK 2.x stateless mode, where elicitation throws; if the daemon goes stateless for HTTP, these shells become the only prompt on Streamable HTTP clients, which raises their importance.

## Size

M. The policy and route are small. Five hosts, each needing a real run, are the cost.

## Decisions for Will

- Which hosts are in? Recommendation: Cursor and Copilot CLI first (a hook can ask), Codex as deny-or-silent, OpenCode only if Step 1 finds MCP coverage.
- On a host where the shell cannot tell who is calling, ask even for the agent's own sessions? Recommendation: yes, and document it; the alternative is a silent destructive call.
- Generate Claude `permissions.ask` entries for the 12 destructive tools outside the tier? Recommendation: offer it as an opt-in install flag, not default, because you chose those twelve to run unasked.
