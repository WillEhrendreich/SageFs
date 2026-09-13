/// The in-sandbox executor (demo-gif-plan.md §4.1), translated from the
/// Phase-0 spike's `spike/cell-agent/Program.fs`: the ONE process bwrap runs
/// as a cell's pid 1. It reads exactly one line of `Wire.ScenarioPlan` JSON
/// from stdin, drives the Dashboard actor + the XTest input edge + the
/// per-step ffmpeg recorder for every step in order, and writes exactly one
/// line of `Wire.StepLog` JSON to stdout. Nothing else crosses the namespace
/// wall (§4.1) except the `/out` bind mount the recorded segments land in.
module SageFs.Demos.CellAgent

open System
open SageFs.Demos.Domain
open SageFs.Demos.Wire
open SageFs.Demos.Actors

/// The X11 display every cell uses — every cell gets a private `/tmp`
/// (§4.1), so every cell can reuse the same display name without colliding.
[<Literal>]
let CellDisplay = ":99"

/// Reconstructs the `Action` kind (Click vs Type) `Input.plan` needs from a
/// `WireStep`'s flattened fields. `Input.plan` only ever pattern-matches on
/// which `Action` case it got — the `Target` payload inside is never
/// inspected (the `ScreenRect` parameter is what actually drives the
/// planners) — so a neutral placeholder target is correct here, not a lie:
/// see `Input.fs`'s `plan`, every case ignores the target it's carrying.
let private actionOf (step: WireStep) : Action option =
  match step.ClickSelector, step.TypeText with
  | Some _, Some text -> Some(Action.Type(Target.WindowCenter ActorId.Dashboard, Text.mk text, CadenceSeed.ofId step.Caption))
  | Some _, None -> Some(Action.Click(Target.WindowCenter ActorId.Dashboard))
  | None, _ -> None

let private pointerPathOf (requests: X11Request list) : int[] list =
  requests
  |> List.choose (function
    | X11Request.FakeMotion(x, y) -> Some [| x; y |]
    | _ -> None)

/// Where a hop's OWN motion should start from: the previous hop's last
/// delivered point, continuing the cursor on from there, or `Input.restPosition`
/// if there was no previous hop — never a hard-coded corner mid-step (§9: a
/// cursor that teleports back to a fixed point between a step's own hops
/// reads as obviously synthetic, not human-driven).
let private lastPointOr (fallback: Point) (path: int[] list) : Point =
  path
  |> List.tryLast
  |> Option.map (fun xy -> { X = xy.[0]; Y = xy.[1] })
  |> Option.defaultValue fallback

/// Resolves `selector`'s live bounding box through the Dashboard actor
/// (never a guessed coordinate) — shared by the primary click/type target
/// and the `SubmitSelector` chained click. `BoundingBoxAsync` auto-waits up
/// to Playwright's own default action timeout (30s) and THROWS on timeout
/// rather than returning null (unlike the plain "does this exist" check
/// `WaitForSelectorAsync` gets elsewhere in this file) — bounded explicitly
/// here and wrapped in `try/with` so a missing/slow selector reports as
/// "target missing" (a `Failed` step, still a clean `StepLog`) instead of
/// an unhandled exception unwinding out of the whole cell-agent (§4.11: a
/// step's own resolution failure is real signal, never something to let
/// crash the process that still needs to write its StepLog for every OTHER
/// step).
let private resolveRect (dashboard: Dashboard.Handle) (selector: string) : Async<ScreenRect option> =
  async {
    try
      let opts = Microsoft.Playwright.LocatorBoundingBoxOptions(Timeout = 10000.0f)
      let! box = dashboard.Page.Locator(selector).BoundingBoxAsync(opts) |> Async.AwaitTask

      return
        match box with
        | null -> None
        | b -> Some { X = int b.X; Y = int b.Y; W = int b.Width; H = int b.Height }
    with _ ->
      return None
  }

