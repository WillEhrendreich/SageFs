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
/// The Neovim half is NOW DERIVED, the same way the VS Code half is, and for
/// the same reason. `sagefs.nvim` IS a sibling checkout on this machine
/// (`../sagefs.nvim`, the one `scripts/sync-nvim-version.fsx` already reads),
/// so the claim that "there is no source here to parse" was only true while
/// nobody wrote the parser. A hand-kept mirror cannot catch its own rot, and
/// this one had already rotted: it listed 23 event names NO F# source emits —
/// most starkly `test_run_started` / `test_run_completed`, which
/// `grep -rn "test_run_completed" --include=*.fs` resolves to this very file
/// and nothing else. The per-event test below only walked
/// `allDaemonSseEvents` (daemon -> mirror), so those 23 phantoms were
/// invisible: the mirror could claim to handle events that do not exist and
/// the gate stayed green. Reading the real `EVENT_CATALOG` table makes that
/// impossible to assert — a phantom has to be deleted, not defended.
///
/// The derivation is deliberately narrower than the VS Code one. A name in
/// `EVENT_CATALOG` only means the plugin can FIRE a User autocmd with that
/// name; whether it also DISPATCHES the daemon's `event:` is a separate
/// question (`sse.lua`'s `type_to_action`, wired in `init.lua`). So this
/// checks the catalogue, and `neovimDispatchDerived` below checks the
/// dispatch table, rather than pretending one number means both.
///
/// Adding a new event to SseWriter.fs:
///   1. Add a match arm in sagefs-vscode/src/LiveTestingListener.fs processEvent
///      (or add it to `vscodeKnownGaps` below with a reason, if deliberately deferred)
///   2. Add an entry in sagefs.nvim/lua/sagefs/events.lua EVENT_CATALOG, and a
///      dispatch entry in its sse.lua `type_to_action` if it is a daemon event
///   3. Nothing here to update by hand — both halves are derived. The counts
///      below are the only thing that must move, and they are self-describing.

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open SageFs

// ── Authoritative daemon event list ─────────────────────────────────────────

/// All SSE events the daemon can emit on /events.
/// Composed from SseWriter.allSseEventTypes (22 formatters — item 15a added
/// cohort_matrix/claim_changed/landing_changed; the cohort claim
/// early-warning item added save_observed; roast-8 §2 deleted domain_model,
/// which had zero production callers for the emitter OR its data source)
/// + the unified SseEvent vocabulary's two channel names (roast-5 §1
/// merged the former SessionEvents.sessionEventType and
/// DaemonStateChange.sseEventType into one classifier).
let allDaemonSseEvents : string list =
  SseWriter.allSseEventTypes
  @ [ SageFs.Server.SseEvent.sseEventTypeSession
      SageFs.Server.SseEvent.sseEventTypeState ]

// ── VS Code handled set — DERIVED from the real source, not hand-typed ──────

let private repoRoot = RepoPaths.repoPathFull [||]

let private vscodeListenerSourcePath : string =
  Path.Combine(repoRoot, "sagefs-vscode", "src", "LiveTestingListener.fs")
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

// ── Neovim half — DERIVED from the real sibling checkout, not hand-typed ─────
// sagefs.nvim is a SEPARATE repository that lives as a sibling of this one, so its
// files are not under the repo root. It is located at RUNTIME: `SAGEFS_NVIM_DIR` if
// set, else `RepoPaths.siblingCheckoutDir`, which looks next to the MAIN checkout —
// the repo root's own parent is not that inside a git worktree. Never a build-time
// constant. Both sets below are parsed out of that repo's Lua source, so a phantom
// cannot be asserted here: to claim the plugin handles an event, this file has to
// point at a line of real Lua.
let private nvimRepoDir : string =
  match System.Environment.GetEnvironmentVariable "SAGEFS_NVIM_DIR" with
  | null | "" -> RepoPaths.siblingCheckoutDir repoRoot "sagefs.nvim"
  | dir -> Path.GetFullPath dir

let private nvimEventsPath : string =
  Path.Combine(nvimRepoDir, "lua", "sagefs", "events.lua")

let private nvimSsePath : string =
  Path.Combine(nvimRepoDir, "lua", "sagefs", "sse.lua")

/// Every `{ "<event_name>", "SageFsUserEvent" }` row in events.lua's
/// `EVENT_CATALOG` table. Both strings are captured; only the first is the
/// daemon-facing `event:` name, which is what the parity check needs.
let extractNvimEventCatalogNames (source: string) : Set<string> =
  Regex.Matches(source, "\{\\s*\"([a-z][a-z0-9_]*)\"\\s*,\\s*\"SageFs[A-Za-z]+\"")
  |> Seq.cast<Match>
  |> Seq.map (fun m -> m.Groups.[1].Value)
  |> Set.ofSeq

