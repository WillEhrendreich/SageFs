module SageFs.Tests.LiveTestFileRoutingTests

/// A change to a session's file reaches THAT session's live testing, whichever session is
/// primary or being viewed, and reaches it exactly when that session has live testing on.
///
/// The defect these pin: `SageFsUpdate` answered `BufferContentChanged` and `FileContentChanged`
/// by first asking whether the PRIMARY cycle had live testing on. A daemon with it off on the
/// primary session and on for another session dropped every save and every unsaved-buffer change
/// for the other one. Status stayed at generation 1 and nothing re-ran, with live testing shown
/// as on.

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features.LiveTesting

// ── world building ──────────────────────────────────────────────────

let private mkSession (name: string) (workingDir: string) : SessionSnapshot =
  { Id = WorkerProtocol.SessionId.newId ()
    Name = Some name
    Projects = [ sprintf "%s/MyProj.fsproj" workingDir ]
    Status = SessionDisplayStatus.Running
    LastActivity = DateTime.UtcNow
    EvalCount = 0
    UpSince = DateTime.UtcNow
    WorkingDirectory = workingDir }

let private update msg model = SageFsUpdate.update msg model |> fst

let private idOf (snap: SessionSnapshot) = WorkerProtocol.SessionId.value snap.Id

/// Which order the world is assembled in: live testing switched on while the first session is
/// being viewed and then the primary moved (the cycles get parked), or the primary moved first.
[<RequireQualifiedAccess>]
type Ordering =
  | EnableThenSwitch
  | SwitchThenEnable

/// A world: sessions in distinct directories, one of them primary, and each with live testing
/// switched on or off through the same per-session message the MCP and the daemon send.
type WorldSpec =
  { Activations: LiveTestingActivation list
    Primary: int
    Target: int
    Ordering: Ordering }

let private dirOf (index: int) = sprintf "/projects/session-%d" index

let private pathIn (index: int) = sprintf "%s/src/Lib.fs" (dirOf index)

let private buildWorld (spec: WorldSpec) : SageFsModel * SessionSnapshot list =
  let snaps = spec.Activations |> List.mapi (fun i _ -> mkSession (sprintf "s%d" i) (dirOf i))
  let created =
    snaps
    |> List.fold (fun m snap -> update (SageFsMsg.Event (TuiEvent.SessionCreated snap)) m) (SageFsModel.initial ())
  let switchTo m =
    update (SageFsMsg.Event (TuiEvent.SessionSwitched (None, idOf snaps.[spec.Primary]))) m
  let enable m =
    List.zip snaps spec.Activations
    |> List.fold
      (fun m (snap, activation) ->
        match activation with
        | LiveTestingActivation.Active -> update (SageFsMsg.EnableLiveTestingForSession (idOf snap)) m
        | LiveTestingActivation.Inactive -> m)
      m
  let model =
    match spec.Ordering with
    | Ordering.EnableThenSwitch -> created |> enable |> switchTo
    | Ordering.SwitchThenEnable -> created |> switchTo |> enable
  model, snaps

/// Did the change land in this session's own live-testing cycle?
let private reached (model: SageFsModel) (snap: SessionSnapshot) (path: string) : bool =
  (SageFsModel.cycleForSession (idOf snap) model).ActiveFile = Some path

let private reachedSet (model: SageFsModel) (snaps: SessionSnapshot list) (path: string) : Set<int> =
  snaps
  |> List.mapi (fun i snap -> i, reached model snap path)
  |> List.filter snd
  |> List.map fst
  |> Set.ofList

/// What should have been reached: only the target, and only when it has live testing on.
let private expectedSet (spec: WorldSpec) : Set<int> =
  match spec.Activations.[spec.Target] with
  | LiveTestingActivation.Active -> Set.singleton spec.Target
  | LiveTestingActivation.Inactive -> Set.empty

// ── generator ───────────────────────────────────────────────────────

let private genActivation =
  Gen.elements [ LiveTestingActivation.Active; LiveTestingActivation.Inactive ]

