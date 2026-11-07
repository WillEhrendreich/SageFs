# Confirmation gates in every agent host

Roadmap item: `confirmation-gates-in-every-agent-host` (Exploring, Agents). Plan written 2026-10-26 against master 59a8692c.

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

- The server side is built. `Affordances.destructiveTools` (`SageFs.Core/Affordances.fs:1211`) is 18 tools published as MCP `destructiveHint: true` on `tools/list` (`applyDestructiveHints`, `McpServer.fs`). `Affordances.confirmationLosses` (`:1280`) is the six-tool confirm tier with the words for what each takes away, and `singleSessionConfirmTools` (`:1290`) is the five that act on one session. `SageFs.Core/ConfirmationPolicy.fs` is the pure `decide : ToolName -> TargetSession -> SessionCreator -> MemberId -> Confirmation` (`Required of ConfirmationReason | NotRequired of OwnSession`), fail closed.
- The prompt path is `confirmationStep` and `confirmWithin` (`McpServer.fs:414`, `:445`): `NotInTier | ClientCannotAsk | Ask`. A client that never advertised elicitation is `ClientCannotAsk` and the call runs, because "the four gates ahead of this one already ran". `ConfirmationRefusal` has five typed cases (`Declined`, `Cancelled`, `AnsweredOther`, `NoAnswerInTime`, `CouldNotBeAsked`) and `wayOut` points at HTTP routes that do the job without asking (`unattendedRoute`).
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
