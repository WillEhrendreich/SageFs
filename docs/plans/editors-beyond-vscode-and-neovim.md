# Rider, Zed, Helix and Emacs

Roadmap item: `editors-beyond-vscode-and-neovim` (Exploring, Editors). Plan written 2026-10-26 against master 59a8692c. Work order added 2026-11-11 against master 8975e898.

## Work order

This work order is written for a contributor who wants to write a SageFs client for an editor we do not ship one for. It names the wire they need, the smallest client that proves it, and the in-repo work that makes the next client cheaper than the last two.

### 1. Outcome

Today a Helix, Emacs, Zed or Rider user has no way to evaluate a block in a SageFs session short of reading `McpServer.fs`. After this item, `docs/editor-protocol.md` says what to send and what comes back for nine routes and the event stream, a test keeps that list mapped, `sagefs eval` and `sagefs events` let any editor with a shell pipe evaluate a block and watch a session, and a conformance suite proves the doc against a real daemon. One first-class client (Rider or Emacs) is a later item, decided by the request count in "Measure first".

Outcome gate: `EditorProtocolConformanceTests` in the host tier drives a real daemon with the same client code `sagefs eval` uses over the matrix in "What done looks like", asserting structured fields (`success`, `errorDetails.case`, the SSE `event` name and `id`), never sentences. Linux only; the CLI is .NET and runs where the tool runs, but nothing has been run on Windows or macOS.

### 2. Where the code is today

Verified by reading on 2026-11-11.

The contract a client needs:

- Discovery: `GET /health` (`SageFs/McpServer.fs:3115`) on the MCP port (37749 by default, `SAGEFS_MCP_PORT`) carries `apiVersion` and `version`; the VS Code client reads `apiVersion` from it (`sagefs-vscode/src/SageFsClient.fs:340`) and requires an exact match with `expectedApiVersion = 4` (`:619`). `GET /api/daemon-info` on the dashboard port replaced `daemon.json` (`SageFs/Dashboard.fs:4231-4232`). The two in-repo discovery modules are `sagefs-vscode/src/DaemonDiscovery.fs` and `sagefs.nvim/lua/sagefs/daemon_discovery.lua`.
- Compatibility: `EndpointContracts.apiVersion = 4` (`SageFs.Core/EndpointContracts.fs:38`; the comment above it says what each bump changed). Neovim declares a range, `M.api_range = { min = 3, max = 4 }` with a `min_reason` and `max_reason` per bound (`sagefs.nvim/lua/sagefs/compat.lua:21-26`), and a `reads` list of optional wire fields each with a degradation spec that `spec/compat_spec.lua` walks. A new client should copy the range pattern, not the exact match: `scripts/sync-nvim-version.fsx -- --compat` refuses a release when the daemon's `apiVersion` is outside the plugin's range.
- Routes: `EndpointContracts.all` (`:144`) is every route; `neovimContract` (`:158`, 19 entries) and `vscodeContract` (`:181`, 17 entries) are the two client contracts, and `EndpointContractTests.fs:162-235` checks each entry exists and is really mapped (`RouteScan`, `:104`). Request shapes live only in handlers: `POST /exec` (`McpServer.fs:2756`) reads `code`, optional `working_directory`, `file_path`, `eval_mode`, `block_start_line`, answers 200 with `success=false` for an eval that failed and a real error status when it never ran; `POST /api/sessions/create` (`:4089`) reads `workingDirectory` or `working_directory`, `projects`, `workflow`; `POST /api/cancel-eval` (`:2904`) reads the session id through `sessionIdFromJson`; `POST /api/completions` (`:4820`). Errors are `structuredErrorBody` with `errorDetails.case`.
- Events: `GET /events` and `GET /events?sessionId=` (`docs/sse-events.md:9-31`); 24 `SseWriter` event types plus `session` (8 subtypes) and `state` (8 variants) (`:63`, `:173`, `:190`), 22 cases in `SageFs/SseEvent.fs`. Resume with `Last-Event-ID: <epoch>:<seq>` (`docs/sse-events.md:32-62`, read at `McpServer.fs:3438`); the daemon replays what it still holds and sends the snapshots either way; a resync is not an error. SSE readers in the tree: `sagefs-vscode/src/sse-helpers.js` and `sagefs.nvim/lua/sagefs/sse.lua`.
- Agent panels: the stdio bridge `sagefs mcp` (`SageFs/McpStdioBridge.fs`) gives any MCP-speaking editor the tool catalogue (38,286 characters, `SageFs.Tests/RatchetBudgets.fs:462`). `sagefs call <tool> --flag value` (`SageFs/McpCallCommand.fs`, `docs/mcp-tools.md:195-250`) calls one tool from a shell with the same client code, JSON on stdout, exit 1 on a tool error, 2 on usage. It speaks MCP over HTTP, not `/exec`, so a shell client can already do `sagefs call send_fsharp_code --code "..."`.
- The smallest client that proves the contract, no code in this repo: `curl -s localhost:37749/health | jq .apiVersion` (expect 4); `curl -s -X POST localhost:37749/exec -H 'content-type: application/json' -d '{"code":"1 + 1;;"}'` (expect `success: true`); `curl -N 'localhost:37749/events?sessionId=<id>'` and read the `id:` lines; disconnect, reconnect with `-H 'Last-Event-ID: <last id>'` and see the gap replayed. A Helix `:pipe-to` or an Emacs `make-process` around those three is a working loop.

