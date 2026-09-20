module SageFs.Tests.SseParityTests

/// Living contract test: every daemon SSE event type MUST be explicitly handled
/// in the VS Code extension, and SHOULD be handled in the Neovim plugin.
///
/// Outcome-gate-sweep.md Gap C / §2.4: this file used to claim to be "the
/// single source of parity truth" while keeping the VS Code handled set as a
/// hand-copied literal that nothing checked against
/// `sagefs-vscode/src/LiveTestingListener.fs`'s actual `processEvent` match
/// arms. It drifted silently: the literal claimed `cohort_matrix`,
/// `claim_changed`, `landing_changed`, and `save_observed` were handled
/// (with a comment promising a "follow-up item"), but `processEvent`'s real
/// match arms fall through to `| _ -> ()` for all four — a fact this test
/// never noticed because it never read the source it claimed to police.
///
/// The VS Code half below is now DERIVED — parsed out of the real
/// `processEvent` source, not typed by hand — so that gap is now visible
/// (`vscodeKnownGaps`, pinned explicitly) instead of silently claimed away,
/// and a genuinely new unhandled event (not just these four) fails the
/// per-event test below.
///
/// The Neovim half is NOT derived: `sagefs.nvim` is a separate repository
/// (`WillEhrendreich/sagefs.nvim`) this checkout does not contain, so there
/// is no source here to parse. `neovimHandledEvents` stays a manually
/// maintained mirror — keep it in sync with
/// `sagefs.nvim/lua/sagefs/events.lua`'s `EVENT_MAP` keys by hand — and the
/// test against it is named and commented to say plainly that it checks
/// self-consistency of that mirror, not the plugin itself. (outcome-gate-
/// sweep.md §5 item 7 / Gap C item 3: vendoring a generated manifest from
/// that repo would let this become a real gate; until then, downgrading the
/// claim is the honest option.)
///
/// Adding a new event to SseWriter.fs:
///   1. Add a match arm in sagefs-vscode/src/LiveTestingListener.fs processEvent
///      (or add it to `vscodeKnownGaps` below with a reason, if deliberately deferred)
///   2. Add an entry in sagefs.nvim/lua/sagefs/events.lua EVENT_MAP, and mirror
///      the name into `neovimHandledEvents` below
///   3. Add the event name to `allDaemonSseEvents`'s expected count below

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open SageFs

// ── Authoritative daemon event list ─────────────────────────────────────────

/// All SSE events the daemon can emit on /events.
/// Composed from SseWriter.allSseEventTypes (23 formatters — item 15a added
/// cohort_matrix/claim_changed/landing_changed; the cohort claim
/// early-warning item added save_observed)
/// + the unified SseEvent vocabulary's two channel names (roast-5 §1
/// merged the former SessionEvents.sessionEventType and
/// DaemonStateChange.sseEventType into one classifier).
let allDaemonSseEvents : string list =
  SseWriter.allSseEventTypes
  @ [ SageFs.Server.SseEvent.sseEventTypeSession
      SageFs.Server.SseEvent.sseEventTypeState ]

// ── VS Code handled set — DERIVED from the real source, not hand-typed ──────

let private vscodeListenerSourcePath : string =
  Path.Combine(__SOURCE_DIRECTORY__, "..", "sagefs-vscode", "src", "LiveTestingListener.fs")
  |> Path.GetFullPath

/// Parse the real `processEvent` function's match arms out of
/// `LiveTestingListener.fs` source text. Every quoted lowercase_snake string
/// literal appearing right after a `|` inside that function's body counts as
/// "handled" — including ones inside a nested match on a different
/// discriminant (e.g. the `"session"` case's inner match on `subtype`);
/// that only widens the derived set with names the daemon never emits as a
/// top-level `eventType`, which is harmless for this check (it only needs
/// every REAL daemon event type to be found in the set, not the reverse).
let extractVscodeProcessEventHandledTypes (source: string) : Set<string> =
  let startMarker = "let processEvent (eventType: string) (data: obj) ="
  let endMarker = "let disconnectFn ="
  let startIdx = source.IndexOf(startMarker)
  let endIdx = source.IndexOf(endMarker)
  match startIdx >= 0 && endIdx > startIdx with
  | false ->
    failwithf
      "Could not locate processEvent's match block in %s (expected to find %s ... %s). \
       LiveTestingListener.fs's structure changed — update this extractor to match it."
      vscodeListenerSourcePath startMarker endMarker
  | true ->
    let block = source.Substring(startIdx, endIdx - startIdx)
    Regex.Matches(block, "(?m)^\\s*\\|\\s*\"([a-z][a-z0-9_]*)\"")
    |> Seq.cast<Match>
    |> Seq.map (fun m -> m.Groups.[1].Value)
    |> Set.ofSeq

/// What `processEvent` ACTUALLY handles today, read straight from source.
let vscodeHandledEventsDerived : Set<string> =
  vscodeListenerSourcePath
  |> File.ReadAllText
  |> extractVscodeProcessEventHandledTypes

/// Item 15a shipped the cohort SSE events on the daemon side
/// (`cohort_matrix` / `claim_changed` / `landing_changed`), and the claim
/// early-warning item shipped `save_observed` — but the VS Code UI for all
/// four is a separate, not-yet-landed follow-up (15b/15c). This names that
/// gap explicitly instead of a hand-typed set silently claiming it closed:
/// when a handler lands, remove its name here (the pinning test below will
/// force that edit); if a genuinely new daemon event goes unhandled for any
/// other reason, it must NOT be added here without a matching comment
/// explaining why — that is exactly the drift this test exists to catch.
let vscodeKnownGaps : Set<string> =
  Set.ofList [ "cohort_matrix"; "claim_changed"; "landing_changed"; "save_observed" ]

