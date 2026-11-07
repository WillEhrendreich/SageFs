namespace SageFs

/// When the session may bring its FSI host up to the patched build by itself.
///
/// A metadata delta changes the app's process and leaves the FSI host, which the REPL and live tests run in, on the build
/// from before it (`ReplFreshness.BehindApp`). A rebuild-free reset of the host closes that gap in about two seconds and
/// leaves the app's process alone. It also throws away everything the host holds, so the session does it only when that
/// loses nothing: no eval is running, the REPL holds none of the user's definitions, no init script defined anything, no
/// dynamic live test points at the host and no test run is in flight.
///
/// This file is pure and sits ahead of `SessionReload.fs` (the freshness state names its blockers) and of `AppState.fs`
/// (the eval actor folds it, and `AppState.fs` has a size budget). The actor reads its own facts and the worker reads the
/// ones the actor cannot see; both go through `ReplLeveling.decide`, so there is one rule.
[<RequireQualifiedAccess>]
type ReplActivity =
  /// The session is starting, resetting or faulted: there is no live host to bring up.
  | NotServing
  | Quiet
  /// An eval is running, or a cancelled one has not confirmed it stopped.
  | EvalInFlight
  /// The FSI host died. Only a reset of the whole session leaves this, and that is not leveling.
  | HostGone

/// Whether the init script (`.SageFs/init.fsx`) defined anything in the host.
[<RequireQualifiedAccess>]
type InitScript =
  | Absent
  | Defined of path: string

/// Whether an `EvalLiveTestFile` defined tests in the host the worker's run-test slot still points at.
[<RequireQualifiedAccess>]
type DynamicTests =
  | Absent
  | Defined

/// How many test runs execute in the host right now.
[<RequireQualifiedAccess>]
type TestRuns =
  | Idle
  | InFlight of count: int

/// Why a session cannot level itself now. A closed set: one description, one wire token.
[<RequireQualifiedAccess>]
type LevelBlocker =
  | NotServing
  | EvalRunning
  | HostGone
  /// `evals` evals have finished in this host since it was built, so it may hold definitions of the user's.
  | ReplHoldsDefinitions of evals: int
  | InitScriptDefined of path: string
  | LiveTestsDefined
  | TestsRunning of count: int
  /// Another leveling of this host is already under way.
  | LevelAlreadyRunning
  /// A replacement worker is starting; it will be built from the current build anyway.
  | SwapInProgress
  /// The cohort's landing gate drives this session, and a reset in the middle of a verification would disturb it.
  | GateDriven

[<RequireQualifiedAccess>]
type LevelVerdict =
  | Level
  /// Never empty.
  | Deferred of LevelBlocker list

/// What a worker answers when the daemon asks it to level its host.
[<RequireQualifiedAccess>]
type LevelAnswer =
  /// The host was replaced by one built from the current build. The daemon still has to fetch the tests and maps again.
  | Leveled
  /// Nothing was changed, and these are the reasons. Never empty.
  | LevelDeferred of LevelBlocker list
  /// The reset itself failed, or the worker could not be asked.
  | LevelFailed of reason: string

module LevelAnswer =
  /// What a hard reset of the host (the one a level performs) answers, as the level's answer.
  let ofReset (result: Result<string, SageFsError>) : LevelAnswer =
    match result with
    | Result.Ok _ -> LevelAnswer.Leveled
    | Result.Error error -> LevelAnswer.LevelFailed (SageFsError.describe error)

/// Who drives a session's live loop, as far as leveling goes.
[<RequireQualifiedAccess>]
type SessionDriver =
  /// The cohort's landing gate drives the session alone, and a reset in the middle of a verification would disturb it.
  | LandingGate
  /// A person, an agent, the file watcher: anyone else.
  | Everyone

/// What the work started for a session does first.
[<RequireQualifiedAccess>]
type LevelWork =
  /// Ask the worker to level its host, then fetch the tests and maps again.
  | AskWorker
  /// Something else just replaced the host (a hard reset): only fetch again.
  | WorkerRebuiltItself

/// What the manager knows about the worker before it asks.
[<RequireQualifiedAccess>]
type LevelStanding =
  /// A Ready worker (this pid), no swap parked, not driven by the landing gate.
  | Servable of workerPid: int
  | Unservable of LevelBlocker

/// Whether a level request or a refetch after a rebuilt host is already out for the session.
[<RequireQualifiedAccess>]
type LevelFlight =
  | NoLevelInFlight
  | LevelInFlight

[<RequireQualifiedAccess>]
type LevelStart =
  | StartLevel of workerPid: int
  | NoStart