let private genSpec : Gen<WorldSpec> =
  gen {
    let! count = Gen.choose (2, 5)
    let! activations = Gen.listOfLength count genActivation
    let! primary = Gen.choose (0, count - 1)
    let! target = Gen.choose (0, count - 1)
    let! ordering = Gen.elements [ Ordering.EnableThenSwitch; Ordering.SwitchThenEnable ]
    return { Activations = activations; Primary = primary; Target = target; Ordering = ordering }
  }

let private propConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }

// ── the reported case, as plain examples ────────────────────────────

/// Two sessions, the first one primary with live testing off, the second with it on.
let private reportedWorld =
  { Activations = [ LiveTestingActivation.Inactive; LiveTestingActivation.Active ]
    Primary = 0
    Target = 1
    Ordering = Ordering.SwitchThenEnable }

[<Tests>]
let reportedCase =
  testList "live testing file routing: the reported case" [

    test "WHY — a save in a session with live testing on reaches it while the primary session has it off" {
      let model, snaps = buildWorld reportedWorld
      let path = pathIn 1
      model.LiveTesting.TestState.Activation
      |> Expect.equal "fixture: the primary session has live testing off" LiveTestingActivation.Inactive
      let after = update (SageFsMsg.FileContentChanged (path, "module Lib\nlet x = 1")) model
      reachedSet after snaps path
      |> Expect.equal "the save reaches session 1 and only session 1" (Set.singleton 1)
    }

    test "WHY — an unsaved-buffer change in a session with live testing on reaches it while the primary session has it off" {
      let model, snaps = buildWorld reportedWorld
      let path = pathIn 1
      let after = update (SageFsMsg.BufferContentChanged (Some (idOf snaps.[1]), path, "module Lib\nlet x = 1")) model
      reachedSet after snaps path
      |> Expect.equal "the buffer change reaches session 1 and only session 1" (Set.singleton 1)
    }

    test "WHY — a save in a session with live testing off reaches nobody even though the primary session has it on" {
      let spec =
        { reportedWorld with
            Activations = [ LiveTestingActivation.Active; LiveTestingActivation.Inactive ]
            Primary = 0
            Target = 1 }
      let model, snaps = buildWorld spec
      let path = pathIn 1
      let after = update (SageFsMsg.FileContentChanged (path, "module Lib\nlet x = 1")) model
      reachedSet after snaps path
      |> Expect.isEmpty "session 1 has live testing off, so its save goes nowhere"
    }

    test "WHY — a save in the primary session's own file still reaches the primary when it has live testing on" {
      let spec =
        { reportedWorld with
            Activations = [ LiveTestingActivation.Active; LiveTestingActivation.Inactive ]
            Target = 0 }
      let model, snaps = buildWorld spec
      let path = pathIn 0
      let after = update (SageFsMsg.FileContentChanged (path, "module Lib\nlet x = 1")) model
      reachedSet after snaps path
      |> Expect.equal "the primary session receives its own save" (Set.singleton 0)
    }

    test "WHY — a file that no session owns goes to the primary cycle only when the primary has live testing on" {
      let outsider = "/elsewhere/Stray.fs"
      let off, _ = buildWorld reportedWorld
      let offAfter = update (SageFsMsg.FileContentChanged (outsider, "module Stray")) off
      offAfter.LiveTesting.ActiveFile
      |> Expect.isNone "the primary has live testing off, so a session-less file is ignored"
      let on, _ =
        buildWorld { reportedWorld with Activations = [ LiveTestingActivation.Active; LiveTestingActivation.Inactive ] }
      let onAfter = update (SageFsMsg.FileContentChanged (outsider, "module Stray")) on
      onAfter.LiveTesting.ActiveFile
      |> Expect.equal "the primary has live testing on, so it takes the session-less file" (Some outsider)
    }

    test "WHY — two sessions sharing one directory both receive a save when both have live testing on" {
      let shared = "/projects/shared"
      let a = mkSession "a" shared
      let b = mkSession "b" shared
      let model =
        SageFsModel.initial ()
        |> update (SageFsMsg.Event (TuiEvent.SessionCreated a))
        |> update (SageFsMsg.Event (TuiEvent.SessionCreated b))
        |> update (SageFsMsg.EnableLiveTestingForSession (idOf a))
        |> update (SageFsMsg.EnableLiveTestingForSession (idOf b))
      let path = shared + "/Lib.fs"
      let after = update (SageFsMsg.FileContentChanged (path, "module Lib")) model
      [ a; b ]
      |> List.map (fun snap -> reached after snap path)
      |> Expect.equal "both owners of the shared directory receive the save" [ true; true ]
    }

    test "WHY — a save belongs to the most specific working directory, not to a session that merely contains it" {
      let outer = mkSession "outer" "/projects/repo"
      let inner = mkSession "inner" "/projects/repo/.claude/worktrees/agent-x"
      let model =
        SageFsModel.initial ()
        |> update (SageFsMsg.Event (TuiEvent.SessionCreated inner))
        |> update (SageFsMsg.Event (TuiEvent.SessionCreated outer))
        |> update (SageFsMsg.EnableLiveTestingForSession (idOf outer))
        |> update (SageFsMsg.EnableLiveTestingForSession (idOf inner))
      let path = "/projects/repo/.claude/worktrees/agent-x/src/Lib.fs"
      let after = update (SageFsMsg.FileContentChanged (path, "module Lib")) model
      [ outer; inner ]
      |> List.map (fun snap -> reached after snap path)
      |> Expect.equal "only the worktree session receives a file in its worktree" [ false; true ]
    }
  ]

