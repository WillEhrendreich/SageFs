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

/// VS Code's SHORT, cell-scratch `--user-data-dir` (mirrors
/// `Runtime.VsCode.RecommendedUserDataDir`'s own literal — re-declared here
/// rather than referenced across the module boundary, exactly like this
/// file's own `CellDisplay` re-declaration: `Runtime.VsCode.fs` compiles
/// AFTER this file in the fsproj, so it cannot be referenced from here).
[<Literal>]
let private VsCodeUserDataDirCell = "/home/demo/vsc"

/// The extension's own loopback control-channel port (`SAGEFS_DEBUG_RECTS_PORT`,
/// `Actors/VsCode.fs`'s `Handle.ControlBaseUrl`) — fixed per cell, legal
/// because every cell has a private network namespace (§4.1).
[<Literal>]
let private VsCodeControlPort = 47760

/// The daemon's own fixed MCP/dashboard ports every cell binds to (mirrors
/// `Runtime.Core`'s own `McpPort`/`DashboardPort` literals — re-declared
/// here for the same never-touch-the-compile-order reason as
/// `VsCodeUserDataDirCell` above).
[<Literal>]
let private McpPortCell = 47749

[<Literal>]
let private DashboardPortCell = 47750

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

/// The full-screen fallback rect every actor used before `Wire.ScenarioPlan`
/// carried real per-actor placement (`ActorRects`, seam-integration
/// threading) — still correct for every `DashboardOnly`-layout scenario
/// (Dashboard, Agent), and a safe default for anything a plan's own
/// `ActorRects` genuinely has no entry for.
let private fullScreenRect: Rect = { X = 0; Y = 0; W = 1280; H = 720 }

/// This actor's placed rect for `token` (`Wire.WireRect.ActorToken`), from
/// the real layout `Runtime.fs`'s `wireRectsOf` computed — never a hardcoded
/// guess once a plan actually carries one.
let private rectFor (plan: ScenarioPlan) (token: string) : Rect =
  plan.ActorRects
  |> List.tryFind (fun r -> r.ActorToken = token)
  |> Option.map (fun r -> { X = r.X; Y = r.Y; W = r.W; H = r.H })
  |> Option.defaultValue fullScreenRect

/// Left-biased map union (`b`'s keys win on collision, which never happens
/// here — every actor arm below builds disjoint single/double-entry maps).
let private mergeActors (a: Map<ActorId, LiveActor>) (b: Map<ActorId, LiveActor>) : Map<ActorId, LiveActor> =
  Map.fold (fun acc k v -> Map.add k v acc) a b

/// The daemon's own MCP HTTP port every cell binds to (mirrors
/// `Actors.VsCode.DaemonMcpPort`'s own re-declaration of this same fixed,
/// documented cell constant — see that module's doc for why it is
/// re-declared per call site rather than shared across the seam boundary).
[<Literal>]
let private DaemonBaseUrl = "http://127.0.0.1:47749"

/// Launches the App co-actor when `plan.App` names a real kind — the SECOND
/// window every hot-reload scenario needs on screen (demo-actors-plan.md
/// §2.3). `Map.empty` (never an `Error`) when the plan carries no App config
/// at all: most scenarios (REPL/LiveTesting/Sessions/Agent) genuinely have no
/// App pane, and that is not a failure to report.
let private appActorsOf (plan: ScenarioPlan) : Async<Result<Map<ActorId, LiveActor>, string>> =
  async {
    match plan.App |> Option.bind (fun cfg -> cfg.Kind) with
    | None -> return Ok Map.empty
    | Some kindToken ->

    match kindToken with
    | "web"
    | "raylib"
    | "console" ->
      let appKind = if kindToken = "web" then AppKind.Web elif kindToken = "raylib" then AppKind.Raylib else AppKind.Console

      let launchConfig: App.LaunchConfig =
        { Display = CellDisplay
          // A `Web`-kind app's real, daemon-published run-app URL is not yet
          // threaded onto this wire (`Runtime.fs`'s own documented gap) —
          // `Raylib`/`Console` need no URL at all (`Actors/App.fs`'s
          // `launchWindowed`, a real X11 window-discovery diff instead).
          AppUrl = None
          ChromePath = plan.ChromePath
          UserDataDir = "/home/demo/chrome-profile-app"
          ReadyTimeoutMs = App.LaunchConfig.DefaultReadyTimeoutMs }

      match! App.launch launchConfig (rectFor plan "app") appKind with
      | Error message -> return Error(sprintf "App co-actor: %s" message)
      | Ok handle -> return Ok(Map.ofList [ ActorId.App, App.toLiveActor handle ])
    | other -> return Error(sprintf "cell-agent: unknown App kind '%s' on the wire" other)
  }

