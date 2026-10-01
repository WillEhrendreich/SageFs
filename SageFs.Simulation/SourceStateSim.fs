namespace SageFs.Simulation

open System
open SageFs
open SageFs.Features.LiveTesting
// After LiveTesting, so `RunVerdict` is the receipt's (LiveTesting has a `RunVerdict` of its own).
open SageFs.Features.RunReceipts

/// Deterministic Simulation Testing for the claim a `run_tests` receipt makes about its source: that the build the tests
/// ran against is (or is not) behind the files on disk.
///
/// Files are edited, builds start, end and fail, the worker loads the build on disk or keeps an older one, an outside
/// build replaces the output, files become unreadable, the daemon's rebuild record is delivered in any order against all
/// of it, the worker goes silent about when it loaded, and a run reads the receipt, in any order. The decision is the REAL
/// `SourceState.decide`; everything else here is the world around it: a virtual disk, a virtual worker, and the evidence an
/// edge would read off them.
///
/// Same design rules as `BuildConfirmationSim`:
///   * Chaos is DATA: a `Scenario` is an ordered op list. Same ops, same trace. The op order IS the scheduler.
///   * Ground truth is tracked by an independent fold of the ops that never reads the decision under test: it speaks in
///     op order (which edit came after which build), where the decision speaks in write times.
///   * Twins reintroduce the bugs the invariants exist to catch.
///
/// One thing the model cannot say, and the decision cannot either: a file edited INSIDE a build's window is written before
/// the output (whose write time is the end of the build) but may not be in it. The ops keep edits and builds from
/// overlapping in ground truth for that reason; docs/decisions.md names the window.
module SourceStateSim =

  [<RequireQualifiedAccess>]
  type Ends =
    | Builds
    | Fails

  [<RequireQualifiedAccess>]
  type Op =
    /// A source file is written.
    | Edit of file: int
    /// A build begins (the daemon's own, or any).
    | BuildStarts
    /// The build in flight ends. The output on disk changes only when it builds.
    | BuildEnds of Ends
    /// The worker is replaced by one that loads the build now on disk.
    | Respawn
    /// A build outside the daemon replaces the output on disk. The worker keeps what it loaded.
    | ExternalBuild
    /// A file stops being readable (its directory loses the execute bit, the file is removed).
    | Hide of file: int
    | Reveal of file: int
    /// The daemon's rebuild record changes: the daemon says a rebuild started, finished, or failed.
    | RecordStarted
    | RecordFinished
    | RecordFailed
    /// The worker stops, or starts again, telling the daemon when it loaded.
    | WorkerSilent
    | WorkerSpeaks
    /// A run finishes with every test passing, and its receipt is read.
    | Run

  type Scenario = { Seed: int; Ops: Op list }

  let private files = 3

  /// What ground truth says about the build the worker runs.
  [<RequireQualifiedAccess>]
  type Truth =
    /// Nothing written since the build the worker runs, and no newer build on disk.
    | Current
    /// A file was written after the build the worker runs, or a newer build is on disk.
    | Behind

  /// Whether every file a run would stat could be read.
  [<RequireQualifiedAccess>]
  type Reads =
    | AllReadable
    | SomeUnreadable

  [<RequireQualifiedAccess>]
  type Worker =
    | Reports
    | Silent

  /// One run, with what was really true and what the decision said.
  type Step =
    { Op: Op
      Truth: Truth
      Reads: Reads
      Worker: Worker
      Recorded: LastRebuild
      Decision: SourceState
      Verdict: RunVerdict }

  type Trace =
    { Scenario: Scenario
      Steps: Step list
      Reducer: string }

  [<RequireQualifiedAccess>]
  type private Policy =
    /// The real decision.
    | Real
    /// TWIN: write times are never compared, so a file edited after the build reads as in sync.
    | IgnoresMtime
    /// TWIN: the rebuild record's last outcome is believed instead of the files: any finished build is in sync.
    | TrustsLastBuildStamp
    /// TWIN: a stamp that cannot be read is taken as an old one, so the file reads as built.
    | SwallowsReadFailure

  [<RequireQualifiedAccess>]
  type private BuildPhase =
    | Idle
    | InFlight

  type private World =
    { Seq: int
      /// When each source file was last written, in op order.
      EditedAt: Map<int, int>
      /// When the output on disk was written.
      DiskBuiltAt: int
      /// When the worker loaded, and the output it loaded.
      LoadedAt: int
      LoadedBuild: int
      Phase: BuildPhase
      Hidden: Set<int>
      Recorded: LastRebuild
      Worker: Worker
      Steps: Step list }

  let private t0 = DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc)
  let private at (seq: int) : DateTime = t0.AddSeconds(float seq)

  /// Sources written at 0, the build made at 1, the worker loaded at 2: in sync.
  let private initial : World =
    { Seq = 2
      EditedAt = [ for f in 0 .. files - 1 -> f, 0 ] |> Map.ofList
      DiskBuiltAt = 1
      LoadedAt = 2
      LoadedBuild = 1
      Phase = BuildPhase.Idle
      Hidden = Set.empty
      Recorded = LastRebuild.NeverRebuilt
      Worker = Worker.Reports
      Steps = [] }

  let private sourcePath (f: int) = sprintf "src/F%d.fs" f

  /// The evidence an edge would read off this world.
  let private evidenceOf (world: World) : LoadedAt * ProjectEvidence list =
    let stamp (path: string) (seq: int) (hidden: bool) : StampedFile =
      match hidden with
      | true -> { Path = path; Stamp = StampRead.Unreadable "permission denied" }
      | false -> { Path = path; Stamp = StampRead.Written (at seq) }
    let loaded =
      match world.Worker with
      | Worker.Reports -> LoadedAt.Reported (at world.LoadedAt)
      | Worker.Silent -> LoadedAt.NotReported "the worker did not answer"
    let sources = [ for f in 0 .. files - 1 -> stamp (sourcePath f) world.EditedAt.[f] (Set.contains f world.Hidden) ]
    loaded, [ ProjectEvidence.Inspected ("P", stamp "bin/P.dll" world.DiskBuiltAt false, sources) ]

  let private truthOf (world: World) : Truth =
    let editedAfterLoadedBuild = world.EditedAt |> Map.exists (fun _ edited -> edited > world.LoadedBuild)
    match editedAfterLoadedBuild || world.DiskBuiltAt > world.LoadedAt with
    | true -> Truth.Behind
    | false -> Truth.Current

  let private readsOf (world: World) : Reads =
    match Set.isEmpty world.Hidden with
    | true -> Reads.AllReadable
    | false -> Reads.SomeUnreadable

  /// The policy's version of the decision: the real one, or the real one with a bug put back.
  let private decideWith (policy: Policy) (world: World) : SourceState =
    let loaded, evidence = evidenceOf world
    match policy with
    | Policy.Real -> SourceState.decide world.Recorded loaded evidence
    | Policy.IgnoresMtime ->
      // Compares nothing: if every stamp was read, the build is current.
      match world.Recorded, evidence with
      | LastRebuild.Latest (RebuildOutcome.InProgress since), _ -> SourceState.Rebuilding since
      | _, [ ProjectEvidence.Inspected (_, _, sources) ] ->
        match sources |> List.exists (fun s -> match s.Stamp with StampRead.Unreadable _ -> true | StampRead.Written _ -> false) with
        | true -> SourceState.Unknown (UnknownReason.Unreadable ("src", "permission denied"))
        | false -> SourceState.InSync (at world.DiskBuiltAt, sources.Length)
      | _ -> SourceState.Unknown UnknownReason.NoProjectLoaded
    | Policy.TrustsLastBuildStamp ->
      // The rebuild record says a build finished, so the build is current.
      match world.Recorded with
      | LastRebuild.Latest (RebuildOutcome.InProgress since) -> SourceState.Rebuilding since
      | LastRebuild.Latest (RebuildOutcome.Succeeded finished) -> SourceState.InSync (finished, files)
      | _ -> SourceState.decide world.Recorded loaded evidence
    | Policy.SwallowsReadFailure ->
      let swallowed =
        evidence
        |> List.map (fun p ->
          match p with
          | ProjectEvidence.Inspected (name, output, sources) ->
            let old (s: StampedFile) =
              match s.Stamp with
              | StampRead.Unreadable _ -> { s with Stamp = StampRead.Written DateTime.MinValue }
              | StampRead.Written _ -> s
            ProjectEvidence.Inspected (name, old output, sources |> List.map old)
          | other -> other)
      SourceState.decide world.Recorded loaded swallowed

  /// A receipt over a run in which every test passed, read against what the decision said.
  let private allPassedVerdict (decision: SourceState) : RunVerdict =
    let line : ReceiptLine =
      { Id = TestId.TestId "t0"
        Name = "t0"
        Outcome = LineOutcome.Passed TimeSpan.Zero }
    let receipt =
      RunReceipt.Ran
        { RequestId = RunRequestId Guid.Empty
          Session = "sim"
          Generation = RunGeneration.zero
          Verdict = RunVerdict.Incomplete
          Counts = { Passing = 1; Failing = 0; Skipping = 0; Unreported = 0 }
          Lines = [ line ]
          Source = SourceState.Unknown UnknownReason.NotAssessed }
    match TestRunReceipt.withSource decision receipt with
    | RunReceipt.Ran ran -> ran.Verdict
    | _ -> RunVerdict.Incomplete

  let private runOp (policy: Policy) (world: World) (op: Op) : World =
    let tick = { world with Seq = world.Seq + 1 }
    match op with
    | Op.Edit file ->
      // An edit does not overlap a build in ground truth: it lands once the build is over.
      match tick.Phase with
      | BuildPhase.InFlight -> world
      | BuildPhase.Idle -> { tick with EditedAt = Map.add (file % files) tick.Seq tick.EditedAt }
    | Op.BuildStarts -> { tick with Phase = BuildPhase.InFlight }
    | Op.BuildEnds ends ->
      match tick.Phase, ends with
      | BuildPhase.Idle, _ -> world
      | BuildPhase.InFlight, Ends.Builds -> { tick with Phase = BuildPhase.Idle; DiskBuiltAt = tick.Seq }
      | BuildPhase.InFlight, Ends.Fails -> { tick with Phase = BuildPhase.Idle }
    | Op.Respawn -> { tick with LoadedAt = tick.Seq; LoadedBuild = tick.DiskBuiltAt }
    | Op.ExternalBuild ->
      match tick.Phase with
      | BuildPhase.InFlight -> world
      | BuildPhase.Idle -> { tick with DiskBuiltAt = tick.Seq }
    | Op.Hide file -> { tick with Hidden = Set.add (file % files) tick.Hidden }
    | Op.Reveal file -> { tick with Hidden = Set.remove (file % files) tick.Hidden }
    | Op.RecordStarted -> { tick with Recorded = LastRebuild.Latest (RebuildOutcome.InProgress (at tick.Seq)) }
    | Op.RecordFinished -> { tick with Recorded = LastRebuild.Latest (RebuildOutcome.Succeeded (at tick.Seq)) }
    | Op.RecordFailed ->
      { tick with Recorded = LastRebuild.Latest (RebuildOutcome.FailedStillServing (SageFsError.SessionCreationFailed "build failed", at tick.Seq)) }
    | Op.WorkerSilent -> { tick with Worker = Worker.Silent }
    | Op.WorkerSpeaks -> { tick with Worker = Worker.Reports }
    | Op.Run ->
      let decision = decideWith policy tick
      let step =
        { Op = op
          Truth = truthOf tick
          Reads = readsOf tick
          Worker = tick.Worker
          Recorded = tick.Recorded
          Decision = decision
          Verdict = allPassedVerdict decision }
      { tick with Steps = step :: tick.Steps }

  let private runWith (name: string) (policy: Policy) (scenario: Scenario) : Trace =
    let final = scenario.Ops |> List.fold (runOp policy) initial
    { Scenario = scenario; Steps = List.rev final.Steps; Reducer = name }

  /// The real decision.
  let run (scenario: Scenario) : Trace = runWith "real (SourceState.decide)" Policy.Real scenario

  /// TWIN: write times are never compared.
  let runIgnoresMtime (scenario: Scenario) : Trace = runWith "twin-ignores-mtime" Policy.IgnoresMtime scenario

  /// TWIN: the last finished build in the rebuild record is believed over the files.
  let runTrustsLastBuildStamp (scenario: Scenario) : Trace = runWith "twin-trusts-last-build-stamp" Policy.TrustsLastBuildStamp scenario

  /// TWIN: a read failure is taken as an old stamp.
  let runSwallowsReadFailure (scenario: Scenario) : Trace = runWith "twin-swallows-read-failure" Policy.SwallowsReadFailure scenario

  // ── invariants ───────────────────────────────────────────────────────────────

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private replay (t: Trace) = sprintf "seed=%d, ops=%A" t.Scenario.Seed t.Scenario.Ops

  let private firstViolation (t: Trace) (check: Step -> string voption) : Outcome =
    t.Steps
    |> List.tryPick (fun s -> match check s with | ValueSome message -> Some message | ValueNone -> None)
    |> function
       | Some message -> Outcome.Violated (sprintf "%s (%s)" message (replay t))
       | None -> Outcome.Holds

  let private isInSync (state: SourceState) = match state with SourceState.InSync _ -> true | _ -> false

  /// NEVER-GREEN-OVER-STALE: a run that passed on a build that is behind the files is never spelled AllPassed.
  let neverGreenOverStale : Invariant =
    { Id = "never-green-over-stale"
      Description = "When the build the worker runs is behind the files on disk, an all-passed run is never AllPassed."
      Check = fun t ->
        firstViolation t (fun s ->
          match s.Truth, s.Verdict with
          | Truth.Behind, RunVerdict.AllPassed ->
            ValueSome (sprintf "the build was behind the files, and the receipt said AllPassed (decision %A)" s.Decision)
          | _ -> ValueNone) }

  /// REBUILD-IS-NEVER-INSYNC: while the daemon's record says a rebuild is in progress, the answer is Rebuilding.
  let rebuildIsNeverInSync : Invariant =
    { Id = "rebuild-is-never-insync"
      Description = "While the rebuild record says InProgress, the source state is Rebuilding and the verdict is not AllPassed."
      Check = fun t ->
        firstViolation t (fun s ->
          match s.Recorded, s.Decision with
          | LastRebuild.Latest (RebuildOutcome.InProgress _), SourceState.Rebuilding _ when s.Verdict <> RunVerdict.AllPassed -> ValueNone
          | LastRebuild.Latest (RebuildOutcome.InProgress _), other ->
            ValueSome (sprintf "a rebuild was in progress and the source state was %A (verdict %A)" other s.Verdict)
          | _ -> ValueNone) }

  /// UNKNOWN-IS-NEVER-INSYNC: when a file could not be read, or the worker did not say when it loaded, the answer is not InSync.
  let unknownIsNeverInSync : Invariant =
    { Id = "unknown-is-never-insync"
      Description = "A read that failed, or a worker that did not report, never yields InSync."
      Check = fun t ->
        firstViolation t (fun s ->
          match s.Reads, s.Worker, isInSync s.Decision with
          | Reads.SomeUnreadable, _, true -> ValueSome "a file could not be read, and the source state was InSync"
          | _, Worker.Silent, true -> ValueSome "the worker did not report when it loaded, and the source state was InSync"
          | _ -> ValueNone) }

  /// INSYNC-IS-EARNED: InSync is said only when ground truth agrees, so the fail-closed rules above cannot be met by
  /// never answering InSync at all.
  let inSyncIsEarned : Invariant =
    { Id = "insync-is-earned"
      Description = "InSync is reported whenever the build is current, every file is readable, the worker reported, and no rebuild is in progress."
      Check = fun t ->
        firstViolation t (fun s ->
          let inProgress = match s.Recorded with LastRebuild.Latest (RebuildOutcome.InProgress _) -> true | _ -> false
          match s.Truth, s.Reads, s.Worker, inProgress, isInSync s.Decision with
          | Truth.Current, Reads.AllReadable, Worker.Reports, false, false ->
            ValueSome (sprintf "everything was current and readable, and the source state was %A" s.Decision)
          | _ -> ValueNone) }

  let all : Invariant list = [ neverGreenOverStale; rebuildIsNeverInSync; unknownIsNeverInSync; inSyncIsEarned ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some (inv.Id, msg))

  // ── generators ───────────────────────────────────────────────────────────────

  module Generators =

    /// A general scenario: 6-30 ops, with a run after most stretches of them. Same seed, identical list.
    let fromSeed (seed: int) : Scenario =
      let rnd = Random(seed)
      let n = rnd.Next(6, 31)
      let ops =
        [ for _ in 1 .. n ->
            match rnd.Next(0, 22) with
            | 0 | 1 | 2 | 3 -> Op.Edit(rnd.Next files)
            | 4 | 5 -> Op.BuildStarts
            | 6 | 7 -> Op.BuildEnds(match rnd.Next 4 with | 0 -> Ends.Fails | _ -> Ends.Builds)
            | 8 | 9 -> Op.Respawn
            | 10 -> Op.ExternalBuild
            | 11 -> Op.Hide(rnd.Next files)
            | 12 -> Op.Reveal(rnd.Next files)
            | 13 -> Op.RecordStarted
            | 14 -> Op.RecordFinished
            | 15 -> Op.RecordFailed
            | 16 -> Op.WorkerSilent
            | 17 -> Op.WorkerSpeaks
            | _ -> Op.Run ]
      { Seed = seed; Ops = ops @ [ Op.Run ] }

    /// Nothing happened since the build the worker runs.
    let untouched : Scenario = { Seed = 400; Ops = [ Op.Run ] }

    /// The Nehemiah case: a source is edited, nothing is rebuilt, the tests run.
    let editedNotRebuilt : Scenario = { Seed = 401; Ops = [ Op.Edit 1; Op.Run ] }

    /// Edit, rebuild, the worker loads the new build, run.
    let rebuiltAfterEdit : Scenario =
      { Seed = 402
        Ops = [ Op.Edit 1; Op.RecordStarted; Op.BuildStarts; Op.BuildEnds Ends.Builds; Op.Respawn; Op.RecordFinished; Op.Run ] }

    /// A rebuild that has started and not finished: the old worker keeps serving.
    let rebuildInFlight : Scenario = { Seed = 403; Ops = [ Op.Edit 0; Op.RecordStarted; Op.BuildStarts; Op.Run ] }

    /// A file cannot be read.
    let unreadable : Scenario = { Seed = 404; Ops = [ Op.Hide 2; Op.Run ] }

    /// The build finished, but the worker was never replaced: it still runs the build from before.
    let builtNotLoaded : Scenario = { Seed = 405; Ops = [ Op.Edit 0; Op.BuildStarts; Op.BuildEnds Ends.Builds; Op.RecordFinished; Op.Run ] }

    /// An outside build replaces the output on disk while the worker keeps its own.
    let externalBuild : Scenario = { Seed = 406; Ops = [ Op.ExternalBuild; Op.Run ] }

    /// The worker does not say when it loaded.
    let silentWorker : Scenario = { Seed = 407; Ops = [ Op.WorkerSilent; Op.Run ] }

    /// The rebuild record says it finished, long after an edit nobody rebuilt for.
    let recordSaysBuilt : Scenario = { Seed = 408; Ops = [ Op.RecordFinished; Op.Edit 2; Op.Run ] }

    /// A build that fails changes nothing the worker runs.
    let buildFails : Scenario = { Seed = 409; Ops = [ Op.Edit 1; Op.BuildStarts; Op.BuildEnds Ends.Fails; Op.RecordFailed; Op.Run ] }
