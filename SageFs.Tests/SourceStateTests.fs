/// Is the build this session runs behind the files on disk? `SourceState.decide` answers from what an edge read (when the
/// worker loaded its build, when the build's output was written, when each source file was written) and from the session's
/// rebuild record. These are the claims it must keep, and the way the answer reaches a receipt:
///
///   * InSync only when every stamp was read, no file is newer than the build it went into, no build is newer than the load,
///     and no rebuild is in progress. Anything else is not InSync, and an IO failure is its own answer (Unknown), never a
///     quiet "fine".
///   * A receipt over a source that is not InSync is never spelled AllPassed.
module SageFs.Tests.SourceStateTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.LiveTesting
// After LiveTesting, so `RunVerdict` is the receipt's (LiveTesting has a `RunVerdict` of its own).
open SageFs.Features.RunReceipts
open SageFs.Tests.LiveTestingTestHelpers

/// A non-negative number from any int (`abs` throws on Int32.MinValue).
let private nat (n: int) = n &&& Int32.MaxValue

let private t0 = DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc)
let private at (seconds: int) = t0.AddSeconds(float seconds)

let private written (path: string) (seconds: int) : StampedFile = { Path = path; Stamp = StampRead.Written (at seconds) }
let private unreadable (path: string) : StampedFile = { Path = path; Stamp = StampRead.Unreadable "permission denied" }

let private project (name: string) (output: StampedFile) (sources: StampedFile list) : ProjectEvidence =
  ProjectEvidence.Inspected (name, output, sources)

let private loadedAt (seconds: int) = LoadedAt.Reported (at seconds)

let private neverRebuilt = LastRebuild.NeverRebuilt
let private rebuilding = LastRebuild.Latest (RebuildOutcome.InProgress (at 50))
let private rebuilt = LastRebuild.Latest (RebuildOutcome.Succeeded (at 60))

let private decide = SourceState.decide