/// Everything the verdict reads.
type LevelFacts =
  { Activity: ReplActivity
    EvalsSinceHost: int
    InitScript: InitScript
    DynamicTests: DynamicTests
    TestRuns: TestRuns }

/// The facts the eval actor cannot see for itself: the worker's init script path and run-test slot, and its test-run gate.
type OutsideFacts =
  { DynamicTests: DynamicTests
    TestRuns: TestRuns }

/// What the host holds of the user's, which the eval actor counts for itself: the evals started in this host since it was
/// built (the REPL's own, never the hot reload's: a reset builds the host from the saved files) and the init script that
/// ran in it. A reset empties both, because the host they lived in is gone.
type HostHolds =
  { EvalsSinceHost: int
    InitScript: InitScript }

module InitScript =
  /// The init script that ran in the host, from the path the session recorded when it loaded one.
  let ofLoaded (loadedPath: string option) : InitScript =
    match loadedPath with
    | Some path -> InitScript.Defined path
    | None -> InitScript.Absent

module HostHolds =
  let empty : HostHolds = { EvalsSinceHost = 0; InitScript = InitScript.Absent }

  /// An eval was started in the host with these arguments. The REPL's own evals are what a level would lose; a hot reload's
  /// `#load` (marked `hotReload`) is not, because a new host is built from the saved file.
  let afterEvalStarted (args: Map<string, obj>) (holds: HostHolds) : HostHolds =
    match Map.containsKey "hotReload" args with
    | true -> holds
    | false -> { holds with EvalsSinceHost = holds.EvalsSinceHost + 1 }

/// What the actor reads when it handles a level: its own counts, and the facts the worker passed with the message.
type LevelOwned =
  { Holds: HostHolds
    Outside: OutsideFacts }

module LevelBlocker =
  /// The wire spelling, one per case.
  let token (blocker: LevelBlocker) : string =
    match blocker with
    | LevelBlocker.NotServing -> "NotServing"
    | LevelBlocker.EvalRunning -> "EvalRunning"
    | LevelBlocker.HostGone -> "HostGone"
    | LevelBlocker.ReplHoldsDefinitions _ -> "ReplHoldsDefinitions"
    | LevelBlocker.InitScriptDefined _ -> "InitScriptDefined"
    | LevelBlocker.LiveTestsDefined -> "LiveTestsDefined"
    | LevelBlocker.TestsRunning _ -> "TestsRunning"
    | LevelBlocker.LevelAlreadyRunning -> "LevelAlreadyRunning"
    | LevelBlocker.SwapInProgress -> "SwapInProgress"
    | LevelBlocker.GateDriven -> "GateDriven"

  /// What stopped it and what to do about it, in a clause a sentence can carry.
  let describe (blocker: LevelBlocker) : string =
    match blocker with
    | LevelBlocker.NotServing -> "the session is starting, resetting or faulted"
    | LevelBlocker.EvalRunning -> "an eval is running (cancel_eval, or wait for it)"
    | LevelBlocker.HostGone -> "the FSI host died (hard_reset_fsi_session recovers it)"
    | LevelBlocker.ReplHoldsDefinitions 1 -> "the REPL has run 1 eval since it was built, and its definitions would be lost"
    | LevelBlocker.ReplHoldsDefinitions evals -> sprintf "the REPL has run %d evals since it was built, and their definitions would be lost" evals
    | LevelBlocker.InitScriptDefined path -> sprintf "the init script %s defined values in the REPL, and running it again would repeat what it did" path
    | LevelBlocker.LiveTestsDefined -> "live tests defined from an editor buffer live in this REPL"
    | LevelBlocker.TestsRunning 1 -> "a test run is in flight"
    | LevelBlocker.TestsRunning count -> sprintf "%d test runs are in flight" count
    | LevelBlocker.LevelAlreadyRunning -> "the REPL is already being brought level"
    | LevelBlocker.SwapInProgress -> "a replacement worker is starting"
    | LevelBlocker.GateDriven -> "the cohort's landing gate drives this session"