// ── the general statement ───────────────────────────────────────────

[<Tests>]
let general =
  testList "live testing file routing: for any primary and any activations" [

    testPropertyWithConfig propConfig
      "WHY — a save in session S reaches S exactly when S has live testing on, and no other session"
      <| Prop.forAll (Arb.fromGen genSpec) (fun spec ->
        let model, snaps = buildWorld spec
        let path = pathIn spec.Target
        let after = update (SageFsMsg.FileContentChanged (path, "module Lib\nlet x = 1")) model
        reachedSet after snaps path = expectedSet spec)

    testPropertyWithConfig propConfig
      "WHY — an unsaved-buffer change in session S reaches S exactly when S has live testing on, and no other session"
      <| Prop.forAll (Arb.fromGen genSpec) (fun spec ->
        let model, snaps = buildWorld spec
        let path = pathIn spec.Target
        let after =
          update (SageFsMsg.BufferContentChanged (Some (idOf snaps.[spec.Target]), path, "module Lib\nlet x = 1")) model
        reachedSet after snaps path = expectedSet spec)
  ]

// ── the file watchers follow the same rule ──────────────────────────

let private watcherDirs (effects: SageFsEffect list) (pick: TestCycleEffect -> string option) : string list =
  effects
  |> List.choose (fun effect ->
    match effect with
    | SageFsEffect.TestCycle cycleEffect -> pick cycleEffect
    | SageFsEffect.Editor _
    | SageFsEffect.SwitchWorkflow _
    | SageFsEffect.Confirm _ -> None)

let private registeredDir (effect: TestCycleEffect) =
  match effect with
  | TestCycleEffect.RegisterFileWatcher (_, dir) -> Some dir
  | _ -> None

let private disposedDir (effect: TestCycleEffect) =
  match effect with
  | TestCycleEffect.DisposeFileWatcher (_, dir) -> Some dir
  | _ -> None

[<Tests>]
let watchers =
  testList "live testing file routing: the watchers" [

    test "WHY — a session switched on by itself is watched once it is running, while the primary session has it off" {
      let a = mkSession "a" (dirOf 0)
      let b = { mkSession "b" (dirOf 1) with Status = SessionDisplayStatus.Starting }
      let model =
        SageFsModel.initial ()
        |> update (SageFsMsg.Event (TuiEvent.SessionCreated a))
        |> update (SageFsMsg.Event (TuiEvent.SessionCreated b))
        |> update (SageFsMsg.EnableLiveTestingForSession (idOf b))
      let _, effects =
        SageFsUpdate.update
          (SageFsMsg.Event (TuiEvent.SessionStatusChanged (idOf b, SessionDisplayStatus.Running)))
          model
      watcherDirs effects registeredDir
      |> Expect.equal "session b's directory is registered" [ dirOf 1 ]
    }

    test "WHY — turning the primary off leaves the watcher of a session that was switched on by itself" {
      let a = mkSession "a" (dirOf 0)
      let b = mkSession "b" (dirOf 1)
      let model =
        SageFsModel.initial ()
        |> update (SageFsMsg.Event (TuiEvent.SessionCreated a))
        |> update (SageFsMsg.Event (TuiEvent.SessionCreated b))
        |> update SageFsMsg.EnableLiveTesting
        |> update (SageFsMsg.EnableLiveTestingForSession (idOf b))
      let disabled, effects = SageFsUpdate.update SageFsMsg.DisableLiveTesting model
      watcherDirs effects disposedDir
      |> Expect.equal "only the primary session's watcher goes" [ dirOf 0 ]
      reached
        (update (SageFsMsg.FileContentChanged (pathIn 1, "module Lib")) disabled)
        b
        (pathIn 1)
      |> Expect.isTrue "and session b still receives its saves"
    }
  ]

