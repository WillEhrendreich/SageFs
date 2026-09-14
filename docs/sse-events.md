# 📡 SSE Events Reference

All connected editors receive events via the main SSE stream (`/events`). Events carry a `SessionId` field for multi-session isolation — except the four Cohort rows below, which describe one per-daemon cohort spanning every session and carry no `SessionId`. The daemon emits **23 event types** across three sources.

## Connection

```
GET /events          → SSE stream (all events below)
GET /diagnostics     → SSE stream (compiler diagnostics only — separate endpoint)
```

The daemon sends a `retry:` hint at connection time so clients auto-reconnect.

---

## SseWriter Events (21)

These are the core daemon events emitted on the `/events` stream. (Pre-existing
gap, not introduced here: `live_bindings` and `coverage_view` are also
`SseWriter` formatters — see `SageFs.Core/SseWriter.fs`'s `allSseEventTypes`
— but were missing from this table before item 15a; the real per-source count
is 23, not 21. Left for a dedicated docs pass rather than folded silently into
this item's diff.)

### Warmup

| Event | Payload | Description |
|:---|:---|:---|
| `warmup_progress` | `Step`, `Total`, `Message`, `Progress`, `Phase` | Progress during session warmup (phases: creating_fsi, scanning_sources, loading_assemblies, finalizing, opening_namespaces). |

### Evaluation

| Event | Payload | Description |
|:---|:---|:---|
| `eval_started` | `filePath`, `blockStartLine` | Signals eval begin — clients should mark inline decorations stale. |
| `eval_heartbeat` | `FilePath`, `BlockStartLine`, `ElapsedMs` | ~500ms heartbeat during eval. Confirms connection alive and shows elapsed time. |
| `eval_result` | `filePath`, `blockStartLine`, `output`, `success`, `durationMs` | Final eval result with output text, success flag, and duration. |
| `eval_diff` | `Lines[]` (kind: Added/Removed/Modified/Unchanged), `Added`, `Removed`, `Modified`, `Unchanged` | Line-by-line diff between the two most recent eval outputs. |
| `eval_timeline` | `Count`, `P50Ms`, `P95Ms`, `P99Ms`, `MeanMs`, `Sparkline` | Eval performance statistics with sparkline visualization. |

### Bindings

| Event | Payload | Description |
|:---|:---|:---|
| `bindings_snapshot` | `Bindings[]` (name, type, value, shadowCount), `BindingValues[]`, `blockStartLine`, `filePath` | Current FSI variable bindings with types, values, and shadow counts. |
| `binding_scope_map` | `Bindings[]`, `ActiveCount`, `ShadowedCount` | Scope hierarchy showing which bindings are active vs shadowed. |

### Live Testing

| Event | Payload | Description |
|:---|:---|:---|
| `test_summary` | Passed/failed/skipped/total counts, activation state | Aggregate test run statistics. |
| `test_results_batch` | Test statuses array, run state, freshness generation | Batch of individual test results with staleness tracking. |
| `test_trace` | Pre-serialized JSON trace data | Execution trace with diagnostic metadata for test runs. |
| `test_source_locations` | `Locations[]` (testId, filePath, lineNumber) | Maps test names to source locations for jump-to-definition. |
| `file_annotations` | `testAnnotations[]`, `codeLenses[]`, `coverageAnnotations[]`, `inlineFailures[]` | Per-file test decorations, coverage data, and inline failure markers. |
| `failure_narratives` | Map of test → `{LastPassedAt, TimeSinceLastPass, CausalChanges[], PropertyViolation, Summary}` | Causal analysis for Passed→Failed transitions — what changed and when. |

### Analysis

| Event | Payload | Description |
|:---|:---|:---|
| `cell_dependencies` | `Nodes[]` (Id, Produces, Consumes), `Edges[]` (From, To) | Dependency graph of code cells. |
| `domain_model` | `Transitions[]` (FromState, ToState, FunctionName, IsErrorBranch, Health) | Annotated DU state machine with health status per transition. |
| `diagnosis_ready` | `Severity`, `FailureCount`, `AffectedCells`, `SuggestionCount`, `TopSuggestions[]`, `Failures[]`, `Performance`, `Summary` | Auto-diagnosis report with causal analysis and suggested fixes. |

### Multi-Agent Cohort (item 15a; `save_observed` is the claim early-warning)

One cohort spans every session/agent connected to the daemon, so — unlike
every event above — these four rows carry no `SessionId`. Backed by
`SageFs.Core/Features/CohortOwner.fs`; pushed live from its `Events` stream
and replayed to a newly-connected client on `/events`. Editor handlers land
in follow-up items 15b (VS Code) / 15c (Neovim).

| Event | Payload | Description |
|:---|:---|:---|
| `cohort_matrix` | `Version`, `Members[]` (id, role, seat, conductor), `Claims[]` (id, scope, holder, fence, state), `Tests[]`, `Rows[]` (generation, pass[], fail[], stale[]) | The full projected `CohortFrame`: every member, every claim, and the test matrix bitplanes. Version-gated like `coverage_view` — an unchanged frame emits nothing. |
| `claim_changed` | `ClaimId`, `Scope`, `Holder` (nullable), `Fence`, `Kind` (acquired\|released\|orphaned\|reassigned) | One claim's state changed. `Holder` is the current holder or null (Orphaned/Released). |
| `landing_changed` | `LandingId`, `Requester`, `State` (queued\|rebasing\|verifying\|blocked\|landed\|withdrawn), `Blocker`, `NextAction` | One landing's state changed. `Blocker`/`NextAction` are populated only when `State = "blocked"`. |
| `save_observed` | `ClaimId`, `Observer`, `Holder`, `Scope`, `Path` | Claim early-warning (multi-agent vision §5.1): a cohort member's file watcher saw a save land inside a DIFFERENT member's held claim. Advisory only — never blocks the save. |

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
| `workflow_switching` | `sessionId`, `fromWorkflow`, `toWorkflow` | Workflow mode transition started. |
| `workflow_switched` | `sessionId`, `workflowLabel`, `replCapability`, `hotReloadActive` | Workflow mode transition completed. |

---

## Daemon State Events (1 event type, 8 variants)

A single `state` event carries variant-specific fields.

| Variant | Payload | Description |
|:---|:---|:---|
| `ModelChanged` | `outputCount`, `diagCount` | FSI output or diagnostics count changed. |
| `SessionReady` | `sessionReady` (sessionId) | Session warmup completed successfully. |
| `HotReloadChanged` | `hotReloadChanged: true` | Hot-reload state toggled. |
| `FileReloaded` | `fileReloaded` (path) | File reloaded from disk. |
| `SessionFaulted` | `sessionFaulted` (sessionId), `error` | Session entered faulted state. |
| `StandbyProgress` | `standbyProgress: true` | Standby session pool changed. |
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
| SseWriter | 21 (see the pre-existing-gap note above — the real count is 23) | warmup_progress, eval_started, eval_heartbeat, eval_result, eval_diff, eval_timeline, bindings_snapshot, binding_scope_map, test_summary, test_results_batch, test_trace, test_source_locations, file_annotations, failure_narratives, cell_dependencies, domain_model, diagnosis_ready, cohort_matrix, claim_changed, landing_changed, save_observed |
| Session | 1 (8 subtypes) | session |
| Daemon state | 1 (8 variants) | state |
| Diagnostics | 1 (separate endpoint) | diagnostics |
| **Total** | **24** | |