What is missing, and what the plan got wrong:

- No request or response shapes are documented outside handlers. `docs/editor-protocol.md` does not exist.
- The plan's `/exec` line (`McpServer.fs:2484`), `SageFsClient.fs:314` and `docs/configuration.md:47` are stale; the numbers above are current (`SAGEFS_EDITOR` is at `docs/configuration.md:146`).
- `SageFs/DaemonClient.fs` already exists and is something else: the `/api/state` parser for the retained TUI (`parseStateEvent`), used by `Dashboard.fs` and three test files. The new module must take another name; this work order uses `EditorClient`.
- The CLI has grown since the plan: `CliCommand` (`SageFs/Program.fs:125`) now has `Member`, `Cohort`, `Data`, `Call`, `CohortExport` beside `Stop`, `Status`, `Check`, `Sweep`, `Hygiene`, `Play`, `Mcp`, `Jupyter`. Nothing evaluates code over `/exec` from a shell or streams `/events`.
- `JupyterDaemonBridge` (`SageFs/JupyterDaemonBridge.fs`: `PostJson` `:27`, `parseExecResponse` `:69`, `httpPostJson` `:117`) is the one place that decodes `/exec` structurally. `McpStdioBridge.fs` reads SSE inline (`:135`, `:161`). `McpCallCommand.clientFor` (`:56`) builds the credentialed `HttpClient`.
- There is no `--integration-hosts` tier anywhere; the host tier is `Integration.hostList` run by `--integration-host` (`SageFs.Tests/Program.fs:299`).

### 3. Steps

