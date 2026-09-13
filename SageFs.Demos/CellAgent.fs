/// The in-sandbox executor (demo-gif-plan.md §4.1), translated from the
/// Phase-0 spike's `spike/cell-agent/Program.fs`: the ONE process bwrap runs
/// as a cell's pid 1. It reads exactly one line of `Wire.ScenarioPlan` JSON
/// from stdin, assembles the `LiveActor`(s) the plan needs (Island F's
/// actor-dispatch seam, demo-actors-plan.md §1.2 — the cell-agent no longer
/// hard-calls `Dashboard.launch` by name), drives the XTest input edge + the
/// per-step ffmpeg recorder for every step in order through whichever actor
/// it targets, and writes exactly one line of `Wire.StepLog` JSON to stdout.
/// Nothing else crosses the namespace wall (§4.1) except the `/out` bind
/// mount the recorded segments land in.
module SageFs.Demos.CellAgent

open System
open SageFs.Demos.Domain
open SageFs.Demos.Wire
open SageFs.Demos.Actors
open SageFs.Demos.Actors.Actor

/// The X11 display every cell uses — every cell gets a private `/tmp`
/// (§4.1), so every cell can reuse the same display name without colliding.
[<Literal>]
let CellDisplay = ":99"

/// Maps a `Wire` `Client`/`TargetActor` string to the strongly-typed
/// `ActorId` the `LiveActor` map is keyed by (Island F, §1.2). Unrecognized
/// strings return `None` — never a silent default to Dashboard — so an
/// unknown or not-yet-implemented actor fails loud instead of quietly
/// driving the wrong window. Not `private`: `CellAgentTests.fs` exercises
/// this mapping directly to prove the dispatch seam without spawning a real
/// cell (Xvfb/Chromium/daemon).
let actorIdOfString (s: string) : ActorId option =
  match s with
  | "dashboard" -> Some ActorId.Dashboard
  | "vscode" -> Some ActorId.VsCode
  | "neovim" -> Some ActorId.Neovim
  | "app" -> Some ActorId.App
  | "agent" -> Some ActorId.Agent
  | _ -> None

/// Launches and wraps every actor `plan.Client` needs into the
/// `Map<ActorId, LiveActor>` the cell-agent dispatches every step through
/// (Island F's seam, demo-actors-plan.md §1.2/§1.3). Today only the
/// Dashboard actor is implemented — every other `Client` fails loud with an
/// actionable message (the "never a silent green no-op" doctrine every
/// actor's own `doctor`/`record` path must follow, §2) rather than launching
/// nothing and letting every step silently fail one at a time. Actor islands
/// extend this one `match` arm by arm; they never touch anything else here.
let private assembleActors (plan: ScenarioPlan) : Async<Result<Map<ActorId, LiveActor>, string>> =
  async {
    match plan.Client with
    | "dashboard" ->
      let! handle = Dashboard.launch plan.ChromePath plan.UserDataDir { X = 0; Y = 0; W = 1280; H = 720 } plan.PageUrl
      return Ok(Map.ofList [ ActorId.Dashboard, Dashboard.toLiveActor handle ])
    // Agent island (demo-actors-plan.md §2.4) — one new match arm, exactly
    // the extension this function's own doc comment invites ("Actor islands
    // extend this one match arm by arm; they never touch anything else
    // here"). No `plan.PageUrl` (that field stays dashboard-shaped, per
    // `Runtime.fs`'s `wirePlanOf`): the Agent actor writes and opens its own
    // `file://` viz page inside the cell and calls the daemon's own
    // already-bound MCP port directly.
    | "agent" ->
      let! handle = Agent.launch plan.ChromePath plan.UserDataDir { X = 0; Y = 0; W = 1280; H = 720 }
      return Ok(Map.ofList [ ActorId.Agent, Agent.toLiveActor handle ])
    | other -> return Error(sprintf "cell-agent: unsupported Client '%s' (only 'dashboard'/'agent' are implemented)" other)
  }

/// Reconstructs the `Action` kind (Click vs Type) `Input.plan` needs from a
/// `WireStep`'s flattened fields. `Input.plan` only ever pattern-matches on
/// which `Action` case it got — the `Target` payload inside is never
/// inspected (the `ScreenRect` parameter is what actually drives the
/// planners) — so carrying the step's own resolved actor as the placeholder
/// target is correct here, not a lie: see `Input.fs`'s `plan`, every case
/// ignores the target it's carrying.
let private actionOf (targetActor: ActorId) (step: WireStep) : Action option =
  match step.ClickSelector, step.TypeText with
  | Some _, Some text -> Some(Action.Type(Target.WindowCenter targetActor, Text.mk text, CadenceSeed.ofId step.Caption))
  | Some _, None -> Some(Action.Click(Target.WindowCenter targetActor))
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

