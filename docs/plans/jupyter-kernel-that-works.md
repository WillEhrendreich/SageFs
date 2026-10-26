# A Jupyter kernel that installs and works

Roadmap item: `jupyter-kernel-that-works` (Later, REPL). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M, then M for the value tree |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: an `execute_request` is answered with `status` ok. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

A person with .NET and Jupyter installed runs one command (`sagefs jupyter install`), opens a notebook in JupyterLab or in VS Code's notebook editor, picks the "F# (SageFs)" kernel, runs a cell, and sees the value, can tab-complete, can interrupt a long cell, and gets a collapsible value tree for a bound record or list next to the printed text. The word "experimental" leaves `Readme.md` only when the outcome gate is green in CI. The gate drives the real `sagefs --jupyter` process over real ZMQ sockets across a matrix: message type (kernel_info, execute ok, execute error, execute with printed output, complete, is_complete, interrupt during a long execute, shutdown) crossed with daemon state (session already Ready, no session yet so the kernel creates one, daemon down). No cell is skipped; a message type the kernel does not support yet is listed as a failing cell until it does.

## What exists today

The roadmap line and `Readme.md:463` say nothing opens a ZMQ socket. That is stale on master. `SageFs.Tests/JupyterKernelSocketTests.fs` (224 lines, added by commit 09717cec on 2026-10-21) starts a daemon, starts `sagefs --jupyter <connection.json>` as a process, connects a NetMQ `DealerSocket` to the shell port and a `SubscriberSocket` to the iopub port, sends a signed `execute_request` for `1 + 1`, and asserts an `execute_result` containing `val it: int = 2` and no `error`. It registers as `Integration.hostList`, so it runs under `--integration-host`. I did not run it, so I can say it exists and what it asserts, not that it passes. `docs/progress.md:549` records an earlier manual run with a real `jupyter_client` evaluating `1 + 1`.

Code (all in the daemon project, not Core; `ArchitectureTests.fs:413` forbids `JupyterKernel` in Core):
- `SageFs/JupyterKernel.fs` (640 lines): message types, `WireProtocol` (HMAC), `Router.route`, `FsiBridge`, `KernelLifecycle.processMessage`, `KernelSpec.generate` and `KernelLifecycle.renderKernelSpecJson`.
- `SageFs/JupyterTransport.fs` (216 lines): five NetMQ sockets, `parseFrames`, `buildReplyFrames`, `buildIOPubFrames`, the shell and control loops (`Async.RunSynchronously` per message on a dedicated thread, commented as deliberate).
- `SageFs/JupyterDaemonBridge.fs` (290 lines): `makeSessionProxy` sends code to the daemon's `/exec`, creates a session for the kernel's working directory when none exists, waits `Timeouts.jupyterSessionWarmup` (600 s, `SAGEFS_JUPYTER_WARMUP_SECONDS`) for it. `Program.fs:732` wires it and passes `Environment.CurrentDirectory`, so a notebook opened in a project folder gets that project's session.
- Tests: `JupyterKernelTests.fs` (893 lines, in-process), `JupyterDaemonBridgeTests.fs` (285), the socket test.