// ── a session the cohort verifies in is driven by the landing gate alone ────────────────

/// The cohort's integration session. The landing gate evaluates a rebase's files into it and runs its tests itself, so a save in
/// its worktree must not also drive its live loop: that loop confirms the save with a real build, the build swaps the worker, and a
/// swap in the middle of a verification (or of a read straight after one) is a race with nothing to gain.
let private markVerifying (snap: SessionSnapshot) (model: SageFsModel) : SageFsModel =
  update (SageFsMsg.SessionVerifiesLandings (idOf snap)) model

[<Tests>]
let verifyingSessionCases =
  testList "live testing file routing: a verifying session is not driven by saves" [

    test "WHY — a save in a verifying session's worktree reaches no session, and is not handed to the primary as a session-less file" {
      let model, snaps = buildWorld { reportedWorld with Primary = 1 }
      let verifying = markVerifying snaps.[1] model
      let path = pathIn 1
      let after = update (SageFsMsg.FileContentChanged (path, "module Lib\nlet x = 1")) verifying
      reachedSet after snaps path |> Expect.equal "it reaches nobody" Set.empty
      after.LiveTesting.ActiveFile |> Expect.isNone "the primary cycle did not take it as a file no session owns"
    }

    test "WHY — an unsaved-buffer change aimed at a verifying session reaches no session" {
      let model, snaps = buildWorld reportedWorld
      let verifying = markVerifying snaps.[1] model
      let path = pathIn 1
      let after = update (SageFsMsg.BufferContentChanged (Some (idOf snaps.[1]), path, "module Lib\nlet x = 1")) verifying
      reachedSet after snaps path |> Expect.equal "it reaches nobody" Set.empty
    }

    test "WHY — a working session beside a verifying one is driven exactly as before" {
      let spec = { Activations = [ LiveTestingActivation.Active; LiveTestingActivation.Active ]; Primary = 0; Target = 0; Ordering = Ordering.SwitchThenEnable }
      let model, snaps = buildWorld spec
      let verifying = markVerifying snaps.[1] model
      let path = pathIn 0
      let after = update (SageFsMsg.FileContentChanged (path, "module Lib\nlet x = 1")) verifying
      reachedSet after snaps path |> Expect.equal "session 0 still receives its own save" (Set.singleton 0)
    }

    testPropertyWithConfig propConfig
      "WHY — whatever the world, a save in a verifying session's directory reaches nobody"
      <| Prop.forAll (Arb.fromGen genSpec) (fun spec ->
        let model, snaps = buildWorld spec
        let verifying = markVerifying snaps.[spec.Target] model
        let path = pathIn spec.Target
        let after = update (SageFsMsg.FileContentChanged (path, "module Lib\nlet x = 1")) verifying
        Set.isEmpty (reachedSet after snaps path))

    testPropertyWithConfig propConfig
      "WHY — marking one session verifying never changes what another session receives"
      <| Prop.forAll (Arb.fromGen genSpec) (fun spec ->
        let model, snaps = buildWorld spec
        let other = (spec.Target + 1) % snaps.Length
        let verifying = markVerifying snaps.[other] model
        let path = pathIn spec.Target
        let after = update (SageFsMsg.FileContentChanged (path, "module Lib\nlet x = 1")) verifying
        reachedSet after snaps path = expectedSet spec)
  ]