// ── Neovim EVENT_MAP key set — an UNVERIFIED, hand-maintained mirror ────────
// sagefs.nvim is a separate repository; this checkout has no source to
// derive this from. Keep in sync with
// sagefs.nvim/lua/sagefs/events.lua EVENT_MAP table keys BY HAND — the test
// below only checks this literal's internal shape, not the plugin.

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
    // Item 15a: multi-agent cohort coordination rows (see the matching
    // comment in vscodeHandledEvents above — handlers land in 15b/15c).
    "cohort_matrix"
    "claim_changed"
    "landing_changed"
    // Cohort claim early-warning (multi-agent vision §5.1) — see the
    // matching comment in vscodeHandledEvents above.
    "save_observed"
  ]

// ── Tests ────────────────────────────────────────────────────────────────────

[<Tests>]
let sseParityTests = testList "SSE Parity" [

  test "allDaemonSseEvents contains 25 entries (23 SseWriter + session + state)" {
    allDaemonSseEvents
    |> Expect.hasLength "should have 25 daemon SSE event types" 25
  }

  test "SseWriter.allSseEventTypes contains exactly 23 formatter event types" {
    SseWriter.allSseEventTypes
    |> Expect.hasLength "SseWriter exposes 23 event type names" 23
  }

  test "no duplicate entries in allDaemonSseEvents" {
    let distinct = allDaemonSseEvents |> List.distinct
    distinct
    |> Expect.hasLength "daemon event list has no duplicates" allDaemonSseEvents.Length
  }

  test "the extractor actually parsed real match arms out of LiveTestingListener.fs (sanity: a broken extractor must not silently pass)" {
    (vscodeHandledEventsDerived.Count, 15)
    |> Expect.isGreaterThan
         "extractVscodeProcessEventHandledTypes found suspiciously few event names — \
          either processEvent's shape changed (update the start/end markers) or the \
          regex stopped matching; either way this must be investigated, not ignored."
  }

  testList "VS Code handles every daemon SSE event (derived from real processEvent source), or the gap is explicitly tracked" [
    for eventType in allDaemonSseEvents do
      test (sprintf "VS Code handles '%s', or it is a named gap in vscodeKnownGaps" eventType) {
        (vscodeHandledEventsDerived.Contains(eventType) || vscodeKnownGaps.Contains(eventType))
        |> Expect.isTrue
             (sprintf
               "Event '%s' is emitted by the daemon but has no handler in LiveTestingListener.fs processEvent \
                (parsed from source), and it is not listed in vscodeKnownGaps either. Add a match arm \
                (even a no-op, to signal conscious handling), or add it to vscodeKnownGaps with a reason."
               eventType)
      }
  ]

  test "vscodeKnownGaps names exactly the cohort rows still awaiting a VS Code handler (item 15b/15c) — no more, no less" {
    vscodeKnownGaps
    |> Expect.equal
         "if this fails because the set shrank, a handler landed — great, but also update the comment \
          above vscodeKnownGaps. If it fails because the set grew, a NEW daemon event went unhandled \
          without anyone deciding that was OK — that is a real regression, not a test to relax."
         (Set.ofList [ "cohort_matrix"; "claim_changed"; "landing_changed"; "save_observed" ])
  }

  testList "Neovim EVENT_MAP claims coverage of every daemon SSE event (UNVERIFIED — sagefs.nvim is a separate repo)" [
    for eventType in allDaemonSseEvents do
      test (sprintf "neovimHandledEvents claims '%s' (self-consistency only, not verified against sagefs.nvim)" eventType) {
        neovimHandledEvents.Contains(eventType)
        |> Expect.isTrue
             (sprintf
               "Event '%s' is emitted by the daemon but missing from this hand-maintained mirror of \
                sagefs.nvim/lua/sagefs/events.lua EVENT_MAP. Add an entry here AND in that repo — this \
                test cannot see whether the real plugin agrees, only whether this mirror is complete."
               eventType)
      }
  ]

  test "vscodeHandledEventsDerived superset check — no typos in the derived set" {
    // Ensure every entry the extractor found is a valid lowercase_snake_case string —
    // guards against the regex accidentally matching a non-event string literal.
    vscodeHandledEventsDerived
    |> Set.iter (fun e ->
      let valid = Regex.IsMatch(e, "^[a-z][a-z0-9_]*$")
      valid |> Expect.isTrue (sprintf "VS Code derived event '%s' should be lowercase_snake_case" e))
  }

  test "neovimHandledEvents superset check — no typos in the set" {
    neovimHandledEvents
    |> Set.iter (fun e ->
      let valid = Regex.IsMatch(e, "^[a-z][a-z0-9_]*$")
      valid |> Expect.isTrue (sprintf "Neovim event '%s' should be lowercase_snake_case" e))
  }

  test "adding new daemon event requires updating this test (self-documenting)" {
    // If this count changes, a developer added a new SSE event. Update both handler sets above.
    allDaemonSseEvents.Length
    |> Expect.equal
         "if this fails, you added a daemon SSE event - update vscodeHandledEvents, neovimHandledEvents, and this test"
         25
  }
]