Gaps I found by reading `JupyterKernel.fs`. Each is verified in the file; whether a given frontend minds is not.
1. Replies are `{}`. In `Router.route` the execute, complete, is_complete and shutdown cases build `Reply = MessageContent.Raw "{}"` (the shutdown case sends `{"restart": ...}`). The Jupyter messaging spec requires `status` (and `execution_count`) in `execute_reply`. The `ExecuteReplyContent` is computed on the line above and used only for IOPub. This is the same bug class the existing "WHY" test already fixed for `kernel_info_reply`. The socket test checks only that the reply has more than 4 frames. Clients that read `reply["content"]["status"]` (I believe nbclient does) would fail; I have not run one.
2. Completion is computed and thrown away: `let! _result = Protocol.handleCompleteRequest ...` and the reply is `{}`. Same for is_complete. Tab completion and multi-line continuation cannot work in a notebook today though the handlers behind them are tested (`FsiBridge.completeHandler` calls `GetCompletions`).
3. `interrupt_request` falls into `MessageContent.Raw` and answers `{}`; the kernelspec declares `interrupt_mode: message`. The daemon has `POST /api/cancel-eval` and `/cancel`, unused by the kernel.
4. No `execute_input` broadcast, so frontends do not show the echoed input or the execution count from the kernel.
5. `StreamOutput` exists in `IOPubMessage` but `Router` never emits it, so printed output is only whatever `/exec` folds into the result string.
6. `Protocol.kernelInfoReply` hard-codes `ImplementationVersion = "0.5.0"`.
7. `ExecuteOutput.MimeType` is a `string` ("text/plain"). The house rule is a DU with one exhaustive to-string for a closed set.
8. Nothing writes a kernelspec. `renderKernelSpecJson` is called only from tests. There is no install command, so "install" today means writing `kernel.json` by hand.
9. `state` is a `ref` written by both the shell and control threads with no lock (`JupyterTransport.processSocket`).
10. The socket test waits `Task.Delay TestTimeouts.jupyterKernelSubscribe` (500 ms) for the iopub subscription to register. That is a fixed sleep guarding a PUB/SUB slow-joiner, which the house rules forbid.

Richer output has no source yet: `/exec` returns a result string. The value tree exists, though. `SageFs.Core/Features/LiveValueTree.fs` builds `LiveValueSnapshot` (`Bindings` of `LiveBindingValue`, each with a `LiveValueNode` tree: `Label`, `TypeName`, `Preview`, `Kind`, `Children`) and the daemon pushes it as the `live_bindings` SSE event after every eval (`docs/sse-events.md`; there is no GET for it). `CellDependencyGraph.producedNames` already extracts the names a cell bound from FSI's `val` lines.

## Design

Phase A makes the kernel correct and installable. Phase B adds the value tree. Phase A stands alone.

Phase A.
- Build every reply from a typed value. Add `ReplyContent` cases (ExecuteOk, ExecuteError, Complete, IsComplete, Interrupt, Shutdown, KernelInfo) with one `toWire` that produces the spec's JSON through `SageFs.Json`; `Router.route` returns the typed value and `KernelLifecycle.processMessage` serialises it at the edge. The `Raw "{}"` fallback stays only for message types we do not support, and `Raw` replies for supported types become a compile error by removing the case from their path.
- `MimeType` becomes a DU (`TextPlain | TextHtml | ApplicationJson | ImagePng` to start) with `MimeType.toWire`; `ExecuteOutput` carries `Data: Map<MimeType, string>` so one cell can hold several representations.
- `execute_input` published before the busy-to-result sequence; `interrupt_request` calls `/api/cancel-eval` through a new `JupyterDaemonBridge` function and replies `status: ok`.
- `sagefs jupyter install [--user | --prefix <dir>] [--name sagefs]` and `--uninstall`: a `CliCommand` case beside `Check` in `Program.fs`, a pure `KernelSpec.installDir` (platform and environment in, `Result<path, reason>` out; honours `JUPYTER_PATH`; defaults from jupyter_core as I remember them: `~/.local/share/jupyter/kernels` on Linux, `~/Library/Jupyter/kernels` on macOS, `%APPDATA%\jupyter\kernels` on Windows, to be confirmed against jupyter_core docs in step 1) and a writer that creates `<dir>/sagefs/kernel.json` with `argv = [absolute path of this executable; "--jupyter"; "{connection_file}"]`. No shelling out to `jupyter`, since a kernelspec is a directory. `sagefs check` gains a "Jupyter kernelspec installed" line (warn, not fail).
- Replace the iopub sleep in the socket test with proof of subscription: send `kernel_info_request` until the subscriber receives its busy/idle status broadcast, then send the real request. The first broadcast seen ends the wait.
- Lock-free state fix: serialise `processMessage` per socket and make the shared state a single-writer channel, or guard it; pick the smaller one in step 8.

