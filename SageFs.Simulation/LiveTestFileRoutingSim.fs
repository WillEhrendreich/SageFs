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
///   * Four TWIN routers reintroduce wrong shapes so the invariants prove they have teeth:
///     `runPrimaryGated` (the original bug: nothing is routed unless the primary has live testing
///     on), `runIgnoresActivation` (every owner receives the change whether it has live testing
///     on or not), `runRawPrefix` (a path is owned by every directory whose TEXT it starts
///     with, so a save in `/x/checkout-lookalike` reaches the session rooted at `/x/checkout`) and
///     `runVerifierDrivenLikeAny` (the session the cohort made to verify landings is driven by
///     saves like any other, so its confirming build can swap the worker under a verification).
module LiveTestFileRoutingSim =

  /// One scripted step. `Save` writes a file directly in the root directory of the session at
  /// that index. `SaveOutsider` writes a file no session's directory contains. `SaveLookalike`
  /// writes a file in a sibling directory whose name begins with session 0's directory name, so
  /// no session owns it.
  [<RequireQualifiedAccess>]
  type RoutingOp =
    | TurnOn of sessionIdx: int
    | TurnOff of sessionIdx: int
    | MakePrimary of sessionIdx: int
    | Save of sessionIdx: int
    | SaveOutsider
    | SaveLookalike

  /// A fully-specified, replayable scenario.
  type Scenario = { Seed: int; Ops: RoutingOp list }

  // The pool. Sessions 1 and 2 share a directory, session 3 lives inside session 0's directory
  // (a worktree under a checkout), session 0 is the checkout, and session 4 is the one the cohort
  // made to verify landings, in a directory of its own.
  let private rootDirs =
    [| "/sim/route/checkout"
       "/sim/route/shared"
       "/sim/route/shared"
       "/sim/route/checkout/.claude/worktrees/agent-x"
       "/sim/route/cohort-integration" |]

  /// The pool index of the session the cohort made to verify landings.
  let verifyingIdx = 4

  let private outsiderPath = "/sim/route/elsewhere/Stray.fs"

  /// A sibling of session 0's directory (`/sim/route/checkout`) whose name starts with the same
  /// text. No session is rooted there.
  let lookalikePath = "/sim/route/checkout-lookalike/Stray.fs"

  let poolSize = rootDirs.Length

  let private sid (raw: string) =
    match SessionId.validate raw with
    | Ok s -> s
    | Error e -> failwithf "sim session id %s invalid: %s" raw e

  let private sessions = [| sid "000000a0"; sid "000000a1"; sid "000000a2"; sid "000000a3"; sid "000000a4" |]

  let private purposeOf (idx: int) : LiveTestFileRouting.SessionPurpose =
    match idx = verifyingIdx with
    | true -> LiveTestFileRouting.SessionPurpose.Verifying
    | false -> LiveTestFileRouting.SessionPurpose.Working

  let private rootsWith (purpose: int -> LiveTestFileRouting.SessionPurpose) : LiveTestFileRouting.SessionRoot list =
    [ for i in 0 .. poolSize - 1 ->
        { SessionId = sessions.[i]; WorkingDirectory = rootDirs.[i]; Purpose = purpose i } ]

  let private roots = rootsWith purposeOf

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

  /// TWIN 3: ownership by text prefix. Every session whose directory the path's text starts with
  /// owns it, the longest directory winning, with no regard for where a segment ends.
  let private rawPrefix : Router =
    fun _ activations path ->
      let activationOf s = activationOfIdx activations (indexOf s)
      let owning =
        roots |> List.filter (fun root -> path.StartsWith(root.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
      match owning with
      | [] -> Routed.Fallback
      | _ ->
        let longest = owning |> List.map (fun root -> root.WorkingDirectory.Length) |> List.max
        owning
        |> List.filter (fun root -> root.WorkingDirectory.Length = longest)
        |> List.map (fun root -> root.SessionId)
        |> LiveTestFileRouting.withLiveTestingOn activationOf
        |> List.map indexOf
        |> Routed.Sessions

  /// TWIN 4: the session the cohort made to verify landings is driven like any other, so a save in
  /// its worktree reaches it once it has live testing on, and its confirming build can swap the
  /// worker under a verification.
  let private verifierDrivenLikeAny : Router =
    fun _ activations path ->
      let activationOf s = activationOfIdx activations (indexOf s)
      let everyoneWorks = rootsWith (fun _ -> LiveTestFileRouting.SessionPurpose.Working)
      match LiveTestFileRouting.route activationOf everyoneWorks path with
      | LiveTestFileRouting.FileRoute.SessionLess -> Routed.Fallback
      | LiveTestFileRouting.FileRoute.ToSessions owning -> Routed.Sessions (owning |> List.map indexOf)

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

  let private decide (router: Router) (state: SimState) (saveIdx: int option) (path: string) : RoutingDecision =
    let owners =
      match saveIdx with
      | Some i -> [ 0 .. poolSize - 1 ] |> List.filter (fun j -> rootDirs.[j] = rootDirs.[i]) |> Set.ofList
      | None -> Set.empty
    let receivers =
      owners
      |> Set.filter (fun j ->
        // The verifying session is driven by the landing gate, never by a save.
        j <> verifyingIdx
        && (match activationOfIdx state.Activations j with
            | LiveTestingActivation.Active -> true
            | LiveTestingActivation.Inactive -> false))
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
        | RoutingOp.Save i -> { state with Decisions = decide router state (Some i) (pathIn i) :: state.Decisions }
        | RoutingOp.SaveOutsider -> { state with Decisions = decide router state None outsiderPath :: state.Decisions }
        | RoutingOp.SaveLookalike -> { state with Decisions = decide router state None lookalikePath :: state.Decisions }
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

  /// Through the text-prefix twin: proves the invariants catch a save in a lookalike sibling
  /// directory being attributed to the session whose directory name it starts with.
  let runRawPrefix (scenario: Scenario) : Trace = runWith "twin-raw-prefix" rawPrefix scenario

  /// Through the twin that drives the verifying session like any other: proves the invariants catch
  /// a save reaching the session the landing gate drives.
  let runVerifierDrivenLikeAny (scenario: Scenario) : Trace =
    runWith "twin-verifier-driven-like-any" verifierDrivenLikeAny scenario