[<Tests>]
let decideTests =
  testList "SourceState.decide" [

    testCase "WHY — every source older than the build, and the build no newer than the load, is in sync, and says how many files it compared" <| fun _ ->
      let state = decide neverRebuilt (loadedAt 100) [ project "Lib" (written "Lib.dll" 50) [ written "A.fs" 10; written "B.fs" 20 ] ]
      state |> Expect.equal "in sync, with the evidence" (SourceState.InSync (at 50, 2))

    testCase "WHY — a source written after the build the session runs is named, with when it was edited and when the build was made" <| fun _ ->
      let state = decide neverRebuilt (loadedAt 100) [ project "Lib" (written "Lib.dll" 50) [ written "A.fs" 10; written "B.fs" 70 ] ]
      state
      |> Expect.equal "stale, naming the edited file" (SourceState.Stale [ { Path = "B.fs"; Because = StaleBecause.EditedAfterBuild (at 70, at 50) } ])

    testCase "WHY — a source written at the very instant of the build went into it, so it is not stale" <| fun _ ->
      let state = decide neverRebuilt (loadedAt 100) [ project "Lib" (written "Lib.dll" 50) [ written "A.fs" 50 ] ]
      state |> Expect.equal "equal stamps are in sync" (SourceState.InSync (at 50, 1))

    testCase "WHY — a build on disk newer than the one the worker loaded means the session runs an older build than the one just made" <| fun _ ->
      let state = decide neverRebuilt (loadedAt 40) [ project "Lib" (written "Lib.dll" 50) [ written "A.fs" 10 ] ]
      state
      |> Expect.equal "stale, naming the build output" (SourceState.Stale [ { Path = "Lib.dll"; Because = StaleBecause.RebuiltAfterLoad (at 50, at 40) } ])

    testCase "WHY — while a rebuild is in progress the answer is Rebuilding, even when every file looks in sync" <| fun _ ->
      let state = decide rebuilding (loadedAt 100) [ project "Lib" (written "Lib.dll" 50) [ written "A.fs" 10 ] ]
      state |> Expect.equal "rebuilding, since it started" (SourceState.Rebuilding (at 50))

    testCase "WHY — while a rebuild is in progress the answer is Rebuilding, even when files are stale or unreadable" <| fun _ ->
      decide rebuilding (loadedAt 100) [ project "Lib" (written "Lib.dll" 50) [ written "A.fs" 90 ] ]
      |> Expect.equal "rebuilding beats stale" (SourceState.Rebuilding (at 50))
      decide rebuilding (loadedAt 100) [ project "Lib" (written "Lib.dll" 50) [ unreadable "A.fs" ] ]
      |> Expect.equal "rebuilding beats unknown" (SourceState.Rebuilding (at 50))

    testCase "WHY — a finished or failed rebuild is not a rebuild in progress" <| fun _ ->
      decide rebuilt (loadedAt 100) [ project "Lib" (written "Lib.dll" 50) [ written "A.fs" 10 ] ]
      |> Expect.equal "succeeded is just history" (SourceState.InSync (at 50, 1))
      decide (LastRebuild.Latest (RebuildOutcome.FailedStillServing (SageFsError.SessionCreationFailed "no", at 60))) (loadedAt 100) [ project "Lib" (written "Lib.dll" 50) [ written "A.fs" 10 ] ]
      |> Expect.equal "a failed rebuild leaves the old build serving, and the old build is what the files built" (SourceState.InSync (at 50, 1))

    testCase "WHY — a source whose write time cannot be read is Unknown and names the file and why, never in sync" <| fun _ ->
      let state = decide neverRebuilt (loadedAt 100) [ project "Lib" (written "Lib.dll" 50) [ written "A.fs" 10; unreadable "B.fs" ] ]
      state |> Expect.equal "unknown, naming the unreadable file" (SourceState.Unknown (UnknownReason.Unreadable ("B.fs", "permission denied")))

    testCase "WHY — a build output that cannot be read is Unknown, because nothing can be compared to it" <| fun _ ->
      let state = decide neverRebuilt (loadedAt 100) [ project "Lib" (unreadable "Lib.dll") [ written "A.fs" 10 ] ]
      state |> Expect.equal "unknown, naming the output" (SourceState.Unknown (UnknownReason.Unreadable ("Lib.dll", "permission denied")))

    testCase "WHY — a worker that did not say when it loaded its build leaves the answer Unknown" <| fun _ ->
      let state = decide neverRebuilt (LoadedAt.NotReported "the worker did not answer") [ project "Lib" (written "Lib.dll" 50) [ written "A.fs" 10 ] ]
      state |> Expect.equal "unknown, with why" (SourceState.Unknown (UnknownReason.LoadTimeNotReported "the worker did not answer"))

    testCase "WHY — a session with no project has no build to be behind, and says that instead of claiming in sync" <| fun _ ->
      decide neverRebuilt (loadedAt 100) [] |> Expect.equal "no project loaded" (SourceState.Unknown UnknownReason.NoProjectLoaded)

    testCase "WHY — a project whose sources could not be listed is Unknown and names the project" <| fun _ ->
      let state = decide neverRebuilt (loadedAt 100) [ ProjectEvidence.NotInspectable ("Lib.fsproj", "uses a wildcard Compile item") ]
      state |> Expect.equal "unknown, naming the project" (SourceState.Unknown (UnknownReason.NotInspectable ("Lib.fsproj", "uses a wildcard Compile item")))

    testCase "WHY — a stale file in one project is reported even when another project could not be read, because it is the fact someone can act on" <| fun _ ->
      let state =
        decide neverRebuilt (loadedAt 100)
          [ project "Core" (unreadable "Core.dll") [ written "C.fs" 1 ]
            project "App" (written "App.dll" 50) [ written "A.fs" 70 ] ]
      state
      |> Expect.equal "stale wins over unknown" (SourceState.Stale [ { Path = "A.fs"; Because = StaleBecause.EditedAfterBuild (at 70, at 50) } ])

    testCase "WHY — with several projects, in sync reports the oldest build and the total of files compared" <| fun _ ->
      let state =
        decide neverRebuilt (loadedAt 100)
          [ project "Core" (written "Core.dll" 30) [ written "C.fs" 1 ]
            project "App" (written "App.dll" 50) [ written "A.fs" 10; written "B.fs" 20 ] ]
      state |> Expect.equal "oldest build, three files" (SourceState.InSync (at 30, 3))
  ]

// ── seeded evidence, and an oracle written the other way round ────────────────────────

