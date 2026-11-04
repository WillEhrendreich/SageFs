# SSE Events Reference

Editors receive daemon events over the main SSE stream, `GET /events` on port 37749. Most events carry a `SessionId` field so a client can filter to the session it cares about. The four cohort events are the exception: a cohort spans every session in its **repository**, so they carry no `SessionId`.

One daemon holds one cohort **per repository**, so these four describe whichever repository's cohort is relevant. The `state` stream's `cohortChanged` event is the one that says *which*, it carries the `CohortScope` that changed, so a client redrawing every cohort it happens to be showing is how you get a change attributed to the wrong repository. Filter on that scope, or on the repository you are working in.

The daemon emits 25 event types across four sources: 22 `SseWriter` events on `/events`, one `session` event (8 subtypes), one `state` event (8 variants), and `diagnostics` on its own stream.

## Connection

```
GET /events                      → SSE stream (all events below except diagnostics)
GET /events?sessionId=<id>       → the same stream, narrowed to one session
GET /api/live-testing/entries?session=<id> → the test rows `test_results_batch` no longer carries (the fetch hydrateFrame and batch_hydration make)
GET /diagnostics                 → SSE stream (compiler diagnostics only — separate endpoint)
```

The daemon sends a `retry:` hint once a connection is caught up, so clients reconnect automatically. A client that reconnects names the last frame it saw and is sent what it missed (see "Resuming after a drop" below).

### Narrowing a connection to one session

With no `sessionId`, `/events` is the firehose: every session's frames, and a catch-up on connect for whichever session is active. That is what the editors use today and it has not changed.

With `?sessionId=<id>` the daemon does the filtering, so a client that only cares about one session never receives another's frames:

- You get that session's frames, plus the ones that are true for the whole daemon: the four cohort events, the `state` events that name no session (the `outputCount`/`diagCount` tick, `systemAlarm`, `cohortChanged`), and the keepalive.
- The catch-up on connect is for that session, whether or not it is the active one. Health is replayed for that session only.
- A malformed id is a `400` and an id the daemon does not hold is a `404`. Both bodies say what to do next. A bad id is never read as "no id", because that would quietly widen the stream to everything.

One limit worth knowing: the daemon keeps one catch-up slot per feature (`eval_diff`, `cell_dependencies`, `binding_scope_map`, `eval_timeline`), not one per session. Each slot remembers which session it was built for, so a stream for A is never handed B's, but if B evaluated last, A's connect-time replay of those four is empty until A evaluates again.

### Resuming after a drop

Every frame that arrives through the daemon's broadcasts is numbered `<epoch>:<seq>` and written with an `id:` line. The epoch is new each time the daemon starts, because the seqs start again at 1 then. The snapshots written on connect, the keepalive comments and the `/diagnostics` stream carry no id. The daemon keeps the most recent numbered frames, up to 2,000 or 16 MiB, whichever is reached first (`SseLog.productionLimits` in `SageFs.Core/SseLog.fs`).

A client that reconnects with `Last-Event-ID: <the last id it saw>` is sent, in this order:

1. The frames it missed, oldest first, limited to what its stream may see: a connection narrowed to one session is not replayed another session's frames.
2. The snapshots a first connection gets (session state, cached test results, the cohort matrix, health, the bindings map and the four per-feature slots). A snapshot is current, and a replay carries what a snapshot cannot. The frames with no snapshot form are `eval_started`, `eval_heartbeat`, `eval_result`, `warmup_progress`, `test_run_completed`, `failure_narratives`, `test_trace`, `live_bindings`, `diagnosis_ready`, `save_observed`, and the `state` frames that announce a faulted session, a reload report or an alarm. A client that resyncs (no id, a foreign epoch, or an id the log has dropped) does not get those back, and the replay window is the only way to receive them after a drop. The snapshots are the warmup and hot-reload state, the session health, the test summary, results batch, source locations, file annotations and coverage views, every cohort's matrix, the bindings, and the four per-feature slots.
3. An id-only frame: `id: <epoch>:<seq>` and no event. It names the newest id at the moment the connection attached, and a client reads it as "caught up through here". A connection cut off in the middle of the snapshots has not read it, so its next reconnect asks again from the last id it had in full.
4. The `retry:` hint.