Phase B.
- `JupyterDaemonBridge` gains an SSE reader on `GET /events?sessionId=<id>` (the stream narrows by session). After `/exec` returns, the bridge waits for the `live_bindings` snapshot whose `Generation` is at least the eval's and takes the bindings named in `producedNames` of the result. The wait ends on that event or on `eval_result` without bindings, not on a timer; a failure bound exists (`Timeouts`, named for its purpose) and fails the cell's tree, not the cell.
- A pure `ValueTreeJson.ofBinding` in `SageFs/` projects `LiveValueNode` into a notebook-friendly JSON (`{ "name", "type", "preview", "children": [...] }`), because the SSE shape is PascalCase with unions as `{Case, Fields}` (see how `sagefs.nvim/lua/sagefs/live_bindings.lua` has to read it) and that is unreadable in a JSON viewer. Emitted as `application/json` next to `text/plain`. JupyterLab renders `application/json` as a collapsible tree natively; I believe VS Code's notebook output does not, and step 10 checks.
- `text/html` for tables of records is out of scope here. Decide after the tree is in front of Will.

Wire and client impact. None on the daemon: the kernel is a client of existing `/exec`, `/api/sessions*`, `/api/cancel-eval` and `/events`. `apiVersion` stays 4, no `Plugin:` trailer is needed unless step 9 touches a wire file. `sagefs.nvim` and `sagefs-vscode` are unaffected. No MCP tool is added (token cost zero). The only new on-disk artefact is the user's `kernel.json`.

## Tests

