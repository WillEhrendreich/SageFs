module SageFs.Tests.SseParityTests

/// Living contract test: every daemon SSE event type MUST be explicitly handled
/// in both the VS Code extension and the Neovim plugin.
///
/// Adding a new event to SseWriter.fs triggers a failure here unless you also:
///   1. Add a match arm in sagefs-vscode/src/LiveTestingListener.fs processEvent
///   2. Add an entry in sagefs.nvim/lua/sagefs/events.lua EVENT_MAP
///   3. Add the event name to both sets below
///
/// This is the single source of parity truth.

open Expecto
open Expecto.Flip
open SageFs

// ── Authoritative daemon event list ─────────────────────────────────────────

/// All SSE events the daemon can emit on /events.
/// Composed from SseWriter.allSseEventTypes (19 formatters)
/// + the session event type (SessionEvents.sessionEventType)
/// + the state event type (DaemonStateChange.sseEventType).
let allDaemonSseEvents : string list =
  SseWriter.allSseEventTypes
  @ [ SessionEvents.sessionEventType
      SageFs.Server.DaemonStateChange.sseEventType ]

// ── VS Code handled set ──────────────────────────────────────────────────────
// Must be kept in sync with sagefs-vscode/src/LiveTestingListener.fs processEvent match arms.
// Includes non-daemon events handled for forward-compatibility (warmup_completed, file_reloaded,
// session_faulted, system_alarm) — these are fine to include; the test only checks that all
// *daemon* events appear here.

let vscodeHandledEvents : Set<string> =
  Set.ofList [
    "test_summary"
    "test_results_batch"
    "state"
    "session"
    "bindings_snapshot"
    "test_trace"
    "eval_diff"
    "cell_dependencies"
    "binding_scope_map"
    "eval_timeline"
    "eval_started"
    "eval_heartbeat"
    "eval_result"
    "test_source_locations"
    "file_annotations"
    "failure_narratives"
    "warmup_progress"
    "warmup_completed"
    "file_reloaded"
    "session_faulted"
    "domain_model"
    "diagnosis_ready"
    "live_bindings"
    "coverage_view"
  ]

// ── Neovim EVENT_MAP key set ─────────────────────────────────────────────────
// Must be kept in sync with sagefs.nvim/lua/sagefs/events.lua EVENT_MAP table keys.

let neovimHandledEvents : Set<string> =
  Set.ofList [
    // Phase 7C and earlier (original set)
    "eval_completed"
    "test_passed"
    "test_failed"
    "test_results_batch"
    "test_run_started"
    "test_run_completed"
    "test_state"
    "tests_discovered"
    "connected"
    "disconnected"
    "coverage_updated"
    "hot_reload_triggered"
    "warmup_context"
    "hotreload_snapshot"
    "providers_detected"
    "affected_tests_computed"
    "test_cycle_timing_recorded"
    "run_tests_requested"
    "test_summary"
    "file_annotations"
    "bindings_snapshot"
    "test_trace"
    "reconnecting"
    "test_recovery_needed"
    "eval_diff"
    "cell_dependencies"
    "binding_scope_map"
    "eval_timeline"
    "eval_result"
    "failure_narratives"
    "warmup_progress"
    "session_faulted"
    "warmup_completed"
    "file_reloaded"
    "system_alarm"
    // Phase 8: SSE parity completeness
    "eval_started"
    "eval_heartbeat"
    "test_source_locations"
    "state"
    "session"
    "domain_model"
    "diagnosis_ready"
    "live_bindings"
    "coverage_view"
  ]

// ── Tests ────────────────────────────────────────────────────────────────────

[<Tests>]
let sseParityTests = testList "SSE Parity" [

  test "allDaemonSseEvents contains 21 entries (19 SseWriter + session + state)" {
    allDaemonSseEvents
    |> Expect.hasLength "should have 21 daemon SSE event types" 21
  }

  test "SseWriter.allSseEventTypes contains exactly 19 formatter event types" {
    SseWriter.allSseEventTypes
    |> Expect.hasLength "SseWriter exposes 19 event type names" 19
  }

  test "no duplicate entries in allDaemonSseEvents" {
    let distinct = allDaemonSseEvents |> List.distinct
    distinct
    |> Expect.hasLength "daemon event list has no duplicates" allDaemonSseEvents.Length
  }

  testList "VS Code handles every daemon SSE event" [
    for eventType in allDaemonSseEvents do
      test (sprintf "VS Code handles '%s'" eventType) {
        vscodeHandledEvents.Contains(eventType)
        |> Expect.isTrue
             (sprintf
               "Event '%s' is emitted by the daemon but has no handler in LiveTestingListener.fs processEvent. \
                Add a match arm (even a no-op) to signal conscious handling."
               eventType)
      }
  ]

  testList "Neovim EVENT_MAP covers every daemon SSE event" [
    for eventType in allDaemonSseEvents do
      test (sprintf "Neovim handles '%s'" eventType) {
        neovimHandledEvents.Contains(eventType)
        |> Expect.isTrue
             (sprintf
               "Event '%s' is emitted by the daemon but missing from sagefs.nvim/lua/sagefs/events.lua EVENT_MAP. \
                Add an entry to route it to an autocmd pattern."
               eventType)
      }
  ]

  test "vscodeHandledEvents superset check — no typos in the set" {
    // Ensure every entry in the VS Code set is a valid lowercase_snake_case string.
    vscodeHandledEvents
    |> Set.iter (fun e ->
      let valid = System.Text.RegularExpressions.Regex.IsMatch(e, "^[a-z][a-z0-9_]*$")
      valid |> Expect.isTrue (sprintf "VS Code event '%s' should be lowercase_snake_case" e))
  }

  test "neovimHandledEvents superset check — no typos in the set" {
    neovimHandledEvents
    |> Set.iter (fun e ->
      let valid = System.Text.RegularExpressions.Regex.IsMatch(e, "^[a-z][a-z0-9_]*$")
      valid |> Expect.isTrue (sprintf "Neovim event '%s' should be lowercase_snake_case" e))
  }

  test "adding new daemon event requires updating this test (self-documenting)" {
    // If this count changes, a developer added a new SSE event. Update both handler sets above.
    allDaemonSseEvents.Length
    |> Expect.equal
         "if this fails, you added a daemon SSE event - update vscodeHandledEvents, neovimHandledEvents, and this test"
         21
  }
]