/// What a seed makes: a rebuild record, a load stamp and a few projects with a few files, some of them unreadable.
let private evidenceOf (seed: int) : LastRebuild * LoadedAt * ProjectEvidence list =
  let rnd = Random seed
  let stamp (path: string) : StampedFile =
    match rnd.Next 12 with
    | 0 -> unreadable path
    | _ -> written path (rnd.Next(0, 110))
  let rebuild =
    match rnd.Next 8 with
    | 0 -> rebuilding
    | 1 -> rebuilt
    | _ -> neverRebuilt
  let loaded =
    match rnd.Next 12 with
    | 0 -> LoadedAt.NotReported "the worker did not answer"
    | _ -> loadedAt 100
  let projects =
    [ for p in 0 .. rnd.Next(0, 3) ->
        match rnd.Next 14 with
        | 0 -> ProjectEvidence.NotInspectable (sprintf "P%d.fsproj" p, "unparsable")
        | _ -> project (sprintf "P%d" p) (stamp (sprintf "P%d.dll" p)) [ for f in 0 .. rnd.Next(0, 4) -> stamp (sprintf "P%dF%d.fs" p f) ] ]
  rebuild, loaded, projects

/// The claim, as a conjunction: in sync means every part of the evidence checks out.
let private everythingChecksOut (rebuild: LastRebuild) (loaded: LoadedAt) (projects: ProjectEvidence list) : bool =
  let noRebuildRunning =
    match rebuild with
    | LastRebuild.Latest (RebuildOutcome.InProgress _) -> false
    | _ -> true
  let loadStamp =
    match loaded with
    | LoadedAt.Reported at -> Some at
    | LoadedAt.NotReported _ -> None
  let projectChecksOut (loadedStamp: DateTime) (evidence: ProjectEvidence) =
    match evidence with
    | ProjectEvidence.NotInspectable _ -> false
    | ProjectEvidence.Inspected (_, output, sources) ->
      match output.Stamp with
      | StampRead.Unreadable _ -> false
      | StampRead.Written built ->
        built <= loadedStamp
        && sources |> List.forall (fun s -> match s.Stamp with StampRead.Written w -> w <= built | StampRead.Unreadable _ -> false)
  noRebuildRunning
  && not (List.isEmpty projects)
  && (match loadStamp with Some l -> projects |> List.forall (projectChecksOut l) | None -> false)

let private isInSync (state: SourceState) = match state with SourceState.InSync _ -> true | _ -> false

[<Tests>]
let propertyTests =
  testList "SourceState.decide properties" [

    testProperty "WHY — NEVER-INSYNC-UNLESS-VERIFIED: the answer is InSync exactly when every stamp was read, nothing is newer than the build it went into, no build is newer than the load, and no rebuild is running" <| fun (seed: int) ->
      let rebuild, loaded, projects = evidenceOf seed
      isInSync (decide rebuild loaded projects) = everythingChecksOut rebuild loaded projects

    testProperty "WHY — REBUILD-IS-NEVER-INSYNC: a rebuild in progress is always Rebuilding, whatever the files say" <| fun (seed: int) ->
      let _, loaded, projects = evidenceOf seed
      match decide rebuilding loaded projects with
      | SourceState.Rebuilding since -> since = at 50
      | _ -> false

    testProperty "WHY — a file that cannot be read never makes a state better: swapping any one stamp for an unreadable one cannot turn a non-InSync answer into InSync" <| fun (seed: int) ->
      let rebuild, loaded, projects = evidenceOf seed
      let before = decide rebuild loaded projects
      let worsened =
        projects
        |> List.map (fun p ->
          match p with
          | ProjectEvidence.Inspected (name, output, first :: rest) -> ProjectEvidence.Inspected (name, output, unreadable first.Path :: rest)
          | other -> other)
      let after = decide rebuild loaded worsened
      not (isInSync after) || isInSync before

    testProperty "WHY — editing a file later never makes a stale session look current: pushing every source stamp forward by a minute cannot turn a non-InSync answer into InSync" <| fun (seed: int) ->
      let rebuild, loaded, projects = evidenceOf seed
      let before = decide rebuild loaded projects
      let later =
        projects
        |> List.map (fun p ->
          match p with
          | ProjectEvidence.Inspected (name, output, sources) ->
            let bump (s: StampedFile) = match s.Stamp with StampRead.Written w -> { s with Stamp = StampRead.Written (w.AddMinutes 1.0) } | _ -> s
            ProjectEvidence.Inspected (name, output, sources |> List.map bump)
          | other -> other)
      let after = decide rebuild loaded later
      not (isInSync after) || isInSync before
  ]

