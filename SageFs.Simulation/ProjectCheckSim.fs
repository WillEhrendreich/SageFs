namespace SageFs.Simulation

open System
open SageFs.Features.ProjectCheckCore

/// Deterministic Simulation Testing for `check_project`: what a call decides about the compiler arguments it keeps, the
/// referenced projects it builds, and what a cancelled or concurrent call leaves behind.
///
/// A three-project world (app references lib and core, lib references core) with a simulated disk of file versions. A
/// history edits sources, project files, the package versions and a props file, builds a project from outside, and checks
/// a project: alone, with a cancel that arrives in one stage, or twice at once. The decisions folded are the REAL ones
/// from `ProjectCheckCore` (`Fingerprint`, `ArgumentCache`, `ArgumentStore`, `Inflight`, `Staleness`).
///
/// Same design rules as the other simulations:
///   * Chaos is DATA: a `Scenario` is an ordered op list. Same ops, same trace.
///   * Ground truth is an independent fold over the disk's file versions: when a run read the files, and whether a
///     project's output is behind anything it was built from. It never reads the cache.
///   * Twins reintroduce the bugs the invariants exist to catch.
module ProjectCheckSim =

  let projects : string list = [ "core"; "lib"; "app" ]

  let projectFile (p: string) : string = sprintf "/w/%s/%s.fsproj" p p
  let sourceFile (p: string) : string = sprintf "/w/%s/%s.fs" p p
  let assetsFile (p: string) : string = sprintf "/w/%s/obj/project.assets.json" p
  let outputFile (p: string) : string = sprintf "/w/%s/obj/Debug/net10.0/ref/%s.dll" p p

  [<Literal>]
  let packagesProps = "/w/Directory.Packages.props"

  [<Literal>]
  let buildProps = "/w/Directory.Build.props"

  let referencesOf (p: string) : string list =
    match p with
    | "app" -> [ "lib"; "core" ]
    | "lib" -> [ "core" ]
    | _ -> []

  [<RequireQualifiedAccess>]
  type Cancel =
    | NoCancel
    | AtArguments
    | AtReferenceBuild
    | AtCompiler

  [<RequireQualifiedAccess>]
  type Op =
    | EditSource of project: string
    | EditProjectFile of project: string
    | EditPackagesProps
    /// A Directory.Build.props appears above the projects, or is touched when it is there.
    | TouchBuildProps
    /// Someone runs `dotnet build` on one project outside the tool.
    | ExternalBuild of project: string
    | Check of project: string * Cancel
    /// Two calls for the same project arrive before either finishes.
    | CheckConcurrently of project: string

  type Scenario = { Seed: int; Ops: Op list }

  type World =
    { Clock: int64
      /// Files that exist, with the version (the write time) each has.
      Files: Map<string, int64>
      /// Each project's output file, when it was built.
      Outputs: Map<string, int64>
      Store: ArgumentStore
      /// Truth: when the run that produced each kept entry read the files.
      ReadAt: Map<CacheKey, int64> }

  /// What one check did, as the truth sees it.
  type RunSummary =
    { Ending: string
      /// Truth: files of the fingerprint written after the arguments in use were read, at the end of a completed check.
      FilesNewerThanArguments: string list
      /// The store as it was before an arguments run that was cancelled, and after.
      StoreBeforeCancelledRun: ArgumentStore option
      StoreAfter: ArgumentStore
      /// MSBuild runs started by the calls of this op.
      MsBuildRuns: int
      Calls: int
      Built: string list
      /// Truth: referenced projects that were behind before the check.
      BehindBefore: string list
      /// Truth: referenced projects behind after a completed check.
      BehindAfter: string list }

  type Snapshot = { Op: Op; Ran: RunSummary option }

  type Trace =
    { Scenario: Scenario
      Snapshots: Snapshot list
      Reducer: string }

  [<RequireQualifiedAccess>]
  type private Policy =
    /// The real behaviour.
    | Real
    /// TWIN: the cache never notices a change to Directory.Packages.props.
    | IgnoresPackagesProps
    /// TWIN: a cancelled arguments run still records what it had read.
    | KeepsCancelledRuns
    /// TWIN: two calls at once each start their own run.
    | NoCoalescing
    /// TWIN: a referenced project is never found behind.
    | NeverBehind
    /// TWIN: every referenced project is built on every check.
    | BuildsEverything

  let private key (project: string) : CacheKey = { Project = projectFile project; Configuration = "Debug" }

  let private identities (project: string) : string list = [ "-r:/nuget/fsharp.core/FSharp.Core.dll"; sourceFile project ]

  let private factsOf (project: string) : MsBuildFacts =
    let arguments =
      match CompilerArguments.ofIdentities (projectFile project) (identities project) with
      | Ok a -> a
      | Error e -> failwithf "the sim's own arguments were refused: %A" e
    { Arguments = arguments
      Configuration = "Debug"
      References = referencesOf project |> List.map (fun r -> { Project = projectFile r; Output = outputFile r })
      Inputs = [ projectFile project; packagesProps ] }

  let private initial : World =
    let files =
      [ for p in projects do
          yield projectFile p
          yield sourceFile p
          yield assetsFile p
        yield packagesProps ]
      |> List.map (fun f -> f, 1L)
      |> Map.ofList
    { Clock = 2L
      Files = files
      Outputs = projects |> List.map (fun p -> p, 1L) |> Map.ofList
      Store = ArgumentStore.empty
      ReadAt = Map.empty }

  let private stampOf (w: World) (path: string) : StampState =
    match Map.tryFind path w.Files with
    | Some version -> StampState.Present(version, 100L)
    | None -> StampState.Absent

  let private versionOf (w: World) (path: string) : int64 option = Map.tryFind path w.Files

  let private touch (path: string) (w: World) : World =
    { w with Files = Map.add path w.Clock w.Files; Clock = w.Clock + 1L }

  // ---- truth ----

  /// Truth: a project is behind when its output is missing, older than a file it is built from, or older than the output
  /// of a project it references, or when anything it references is behind. Read from versions alone.
  let rec private behind (w: World) (project: string) : bool =
    match Map.tryFind project w.Outputs with
    | None -> true
    | Some built ->
      let own = [ projectFile project; sourceFile project; assetsFile project; packagesProps ] |> List.choose (versionOf w)
      let depOutputs = referencesOf project |> List.choose (fun d -> Map.tryFind d w.Outputs)
      (own @ depOutputs |> List.exists (fun v -> v > built))
      || (referencesOf project |> List.exists (behind w))

  let private behindAmong (w: World) (names: string list) : string list = names |> List.filter (behind w)

  // ---- the system under test: the real decisions, composed the way the effect module composes them ----

  /// The write time of a file on the simulated disk: a source or project file, or a project's output.
  let private writeTime (w: World) (path: string) : int64 option =
    match Map.tryFind path w.Files with
    | Some version -> Some version
    | None -> projects |> List.tryFind (fun p -> outputFile p = path) |> Option.bind (fun p -> Map.tryFind p w.Outputs)

  let private stalenessOf (w: World) (project: string) : Staleness =
    let files = [ projectFile project; sourceFile project; assetsFile project; packagesProps ]
    let inputs = Staleness.inputsOf (writeTime w) files (referencesOf project |> List.map outputFile)
    let output =
      match writeTime w (outputFile project) with
      | Some built -> OutputState.OutputAt(outputFile project, built)
      | None -> OutputState.NoOutput(outputFile project)
    Staleness.decide output inputs

  let private closure (root: string list) : string list =
    let rec go (seen: string list) (next: string list) =
      match next with
      | [] -> seen
      | p :: rest when List.contains p seen -> go seen rest
      | p :: rest -> go (p :: seen) (referencesOf p @ rest)
    go [] root |> List.rev

  let private emptySummary (w: World) : RunSummary =
    { Ending = "checked"
      FilesNewerThanArguments = []
      StoreBeforeCancelledRun = None
      StoreAfter = w.Store
      MsBuildRuns = 0
      Calls = 1
      Built = []
      BehindBefore = []
      BehindAfter = [] }

  let private check (policy: Policy) (start: World) (project: string) (cancel: Cancel) (calls: int) : World * RunSummary =
    let k = key project
    let facts = factsOf project
    let current (w: World) : string -> StampState =
      match policy with
      | Policy.IgnoresPackagesProps ->
        // The twin's probe reports the props file as it was when the entry was read.
        fun path ->
          match path = packagesProps, ArgumentStore.tryFind k w.Store with
          | true, Some entry -> entry.Stamps |> List.tryFind (fun s -> s.Path = path) |> Option.map (fun s -> s.State) |> Option.defaultValue (stampOf w path)
          | _ -> stampOf w path
      | _ -> stampOf w
    let refsBefore = closure (referencesOf project) |> List.filter (fun p -> p <> project)
    let behindBefore = behindAmong start refsBefore
    let decision = ArgumentCache.decide (ArgumentStore.tryFind k start.Store) (current start)
    // Concurrency: the first call starts the run; with coalescing the others follow it and run nothing.
    let starts =
      match policy with
      | Policy.NoCoalescing -> calls
      | Policy.Real
      | Policy.IgnoresPackagesProps
      | Policy.KeepsCancelledRuns
      | Policy.NeverBehind
      | Policy.BuildsEverything ->
        [ 1..calls ]
        |> List.fold
          (fun (inflight, n) _ ->
            match Inflight.join k inflight with
            | Joined.Starts, next -> next, n + 1
            | Joined.Follows, next -> next, n)
          (Inflight.empty, 0)
        |> snd
    let runsForRefresh =
      match decision with
      | CacheDecision.Reuse _ -> 0
      | CacheDecision.Refresh _ -> starts
    let base' ={ emptySummary start with Calls = calls; MsBuildRuns = runsForRefresh; BehindBefore = behindBefore }
    match decision, cancel with
    | CacheDecision.Refresh _, Cancel.AtArguments ->
      let ending =
        match policy with
        | Policy.KeepsCancelledRuns -> RunEnd.Completed
        | _ -> RunEnd.Cancelled
      let partial = { Key = k; Facts = facts; Stamps = Fingerprint.stamp (stampOf start) facts }
      let store = ArgumentStore.record partial ending start.Store
      { start with Store = store },
      { base' with Ending = "cancelled at arguments"; StoreBeforeCancelledRun = Some start.Store; StoreAfter = store }
    | _ ->
      // The arguments are read (or kept) here.
      let w1, usedReadAt =
        match decision with
        | CacheDecision.Reuse _ -> start, (Map.tryFind k start.ReadAt |> Option.defaultValue 0L)
        | CacheDecision.Refresh _ ->
          let entry = { Key = k; Facts = facts; Stamps = Fingerprint.stamp (stampOf start) facts }
          { start with Store = ArgumentStore.record entry RunEnd.Completed start.Store; ReadAt = Map.add k start.Clock start.ReadAt; Clock = start.Clock + 1L },
          start.Clock
      let refs = closure (referencesOf project) |> List.filter (fun p -> p <> project)
      let referencesMap = refs |> List.map (fun p -> p, referencesOf p) |> Map.ofList
      let own =
        refs
        |> List.map (fun p ->
          p,
          match policy with
          | Policy.NeverBehind -> Staleness.Current
          | Policy.BuildsEverything -> Staleness.Missing "everything"
          | Policy.Real
          | Policy.IgnoresPackagesProps
          | Policy.KeepsCancelledRuns
          | Policy.NoCoalescing -> stalenessOf w1 p)
        |> Map.ofList
      let order = Staleness.buildOrder referencesMap own
      let stopped, built, w2 =
        order
        |> List.fold
          (fun (stopped, built, w) p ->
            match stopped with
            | Some _ -> stopped, built, w
            | None ->
              match cancel, built with
              | Cancel.AtReferenceBuild, [] -> Some p, built, w
              | _ ->
                let w' = { w with Outputs = Map.add p w.Clock w.Outputs; Clock = w.Clock + 1L }
                None, built @ [ p ], w')
          (None, [], w1)
      let summary = { base' with Built = built; StoreAfter = w2.Store }
      match stopped, cancel with
      | Some _, _ -> w2, { summary with Ending = "cancelled at reference build" }
      | None, Cancel.AtCompiler -> w2, { summary with Ending = "cancelled at compiler" }
      | None, _ ->
        let newer =
          Fingerprint.paths facts
          |> List.filter (fun path -> match versionOf w2 path with Some v -> v > usedReadAt | None -> false)
        w2, { summary with FilesNewerThanArguments = newer; BehindAfter = behindAmong w2 refs }

  let private step (policy: Policy) (w: World) (op: Op) : World * RunSummary option =
    match op with
    | Op.EditSource p -> touch (sourceFile p) w, None
    | Op.EditProjectFile p -> touch (projectFile p) w, None
    | Op.EditPackagesProps -> touch packagesProps w, None
    | Op.TouchBuildProps -> touch buildProps w, None
    | Op.ExternalBuild p -> { w with Outputs = Map.add p w.Clock w.Outputs; Clock = w.Clock + 1L }, None
    | Op.Check(p, cancel) ->
      let w', summary = check policy w p cancel 1
      w', Some summary
    | Op.CheckConcurrently p ->
      let w', summary = check policy w p Cancel.NoCancel 2
      w', Some summary

  let private runWith (name: string) (policy: Policy) (scenario: Scenario) : Trace =
    let _, snapshots =
      scenario.Ops
      |> List.fold
        (fun (w, acc) op ->
          let w', ran = step policy w op
          w', { Op = op; Ran = ran } :: acc)
        (initial, [])
    { Scenario = scenario; Snapshots = List.rev snapshots; Reducer = name }

  /// The real decisions.
  let run (scenario: Scenario) : Trace = runWith "real (ProjectCheckCore decisions)" Policy.Real scenario

  /// TWIN: the cache never notices a change to Directory.Packages.props.
  let runIgnoresPackagesProps (scenario: Scenario) : Trace = runWith "twin-ignores-packages-props" Policy.IgnoresPackagesProps scenario

  /// TWIN: a cancelled arguments run still records what it had read.
  let runKeepsCancelledRuns (scenario: Scenario) : Trace = runWith "twin-keeps-cancelled-runs" Policy.KeepsCancelledRuns scenario

  /// TWIN: two calls at once each start their own run.
  let runNoCoalescing (scenario: Scenario) : Trace = runWith "twin-no-coalescing" Policy.NoCoalescing scenario

  /// TWIN: a referenced project is never found behind.
  let runNeverBehind (scenario: Scenario) : Trace = runWith "twin-never-behind" Policy.NeverBehind scenario

  /// TWIN: every referenced project is built on every check.
  let runBuildsEverything (scenario: Scenario) : Trace = runWith "twin-builds-everything" Policy.BuildsEverything scenario

module ProjectCheckInvariants =
  open ProjectCheckSim

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private firstViolation (t: Trace) (violates: int -> Snapshot -> RunSummary -> string option) : Outcome =
    t.Snapshots
    |> List.mapi (fun i s -> s.Ran |> Option.bind (violates i s))
    |> List.tryPick id
    |> function
      | Some message -> Outcome.Violated(sprintf "%s (seed=%d, ops=%A)" message t.Scenario.Seed t.Scenario.Ops)
      | None -> Outcome.Holds

  /// arguments-are-never-older-than-a-file-that-shaped-them: a check that completed used arguments read after the last
  /// write to every file of their fingerprint.
  let argumentsAreNeverOlderThanAFileThatShapedThem : Invariant =
    { Id = "arguments-are-never-older-than-a-file-that-shaped-them"
      Description = "After a completed check, no file of the arguments' fingerprint was written after the arguments were read."
      Check = fun t ->
        firstViolation t (fun i s r ->
          match r.FilesNewerThanArguments with
          | [] -> None
          | files -> Some(sprintf "after op #%d (%A) the check used arguments older than %A" i s.Op files)) }

  /// a-cancelled-run-keeps-nothing: an arguments run that was cancelled leaves the store as it was.
  let aCancelledRunKeepsNothing : Invariant =
    { Id = "a-cancelled-run-keeps-nothing"
      Description = "The store after a cancelled arguments run equals the store before it."
      Check = fun t ->
        firstViolation t (fun i s r ->
          match r.StoreBeforeCancelledRun with
          | Some before when before <> r.StoreAfter -> Some(sprintf "after op #%d (%A) a cancelled run changed the kept arguments" i s.Op)
          | Some _
          | None -> None) }

  /// calls-at-once-share-one-run: however many calls for one key arrive together, MSBuild runs at most once.
  let callsAtOnceShareOneRun : Invariant =
    { Id = "calls-at-once-share-one-run"
      Description = "An op with several simultaneous calls for one key starts at most one MSBuild run."
      Check = fun t ->
        firstViolation t (fun i s r ->
          match r.MsBuildRuns > 1 with
          | true -> Some(sprintf "after op #%d (%A) %d calls started %d MSBuild runs" i s.Op r.Calls r.MsBuildRuns)
          | false -> None) }

  /// a-referenced-project-is-built-only-when-behind-and-never-left-behind: the projects built are exactly the ones that were
  /// behind (by the truth), each once, and a completed check leaves none behind.
  let aReferencedProjectIsBuiltOnlyWhenBehindAndNeverLeftBehind : Invariant =
    { Id = "a-referenced-project-is-built-only-when-behind-and-never-left-behind"
      Description = "Builds are a subset of the projects that were behind, each once; none is behind after a completed check."
      Check = fun t ->
        firstViolation t (fun i s r ->
          let unneeded = r.Built |> List.filter (fun p -> not (List.contains p r.BehindBefore))
          let twice = r.Built |> List.length |> (<>) (List.distinct r.Built |> List.length)
          match unneeded, twice, r.Ending, r.BehindAfter with
          | _ :: _, _, _, _ -> Some(sprintf "after op #%d (%A) built %A that were not behind" i s.Op unneeded)
          | _, true, _, _ -> Some(sprintf "after op #%d (%A) built a project twice: %A" i s.Op r.Built)
          | _, _, "checked", (_ :: _ as left) -> Some(sprintf "after op #%d (%A) the check ran with %A still behind" i s.Op left)
          | _ -> None) }

  let all : Invariant list =
    [ argumentsAreNeverOlderThanAFileThatShapedThem
      aCancelledRunKeepsNothing
      callsAtOnceShareOneRun
      aReferencedProjectIsBuiltOnlyWhenBehindAndNeverLeftBehind ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated message -> Some(inv.Id, message))

module ProjectCheckGenerators =
  open ProjectCheckSim

  let private project (rnd: Random) : string = [| "core"; "lib"; "app" |].[rnd.Next 3]

  let private cancel (rnd: Random) : Cancel =
    match rnd.Next 8 with
    | 0 -> Cancel.AtArguments
    | 1 -> Cancel.AtReferenceBuild
    | 2 -> Cancel.AtCompiler
    | _ -> Cancel.NoCancel

  let private op (rnd: Random) : Op =
    match rnd.Next 16 with
    | 0 | 1 | 2 -> Op.EditSource(project rnd)
    | 3 -> Op.EditProjectFile(project rnd)
    | 4 -> Op.EditPackagesProps
    | 5 -> Op.TouchBuildProps
    | 6 -> Op.ExternalBuild(project rnd)
    | 7 | 8 | 9 | 10 | 11 -> Op.Check("app", cancel rnd)
    | 12 -> Op.Check("lib", cancel rnd)
    | 13 | 14 -> Op.CheckConcurrently "app"
    | _ -> Op.CheckConcurrently "lib"

  /// A general history of 3 to 16 ops. Same seed, identical list.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(3, 17)
    { Seed = seed; Ops = [ for _ in 1..n -> op rnd ] }

  // Named minimal scenarios

  /// A check, then the package versions change, then a check: the second reads the arguments again.
  let packagesChangeBetweenChecks : Scenario =
    { Seed = 400; Ops = [ Op.Check("app", Cancel.NoCancel); Op.EditPackagesProps; Op.Check("app", Cancel.NoCancel) ] }

  /// A cancel while MSBuild runs, then a check.
  let cancelledArgumentsRun : Scenario =
    { Seed = 401; Ops = [ Op.Check("app", Cancel.AtArguments); Op.Check("app", Cancel.NoCancel) ] }

  /// Two calls at once on a cold cache.
  let twoCallsAtOnce : Scenario = { Seed = 402; Ops = [ Op.CheckConcurrently "app" ] }

  /// A referenced project's source changes, so the check builds it and what stands on it.
  let referencedSourceEdited : Scenario =
    { Seed = 403; Ops = [ Op.EditSource "core"; Op.Check("app", Cancel.NoCancel); Op.Check("app", Cancel.NoCancel) ] }

  /// A deep project is built from outside after the one above it, so the one above is behind its reference.
  let externalBuildOfAReference : Scenario =
    { Seed = 404; Ops = [ Op.ExternalBuild "core"; Op.Check("app", Cancel.NoCancel) ] }

  /// A Directory.Build.props appears after the arguments were kept.
  let buildPropsAppears : Scenario =
    { Seed = 405; Ops = [ Op.Check("lib", Cancel.NoCancel); Op.TouchBuildProps; Op.Check("lib", Cancel.NoCancel) ] }

  /// A cancel arrives while a referenced project builds.
  let cancelledReferenceBuild : Scenario =
    { Seed = 406; Ops = [ Op.EditSource "core"; Op.Check("app", Cancel.AtReferenceBuild); Op.Check("app", Cancel.NoCancel) ] }