/// Runs one already-resolved `WireStep` end to end: resolve the click
/// target's live `ScreenRect` through the Dashboard actor, deliver the input
/// plan via XTest — chaining a SECOND click at `SubmitSelector` (if any)
/// starting from wherever the primary action's motion ended, so the cursor
/// moves on continuously instead of resetting (§9's "type the expression,
/// then click [EVAL]" demo step) — observe the expectation, and report;
/// recording brackets exactly this step's active window (§4.5).
let private runStep
  (live: XTest.LiveDisplay)
  (mapping: KeyboardMapping)
  (dashboard: Dashboard.Handle)
  (outDir: string)
  (sw: Diagnostics.Stopwatch)
  (step: WireStep)
  : Async<WireStepResult> =
  async {
    let startedMs = sw.ElapsedMilliseconds
    let segmentPath = IO.Path.Combine(outDir, sprintf "step-%02d.mkv" step.Index)
    let recording = Recorder.start CellDisplay segmentPath

    // `PreClickSelector` (§9's "expand this collapsed panel, then type into
    // it — same step, no gap"): resolved and clicked FIRST, deliberately
    // with NO settle delay before the primary click that follows — the
    // whole point of folding this into one step is to close the window a
    // server-driven re-render could otherwise reopen the panel in.
    let! preClickRectOpt =
      match step.PreClickSelector with
      | None -> async { return None }
      | Some selector -> resolveRect dashboard selector

    let preClickMissing = step.PreClickSelector.IsSome && preClickRectOpt.IsNone

    let preClickPointerPath =
      match preClickRectOpt with
      | Some rect ->
        let requests = Input.clickFrom Input.restPosition rect
        XTest.deliver live requests
        pointerPathOf requests
      | None -> []

    let! rectOpt =
      match step.ClickSelector with
      | None -> async { return None }
      | Some selector -> resolveRect dashboard selector

    let primaryMissing = step.ClickSelector.IsSome && rectOpt.IsNone

    let primaryPointerPath =
      match rectOpt, actionOf step with
      | Some rect, Some action ->
        // Continue on from wherever the pre-click hop (if any) actually
        // ended — never reset to the corner mid-step (§9).
        let start = lastPointOr Input.restPosition preClickPointerPath
        let requests = Input.plan mapping start action rect
        XTest.deliver live requests
        preClickPointerPath @ pointerPathOf requests
      | _ -> preClickPointerPath

    // A real user's next click always lands after their FIRST click's own
    // on-screen effect (a navigation, an SSE-pushed re-render) has actually
    // shown up — deliver-then-immediately-resolve the next target does not,
    // and was confirmed directly against real recordings to race a session
    // card's click against the dashboard's own SSE-driven navigation: the
    // click was delivered, but `SubmitSelector` resolution (or the FOLLOWING
    // step's OWN primary resolution) sometimes ran before the session view
    // had actually mounted, so "#evaluate-section summary"/the eval textarea
    // intermittently reported "not found" even though the selector itself is
    // correct. Settling briefly after EVERY delivered click — not just
    // before a chained `SubmitSelector` — closes the same race for the next
    // STEP's own primary click too.
    if not (List.isEmpty primaryPointerPath) then
      do! Async.Sleep 500

    let! submitRectOpt =
      match step.SubmitSelector with
      | None -> async { return None }
      | Some selector -> resolveRect dashboard selector

    let submitMissing = step.SubmitSelector.IsSome && submitRectOpt.IsNone

    let submitPointerPath =
      match submitRectOpt with
      | Some rect ->
        let startPoint = lastPointOr Input.restPosition primaryPointerPath
        let requests = Input.clickFrom startPoint rect
        XTest.deliver live requests
        pointerPathOf requests
      | None -> []

    let pointerPath = primaryPointerPath @ submitPointerPath
    let targetMissing = preClickMissing || primaryMissing || submitMissing

    let! observed =
      match step.ExpectSelector with
      | None -> async { return true }
      | Some selector ->
        async {
          try
            // 90s, not 8s: a real session warmup (FSI cold start inside a
            // fresh tmpfs cell with no warm disk cache) can genuinely take
            // longer than a UI-click expectation ever needed to, especially
            // under CPU contention — confirmed directly: a still frame from
            // a 45s-timeout run showed the tabline had ALREADY reached
            // "[Ready]" (readiness genuinely happens, the selector is
            // right), just not comfortably inside a 45s ceiling on a busy
            // box. §9's "Await session Ready" step needs a ceiling sized for
            // the real transition, not one sized for "did a button click
            // register".
            let opts = Microsoft.Playwright.PageWaitForSelectorOptions(Timeout = 90000.0f)
            let! _ = dashboard.Page.WaitForSelectorAsync(selector, opts) |> Async.AwaitTask
            return true
          with _ ->
            return false
        }

    let observedAtMs = sw.ElapsedMilliseconds
    // A short settle so the recorded segment ends on a held, readable final
    // frame rather than cutting the instant the expectation resolves (§9:
    // "≥ 1.0s dwell after every observed change").
    do! Async.Sleep(max 200 step.DwellMs)
    do! Recorder.stop recording

    let outcome = not targetMissing && observed

    let message =
      if preClickMissing then
        sprintf "pre-click target '%s' not found (no bounding box)" (step.PreClickSelector |> Option.defaultValue "")
      elif primaryMissing then
        sprintf "click target '%s' not found (no bounding box)" (step.ClickSelector |> Option.defaultValue "")
      elif submitMissing then
        sprintf "submit target '%s' not found (no bounding box)" (step.SubmitSelector |> Option.defaultValue "")
      else
        match step.ExpectSelector with
        | Some sel when observed -> sprintf "'%s' appeared" sel
        | Some sel -> sprintf "'%s' did not appear within timeout" sel
        | None -> "no expectation for this step"

    return
      { Index = step.Index
        Caption = step.Caption
        Segment = segmentPath
        StartedMs = startedMs
        EndedMs = sw.ElapsedMilliseconds
        PointerPath = pointerPath
        ObservedAtMs = observedAtMs
        Outcome = (if outcome then "Passed" else "Failed")
        Message = message }
  }