With no `Last-Event-ID`, or one the daemon cannot replay from, the connection gets steps 2 to 4 and no replayed frames. That is the case when the header is absent, is not `<epoch>:<seq>`, names a different run of the daemon, is ahead of anything this run has sent, or asks for a frame that was dropped to stay inside the limits (`SseReplay.ResyncReason` in `SageFs.Core/SseReplay.fs`). None of these is an error, and the daemon logs which one it was at debug level.

On one connection each numbered frame arrives once, in the order the log numbered it. The daemon attaches the connection to its log before it writes anything, in the same step as it works out what was missed (`SseLog.Attach` in `SageFs.Core/SseLog.fs`), so a frame pushed while the catch-up is being written is queued behind it and arrives once. That order is the order frames reached the log. For the frames a model change pushes it is the order the handlers ran, and they run one at a time: a change that arrives while one is being handled replaces any change already waiting (`Coalescer` in `SageFs.Core/ChangeCoalescer.fs`), so a burst of output lines is handled once, not once per line.

Two things can still reach a client twice. A snapshot can describe state that a later queued frame also describes, so the same change can arrive once in each form. And the four per-feature slots are logged and then written again from the slot on connect, the second copy without an id.

A session starting, stopping or becoming the active one is announced as a `session` frame (`session_created`, `session_stopped`, `session_activated`). The daemon derives them from its session registry, by comparing it with what it announced last time (`SessionLifecycle.observe`), so there is one record of a session's life and not a second one that could disagree. They are numbered in the replay log, so a client that was away is told in order what happened to the list while it was gone, and a replay that holds frames for a session stopped during the gap ends with the stop. The first look at the registry announces nothing: sessions already running are in the connect snapshot. A session the registry keeps listed as Stopped is announced once, when it stops. A client that needs the list itself reads `GET /api/sessions`, which is what both in-repo editors do when one of these frames arrives.

While a connection is quiet, its keepalive carries the log's newest id whenever that has moved, as an id-only frame, so a client narrowed to one session keeps a recent last id while other sessions are busy. Without it, a quiet scoped client that reconnected after the log had moved on would be told to resync from snapshots having missed nothing.

The test summary, the results batch and the whole `test_source_locations` list are pushed when they differ from the last one pushed for the session, and the connect snapshot carries all three. A model change that changes none of them pushes none of them, which keeps the log's 2,000 frames for frames that carry news.

A client that falls behind is disconnected, not trimmed: when its queue fills, the connection closes and the client resumes from its last id (`SseOutbox` in `SageFs.Core/SseOutbox.fs`). The queue is full at 1,024 frames or 32 MiB, whichever comes first; a single frame larger than the cap is still accepted when nothing is waiting. Before this, the queue dropped its oldest frame without telling the client.

`SageFs.Simulation/SseReplaySim.fs` folds seeded connects, drops, publishes and daemon restarts through the real replay and checks three things: nothing visible is missed after a reconnect, nothing arrives twice or out of order, and a stream narrowed to one session never receives another's frame. Each check has a twin that breaks exactly that guarantee and is caught.

---

## SseWriter Events (22)

The event names are defined in `allSseEventTypes` in `SageFs.Core/SseWriter.fs`.

### Warmup

| Event | Payload | Description |
|:---|:---|:---|
| `warmup_progress` | `Step`, `Total`, `Message`, `Progress`, `Phase` | Progress during session warmup (phases: creating_fsi, scanning_sources, loading_assemblies, opening_namespaces, finalizing). |

### Evaluation

| Event | Payload | Description |
|:---|:---|:---|
| `eval_started` | `filePath`, `blockStartLine` | Eval began. Clients should mark inline decorations stale. |
| `eval_heartbeat` | `FilePath`, `BlockStartLine`, `ElapsedMs` | ~500ms heartbeat during eval. Confirms the connection is alive and shows elapsed time. |
| `eval_result` | `filePath`, `blockStartLine`, `output`, `success`, `durationMs` | Final eval result with output text, success flag, and duration. |
| `eval_diff` | `Lines[]` (kind: Added/Removed/Modified/Unchanged), `Added`, `Removed`, `Modified`, `Unchanged` | Line-by-line diff between the two most recent eval outputs. |
| `eval_timeline` | `Count`, `P50Ms`, `P95Ms`, `P99Ms`, `MeanMs`, `Sparkline` | Eval performance statistics with a sparkline. |

