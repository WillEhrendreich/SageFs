# SSE Events Reference

Editors receive daemon events over the main SSE stream, `GET /events` on port 37749. Most events carry a `SessionId` field so a client can filter to the session it cares about. The four cohort events are the exception: one cohort spans every session on the daemon, so they carry no `SessionId`.

The daemon emits 25 event types across four sources: 22 `SseWriter` events on `/events`, one `session` event (8 subtypes), one `state` event (8 variants), and `diagnostics` on its own stream.

## Connection

```
GET /events          → SSE stream (all events below except diagnostics)
GET /diagnostics      → SSE stream (compiler diagnostics only — separate endpoint)
```

The daemon sends a `retry:` hint at connection time so clients reconnect automatically.

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
| `test_results_batch` | Test statuses array, run state, freshness generation | Batch of individual test results with staleness tracking. |
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
| `cohort_matrix` | `Version`, `Members[]` (id, role, seat, conductor), `Claims[]` (id, scope, holder, fence, state), `Tests[]`, `Rows[]` (generation, pass[], fail[], stale[]) | The full projected cohort frame: every member, every claim, and the test matrix. Version-gated — an unchanged frame emits nothing. |
| `claim_changed` | `ClaimId`, `Scope`, `Holder` (nullable), `Fence`, `Kind` (acquired\|released\|orphaned\|reassigned) | One claim's state changed. `Holder` is the current holder, or null when orphaned or released. |
| `landing_changed` | `LandingId`, `Requester`, `State` (queued\|rebasing\|verifying\|blocked\|landed\|withdrawn), `Blocker`, `NextAction` | One landing's state changed. `Blocker` and `NextAction` are populated only when `State = "blocked"`. |
| `save_observed` | `ClaimId`, `Observer`, `Holder`, `Scope`, `Path` | Claim early warning: a cohort member's file watcher saw a save land inside a different member's held claim. Advisory only — it never blocks the save. |

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
| `workflow_switching` | `sessionId`, `fromWorkflow`, `toWorkflow` | Workflow-mode transition started. |
| `workflow_switched` | `sessionId`, `workflowLabel`, `replCapability`, `hotReloadActive` | Workflow-mode transition completed. |

---

## Daemon State Events (1 event type, 8 variants)

A single `state` event carries variant-specific fields.

| Variant | Payload | Description |
|:---|:---|:---|
| `ModelChanged` | `outputCount`, `diagCount` | FSI output or diagnostics count changed. |
| `SessionReady` | `sessionReady` (sessionId) | Session warmup completed successfully. |
| `HotReloadChanged` | `hotReloadChanged: true` | Hot-reload state toggled. |
| `ReloadReported` | `reloadReported` (`state`, then `outcome`, `mechanism`, `patched`, `considered`, `message`, `suggestedAction` once finished; `null` before the first save), `sessionId` | What the worker said a save did to the running process. Sent once the worker has decided, so `NoEffect` and `RestartRequired` reach you even when the app has no browser page. A patch sends `PatchPending` first (applied, the new code has not been seen running), then `Patched` (it ran) or `NeverEntered` (the bound passed without it running). A patch applied in the worker's own process also carries `declarations`, the names it patched, because those are what the REPL, which keeps the build from before the patch, is then behind on (see `replFreshness` in `get_session_status` and `/api/sessions`: `{state: "InSync"}` or `{state: "BehindApp", savesSince, declarations, message}`). `mechanism` says how a patch reached the process: `detour` (a method re-pointed in the reload agent's process) or `metadata-delta` (a delta applied to the assembly an app started with `run_app` runs from). It is an empty string for a verdict that is not a patch (a restart, a compile failure, nothing changed), and read it from here, never from the words in `message`. See [Hot Reload](hot-reload.md#what-patched-means). The same object is `lastReload` in `get_session_status` and `/api/sessions`. |
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