/// The cell-agent's whole run: read the plan, drive every step in order,
/// write the StepLog. Never throws past this function — the only `failwith`
/// (an empty/missing stdin line) runs inside this function body, not a
/// module-level `let`/`.cctor` (§4.11), and `Program.fs`'s `cell-agent` verb
/// wraps the call in the top-level `try/with` §4.11 requires everywhere.
let run () : Async<int> =
  async {
    let sw = Diagnostics.Stopwatch.StartNew()
    let line = Console.In.ReadLine()

    if String.IsNullOrWhiteSpace line then
      failwith "no ScenarioPlan JSON received on stdin"

    let plan = Wire.deserializePlan line

    match XTest.openDisplay (Display CellDisplay) with
    | None ->
      Wire.serializeStepLog { ScenarioId = plan.ScenarioId; Steps = [] } |> Console.Out.WriteLine
      Console.Out.Flush()
      return 1
    | Some live ->
      // Fetched ONCE, live, from this cell's own Xvfb display — never the
      // fictitious "keysym == keycode" placeholder `Input.fs` used to carry
      // internally (§9's root cause for corrupted typed text: raw ASCII
      // codepoints delivered as literal X11 keycodes land on whatever
      // physical key the real layout happens to put at that number).
      let mapping = XTest.keyboardMapping live
      let! dashboard = Dashboard.launch plan.ChromePath plan.UserDataDir { X = 0; Y = 0; W = 1280; H = 720 } plan.PageUrl

      let mutable results = []

      for step in plan.Steps |> List.sortBy (fun s -> s.Index) do
        let! result = runStep live mapping dashboard plan.OutDir sw step
        results <- results @ [ result ]

      do! Dashboard.close dashboard
      XTest.closeDisplay live

      let log: Wire.StepLog = { ScenarioId = plan.ScenarioId; Steps = results }
      Wire.serializeStepLog log |> Console.Out.WriteLine
      Console.Out.Flush()
      return (if log.Steps |> List.forall (fun s -> s.Outcome = "Passed") then 0 else 1)
  }