/// Every `type_to_action` ENTRY as a (wire name -> action) pair. The KEYS are
/// what the daemon may put in `event:` — that is the side the parity check
/// needs. The values are the action names init.lua registers handlers under,
/// so a wire name only counts as handled when its action is registered too.
let extractNvimDispatchMap (source: string) : Map<string, string> =
  let startMarker = "local type_to_action = {"
  let startIdx = source.IndexOf(startMarker)
  if startIdx < 0 then
    failwithf
      "Could not locate type_to_action in %s (expected to find %s). sse.lua's shape \
       changed — update this extractor to match it."
      nvimSsePath startMarker
  let rest = source.Substring(startIdx + startMarker.Length)
  let closeIdx = rest.IndexOf("\n  }", System.StringComparison.Ordinal)
  let block = if closeIdx > 0 then rest.Substring(0, closeIdx) else rest
  Regex.Matches(block, "(?m)^\\s*([A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*\"([a-z][a-z0-9_]*)\"")
  |> Seq.cast<Match>
  |> Seq.map (fun m -> (m.Groups.[1].Value, m.Groups.[2].Value))
  |> Map.ofSeq

/// Every `{ action = "<x>"` in init.lua's SSE_HANDLER_DEFS, plus every
/// `handlers.<name> =` custom handler. Together: the actions the plugin
/// actually has code for.
let extractNvimHandledActions (source: string) : Set<string> =
  let defs =
    Regex.Matches(source, "\\{\\s*action\\s*=\\s*\"([a-z][a-z0-9_]*)\"")
    |> Seq.cast<Match>
    |> Seq.map (fun m -> m.Groups.[1].Value)
  let custom =
    Regex.Matches(source, "handlers\\.([a-z][a-z0-9_]*)\\s*=\\s*function")
    |> Seq.cast<Match>
    |> Seq.map (fun m -> m.Groups.[1].Value)
  Seq.append defs custom |> Set.ofSeq

/// What the plugin's EVENT_CATALOG really contains, read from its own source.
let neovimCatalogDerived : Set<string> =
  nvimEventsPath |> File.ReadAllText |> extractNvimEventCatalogNames

let private neovimSseSource = File.ReadAllText nvimSsePath

let private neovimInitSource : string =
  Path.Combine(nvimRepoDir, "lua", "sagefs", "init.lua")
  |> File.ReadAllText

/// The daemon `event:` names the plugin actually ROUTES to a handler. The
/// honest "handled" set: a wire name counts only when sse.lua maps it onto an
/// action AND init.lua registers a handler under that action.
let neovimDispatchDerived : Set<string> =
  let registered = neovimInitSource |> extractNvimHandledActions
  neovimSseSource
  |> extractNvimDispatchMap
  |> Map.toList
  |> List.choose (fun (wire, action) ->
    if registered.Contains action && Regex.IsMatch(wire, "^[a-z][a-z0-9_]*$") then
      Some wire
    else
      None)
  |> Set.ofList