1. Measure (one hour). `gh search issues --repo WillEhrendreich/SageFs "Rider"`, then `Zed`, `Helix`, `Emacs`; count distinct people and write the four numbers into "Measure first". Also time `GET /api/live-testing/entries` on a `SageFs.Tests` session (the rehydrate cost a client with no id pays).
2. RED. `EndpointContractTests.fs`: a `testList "Thin editor contract"` shaped like the Neovim one (`:164-213`): exists in `all`, really mapped, expected count 9. Fails to compile until `thinEditorContract` exists. Commit red.
3. GREEN. `EndpointContracts.thinEditorContract` (`GET /health`, `GET /api/sessions`, `POST /api/sessions/create`, `POST /exec`, `POST /api/cancel-eval`, `POST /api/completions`, `GET /events`, `GET /api/live-testing/file-annotations`, `GET /api/live-testing/status`), and the first draft of `docs/editor-protocol.md` with the request and reply fields read from the handlers named above, the compat rule and the resume rule. Trailer `Plugin: n/a adds a contract list, no endpoint changes`.
4. RED, pure. `SageFs.Tests/SseLineReaderTests.fs`: an event split at every byte offset parses the same; comments and `retry:` are skipped; `id:` is carried. Then `SageFs/SseLineReader.fs` (pure reducer over chunks) and `McpStdioBridge.fs` rewired to it with no behaviour change. Coordinate with `jupyter-kernel-that-works` so the reader is extracted once.
5. RED, pure. `SageFs.Tests/EditorClientTests.fs`: `SessionPick` cases (zero, one Ready, one warming, two unnamed gives `Several`) and `DaemonReach` from a health payload. Then `SageFs/EditorClient.fs` with `DaemonReach`, `SessionPick`, `EventPump`, using `JupyterDaemonBridge.PostJson` and `parseExecResponse`.
6. Edge. `Eval of args`, `Events of args`, `Sessions of args` in `CliCommand`; `sagefs eval` reads code from stdin, exit 0 success, 1 eval failed, 2 never ran; `sagefs events` writes NDJSON and resumes by `Last-Event-ID`. Test 6 with a fake `PostJson`. Help text in `Program.fs`.
7. Simulation `SageFs.Simulation/EventPumpSim.fs` with P1 to P4 and the two twins under Tests; registered in both fsproj files.
8. `SageFs.Tests/EditorProtocolConformanceTests.fs` as `Integration.hostList`, daemon via `Harness.startDaemonWithArgs port workDir [ "--no-resume" ]` and `TestPorts.reservePair ()` as `JupyterKernelSocketTests.fs:139-144` does.
9. Docs: the Helix and Emacs recipes in `docs/editor-protocol.md`, each run once by hand; `docs/agents.md` gets the agent-panel sentence (`sagefs mcp`, and the catalogue's size).
10. DECISION, then a new plan file for the first client.

DECISION: is a CLI (`sagefs eval`, `events`, `sessions`) a supported surface? Recommendation: yes; it is the conformance harness as well.
DECISION: first real client, Rider or Emacs, after step 1's counts. Recommendation: Emacs unless Rider's count is clearly larger and a Kotlin repo is acceptable.
DECISION: an `sagefs lsp` server. Recommendation: later, after the CLI shows what it cannot cover.

### 4. How to check it

- Inner loop: a session on `SageFs.Tests`; `send_fsharp_code` the `SessionPick` and reader cases before writing files.
- `dotnet build SageFs.Tests -c Release`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`.
- Filtered: `... --summary --filter-test-list "Thin editor contract"`, `"EditorClient"`, `"SseLineReader"`.
- Acceptance: unfiltered `--summary` (TRUST `Trusted`); `--integration-host --summary` for the conformance suite; `cd ~/Work/sagefs.nvim && busted` stays green because nothing on the wire changed.
- By hand: the three `curl` lines above against the installed daemon, then `echo '1 + 1;;' | sagefs eval` once step 6 lands.

### 5. Traps in this repo

- `EndpointContracts.fs` is on the wire-surface list: every commit touching it carries a `Plugin:` trailer, and the plugin lands first when a route changes shape. Steps 2 and 3 change no route.
- `EndpointContractTests` has a count test per contract and a "no duplicate endpoint" test; a new list is fine, a new route is not part of this item.
- `sagefs call` output shapes and the new CLI shapes are governed by `apiVersion`; a change to them later is a bump.
- Never match the daemon's sentences in the CLI: commit 09717cec fixed that in the Jupyter bridge. Branch on `errorDetails.case`.
- SAGE-005: `sagefs eval --session <id> --file <path>` parses into `SessionId` and a file path type, never two strings in one function signature.
- `SageFs.Json` profiles for every JSON the CLI reads or writes.
- Timing: the conformance suite waits on the ready event and on a named frame, never a sleep; the drop cell closes the client socket and waits for the replayed `id:`.
- `JsonCentralizationTests`, `TimeoutLiteralsTests`: no `JsonSerializerOptions`, no bare `TimeSpan` at a call site.
- Commit `.fsproj` files whole. No new NuGet package: the SSE reader and the JSON-RPC-free CLI need none.
- Linux only.

### 6. Size and order

Steps 2 and 3: one day. Steps 4 and 5: two days. Step 6: one day. Steps 7 and 8: two to three days. About a week and a half for one person. Step 1 runs first and in parallel with 2. Step 4 can run in parallel with 2 and 3. First commit: `test(editor-protocol): RED the thin editor contract endpoints exist and are mapped` touching `EndpointContractTests.fs` only.

### 7. Not now

- A plugin repo for Rider, Zed, Helix or Emacs: its own item after the DECISION.
- An LSP server: after the CLI.
- A per-client `apiVersion` range check in the daemon (the daemon does not know who connects): `compat.lua` and `expectedApiVersion` are the client's job and stay so.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | S for the contract, M for Emacs, L for Rider |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: the thin-editor endpoints exist and are mapped. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

Someone using Helix, Emacs, Zed or Rider can evaluate a block in a SageFs session and see the result, see the live test status of the file they are in, and watch the session's events, without waiting for a plugin from us. The route to that is a documented, tested editor protocol plus a command-line client built on it, then one first-class client for the editor people ask for most. The outcome gate is a conformance suite that drives the real daemon with the same reference client the CLI uses, across a matrix: daemon state {not running, running with no session, one Ready session, two sessions with none named, a session still warming} x action {eval ok, eval that fails to type-check, eval refused while warming, cancel a long eval, narrowed event stream, event stream dropped and resumed}. Each cell asserts structured fields (`success`, `errorDetails.case`, event `type`), never message text, and a cell that cannot run is a failing cell.

## What exists today

Two first-class clients, both large. `sagefs-vscode/src` is 13,165 lines of Fable F# across about forty files; `sagefs.nvim/lua/sagefs` is 20,741 lines of Lua across 77 top-level files (counted with `wc -l`, specs and the `dashboard` subfolder not included). Each re-implements the same things independently:
- daemon discovery (`sagefs-vscode/src/DaemonDiscovery.fs`, `lua/sagefs/daemon_discovery.lua`; both try the `daemon.json` port, then the configured port, then 37749);
- a compatibility gate on `apiVersion` (VS Code requires an exact match, `expectedApiVersion = 4` at `sagefs-vscode/src/SageFsClient.fs:619`, read from `/health` at `:340`; Neovim declares `api_range` in `lua/sagefs/compat.lua:21`, currently 3 to 4);
- SSE parsing (`sse-helpers.js`, `lua/sagefs/sse.lua`);
- the eval-block rule (`sagefs-vscode/src/CodeBlocks.fs` and `lua/sagefs/cells.lua`: `;;` delimiters if the file has any, else blank-line blocks);
- rendering of test status, coverage, bindings, hot reload.

The contract between daemon and clients is a list of paths and nothing else. `SageFs.Core/EndpointContracts.fs` defines `neovimContract` (19 entries) and `vscodeContract`, plus `all`, and `SageFs.Tests/EndpointContractTests.fs` checks that every listed route exists and is really mapped (`RouteScan.mapped`). Request and response shapes live only in the handlers (`/exec` is `McpServer.fs:2756` on 2026-11-11: body `code`, optional `working_directory`, `file_path`, `eval_mode`, `block_start_line`; it answers HTTP 200 with `success=false` for an eval that fails, a real error status when it never ran). The SSE events are documented in `docs/sse-events.md` (24 `SseWriter` event types plus `session` and `state`; `GET /events` and `GET /events?sessionId=` narrow the stream; `GET /diagnostics` is a separate SSE stream).

What a thin client can already do with the existing wire, all verified as routes in `EndpointContracts.fs`: `GET /health` (status, `apiVersion`, `version`, session states), `GET /api/sessions`, `POST /api/sessions/create`, `POST /exec`, `POST /api/cancel-eval`, `POST /api/completions`, `GET /api/live-testing/status`, `GET /api/live-testing/file-annotations`, `POST /api/live-testing/run`, and the events `eval_started`, `eval_result`, `warmup_progress`, `test_summary`, `file_annotations`, `live_bindings`, `session`, `state`.

Gaps in that picture.
- No documented request and response shapes, so a third client author reads F# handlers.
- The reconnect story was thinner than the code suggested when this plan was written: the replay buffer had no caller and the `/events` handler read no `Last-Event-ID` header, so a client rehydrated after a drop, as `sagefs.nvim`'s `batch_hydration.lua` does against `/api/live-testing/entries`. It is wired now. The daemon numbers every broadcast frame `<epoch>:<seq>`, replays what a reconnecting client missed, and sends the snapshots either way (`docs/sse-events.md`, "Resuming after a drop"). `sagefs.nvim` and the VS Code extension send the header, and a new client should too.
- There is no command-line client over `/exec` and `/events`. `Program.fs` has `stop`, `status`, `check`, `sweep`, `hygiene`, `play`, `mcp`, `member`, `cohort`, `data`, `call`, `cohort-export`, `--jupyter` (`CliCommand`, `Program.fs:125` on 2026-11-11). `sagefs call <tool>` (`SageFs/McpCallCommand.fs`) reaches any MCP tool from a shell, so `sagefs call send_fsharp_code --code "..."` evaluates code today; nothing streams events.
- No LSP server of ours (`grep -i "language server"` over the docs and daemon code finds only `docs/live-testing-as-you-type.md`, which is about live testing).
- The MCP stdio bridge (`sagefs mcp`, `SageFs/McpStdioBridge.fs`) already makes the tool catalogue available to any editor with an agent panel that speaks MCP. That is the zero-code path for agent-driven use. It carries the catalogue's roughly 28,000 tokens into that agent's context, which suits an agent and does not suit a human who wants "run this block".
- `SAGEFS_EDITOR` already picks Rider or VS Code link schemes for clickable `file:line:column` (`docs/configuration.md:146`, `SageFs.Core/EditorLink.fs`); that is the only Rider-specific thing in the tree.

I checked nothing about the four editors beyond our own repo. Everything below about their extension systems comes from memory and is marked "unverified"; step 3 verifies each against current documentation before any decision rests on it.

## Design

The seam is a small, documented, tested protocol that any editor can speak. Three rungs, each useful alone.

Rung 0, the contract as a document and a test.
- `docs/editor-protocol.md` (new): for each call in a `thinEditorContract` list, the request body, the response body, the status codes, and the structured error (`errorDetails.case` and `fields`). Derived from the handlers, not from memory. Plus the event list a thin client may subscribe to, the compat rule (`apiVersion`), and the reconnect rule (resume by `Last-Event-ID`; the daemon sends the snapshots either way).
- `EndpointContracts.thinEditorContract` beside the other two, and a test list in `EndpointContractTests.fs` shaped like the existing ones (exist, really mapped). The list: `GET /health`, `GET /api/sessions`, `POST /api/sessions/create`, `POST /exec`, `POST /api/cancel-eval`, `POST /api/completions`, `GET /events`, `GET /api/live-testing/file-annotations`, `GET /api/live-testing/status`. This touches `EndpointContracts.fs`, which is on the wire-surface list, so the commit carries `Plugin: n/a adds a contract list, no endpoint changes`.
- Because shapes are only in handlers, the conformance suite below checks the doc against the daemon.

Rung 1, a reference client and a CLI built on it. Pure core in `SageFs/` (the daemon project, beside `JupyterDaemonBridge.fs`). The module is `EditorClient`, because `SageFs/DaemonClient.fs` already exists and parses `/api/state` for the retained TUI (seen 2026-11-11):

```fsharp
type DaemonReach =
  | NotRunning of port: int
  | RunningOldApi of found: int * wanted: int
  | Running of ports: DaemonPorts * apiVersion: int

type SessionPick =
  | OnlySession of SessionId
  | Named of SessionId
  | NoSessionYet of why: NoSessionReason  // no session, still warming, faulted: a stated reason
  | Several of candidates: SessionId list // never a silent pick

type EventPump =                           // the reconnect state machine, pure
  | Connected of lastSeq: int64
  | Rehydrating
  | Waiting of attempt: int
```

and the verbs `sagefs eval [--session <id>] [--file <path> --line <n>] [--json]` (code on stdin; exit 0 on success, 1 when the eval failed, 2 when it never ran, so an editor binding can branch on it), `sagefs events [--session <id>] [--type a,b]` (NDJSON, one event per line, reconnects with a rehydrate step), `sagefs sessions [--json]`. The eval verb takes the structured error from the daemon (`errorDetails.case`) and never matches sentences: commit 09717cec fixed exactly that mistake in the Jupyter bridge, where a copy of the daemon's wording went stale. Do not build a second HTTP and SSE layer: `JupyterDaemonBridge` (HTTP posts, session routing) and `McpStdioBridge` (an SSE line reader) already exist in the same project. Extract one `DaemonClient` module and one `SseLineReader` and have the CLI, the Jupyter kernel's Phase B (`jupyter-kernel-that-works`) and the stdio bridge use it. The CLI output shapes are a contract; `apiVersion` governs them. Helix gets `:pipe-to`/`:sh` bindings around `sagefs eval`; Emacs gets a `make-process` filter around `sagefs events`; neither needs a plugin repo to start.

Rung 2, one real client. The candidates:

| Editor | Extension surface (unverified, from memory) | What the thin contract can drive | Stack and test story | Cost |
|---|---|---|---|---|
| Rider | JetBrains plugin in Kotlin on the platform SDK; Rider's F# support is its own backend; Rider has its own F# Interactive window; plugin LSP support is limited to some editions | tool window for sessions and results, a run action for the selected block, gutter icons for test status | JVM, Gradle, an IDE download to run tests; no F# in the loop | L |
| Emacs | Elisp, any process, any network; `eglot`/`lsp-mode` for LSP | eval region, overlay results, `sagefs events` filter for status | Elisp, ERT in `emacs --batch`, no GUI needed | M |
| Zed | Rust-to-WASM extensions; slash commands, language servers, MCP context servers; no custom panels | agent panel via MCP today; language server for diagnostics and lens | Rust toolchain | M, narrow surface |
| Helix | No plugin system I can rely on; languages.toml, LSP, shell pipes | shell pipes around the CLI; LSP for diagnostics | none | S with the CLI |

Rider comes up most in what people ask for, so it is the default first client. It is also the heaviest stack and the only one with a competing in-IDE F# Interactive. The plan does not start it until Will says the stack is acceptable and a count of real requests exists (see Measure first). Per the Neovim precedent the client lives in its own repo and tracks `apiVersion` through a range, with a `Plugin:` trailer on wire commits.

The LSP route, costed against four clients. An `sagefs lsp` stdio server (hand-written JSON-RPC with `Content-Length` framing; adding an LSP NuGet package needs a discussion under AGENTS.md, so assume none) could publish diagnostics for SageFs-originated problems (eval errors, test failures, hot-reload refusals), code lenses for test status per test from `file_annotations`, `workspace/executeCommand` for "evaluate block", and `$/progress` for warmup. It cannot draw a session picker, a value tree or a pane, so it never replaces a client; it replaces the diagnostics-and-lens half of four clients with one server. It must run beside the editor's own F# server (FsAutoComplete) and publish only what that server does not, or users see every compile error twice. Whether Helix, Zed and Emacs `eglot` run two servers for one language, and how Rider takes a third-party LSP, is unverified. Estimate, with the Neovim and VS Code sizes above as the yardstick: one thin client is a few thousand lines per editor, so four is tens of thousands in four stacks; one LSP server is perhaps two to three thousand lines in F# plus a short config per editor, for a smaller surface. The LSP rung waits until Rungs 0 and 1 show which half the CLI cannot cover.

Wire and client impact. Rungs 0 and 1 change no endpoint, MCP tool or SSE event, so `apiVersion` stays 4. `sagefs.nvim` and `sagefs-vscode` are untouched. The thin contract and the CLI shapes are new surface to version from now on. Token cost: no MCP tool is added.

## Tests

RED first.
1. `EndpointContractTests.fs`: "thin editor contract endpoints exist and are really mapped" (compile-red until `thinEditorContract` exists) and "has the expected entries".
2. `DaemonClientTests.fs` (new): pure cases of `SessionPick` from a `/api/sessions` payload: zero sessions gives `NoSessionYet NoSession`, one Ready gives `OnlySession`, one warming gives `NoSessionYet Warming`, two with none named gives `Several` and never picks. Property: `Several` is returned for every list of two or more when no id is named.
3. Same file: `DaemonReach` from a health payload: older `apiVersion` gives `RunningOldApi`; connection refused gives `NotRunning` with the port.
4. `SseLineReaderTests.fs`: framing properties (an event split across chunk boundaries at every byte offset parses identically; comment lines and `retry:` are ignored; `id:` is carried).
5. `EventPump` reducer: after a drop the next action is `Resume lastId` when the client holds an id and `Rehydrate` when it holds none. The daemon replays what it still has and sends the snapshots either way. Property: a frame is applied at most once.
6. CLI: `sagefs eval` exit codes for a success, a failed type-check (HTTP 200, `success=false`), and a refusal, using a fake daemon over the same `PostJson` function type `JupyterDaemonBridge` uses.

Simulation (`SageFs.Simulation/EventPumpSim.fs`, Generators, Invariants). Real code as subject: the `EventPump` reducer and `SessionPick`. Pool: two sessions, event frames with sequence ids. Ops as seeded data: Frame, Drop, Reconnect, DaemonRestart (ids restart), DuplicateFrame, ReorderAcrossSessions, SessionStopped. Invariants: (P1) no frame applied twice; (P2) after Reconnect and Rehydrate the client's view equals the daemon's; (P3) a client narrowed to a session never applies another session's frame; (P4) after DaemonRestart the client does not trust an old `lastSeq`. Twin: a client that resumes from an old run's `lastSeq` after a restart must violate P2 (`SageFs.Simulation/SseReplaySim.fs` has the daemon-side version of that bug as `IgnoresEpochTwin`), and one that ignores session ids must violate P3.

Outcome gate: `SageFs.Tests/EditorProtocolConformanceTests.fs`, a host-tier suite (`Integration.hostList`) using a real daemon on a reserved port pair with its own data dir (follow `HttpApiIntegrationTests` and the harness in `JupyterKernelSocketTests.fs`: `startDaemonWithArgs`, `TestPorts.reservePair`, `--no-resume`). Sessions target the small sample projects, not `SageFs.Tests.fsproj`. The matrix is in the first paragraph. Waits: session Ready by the ready-wait path, events by awaiting the specific frame; the drop-and-resume cell kills the connection from the client side and waits for the rehydrate to complete, no sleeps.

## Steps

1. Measure first (below), items a and b. Output: a count and a verdict on the stack.
2. RED test 1, `thinEditorContract`, and `docs/editor-protocol.md` first draft with shapes read from `McpServer.fs`. Owns `EndpointContracts.fs` (with the `Plugin: n/a` trailer), the contract test, the doc.
3. Verify the unverified editor facts (extension surface, multi-server LSP, MCP support) against each editor's current docs, and write the results into `docs/editor-protocol.md`'s "Other editors" section. Docs only; parallel with 2.
4. Extract `DaemonClient` and `SseLineReader` from `JupyterDaemonBridge.fs` and `McpStdioBridge.fs` under their existing tests (no behaviour change, tests 4 first). Coordinate with `jupyter-kernel-that-works`, which needs the same SSE reader for Phase B; do this once, here or there, not twice. Owns the new module file plus small edits to the two bridges.
5. Tests 2, 3, 5 and `SessionPick` / `DaemonReach` / `EventPump`. Same new files; serial after 4.
6. CLI verbs (test 6) in `Program.fs`: `CliCommand` cases and handlers. Owns `Program.fs`; serial after 5.
7. Sim files. File-disjoint from 6; starts once step 5's types are fixed.
8. Conformance suite. Needs 6.
9. Docs: `docs/agents.md` gets a section for editors with MCP agent panels (`sagefs mcp`), and the Helix and Emacs recipes (`sagefs eval` bindings) go in the editor doc, each checked by running it.
10. Decide the first client (Decisions), then plan it as its own item. Do not start a plugin repo from this plan.

## Measure first

a. Demand. Search the repo's issues and discussions for Rider, Zed, Helix and Emacs requests (`gh search issues` against the repository, a few minutes) and count distinct people. The roadmap says Rider comes up most; there is no count in the repo. If Emacs or Helix has as many asks as Rider, the cheaper stack goes first.

b. The thinnest useful client. After step 6, give yourself two hours per editor to build the smallest working loop with no plugin repo: Helix with `:pipe`, Emacs with a 40-line elisp using `sagefs eval` and `sagefs events`. If the loop is pleasant, the CLI is the product for those editors and no client is written. If it is not, the missing piece names the next rung (probably gutter or inline results, which needs a real client or LSP).

c. Whether a Rider plugin can run its tests in CI on this repo's runners without a 1 GB IDE download per run. If not, its test cost changes the size from L to larger.

d. Time cost of the rehydrate on a big session (`GET /api/live-testing/entries` for a session with thousands of tests), because a client with no id (a first connection) or one the daemon could not replay for depends on it. A reconnecting client that holds an id no longer does: the daemon replays what it missed.

## Risks and dependencies

- A CLI contract is one more thing to keep stable. `apiVersion` is the only version signal; the CLI prints it with `--json` so scripts can check.
- Editors with agent panels will load the 28,000-token MCP catalogue whether or not their user wanted it. The docs should say the CLI is the cheaper route for a human at a keyboard.
- A Rider client in Kotlin breaks the repo's F#-everywhere habit; the plugin repo is separate, like `sagefs.nvim`, but it needs an owner.
- Depends on `jupyter-kernel-that-works` for the shared SSE reader (whichever lands first extracts it). `a-notebook-over-a-live-session` benefits from the same reader. Nothing else depends on this item.

## Size

S for Rungs 0 and 1 together if the extraction in step 4 stays mechanical (about a week with the sim and the conformance suite). A first client is M for Emacs and L for Rider. An LSP server is M. A decision on which comes after the measurements.

## Decisions for Will

1. Is a CLI (`sagefs eval`, `events`, `sessions`) acceptable as a supported surface? Recommendation: yes. It is the cheapest way to cover Helix and Emacs and it doubles as the conformance harness.
2. First real client: Rider or Emacs? Recommendation: Rider only if the request count supports it and you accept a Kotlin repo; otherwise Emacs first, because its test story (ERT in batch mode) is the cheapest to keep honest.
3. Settled: the replay is wired into `/events`. A client resumes by `Last-Event-ID` and rehydrates when it has no id or the daemon says it cannot replay.
4. LSP server: yes or later? Recommendation: later, after the CLI shows which half it cannot cover.