/// Runs one already-resolved `WireStep` end to end: resolve the click
/// target's live `ScreenRect` through the step's own target actor
/// (`actors.[targetActor]`, Island F's seam — no more hard-coded Dashboard
/// handle), deliver the input plan via XTest — chaining a SECOND click at
/// `SubmitSelector` (if any) starting from wherever the primary action's
/// motion ended, so the cursor moves on continuously instead of resetting
/// (§9's "type the expression, then click [EVAL]" demo step) — observe the
/// expectation through that same actor, and report; recording brackets
/// exactly this step's active window (§4.5).
let private runStep
  (live: XTest.LiveDisplay)
  (mapping: KeyboardMapping)
  (actors: Map<ActorId, LiveActor>)
  (targetActor: ActorId)
  (outDir: string)
  (sw: Diagnostics.Stopwatch)
  (step: WireStep)
  : Async<WireStepResult> =
  async {
    let actor = actors.[targetActor]
    let startedMs = sw.ElapsedMilliseconds
    let segmentPath = IO.Path.Combine(outDir, sprintf "step-%02d.mkv" step.Index)
    let recording = Recorder.start CellDisplay segmentPath

    // `PreClickSelector` (§9's "expand this collapsed panel, then type into
    // it — same step, no gap"): resolved and clicked FIRST, deliberately
    // with NO settle delay before the primary click that follows — the
    // whole point of folding this into one step is to close the window a
    // server-driven re-render could otherwise reopen the panel in.
    //
    // `ResolveRect`/`Observe` below throw on a missing/slow selector inside
    // each actor's own implementation and are caught there (§4.11: a step's
    // own resolution failure is real signal, never something to let crash
    // the process that still needs to write its StepLog for every OTHER
    // step) — the cell-agent only ever sees the clean `option`/`bool`.
    let! preClickRectOpt =
      match step.PreClickSelector with
      | None -> async { return None }
      | Some selector -> actor.ResolveRect selector

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
      | Some selector -> actor.ResolveRect selector

    let primaryMissing = step.ClickSelector.IsSome && rectOpt.IsNone

    let primaryPointerPath =
      match rectOpt, actionOf targetActor step with
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
      | Some selector -> actor.ResolveRect selector

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

    // 90s, not 8s: a real session warmup (FSI cold start inside a fresh
    // tmpfs cell with no warm disk cache) can genuinely take longer than a
    // UI-click expectation ever needed to, especially under CPU contention —
    // confirmed directly: a still frame from a 45s-timeout run showed the
    // tabline had ALREADY reached "[Ready]" (readiness genuinely happens,
    // the selector is right), just not comfortably inside a 45s ceiling on a
    // busy box. §9's "Await session Ready" step needs a ceiling sized for
    // the real transition, not one sized for "did a button click register".
    let! observed =
      match step.ExpectSelector with
      | None -> async { return true }
      | Some selector -> actor.Observe selector 90000.0

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

      match! assembleActors plan with
      | Error message ->
        XTest.closeDisplay live

        let log: Wire.StepLog =
          { ScenarioId = plan.ScenarioId
            Steps =
              [ { Index = 0
                  Caption = "actor assembly"
                  Segment = ""
                  StartedMs = 0L
                  EndedMs = sw.ElapsedMilliseconds
                  PointerPath = []
                  ObservedAtMs = 0L
                  Outcome = "Failed"
                  Message = message } ] }

        Wire.serializeStepLog log |> Console.Out.WriteLine
        Console.Out.Flush()
        return 1
      | Ok actors ->

      let mutable results = []

      for step in plan.Steps |> List.sortBy (fun s -> s.Index) do
        let requestedActor = step.TargetActor |> Option.defaultValue plan.Client
        let targetActorId = requestedActor |> actorIdOfString |> Option.filter actors.ContainsKey

        match targetActorId with
        | None ->
          results <-
            results
            @ [ { Index = step.Index
                  Caption = step.Caption
                  Segment = ""
                  StartedMs = sw.ElapsedMilliseconds
                  EndedMs = sw.ElapsedMilliseconds
                  PointerPath = []
                  ObservedAtMs = 0L
                  Outcome = "Failed"
                  Message = sprintf "no live actor for target '%s'" requestedActor } ]
        | Some actorId ->
          let! result = runStep live mapping actors actorId plan.OutDir sw step
          results <- results @ [ result ]

      for actor in actors |> Map.toList |> List.map snd do
        do! actor.Close()

      XTest.closeDisplay live

      let log: Wire.StepLog = { ScenarioId = plan.ScenarioId; Steps = results }
      Wire.serializeStepLog log |> Console.Out.WriteLine
      Console.Out.Flush()
      return (if log.Steps |> List.forall (fun s -> s.Outcome = "Passed") then 0 else 1)
  }