// ── worse: a run that spans two readings ──────────────────────────────────────────────

let private staleA = SourceState.Stale [ { Path = "A.fs"; Because = StaleBecause.EditedAfterBuild (at 70, at 50) } ]
let private staleB = SourceState.Stale [ { Path = "B.fs"; Because = StaleBecause.EditedAfterBuild (at 80, at 50) } ]
let private inSync = SourceState.InSync (at 50, 2)
let private rebuildingState = SourceState.Rebuilding (at 60)
let private unknownState = SourceState.Unknown (UnknownReason.Unreadable ("A.fs", "denied"))

[<Tests>]
let worseTests =
  testList "SourceState.worse" [

    testCase "WHY — a run dispatched in sync and finished stale is stale: an edit during the run counts" <| fun _ ->
      SourceState.worse inSync staleA |> Expect.equal "stale" staleA
      SourceState.worse staleA inSync |> Expect.equal "stale either way round" staleA

    testCase "WHY — two stale readings keep every file either one named, each once" <| fun _ ->
      match SourceState.worse staleA staleB with
      | SourceState.Stale files -> files |> List.map (fun f -> f.Path) |> List.sort |> Expect.equal "both files" [ "A.fs"; "B.fs" ]
      | other -> failtestf "expected Stale, got %A" other
      match SourceState.worse staleA staleA with
      | SourceState.Stale files -> files |> List.length |> Expect.equal "a file named twice is named once" 1
      | other -> failtestf "expected Stale, got %A" other

    testCase "WHY — stale outranks a rebuild, a rebuild outranks unknown, and unknown outranks in sync" <| fun _ ->
      SourceState.worse rebuildingState staleA |> Expect.equal "stale beats rebuilding" staleA
      SourceState.worse unknownState rebuildingState |> Expect.equal "rebuilding beats unknown" rebuildingState
      SourceState.worse inSync unknownState |> Expect.equal "unknown beats in sync" unknownState

    testCase "WHY — two in-sync readings are in sync" <| fun _ ->
      SourceState.worse inSync inSync |> Expect.equal "in sync" inSync

    testProperty "WHY — worse is InSync only when both readings are" <| fun (a: int, b: int) ->
      let pick (n: int) = match nat n % 4 with 0 -> inSync | 1 -> staleA | 2 -> rebuildingState | _ -> unknownState
      isInSync (SourceState.worse (pick a) (pick b)) = (isInSync (pick a) && isInSync (pick b))
  ]

// ── what a reader sees ──────────────────────────────────────────────────────────────

let private parse (state: SourceState) : System.Text.Json.JsonElement =
  use doc = System.Text.Json.JsonDocument.Parse(Json.serialize Json.standard (SourceState.toWire state))
  doc.RootElement.Clone()