/// The daemon's SSE events that sagefs.nvim does NOT route to a handler — a
/// real gap in the plugin, named with a reason each. Measured, not asserted:
/// `neovimDispatchDerived` is read out of the plugin's own dispatch table, so
/// an entry here is either true or the set does not match. The pin test
/// below is what keeps it from growing by accident.
let neovimKnownGaps : Set<string> =
  // `eval_started` / `eval_heartbeat`: push-only eval decorations. The
  // plugin drives its own eval lifecycle from the /exec response, so these
  // cost nothing to ignore — but the daemon does send them, so they are
  // declared rather than assumed away.
  // `diagnosis_ready`: the diagnose() agent's report. The plugin surfaces
  // diagnosis through the /diagnostics HTTP route, not the push row.
  // `live_bindings`: the reflection-walked watch window. The plugin has no
  // bindings-view UI bound to it yet; it reads bindings from the
  // bindings_snapshot row instead.
  Set.ofList [ "eval_started"; "eval_heartbeat"; "diagnosis_ready"; "live_bindings" ]

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

  test "the Neovim extractors actually parsed real rows out of the plugin (sanity: a broken extractor must not silently pass)" {
    (neovimCatalogDerived.Count, 20)
    |> Expect.isGreaterThan
         "extractNvimEventCatalogNames found suspiciously few event names — either events.lua's \
          EVENT_CATALOG shape changed (update the regex) or the sibling checkout moved; either way \
          this must be investigated, not ignored."
    (neovimDispatchDerived.Count, 10)
    |> Expect.isGreaterThan
         "extractNvimDispatchActions ∩ extractNvimHandledActions found suspiciously few names — \
          sse.lua's type_to_action or init.lua's SSE_HANDLER_DEFS shape changed."
  }

  testList "Neovim handles every daemon SSE event (derived from the real sagefs.nvim source), or the gap is explicitly tracked" [
    for eventType in allDaemonSseEvents do
      test (sprintf "Neovim handles '%s', or it is a named gap in neovimKnownGaps" eventType) {
        (neovimDispatchDerived.Contains(eventType) || neovimKnownGaps.Contains(eventType))
        |> Expect.isTrue
             (sprintf
               "Event '%s' is emitted by the daemon but sagefs.nvim routes it nowhere: sse.lua's \
                type_to_action maps no wire name onto it, or init.lua registers no handler for the \
                action it maps to. Add the dispatch entry (and the handler) in that repo, or add \
                '%s' to neovimKnownGaps with a reason."
               eventType eventType)
      }
  ]

  test "the hand-kept Neovim mirror's retired names are gone: nothing asserts daemon coverage for an event no emitter produces" {
    // The old hand-kept `neovimHandledEvents` listed 23 event names that NO
    // F# source emits — a graveyard of retired daemon events kept as if it
    // were live coverage. The per-event test only ever walked
    // `allDaemonSseEvents` (daemon -> mirror), so every one of those phantoms
    // was invisible: the mirror could claim to handle events that do not
    // exist and the gate stayed green.
    //
    // `test_run_completed` was the sharpest case, and this list is why: the
    // formatter existed, the registry entry existed, and NO emitter produced the
    // event — `grep -rn "test_run_completed" --include=*.fs` resolved to this
    // file alone. Registering the name therefore demanded handler coverage for
    // an event no client could ever receive. It is now genuinely emitted (the
    // daemon publishes it once per finished run, gated on a real transition), so
    // it has left this list for the same reason it was on it: an event the
    // daemon does not emit must never be claimed as coverage.
    //
    // The rot is now impossible to reintroduce by hand (both halves are
    // derived), so this pins the DIRECTION that matters: an event the daemon
    // does not emit must never enter `allDaemonSseEvents`, because every
    // coverage test here is walked FROM that list. A phantom there would
    // demand handler coverage nobody can provide.
    let emitted = allDaemonSseEvents |> Set.ofList
    let retired =
      [ "test_run_started"
        "test_passed"
        "test_failed"
        "test_state"
        "connected"
        "disconnected"
        "warmup_context"
        "warmup_completed"
        "hotreload_snapshot"
        "hot_reload_triggered"
        "providers_detected"
        "test_recovery_needed"
        "coverage_updated"
        "reconnecting"
        "run_tests_requested" ]
    let leaked = retired |> List.filter (fun e -> Set.contains e emitted)
    leaked
    |> Expect.equal
         (sprintf
           "These retired daemon events must NOT be in allDaemonSseEvents — no emitter produces \
            them, so they are not daemon coverage. Leaked: %s"
           (String.concat ", " leaked))
         ([]: string list)
  }

  test "every daemon event the plugin does not route is a NAMED gap, and the set is pinned" {
    // `neovimKnownGaps` is the plugin's honest, hand-written list of daemon
    // events it does not route. Each entry is a real gap, so the pin is
    // "exactly these four": shrink it when a handler lands, and never grow it
    // silently — a new unrouted daemon event must be a decision, not an
    // accident.
    neovimKnownGaps
    |> Expect.equal
         "a handler landed (delete it here) or a NEW daemon event went unrouted without anyone \
          deciding that was OK (a real gap, not a test to relax)"
         (Set.ofList [ "eval_started"; "eval_heartbeat"; "diagnosis_ready"; "live_bindings" ])
  }

  test "the derived Neovim dispatch set covers every daemon event except the four named gaps" {
    allDaemonSseEvents
    |> List.filter (neovimDispatchDerived.Contains >> not)
    |> Set.ofList
    |> Expect.equal
         "these daemon events are emitted but the plugin routes none of them"
         neovimKnownGaps
  }

  test "vscodeHandledEventsDerived superset check — no typos in the derived set" {
    // Ensure every entry the extractor found is a valid lowercase_snake_case string —
    // guards against the regex accidentally matching a non-event string literal.
    vscodeHandledEventsDerived
    |> Set.iter (fun e ->
      let valid = Regex.IsMatch(e, "^[a-z][a-z0-9_]*$")
      valid |> Expect.isTrue (sprintf "VS Code derived event '%s' should be lowercase_snake_case" e))
  }

  test "neovim derived sets superset check — no typos in the derived sets" {
    Set.iter
      (fun e ->
        let valid = Regex.IsMatch(e, "^[a-z][a-z0-9_]*$")
        valid |> Expect.isTrue (sprintf "Neovim event '%s' should be lowercase_snake_case" e))
      (Set.union neovimCatalogDerived neovimDispatchDerived)
  }

  test "adding new daemon event requires updating this test (self-documenting)" {
    // If this count changes, a developer added a new SSE event. The two
    // handler sets are derived, so nothing hand-kept needs editing — but the
    // vscode/neovim gap tests will fail if neither extension handles it, and
    // that failure is the point.
    allDaemonSseEvents.Length
    |> Expect.equal
         "if this fails, you added a daemon SSE event - the VS Code and Neovim coverage tests above \
           will tell you whether either extension handles it"
         25
  }
]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant sseParityTests |> ignore