module ReplLeveling =
  /// The one rule: the session levels itself exactly when nothing the host holds would be lost. Every blocker is listed, in a
  /// fixed order, so a person told "run hard_reset_fsi_session" is told everything it would cost.
  let decide (facts: LevelFacts) : LevelVerdict =
    let blockers =
      [ match facts.Activity with
        | ReplActivity.NotServing -> yield LevelBlocker.NotServing
        | ReplActivity.EvalInFlight -> yield LevelBlocker.EvalRunning
        | ReplActivity.HostGone -> yield LevelBlocker.HostGone
        | ReplActivity.Quiet -> ()
        match facts.EvalsSinceHost with
        | 0 -> ()
        | evals -> yield LevelBlocker.ReplHoldsDefinitions evals
        match facts.InitScript with
        | InitScript.Defined path -> yield LevelBlocker.InitScriptDefined path
        | InitScript.Absent -> ()
        match facts.DynamicTests with
        | DynamicTests.Defined -> yield LevelBlocker.LiveTestsDefined
        | DynamicTests.Absent -> ()
        match facts.TestRuns with
        | TestRuns.InFlight count -> yield LevelBlocker.TestsRunning count
        | TestRuns.Idle -> () ]
    match blockers with
    | [] -> LevelVerdict.Level
    | _ -> LevelVerdict.Deferred blockers

  /// A blocker list as one clause run, for a sentence.
  let describeBlockers (blockers: LevelBlocker list) : string =
    blockers |> List.map LevelBlocker.describe |> String.concat "; "

/// Where the worker's test runs stand, as the gate in front of the host sees them.
///
/// A test run executes in the FSI host, so a reset of the host in the middle of one would kill it. The worker holds every run
/// (`RunTests`, `EvalLiveTestFile`, `DebugTestBegin`) behind this gate. A level asks the gate first: with nothing running it
/// moves straight to `Leveling`, and a run that arrives meanwhile is held until the level has ended, never dropped and never run
/// on the old host. With runs in flight the level waits for the last one to leave (`LevelPending`), and a run that arrives
/// meanwhile is held too, so a steady stream of runs cannot starve the level. A held run is served when the level ends.
[<RequireQualifiedAccess>]
type GateStanding =
  | Idle
  | Running of count: int
  | LevelPending of running: int
  | Leveling

[<RequireQualifiedAccess>]
type GateEntry =
  | Admitted
  /// A level is pending or under way: wait for it to end, then ask again.
  | Held

[<RequireQualifiedAccess>]
type GateExit =
  /// Other runs are still in flight.
  | Left
  /// The last run left and nothing was waiting for it.
  | Drained
  /// The last run left and a level was waiting for it: the level may start now.
  | LevelMayStart
  /// Nothing was running. A leave without an enter is a bug in the caller, and changes nothing.
  | NotRunning

[<RequireQualifiedAccess>]
type GateLevelRequest =
  | Start
  /// `running` runs are in flight. The level starts when the last leaves (`GateExit.LevelMayStart`).
  | WaitFor of running: int
  /// Another level is pending or under way.
  | AlreadyRequested

module TestRunGate =
  let enter (standing: GateStanding) : GateStanding * GateEntry =
    match standing with
    | GateStanding.Idle -> GateStanding.Running 1, GateEntry.Admitted
    | GateStanding.Running count -> GateStanding.Running(count + 1), GateEntry.Admitted
    | GateStanding.LevelPending _
    | GateStanding.Leveling -> standing, GateEntry.Held

  let leave (standing: GateStanding) : GateStanding * GateExit =
    match standing with
    | GateStanding.Running 1 -> GateStanding.Idle, GateExit.Drained
    | GateStanding.Running count when count > 1 -> GateStanding.Running(count - 1), GateExit.Left
    | GateStanding.LevelPending 1 -> GateStanding.Leveling, GateExit.LevelMayStart
    | GateStanding.LevelPending count when count > 1 -> GateStanding.LevelPending(count - 1), GateExit.Left
    | GateStanding.Idle
    | GateStanding.Leveling
    | GateStanding.Running _
    | GateStanding.LevelPending _ -> standing, GateExit.NotRunning

  let requestLevel (standing: GateStanding) : GateStanding * GateLevelRequest =
    match standing with
    | GateStanding.Idle -> GateStanding.Leveling, GateLevelRequest.Start
    | GateStanding.Running count -> GateStanding.LevelPending count, GateLevelRequest.WaitFor count
    | GateStanding.LevelPending _
    | GateStanding.Leveling -> standing, GateLevelRequest.AlreadyRequested

  /// The level ended, whatever it answered. Only `Leveling` moves.
  let finishLevel (standing: GateStanding) : GateStanding =
    match standing with
    | GateStanding.Leveling -> GateStanding.Idle
    | GateStanding.Idle
    | GateStanding.Running _
    | GateStanding.LevelPending _ -> standing

  /// The runs a level's facts count: those in flight. A level that has begun has none.
  let runsOf (standing: GateStanding) : TestRuns =
    match standing with
    | GateStanding.Running count
    | GateStanding.LevelPending count -> TestRuns.InFlight count
    | GateStanding.Idle
    | GateStanding.Leveling -> TestRuns.Idle