### Bindings

| Event | Payload | Description |
|:---|:---|:---|
| `bindings_snapshot` | `Bindings[]` (name, type, value, shadowCount), `BindingValues[]`, `blockStartLine`, `filePath` | Current FSI variable bindings with types, values, and shadow counts. |
| `binding_scope_map` | `Bindings[]`, `ActiveCount`, `ShadowedCount` | Scope hierarchy showing which bindings are active vs shadowed. |
| `live_bindings` | `LiveValueSnapshot` (expanded value tree per binding) | Expanded, best-effort view of each bound value for the live watch view. What the walk will and won't touch is below the table. |

#### What the `live_bindings` walk does to your values

The walk has a mode, per session, and the default is `Safe` (`SageFs.Core/Features/LiveValueTree.fs`; the reasoning is in
[decisions.md](decisions.md#the-live-bindings-pane-starts-in-safe-mode-and-a-click-walks-the-binding-again)):

| Mode | What it does with a class instance |
|:---|:---|
| `Safe` (default) | Reads fields. Runs a getter only when its compiled body provably does nothing. Every other getter is a `NotEvaluated` node. |
| `Everything` | Runs every public getter, after every eval, under the one-second walk budget. That is your code running. |
| `Off` | Does not open a class instance. Records, unions, tuples, lists and maps still show. |

A mode is chosen with `POST /api/sessions/{sid}/live-values/mode` (`{"mode": "Safe" | "Everything" | "Off"}`), read back with
`GET` on the same path, and set for new sessions in `.SageFs/config.fsx` with `ValueWalk = WalkSafe | WalkEverything | WalkOff`.

A row the walk listed and did not read is a node whose `Kind` is `{"Case":"NotEvaluated","Fields":[<reason>]}`, and its `Preview`
says why in words. The reason is one of `GetterRunsCode`, `GetterLoops`, `SequenceNotEnumerated`, `ClassesCollapsed`,
`EvaluationTimedOut`, `EvaluationThrew` (with the message) and `EvaluationNotContained` (with why). The first two can be run on
demand: `POST /api/sessions/{sid}/live-values/evaluate` with `{"binding": "box", "path": ["RunsCode"]}` runs that one getter on a
dedicated thread under a deadline and, on Linux x86-64, a syscall filter, answers with what it came to (and a containment line to
show), and replaces that binding's tree. The event is pushed again after a click and after a mode switch, not only after an eval.
The last three reasons are what a click came to when it did not give a value: show "unknown" and the reason, never an empty row.

Whatever the mode:

- A `Task` is shown by its status. The walk never waits on it, and reads `Result`
  or `Exception` only once the task has finished. `ValueTask` is handled the same
  way.
- A `Lazy` is shown as created or not created, and `Value` is read only after it
  has been created. The walk never forces one.
- Each property is read once per walk, not twice.
- One walker thread takes the bindings in order, and the caller waits on each
  with a deadline, `Timeouts.liveValueBindingBudget` (1 second). A binding whose
  getter does not return in that time shows as unreadable, is not walked again,
  and the walk carries on with the next binding. Once 16 abandoned walkers are
  still stuck, the pass says so and stops starting new ones until the session
  restarts.

What this does not cover. In `Everything` mode a getter stuck in an infinite loop (a `member this.Self = this.Self` compiles to
one, because F# makes the self-call a tail call) never returns, and the abandoned walker thread keeps spinning a core for the life
of the host. A getter that overflows the stack kills the host process. `Safe` mode never runs those on its own. A click can still
run one, and the containment (a deadline, and a syscall filter where the OS has one) does not stop a spin, an overflow or an
in-memory effect. The same is true of a getter a click runs on an OS with no filter, and the pane says so.

### Live Testing

| Event | Payload | Description |
|:---|:---|:---|
| `test_summary` | Passed/failed/skipped/total counts, activation state | Aggregate test-run statistics. |
| `test_results_batch` | `Completion`, `Freshness`, `Generation`, `LastDecision`, `Summary` (no list) | The tally for one run. The rows are a fetch (`GET /api/live-testing/entries?session=<id>`): a 2,604-case list made this frame ~990 KB, so it moved off the stream in 0.6.928. |
| `test_trace` | Pre-serialized JSON trace data | Execution trace with diagnostic metadata for test runs. |
| `test_source_locations` | `Locations[]` (testId, filePath, lineNumber) | Maps test names to source locations for jump-to-definition. |
| `file_annotations` | `testAnnotations[]`, `codeLenses[]`, `coverageAnnotations[]`, `inlineFailures[]` | Per-file test decorations, coverage data, and inline failure markers. |
| `failure_narratives` | Map of test → `{LastPassedAt, TimeSinceLastPass, CausalChanges[], PropertyViolation, Summary}` | Causal analysis for Passed→Failed transitions: what changed and when. |
| `coverage_view` | `Generation`, `Symbol`, `FilePath`, `DefinitionLine`, `TotalCount`, `Overflow`, `InlineBadgeText`, `Health` | Per-symbol coverage badge for inline display. Version-gated: an unchanged frame emits nothing. |

### Analysis

| Event | Payload | Description |
|:---|:---|:---|
| `cell_dependencies` | `Nodes[]` (Id, Produces, Consumes), `Edges[]` (From, To) | Dependency graph of code cells. |
| `diagnosis_ready` | `Severity`, `FailureCount`, `AffectedCells`, `SuggestionCount`, `TopSuggestions[]`, `Failures[]`, `Performance`, `Summary` | Auto-diagnosis report with causal analysis and suggested fixes. |

### Multi-Agent Cohort

One cohort spans every session and agent on the daemon, so these four events carry no `SessionId`. They are backed by `SageFs.Core/Features/CohortOwner.fs`, pushed live from its `Events` stream and replayed to a newly connected client on `/events`.

| Event | Payload | Description |
|:---|:---|:---|
| `cohort_matrix` | `Version`, `CohortScope`, `Members[]` (id, role, seat, conductor), `Claims[]` (id, scope, holder, fence, state), `Tests[]`, `Rows[]` (generation, pass[], fail[], stale[]) | The full projected cohort frame: every member, every claim, and the test matrix. Version-gated per cohort: an unchanged frame emits nothing. The daemon runs one cohort per scope, `CohortScope` names the one the frame is about, and every cohort that exists is pushed and replayed on connect. `claim_changed`, `landing_changed` and `save_observed` come from the cohort the daemon was launched in only; for any other cohort the matrix is the whole report. |
| `claim_changed` | `ClaimId`, `Scope`, `Holder` (nullable), `Fence`, `Kind` (acquired\|released\|orphaned\|reassigned) | One claim's state changed. `Holder` is the current holder, or null when orphaned or released. |
| `landing_changed` | `LandingId`, `Requester`, `State` (queued\|rebasing\|verifying\|blocked\|landed\|withdrawn), `Blocker`, `NextAction` | One landing's state changed. `Blocker` and `NextAction` are populated only when `State = "blocked"`. |
| `save_observed` | `ClaimId`, `Observer`, `Holder`, `Scope`, `Path` | Claim early warning: a cohort member's file watcher saw a save land inside a different member's held claim. Advisory only: it never blocks the save. |

---

## Session Events (1 event type, 8 subtypes)

A single `session` event type carries a `type` discriminator for the subtype.

| Subtype | Payload | Description |
|:---|:---|:---|
| `warmup_context_snapshot` | `sessionId`, `context` (sourceFilesScanned, warmupDurationMs, phaseTiming, assembliesLoaded, namespacesOpened, failedOpens) | Warmup completion context with assemblies and namespace results. |
| `hotreload_snapshot` | `sessionId`, `watchedFiles[]` | Current set of files under hot-reload watch. |
| `hotreload_file_toggled` | `sessionId`, `file`, `watched` | A single file's hot-reload state changed. |
| `session_activated` | `sessionId` | Session became the active target (multi-session switch). |
| `session_created` | `sessionId`, `projectNames[]` | New session initialized with loaded projects. |
| `session_stopped` | `sessionId` | Session terminated cleanly. |
| `workflow_switching` | `sessionId`, `fromWorkflow`, `toWorkflow` | Workflow-mode transition started: the daemon accepted a replacement worker for the new workflow (`POST /api/sessions/{sid}/workflow`, the dashboard switcher, or `run_app` switching into hot reload). Scoped to that session. |
| `workflow_switched` | `sessionId`, `workflowLabel`, `replCapability`, `hotReloadActive` | Workflow-mode transition completed: the replacement worker is serving. Scoped to that session. A switch whose replacement worker faults sends `workflow_switching` and no `workflow_switched`; the session's `session_health_changed` says why. |

---

## Daemon State Events (1 event type, 8 variants)

A single `state` event carries variant-specific fields.

| Variant | Payload | Description |
|:---|:---|:---|
| `ModelChanged` | `outputCount`, `diagCount` | FSI output or diagnostics count changed. |
| `SessionReady` | `sessionReady` (sessionId) | Session warmup completed successfully. |
| `HotReloadChanged` | `hotReloadChanged: true` | Hot-reload state toggled. |
| `ReloadReported` | `reloadReported` (`state`, then `outcome`, `mechanism`, `patched`, `considered`, `message`, `suggestedAction`, `callers` once finished; `null` before the first save), `sessionId` | What the worker said a save did to the running process. Sent once the worker has decided, so `NoEffect` and `RestartRequired` reach you even when the app has no browser page. A patch sends `PatchPending` first (applied, the new code has not been seen running), then `Patched` (it ran) or `NeverEntered` (the bound passed without it running). A patch applied in the worker's own process also carries `declarations`, the names it patched, because those are what the REPL, which keeps the build from before the patch, is then behind on (see `replFreshness` in `get_session_status` and `/api/sessions`: `{state: "InSync"}`, `{state: "BehindApp", savesSince, declarations, message}` or `{state: "Unknown", reason: {kind, detail}, message}` when it could not be read). `mechanism` says how a patch reached the process: `detour` (a method re-pointed in the reload agent's process) or `metadata-delta` (a delta applied to the assembly an app started with `run_app` runs from). It is an empty string for a verdict that is not a patch (a restart, a compile failure, nothing changed), and read it from here, never from the words in `message`. See [Hot Reload](hot-reload.md#what-patched-means). The same object is `lastReload` in `get_session_status` and `/api/sessions`. |
| `FileReloaded` | `fileReloaded` (path) | File reloaded from disk. |
| `SessionFaulted` | `sessionFaulted` (sessionId), `error` | Session entered a faulted state. |
| `WarmupProgress` | `warmupProgress: true`, `sessionId`, `step`, `total` | Session warmup step progress. |
| `SystemAlarm` | `systemAlarm: true`, `phase`, `message` | Critical system event (resource exhaustion, shutdown). |

---

## Diagnostics Endpoint (separate stream)

| Event | Endpoint | Description |
|:---|:---|:---|
| `diagnostics` | `GET /diagnostics` | F# compiler diagnostics as a JSON array. Emitted on its own SSE stream, not on `/events`. |

---

## Summary

| Category | Count | Events |
|:---|:---|:---|
| SseWriter | 22 | warmup_progress, eval_started, eval_heartbeat, eval_result, eval_diff, eval_timeline, bindings_snapshot, binding_scope_map, live_bindings, test_summary, test_results_batch, test_trace, test_source_locations, file_annotations, failure_narratives, coverage_view, cell_dependencies, diagnosis_ready, cohort_matrix, claim_changed, landing_changed, save_observed |
| Session | 1 (8 subtypes) | session |
| Daemon state | 1 (8 variants) | state |
| Diagnostics | 1 (separate endpoint) | diagnostics |
| **Total** | **25** | |