[<Tests>]
let renderTests =
  testList "SourceState rendering" [

    testCase "WHY — each state has its own token, and the wire carries it next to a sentence" <| fun _ ->
      [ inSync, "InSync"; staleA, "Stale"; rebuildingState, "Rebuilding"; unknownState, "Unknown" ]
      |> List.iter (fun (state, expected) ->
        SourceState.token state |> Expect.equal (sprintf "token for %A" state) expected
        let wire = parse state
        wire.GetProperty("state").GetString() |> Expect.equal "wire state" expected
        wire.GetProperty("message").GetString() |> Expect.isNotEmpty "wire message")

    testCase "WHY — a stale state names every changed file on the wire, with what changed and when" <| fun _ ->
      let wire = parse staleA
      let files = [ for f in wire.GetProperty("changedFiles").EnumerateArray() -> f ]
      files |> List.length |> Expect.equal "one changed file" 1
      files.[0].GetProperty("path").GetString() |> Expect.equal "the path" "A.fs"
      files.[0].GetProperty("because").GetString() |> Expect.equal "the cause token" "EditedAfterBuild"

    testCase "WHY — an unknown state says what it could not tell, as a token and as words" <| fun _ ->
      let wire = parse unknownState
      wire.GetProperty("reason").GetProperty("kind").GetString() |> Expect.equal "the reason kind" "Unreadable"
      SourceState.describe unknownState |> Expect.stringContains "names the file" "A.fs"
      SourceState.describe unknownState |> Expect.stringContains "says it could not tell" "could not"

    testCase "WHY — a stale description says STALE, names the file, and says what to do about it" <| fun _ ->
      let text = SourceState.describe staleA
      text |> Expect.stringContains "says STALE" "STALE"
      text |> Expect.stringContains "names the file" "A.fs"
      text |> Expect.stringContains "says how to fix it" "rebuild"

    testCase "WHY — the banner is empty only when the build is current" <| fun _ ->
      SourceState.banner inSync |> Expect.equal "nothing to warn about" ""
      [ staleA; rebuildingState; unknownState ]
      |> List.iter (fun state -> SourceState.banner state |> Expect.isNotEmpty (sprintf "a warning for %A" state))

    testCase "WHY — a session list always has a source line: plain when in sync, and loud when not, so an absence is never read as fine" <| fun _ ->
      SourceState.listLine inSync |> Expect.stringContains "in sync, plainly" "in sync"
      SourceState.listLine staleA |> Expect.stringContains "stale, loudly" "STALE SOURCE"
      SourceState.listLine rebuildingState |> Expect.stringContains "a rebuild is said" "rebuild"
      SourceState.listLine unknownState |> Expect.stringContains "unknown says why" "A.fs"
      SourceState.listLine (SourceState.Unknown UnknownReason.NoProjectLoaded) |> Expect.stringContains "a bare session says it has no project" "no project"

    testCase "WHY — annotate keeps the result first and whole, and adds the warning only when the build is not known to be current" <| fun _ ->
      SourceState.annotate inSync "result" |> Expect.equal "nothing added" "result"
      let text = SourceState.annotate staleA "result"
      text |> Expect.stringContains "the result is still first" "result\n\nWARNING"
      text |> Expect.stringContains "and the warning says STALE" "STALE"

    testCase "WHY — a rebuild is described as in progress, with the old build still serving" <| fun _ ->
      SourceState.describe rebuildingState |> Expect.stringContains "says rebuild" "rebuild"
      SourceState.describe rebuildingState |> Expect.stringContains "says the old build keeps serving" "keeps serving"
  ]

// ── the receipt ─────────────────────────────────────────────────────────────────────────

let private lineOf (name: string) (outcome: LineOutcome) : ReceiptLine =
  { Id = TestId.create name TestFramework.Expecto; Name = name; Outcome = outcome }

let private receiptOf (outcomes: LineOutcome list) : RunReceipt =
  let lines = outcomes |> List.mapi (fun i o -> lineOf (sprintf "t%d" i) o)
  let count (pick: LineOutcome -> bool) = lines |> List.filter (fun l -> pick l.Outcome) |> List.length
  RunReceipt.Ran
    { RequestId = RunRequestId.fresh ()
      Session = "s1"
      Generation = RunGeneration.zero
      Verdict = RunVerdict.Incomplete
      Counts =
        { Passing = count (function LineOutcome.Passed _ -> true | _ -> false)
          Failing = count (function LineOutcome.Failed _ -> true | _ -> false)
          Skipping = count (function LineOutcome.Skipped _ -> true | _ -> false)
          Unreported = count (function LineOutcome.DidNotReport _ -> true | _ -> false) }
      Lines = lines
      Source = SourceState.Unknown UnknownReason.NotAssessed }

let private passed = LineOutcome.Passed FixtureDurations.usualResult

let private verdictWith (source: SourceState) (outcomes: LineOutcome list) : RunVerdict =
  match TestRunReceipt.withSource source (receiptOf outcomes) with
  | RunReceipt.Ran ran -> ran.Verdict
  | other -> failtestf "expected Ran, got %A" other

