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

/// Runs one already-resolved `WireStep` end to end: resolve the click
/// target's live `ScreenRect` through the Dashboard actor (never a guessed
/// coordinate), deliver the input plan via XTest, observe the expectation,
/// and report — recording brackets exactly this step's active window (§4.5).
let private runStep
  (live: XTest.LiveDisplay)
  (dashboard: Dashboard.Handle)
  (outDir: string)
  (sw: Diagnostics.Stopwatch)
  (step: WireStep)
  : Async<WireStepResult> =
  async {
    let startedMs = sw.ElapsedMilliseconds
    let segmentPath = IO.Path.Combine(outDir, sprintf "step-%02d.mkv" step.Index)
    let recording = Recorder.start CellDisplay segmentPath

    let! rectOpt =
      match step.ClickSelector with
      | None -> async { return None }
      | Some selector ->
        async {
          let! box = dashboard.Page.Locator(selector).BoundingBoxAsync() |> Async.AwaitTask

          return
            match box with
            | null -> None
            | b -> Some { X = int b.X; Y = int b.Y; W = int b.Width; H = int b.Height }
        }

    let targetMissing = step.ClickSelector.IsSome && rectOpt.IsNone

    let pointerPath =
      match rectOpt, actionOf step with
      | Some rect, Some action ->
        let requests = Input.plan action rect
        XTest.deliver live requests
        pointerPathOf requests
      | _ -> []

    let! observed =
      match step.ExpectSelector with
      | None -> async { return true }
      | Some selector ->
        async {
          try
            let opts = Microsoft.Playwright.PageWaitForSelectorOptions(Timeout = 8000.0f)
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
      if targetMissing then
        sprintf "click target '%s' not found (no bounding box)" (step.ClickSelector |> Option.defaultValue "")
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
      let! dashboard = Dashboard.launch plan.ChromePath plan.UserDataDir { X = 0; Y = 0; W = 1280; H = 720 } plan.PageUrl

      let mutable results = []

      for step in plan.Steps |> List.sortBy (fun s -> s.Index) do
        let! result = runStep live dashboard plan.OutDir sw step
        results <- results @ [ result ]

      do! Dashboard.close dashboard
      XTest.closeDisplay live

      let log: Wire.StepLog = { ScenarioId = plan.ScenarioId; Steps = results }
      Wire.serializeStepLog log |> Console.Out.WriteLine
      Console.Out.Flush()
      return (if log.Steps |> List.forall (fun s -> s.Outcome = "Passed") then 0 else 1)
  }
