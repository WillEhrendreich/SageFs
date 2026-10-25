namespace SageFs.Simulation

open System
open SageFs
open SageFs.Features.LiveTesting
open SageFs.WorkerProtocol

/// Deterministic Simulation Testing for "which sessions' live testing does a file change reach".
///
/// Folds the REAL `LiveTestFileRouting.route` (the pure core `SageFsUpdate` consults for
/// `FileContentChanged`), never a reimplemented candidate. The world is a small fixed pool of
/// sessions, one of them primary, each with live testing on or off. A scenario is a seed plus an
/// ordered op list that turns live testing on and off, moves the primary and saves files.
///
/// The thing under test is that the primary session is NOT a precondition. The bug this guards
/// asked the primary's activation before routing, so a session with live testing on never saw a
/// save while the primary had it off.
///
/// Same design as `FileReloadRoutingSim`:
///   * Chaos is DATA. Same seed, same ops, identical trace, forever. No clock, no IO.
///   * The oracle is INDEPENDENT of the router: expected receivers come from the sim's own
///     bookkeeping of which pool directory each file was saved in and which sessions are on.
///   * Two TWIN routers reintroduce wrong shapes so the invariants prove they have teeth:
///     `runPrimaryGated` (the original bug: nothing is routed unless the primary has live testing
///     on) and `runIgnoresActivation` (every owner receives the change whether it has live testing
///     on or not).
module LiveTestFileRoutingSim =

  /// One scripted step. `Save` writes a file directly in the root directory of the session at
  /// that index. `SaveOutsider` writes a file no session's directory contains.
  [<RequireQualifiedAccess>]
  type RoutingOp =
    | TurnOn of sessionIdx: int
    | TurnOff of sessionIdx: int
    | MakePrimary of sessionIdx: int
    | Save of sessionIdx: int
    | SaveOutsider

  /// A fully-specified, replayable scenario.
  type Scenario = { Seed: int; Ops: RoutingOp list }

  // The pool. Sessions 1 and 2 share a directory, session 3 lives inside session 0's directory
  // (a worktree under a checkout), session 0 is the checkout.
  let private rootDirs =
    [| "/sim/route/checkout"
       "/sim/route/shared"
       "/sim/route/shared"
       "/sim/route/checkout/.claude/worktrees/agent-x" |]

  let private outsiderPath = "/sim/route/elsewhere/Stray.fs"

  let poolSize = rootDirs.Length

  let private sid (raw: string) =
    match SessionId.validate raw with
    | Ok s -> s
    | Error e -> failwithf "sim session id %s invalid: %s" raw e

  let private sessions = [| sid "000000a0"; sid "000000a1"; sid "000000a2"; sid "000000a3" |]

  let private roots : LiveTestFileRouting.SessionRoot list =
    [ for i in 0 .. poolSize - 1 -> { SessionId = sessions.[i]; WorkingDirectory = rootDirs.[i] } ]

  let private pathIn (sessionIdx: int) = IO.Path.Combine(rootDirs.[sessionIdx], "Lib.fs")

  let private indexOf (s: SessionId) : int =
    sessions |> Array.findIndex (fun x -> x = s)

  /// What a router decides for one save: the session indices that receive it, or the fallback.
  [<RequireQualifiedAccess>]
  type Routed =
    | Fallback
    | Sessions of int list

  /// A router: given who is primary, who has live testing on, and a path, where does it go?
  type Router = int -> Map<int, LiveTestingActivation> -> string -> Routed

  let private activationOfIdx (activations: Map<int, LiveTestingActivation>) (i: int) =
    activations |> Map.tryFind i |> Option.defaultValue LiveTestingActivation.Inactive

  /// The subject under test, composed as `SageFsUpdate` composes it.
  let private real : Router =
    fun _ activations path ->
      let activationOf s = activationOfIdx activations (indexOf s)
      match LiveTestFileRouting.route activationOf roots path with
      | LiveTestFileRouting.FileRoute.SessionLess -> Routed.Fallback
      | LiveTestFileRouting.FileRoute.ToSessions owning -> Routed.Sessions (owning |> List.map indexOf)

  /// TWIN 1: the original defect. The primary's activation is asked first.
  let private primaryGated : Router =
    fun primary activations path ->
      match activationOfIdx activations primary with
      | LiveTestingActivation.Inactive -> Routed.Sessions []
      | LiveTestingActivation.Active -> real primary activations path

  /// TWIN 2: every owner receives the change, whether it has live testing on or not.
  let private ignoresActivation : Router =
    fun primary _ path ->
      let allOn = [ 0 .. poolSize - 1 ] |> List.map (fun i -> i, LiveTestingActivation.Active) |> Map.ofList
      real primary allOn path

  /// One save's outcome. `ExpectedOwners` and `ExpectedReceivers` come from the sim's own
  /// bookkeeping (the pool directory the file was saved in, and the on-set at that moment), never
  /// from the router.
  type RoutingDecision =
    { SaveIdx: int option
      Path: string
      Primary: int
      ExpectedOwners: Set<int>
      ExpectedReceivers: Set<int>
      Routed: Routed }

  /// The fold state. Everything starts with live testing off and session 0 primary.
  type SimState =
    { Primary: int
      Activations: Map<int, LiveTestingActivation>
      /// Most recent first while folding; reversed to op order in the `Trace`.
      Decisions: RoutingDecision list }

  let private initial : SimState =
    { Primary = 0; Activations = Map.empty; Decisions = [] }

  let private decide (router: Router) (state: SimState) (saveIdx: int option) : RoutingDecision =
    let path =
      match saveIdx with
      | Some i -> pathIn i
      | None -> outsiderPath
    let owners =
      match saveIdx with
      | Some i -> [ 0 .. poolSize - 1 ] |> List.filter (fun j -> rootDirs.[j] = rootDirs.[i]) |> Set.ofList
      | None -> Set.empty
    let receivers =
      owners
      |> Set.filter (fun j ->
        match activationOfIdx state.Activations j with
        | LiveTestingActivation.Active -> true
        | LiveTestingActivation.Inactive -> false)
    { SaveIdx = saveIdx
      Path = path
      Primary = state.Primary
      ExpectedOwners = owners
      ExpectedReceivers = receivers
      Routed = router state.Primary state.Activations path }

  let rec private go (router: Router) (state: SimState) (ops: RoutingOp list) : SimState =
    match ops with
    | [] -> state
    | op :: rest ->
      let state' =
        match op with
        | RoutingOp.TurnOn i -> { state with Activations = state.Activations |> Map.add i LiveTestingActivation.Active }
        | RoutingOp.TurnOff i -> { state with Activations = state.Activations |> Map.add i LiveTestingActivation.Inactive }
        | RoutingOp.MakePrimary i -> { state with Primary = i }
        | RoutingOp.Save i -> { state with Decisions = decide router state (Some i) :: state.Decisions }
        | RoutingOp.SaveOutsider -> { state with Decisions = decide router state None :: state.Decisions }
      go router state' rest

  /// The result of folding a scenario to completion.
  type Trace = { Scenario: Scenario; Final: SimState; Router: string }

  let private runWith (name: string) (router: Router) (scenario: Scenario) : Trace =
    let final = go router initial scenario.Ops
    { Scenario = scenario; Final = { final with Decisions = List.rev final.Decisions }; Router = name }

  /// Through the real `LiveTestFileRouting.route`.
  let run (scenario: Scenario) : Trace = runWith "real (LiveTestFileRouting.route)" real scenario

  /// Through the primary-gated twin: proves the invariants catch the original defect.
  let runPrimaryGated (scenario: Scenario) : Trace = runWith "twin-primary-gated" primaryGated scenario

  /// Through the activation-ignoring twin: proves the invariants catch a delivery to a session
  /// that has live testing off.
  let runIgnoresActivation (scenario: Scenario) : Trace = runWith "twin-ignores-activation" ignoresActivation scenario