[<Tests>]
let receiptTests =
  testList "a receipt carries the source it ran against" [

    testCase "WHY — all passed over an in-sync source is AllPassed, and only then" <| fun _ ->
      verdictWith inSync [ passed; passed ] |> Expect.equal "plain AllPassed" RunVerdict.AllPassed

    testCase "WHY — NEVER-GREEN-OVER-STALE: all passed over a stale source is PassedOnStaleSource, never AllPassed" <| fun _ ->
      verdictWith staleA [ passed; passed ] |> Expect.equal "passed, but stale" RunVerdict.PassedOnStaleSource

    testCase "WHY — all passed during a rebuild says so, and is not AllPassed" <| fun _ ->
      verdictWith rebuildingState [ passed; passed ] |> Expect.equal "passed, while rebuilding" RunVerdict.PassedWhileRebuilding

    testCase "WHY — all passed over a source nothing could judge says so, and is not AllPassed" <| fun _ ->
      verdictWith unknownState [ passed; passed ] |> Expect.equal "passed, on an unknown source" RunVerdict.PassedOnUnknownSource

    testCase "WHY — a failure stays SomeFailed and an unfinished run stays Incomplete, whatever the source says" <| fun _ ->
      [ inSync; staleA; rebuildingState; unknownState ]
      |> List.iter (fun source ->
        verdictWith source [ passed; LineOutcome.Failed "boom" ] |> Expect.equal "some failed" RunVerdict.SomeFailed
        verdictWith source [ passed; LineOutcome.DidNotReport "never reported" ] |> Expect.equal "incomplete" RunVerdict.Incomplete)

    testCase "WHY — a receipt read without a source check is never AllPassed: observe starts Unknown, so the verdict says it was not checked" <| fun _ ->
      let requestId = RunRequestId.fresh ()
      let id = TestId.create "a" TestFramework.Expecto
      let requested, generation = RequestedRuns.request (Some requestId) "s1" [ id ] LiveTestState.empty
      let running = RequestedRuns.started "s1" generation requested
      let result = mkResult id (TestResult.Passed FixtureDurations.usualResult)
      let finished =
        { RequestedRuns.stampResults (Some "s1") [ result ] running with
            LastResults = Map.ofList [ id, result ] }
        |> RequestedRuns.completed "s1"
      match TestRunReceipt.observe requestId finished with
      | RunReceipt.Ran ran ->
        ran.Source |> Expect.equal "not assessed" (SourceState.Unknown UnknownReason.NotAssessed)
        ran.Verdict |> Expect.equal "not green" RunVerdict.PassedOnUnknownSource
      | other -> failtestf "expected Ran, got %A" other

    testCase "WHY — the summary of a stale receipt says passed, but on STALE source, names the file and never claims the plain all-passed sentence" <| fun _ ->
      let text = TestRunReceipt.summarize (TestRunReceipt.withSource staleA (receiptOf [ passed; passed ]))
      text |> Expect.stringContains "says STALE" "STALE"
      text |> Expect.stringContains "names the file" "A.fs"
      text |> Expect.stringContains "says they passed" "passed"
      (text.Contains "Every requested test passed in this run.") |> Expect.isFalse "must not use the plain AllPassed sentence"

    testCase "WHY — the summary of an in-sync receipt keeps the plain sentence and says the source was checked" <| fun _ ->
      let text = TestRunReceipt.summarize (TestRunReceipt.withSource inSync (receiptOf [ passed; passed ]))
      text |> Expect.stringContains "plain sentence" "Every requested test passed in this run."
      text |> Expect.stringContains "source line" "Source:"

    testCase "WHY — the receipt's JSON carries the verdict token and the source as data, so an agent branches on a token" <| fun _ ->
      let json = TestRunReceipt.toJson (TestRunReceipt.withSource staleA (receiptOf [ passed; passed ]))
      json["verdict"].GetValue<string>() |> Expect.equal "verdict token" "PassedOnStaleSource"
      let source = json["source"]
      source["state"].GetValue<string>() |> Expect.equal "source token" "Stale"
      source["changedFiles"].AsArray().Count |> Expect.equal "the changed file" 1

    testProperty "WHY — AllPassed is the verdict exactly when something passed, nothing else happened, and the source is in sync" <| fun (n: int, m: int, k: int) ->
      let outcomes =
        [ for _ in 1 .. nat n % 4 -> passed ]
        @ [ for _ in 1 .. nat m % 2 -> LineOutcome.Failed "x" ]
        @ [ for _ in 1 .. nat k % 2 -> LineOutcome.Skipped "y" ]
      let source = [ inSync; staleA; rebuildingState; unknownState ] |> List.item (nat (n ^^^ m ^^^ k) % 4)
      let everyPassed = not (List.isEmpty outcomes) && outcomes |> List.forall (function LineOutcome.Passed _ -> true | _ -> false)
      (verdictWith source outcomes = RunVerdict.AllPassed) = (everyPassed && isInSync source)
  ]