RED first, each named by assertion.
1. `JupyterKernelTests.fs`, Router: "an execute_request is answered with status ok and the execution count" (parse the reply JSON, expect `status = "ok"`, `execution_count = 1`); "a failing execute is answered with status error, ename, evalue and traceback". Both fail today because the reply is `{}`.
2. "a complete_request is answered with the handler's matches, cursor_start and cursor_end"; "is_complete maps Complete, Incomplete indent, Invalid, Unknown to the spec's four statuses".
3. "an interrupt_request calls the cancel function once and replies ok"; "an interrupt with nothing running replies ok and calls nothing" (decide by the fake bridge's call log).
4. "execute_input precedes busy-result-idle on iopub with the code and the same execution count".
5. `MimeType.toWire` exhaustive, and a round trip through `ExecuteOutput.Data`.
6. `KernelSpec.installDir`: a table test over (platform, `JUPYTER_PATH` set or unset, `--user` or `--prefix`) with paths built by `Path.Combine`; unknown platform is an `Error` that says what to do.
7. Install writer in a temp directory: writes `kernel.json` whose `argv` has the executable path and the literal `{connection_file}`; running it twice changes nothing; a read-only target returns an error with the path.
8. Phase B: `ValueTreeJson.ofBinding` properties with FsCheck over generated `LiveValueNode` trees: output parses, depth and child counts match the input's bounds, a `NotEvaluated` node keeps its reason text, no node is lost.

Simulation. The subject is the pure `KernelLifecycle.processMessage`, which lives in the daemon project. `SageFs.Simulation` references only Core (`SageFs.Simulation.fsproj:318`), so put `JupyterKernelSim` in `SageFs.Tests` using `Scenario.fs`, `FaultInjection.fs` and `Shrink.fs` from the Simulation project.
- Pool: shell and control messages of every supported type, with a fake daemon that can answer ok, fail, answer "session warming" (refused), or never answer within the scenario.
- Faults: duplicate a request, reorder shell versus control, drop the daemon's answer, deliver `shutdown_request` mid-execute.
- Invariants: every request gets exactly one reply whose parent header is that request; `busy` precedes and `idle` follows every execute on iopub; `execution_count` rises by one per executed, stored cell; reply status agrees with the iopub outcome; interrupt on an idle kernel changes nothing; after shutdown no further replies.
- Twin: the current behaviour (a router that answers `{}`) must violate the reply-status invariant, so the invariant is shown to catch it.

Outcome gate: extend `JupyterKernelSocketTests.fs` into the matrix above (one `testTask` per cell, all in `Integration.hostList`, so `--integration-host` and the trust ledger pick it up). The assertions read frames off the sockets: reply status, iopub order, the `application/json` payload for a bound record, the daemon-down cell expects a stated error naming the port, not a hang (AGENTS.md rule 6: a tool that never answers is a bug). Waiting: every receive ends on a message; the existing 120 s `jupyterKernelExec` stays only as the failure bound for a cold session, and the session warmup wait inside the kernel is already `Timeouts.jupyterSessionWarmup`. Add a cell for a cold session that waits on the `session` ready event path in `JupyterDaemonBridge`, not on a sleep.

Manual proof, once, recorded in the PR: JupyterLab and VS Code notebook against the installed kernelspec, because no automated cell here uses a Jupyter frontend. Python is not available as a test dependency in this repo.

## Steps

1. Confirm the socket test passes on master (look at the last gate's trust row for `jupyter`), and confirm the jupyter_core kernel directories. If the test is red, fix that first. Output: a note in the PR.
2. RED test 1, then typed replies for execute (Router and `processMessage`). Smallest green change: build the reply from the `ExecuteReplyContent` already computed.
3. Complete and is_complete replies (tests 2). Same files; serial after 2.
4. `MimeType` DU and `Data` map (test 5). Touches `ExecuteOutput` and `FsiBridge`; serial after 2.
5. `execute_input` and interrupt (tests 3, 4). Adds `JupyterDaemonBridge` cancel function; serial after 3.
6. Install: `KernelSpec.installDir`, writer, `Program.fs` command, `check` line (tests 6, 7). File-disjoint from 2 to 5 except `Program.fs`; can run in parallel with them.
7. Socket test: replace the sleep with subscription proof; grow it into the matrix. Needs 2, 3, 5.
8. Sim in `SageFs.Tests`. Parallel with 6 once 2 has fixed the types.
9. Delete the "experimental" paragraph at `Readme.md:463` and update `docs/coming-from-jupyter.md` and the roadmap text, only after 7 is green in a full gate. (Edit `RoadmapItems.fs` through the roadmap process, not by hand.)
10. Phase B: SSE reader in the bridge, `ValueTreeJson`, tests 8, the `application/json` cell of the matrix, then the manual check in JupyterLab and VS Code.

## Measure first

- Which frontends render `application/json` as a tree? Run `jupyter lab` and VS Code with a throwaway kernel that sends one `execute_result` with `application/json`. If VS Code shows raw text, Phase B's value in VS Code needs a custom MIME renderer, which is a JS/TS extension and a different stack from the rest of the repo; then Phase B ships for JupyterLab only and the VS Code notebook story waits for `a-notebook-over-a-live-session`.
- Does a real client fail on `{}` replies today? Run `jupyter console --kernel sagefs` once before step 2 and write down what it does. This sets how loudly the README should apologise for the interim, nothing else.

## Risks and dependencies

- Kernel working directory decides the session. Jupyter starts kernels in the notebook's folder; a notebook outside any project gets a session for that folder, which may not load a project. The install command prints this.
- Snapshot ordering in Phase B: `live_bindings` for eval N may arrive after `execute_reply`. The generation check and the failure bound handle it; the sim must include a late snapshot.
- Absolute executable path in `kernel.json` goes stale when the tool updates. Prefer the `dotnet tool` shim path; `sagefs jupyter install` is idempotent so re-running repairs it.
- Phase B needs an SSE line reader in the daemon project. `McpStdioBridge.fs` already has one, and `editors-beyond-vscode-and-neovim` plans to extract a shared `SseLineReader` and `DaemonClient`. Take it from there if it has landed; do not write a third.
- `a-notebook-over-a-live-session` builds on Phase B (the SSE reader and `ValueTreeJson`). Nothing in this plan depends on another item.

## Size

M for Phase A (a week or so: the protocol fixes are small, the sim and the matrix are the work), M for Phase B (the SSE reader and the projection are new code in a new surface). Phase A alone removes the word experimental.

## Decisions for Will

1. Ship Phase A and drop "experimental" before Phase B? Recommendation: yes. A correct plain-text kernel is worth having now.
2. Should the kernel install command live under `sagefs jupyter install` or be part of `sagefs check --fix`? Recommendation: its own command; `check` only reports.
3. Is a JS/TS renderer acceptable if the VS Code measurement says it is needed? Recommendation: no for now; the repo's rule is F# for test and tooling, and the VS Code extension is Fable F#. Look at a Fable-built renderer only if the notebook item goes ahead.