/// Launches and wraps every actor `plan.Client` needs into the
/// `Map<ActorId, LiveActor>` the cell-agent dispatches every step through
/// (Island F's seam, demo-actors-plan.md §1.2/§1.3). Every unrecognized
/// `Client` still fails loud with an actionable message (the "never a
/// silent green no-op" doctrine every actor's own `doctor`/`record` path
/// must follow, §2) rather than launching nothing and letting every step
/// silently fail one at a time.
let private assembleActors (plan: ScenarioPlan) : Async<Result<Map<ActorId, LiveActor>, string>> =
  async {
    match plan.Client with
    | "dashboard" ->
      let! handle = Dashboard.launch plan.ChromePath plan.UserDataDir (rectFor plan "dashboard") plan.PageUrl
      return Ok(Map.ofList [ ActorId.Dashboard, Dashboard.toLiveActor handle ])
    // No `plan.PageUrl` (that field stays dashboard-shaped, per
    // `Runtime.fs`'s `wirePlanOf`): the Agent actor writes and opens its own
    // `file://` viz page inside the cell and calls the daemon's own
    // already-bound MCP port directly.
    | "agent" ->
      let! handle = Agent.launch plan.ChromePath plan.UserDataDir (rectFor plan "agent")
      return Ok(Map.ofList [ ActorId.Agent, Agent.toLiveActor handle ])
    // The standalone editor arms (demo-actors-plan.md §2.1/§2.2): each
    // co-launches the SAME shared Dashboard narrator pane `EditorFull`/
    // `EditorLeft` always places alongside the editor (real, on-screen, and
    // the actor `expectationWire`'s dashboard-shaped expectations are
    // genuinely observed through — never a DOM fabrication), plus the App
    // co-actor when this scenario's layout also reserves it a pane.
    | "vscode" ->
      match plan.VsCode, plan.WorkspaceDir with
      | None, _
      | _, None -> return Error "cell-agent: Client 'vscode' requires plan.VsCode and plan.WorkspaceDir (Runtime.fs's wirePlanOf/resolveActorExtras must supply both)"
      | Some vsCodeConfig, Some workspaceDir ->

      match vsCodeConfig.ExtensionDevPath with
      | None -> return Error "cell-agent: plan.VsCode.ExtensionDevPath is missing"
      | Some extensionDevPath ->

      // The extension reads its own `sagefs.mcpPort`/`sagefs.dashboardPort`
      // workspace settings (default 37749/37750 — the REAL daemon's ports)
      // to build every HTTP call it makes (`SageFsClient.fs`'s `baseUrl`).
      // This cell's daemon listens on the fixed cell ports instead
      // (`McpPortCell`/`DashboardPortCell`), and the real project directory
      // this actor opens as a workspace is RO-bound (shared with the host —
      // no `.vscode/settings.json` can be written into it), so the only
      // writable place to point the extension at the right ports is its own
      // `--user-data-dir` PROFILE settings (`User/settings.json`), seeded
      // BEFORE launch — without this the extension would try to reach a
      // daemon that does not exist inside this cell's network namespace.
      let userSettingsDir = IO.Path.Combine(VsCodeUserDataDirCell, "User")
      IO.Directory.CreateDirectory userSettingsDir |> ignore

      // `workbench.startupEditor: "none"` suppresses the Getting-
      // Started/Copilot-welcome page a fresh `--user-data-dir` profile
      // opens on first launch — confirmed directly against a real
      // recording: it renders as the WHOLE editor pane's content (a modal
      // overlay VS Code treats as just another editor tab), silently
      // hiding the real file every subsequent click/type step needs to see.
      IO.File.WriteAllText(
        IO.Path.Combine(userSettingsDir, "settings.json"),
        sprintf
          "{ \"sagefs.mcpPort\": %d, \"sagefs.dashboardPort\": %d, \"workbench.startupEditor\": \"none\" }"
          McpPortCell
          DashboardPortCell
      )

      match! VsCode.launch "/vscode-bin/code" extensionDevPath VsCodeUserDataDirCell "/home/demo/vsc-extensions" workspaceDir (rectFor plan "vscode") CellDisplay VsCodeControlPort with
      | Error message -> return Error(sprintf "VS Code actor: %s" message)
      | Ok handle ->

      let vsCodeActor = VsCode.toLiveActor handle DaemonBaseUrl
      let! dashboardHandle = Dashboard.launch plan.ChromePath plan.UserDataDir (rectFor plan "dashboard") plan.PageUrl
      let dashboardActor = Dashboard.toLiveActor dashboardHandle
      // The App co-actor is deliberately NOT launched here: it "finds,
      // places, and observes" a window the session's own run-app call
      // produces, and at actor-ASSEMBLY time no session/app has been
      // started yet (a scenario's own `Setup(RunApp)` step, which the run
      // loop below launches App right after, hasn't run). Launching it
      // eagerly here made every joint hot-reload scenario fail loud before
      // its very first real step ever ran (confirmed directly against a
      // real recording: `App(Raylib): no new X11 window appeared within
      // 30000ms` at "actor assembly", 0 of the scenario's own steps
      // attempted) — see `run`'s own lazy-launch handling.
      return Ok(Map.ofList [ ActorId.VsCode, vsCodeActor; ActorId.Dashboard, dashboardActor ])
    | "neovim" ->
      match plan.Nvim with
      | None -> return Error "cell-agent: Client 'neovim' requires plan.Nvim (Runtime.fs's wirePlanOf/resolveActorExtras must supply it)"
      | Some nvimConfig ->

      match nvimConfig.PluginRuntimePath with
      | None -> return Error "cell-agent: plan.Nvim.PluginRuntimePath is missing"
      | Some pluginRuntimePath ->

      // A bare `WorkspaceDir` (a directory) makes Neovim open `netrw`'s
      // directory listing instead of an editable buffer (confirmed directly
      // against a real recording — `Wire.ScenarioPlan.NvimOpenFilePath`'s own
      // doc) — `Runtime.fs`'s `wirePlanOf` resolves the real file this
      // scenario needs open; falling back to the bare directory only if
      // genuinely nothing better was resolved (never worse than before).
      let openFilePath = plan.NvimOpenFilePath |> Option.orElse plan.WorkspaceDir

      let! neovimHandle =
        Neovim.launch
          "/usr/bin/kitty"
          "/usr/bin/nvim"
          (Some pluginRuntimePath)
          (Display CellDisplay)
          (rectFor plan "neovim")
          "/home/demo/nvim-work"
          openFilePath
          McpPortCell
          DashboardPortCell
          plan.WorkspaceDir

      let neovimActor = Neovim.toLiveActor neovimHandle
      let! dashboardHandle = Dashboard.launch plan.ChromePath plan.UserDataDir (rectFor plan "dashboard") plan.PageUrl
      let dashboardActor = Dashboard.toLiveActor dashboardHandle
      // App co-actor: NOT launched eagerly here — see the identical doc on
      // the "vscode" arm above; `run`'s own lazy-launch handling brings it
      // in once a real `Setup(RunApp)` step has actually run.
      return Ok(Map.ofList [ ActorId.Neovim, neovimActor; ActorId.Dashboard, dashboardActor ])
    | other -> return Error(sprintf "cell-agent: unsupported Client '%s' (only 'dashboard'/'agent'/'vscode'/'neovim' are implemented)" other)
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
  (actorsRef: Map<ActorId, LiveActor> ref)
  (targetActor: ActorId)
  (observeActor: ActorId)
  (plan: ScenarioPlan)
  (sw: Diagnostics.Stopwatch)
  (step: WireStep)
  : Async<WireStepResult> =
  async {
    let actor = actorsRef.Value.[targetActor]
    let startedMs = sw.ElapsedMilliseconds
    let segmentPath = IO.Path.Combine(plan.OutDir, sprintf "step-%02d.mkv" step.Index)
    let recording = Recorder.start CellDisplay segmentPath

    // `Action.Setup`'s wire image (seam-integration threading): a real,
    // non-filmed API-level command run through this step's own target
    // actor's `Command` surface (e.g. Neovim's `:SageFsRunApp`) BEFORE
    // anything else the step does — exactly the ordering a human "run the
    // app, then start editing" beat needs.
    match step.SetupCommand with
    | Some command -> do! actor.Command command
    | None -> ()

    // An editor client (VS Code/Neovim) creates its session directly
    // through the daemon API/plugin command (`Runtime.fs`'s
    // `create-session*`/`create-session:*` wire tokens) — no click ever
    // reaches the Dashboard narrator pane `assembleActors` co-launches
    // alongside it. The product's own doctrine is that creating a session
    // never switches the dashboard's main panel away from the "Start a
    // Session" picker — only clicking a session card does
    // (`Scenarios.fs`'s `helloDashboard` step 2 comment, and every
    // Dashboard-client scenario drives that click as a real step of its
    // own). Left unhandled, the narrator sits on the picker forever and
    // `#session-output`/`#session-status` never render for an editor
    // scenario. Mirroring that click here — automatically, right after a
    // real create-session command, through the narrator's OWN `Command`
    // (`Actors/Dashboard.fs`'s `"select-session"`) rather than XTest —
    // means the scenario author never has to add a click step of their
    // own for a pane that is only ever an observation window, never the
    // on-camera actor. Guarded to editor clients only: a Dashboard-client
    // scenario's own `targetActor` IS `ActorId.Dashboard`, and it already
    // drives its own real, filmed session-card click as a step.
    if targetActor <> ActorId.Dashboard && (step.SetupCommand |> Option.exists (fun c -> c.StartsWith "create-session")) then
      match actorsRef.Value |> Map.tryFind ActorId.Dashboard with
      | Some dashboardActor -> do! dashboardActor.Command "select-session"
      | None -> ()

    // The App co-actor is deliberately launched HERE, lazily, right after a
    // real `"run-app"` command has actually been dispatched to the daemon —
    // never eagerly at initial actor assembly (`assembleActors`'s own doc:
    // launching it upfront made every joint hot-reload scenario fail loud
    // before its first real step ever ran, because the window/URL it polls
    // for genuinely does not exist until run-app has actually happened).
    // Idempotent: only launches once (`not (... .ContainsKey ActorId.App)`),
    // so a scenario with multiple steps naming "run-app" never double-launches.
    if step.SetupCommand = Some "run-app" && not (actorsRef.Value.ContainsKey ActorId.App) then
      match! appActorsOf plan with
      | Error message ->
        // A real, honest failure to report on THIS step (never silently
        // swallowed) — the step's own observation below will then find no
        // App actor for `Observe` and fail the same way a missing selector
        // does, but the message here is the actionable one.
        eprintfn "cell-agent: App co-actor failed to launch after run-app: %s" message
      | Ok appActors -> actorsRef.Value <- mergeActors actorsRef.Value appActors

    let observer = actorsRef.Value |> Map.tryFind observeActor |> Option.defaultValue actor

    // `Action.Chord`'s wire image: delivered directly via XTest — no rect to
    // resolve (a chord acts on whatever window already has focus, exactly
    // like a real Ctrl+S needs no mouse move first). `Input.plan`'s own
    // `Chord` case ignores the `target`/`start` it's handed (see that
    // function's doc), so a zero rect and `Input.restPosition` are correct
    // placeholders here, not guesses.
    match step.ChordKeys with
    | Some tokens ->
      let keys = tokens |> List.choose Key.ofToken

      if not (List.isEmpty keys) then
        let requests = Input.plan mapping Input.restPosition (Action.Chord keys) { X = 0; Y = 0; W = 0; H = 0 }
        XTest.deliver live requests
    | None -> ()

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
      // 5-minute ceiling: a cohort `land_and_wait` beat polls until the daemon
      // has really rebased + built the fixture worktree offline + run the tests
      // in the integration session — which is minutes in the sealed cell, not
      // the ~seconds a click/eval takes. A generous ceiling only bites when a
      // step is genuinely stuck; fast steps still return the instant they match.
      | Some selector -> observer.Observe selector 300000.0

    let observedAtMs = sw.ElapsedMilliseconds
    // A short settle so the recorded segment ends on a held, readable final
    // frame rather than cutting the instant the expectation resolves (§9:
    // "≥ 1.0s dwell after every observed change").
    do! Async.Sleep(max 200 step.DwellMs)
    do! Recorder.stop recording

    // Fail-CLOSED scoring: a step with NO expectation selector proved nothing,
    // so it is Skipped (honestly unverified), NEVER a false green. Only a
    // selector that actually appeared is Passed; a selector that didn't appear,
    // or a missing click target, is Failed. (Previously a None selector scored
    // `observed = true` and the beat passed vacuously — the flagship
    // `TestOutcome` expectation lowers to None for every client, so "a test went
    // green" recorded as Passed without ever being observed. Skipped makes that
    // gap visible in the manifest instead of stamping it green.)
    let outcomeStr =
      if targetMissing then "Failed"
      else
        match step.ExpectSelector with
        | None -> "Skipped"
        | Some _ -> if observed then "Passed" else "Failed"

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
        Outcome = outcomeStr
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
      | Ok initialActors ->

      // A mutable cell, not a plain map: the App co-actor is added to it
      // mid-run, lazily, the first time a step's own `Setup(RunApp)`
      // actually dispatches (`runStep`'s own doc) — every step before that
      // point, and every scenario with no App pane at all, sees exactly the
      // same fixed map `assembleActors` built.
      let actorsRef = ref initialActors
      let mutable results = []

      for step in plan.Steps |> List.sortBy (fun s -> s.Index) do
        let requestedActor = step.TargetActor |> Option.defaultValue plan.Client
        let targetActorId = requestedActor |> actorIdOfString |> Option.filter actorsRef.Value.ContainsKey

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
          // `Wire.WireStep.ObserveActor` (seam-integration threading): which
          // actor proves this step's expectation, distinct from the actor
          // that drove its input — defaults to the SAME actor when absent
          // (every plan built before this field existed, and every
          // Dashboard/Agent step today), so this is purely additive. NOT
          // filtered by "is it live yet" here: the App co-actor's own
          // observing step is often the SAME step that lazily launches it
          // inside `runStep` — `runStep`'s own `Map.tryFind` (falling back
          // to the target actor) is what actually tolerates "not live yet".
          let requestedObserver = step.ObserveActor |> Option.defaultValue requestedActor
          let observeActorId = requestedObserver |> actorIdOfString |> Option.defaultValue actorId

          let! result = runStep live mapping actorsRef actorId observeActorId plan sw step
          results <- results @ [ result ]

      for actor in actorsRef.Value |> Map.toList |> List.map snd do
        do! actor.Close()

      XTest.closeDisplay live

      let log: Wire.StepLog = { ScenarioId = plan.ScenarioId; Steps = results }
      Wire.serializeStepLog log |> Console.Out.WriteLine
      Console.Out.Flush()
      // Fail only on a real Failed beat; a Skipped (no-expectation, honestly
      // unverified) beat does not fail the record but is visible as not-Passed in
      // the manifest — so a green record can be audited for what it did NOT prove.
      return (if log.Steps |> List.forall (fun s -> s.Outcome <> "Failed") then 0 else 1)
  }
