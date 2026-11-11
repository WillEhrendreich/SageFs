/// Hot paths that run on every save or every keystroke:
///
///   - the remote completion list turned every item's glyph name into a union case by scanning
///     `FSharpType.GetUnionCases` and calling `FSharpValue.MakeUnion`, once per item per keystroke,
///   - `InputHashCoverage.coveredFiles` listed every probe index of the instrumentation map to find the few that a test hit,
///   - the Expecto executor looks reflection handles up per run. That one is pinned for behaviour only: measured on the
///     thread CPU clock, 2000 sync bodies cost 7.6 ms with the lookups and 7.2 ms with direct casts (async: 8.6 and 7.8),
///     and allocate the same bytes, because the runtime caches member lookups per type. The cost is the async wrapper,
///     so there is no budget the lookups could fail and no case for one here.
///
/// Each budget is on the calling thread's CPU time (`PerfBudget.minMs`), so a busy machine cannot fail it. It sits well
/// under the measured cost of the old code and well over the cost of the new, and the numbers are printed so a reader
/// sees both sides. The new code also has to return exactly what the old one returned, which the old one proves: it is
/// kept here as the oracle.
module SageFs.Tests.HotPathPerfTests

open System.Threading
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Microsoft.FSharp.Reflection
open FSharp.Compiler.EditorServices
open SageFs
open SageFs.Features
open SageFs.Features.LiveTesting
open SageFs.Features.LiveTesting.BuiltInExecutors

/// Timed runs; the fastest counts (`PerfBudget.minMs` also warms up first).
let timedRuns = 5

/// Completion list: the size of a list the editor asks for on a keystroke, and the most thread CPU time, in milliseconds,
/// mapping its glyphs may take. The per-item union-case scan cost about 120 microseconds an item, 62 ms for the list.
let completionItemsPerList = 500
let completionListBudget = 5.0

/// Coverage: a map the size of a mid-sized solution's, the share of probes one test hits, and how many tests are
/// hashed in one run (one hash per test), and the most thread CPU time, in milliseconds, they may take together. The list
/// version cost 102 ms; the bit walk costs about 12.
let probesInMap = 20000
let filesInMap = 100
let probeHitEvery = 20
let testsHashedPerRun = 400
let coveredFilesBudget = 25.0

// --- Expecto executor ---

let expectoCache () : ExpectoExecutor.ReflectionCache =
  match ExpectoExecutor.tryBuildCacheFromExpecto typeof<Expecto.Test>.Assembly with
  | Some cache -> cache
  | None -> failtest "the Expecto reflection cache could not be built from the Expecto assembly"

let reflectedOf (cache: ExpectoExecutor.ReflectionCache) (tree: Expecto.Test) : ExpectoExecutor.ReflectedFlatTest =
  ExpectoExecutor.lookupFromBindings cache [ ("Binding", (fun () -> box tree)) ]
  |> Map.toList
  |> List.exactlyOne
  |> snd

/// A result without its duration, which differs between two runs of the same test.
let describe (result: TestResult) : string =
  match result with
  | TestResult.Passed _ -> "Passed"
  | TestResult.Failed (TestFailure.ExceptionThrown (message, _), _) -> sprintf "ExceptionThrown %s" message
  | TestResult.Failed (failure, _) -> sprintf "Failed %A" failure
  | TestResult.Skipped reason -> sprintf "Skipped %s" reason
  | other -> sprintf "%A" other

let runOnce (cache: ExpectoExecutor.ReflectionCache) (token: CancellationToken) (rft: ExpectoExecutor.ReflectedFlatTest) : Async<string> =
  async {
    let! result = ExpectoExecutor.executeReflected cache rft token
    return describe result
  }

let syncTree () : Expecto.Test = testList "subject" [ test "sync" { () } ]
let asyncTree () : Expecto.Test = testList "subject" [ testAsync "async" { () } ]
let cancelTree () : Expecto.Test =
  Expecto.TestLabel(
    "with cancel",
    Expecto.TestCase(Expecto.SyncWithCancel (fun (_: CancellationToken) -> ()), Expecto.Normal),
    Expecto.Normal)
let failingSyncTree () : Expecto.Test = testList "subject" [ test "fails" { failtest "an assertion" } ]
let throwingSyncTree () : Expecto.Test = testList "subject" [ test "throws" { failwith "boom" } ]
let throwingAsyncTree () : Expecto.Test = testList "subject" [ testAsync "throws" { failwith "boom" } ]

[<Tests>]
let executorTests =
  testList "ExpectoExecutor reflected bodies" [

    testAsync "a run twice returns the same result each time, for every kind of test body" {
      let cache = expectoCache ()
      let trees =
        [ "sync", syncTree ()
          "async", asyncTree ()
          "sync with cancel", cancelTree ()
          "a failed assertion", failingSyncTree ()
          "a thrown exception", throwingSyncTree ()
          "an async exception", throwingAsyncTree () ]
      for (name, tree) in trees do
        let rft = reflectedOf cache tree
        let! first = runOnce cache CancellationToken.None rft
        let! second = runOnce cache CancellationToken.None rft
        second |> Expect.equal (sprintf "%s: the second run reads like the first" name) first
    }

    testAsync "the results are the ones Expecto's own exceptions map to" {
      let cache = expectoCache ()
      let run tree = runOnce cache CancellationToken.None (reflectedOf cache tree)
      let! sync = run (syncTree ())
      sync |> Expect.equal "sync passes" "Passed"
      let! asyncOutcome = run (asyncTree ())
      asyncOutcome |> Expect.equal "async passes" "Passed"
      let! withCancel = run (cancelTree ())
      withCancel |> Expect.equal "sync with cancel passes" "Passed"
      let! failed = run (failingSyncTree ())
      failed |> Expect.stringContains "a failed assertion names itself" "AssertionFailed"
      let! thrown = run (throwingSyncTree ())
      thrown |> Expect.stringContains "a thrown exception keeps its message" "boom"
      let! asyncThrown = run (throwingAsyncTree ())
      asyncThrown |> Expect.stringContains "an async exception keeps its message" "boom"
    }

    testAsync "a cancelled token skips a sync body and an async body, whatever the reflection wrapped" {
      let cache = expectoCache ()
      use source = new CancellationTokenSource()
      source.Cancel()
      for tree in [ syncTree (); asyncTree () ] do
        let! outcome = runOnce cache source.Token (reflectedOf cache tree)
        outcome |> Expect.equal "cancelled" "Skipped Cancelled"
    }
  ]

// --- Remote completion kinds ---

/// What the code did before: scan the union cases for the name and build the case, for every item.
let kindOfGlyphOracle (name: string) : AutoCompletion.CompletionKind =
  match FSharpType.GetUnionCases typeof<FSharpGlyph> |> Array.tryFind (fun case -> case.Name = name) with
  | Some case -> AutoCompletion.CompletionKind.ofGlyph (FSharpValue.MakeUnion(case, [||]) :?> FSharpGlyph)
  | None -> AutoCompletion.CompletionKind.Type

let glyphNames : string array =
  FSharpType.GetUnionCases typeof<FSharpGlyph> |> Array.map (fun case -> case.Name)

[<Tests>]
let completionKindTests =
  // `testSequenced`: the budget case is measured, and `PerfBudget.minMs` forces collections, which in the parallel pool
  // stop every other case; a sequenced list runs after the pool, alone.
  testSequenced <| testList "RemoteFsiSession.kindOfGlyph" [

    testCase "every glyph FCS has maps to the kind the existing exhaustive ofGlyph gives it" <| fun _ ->
      for name in glyphNames do
        RemoteFsiSession.kindOfGlyph name
        |> Expect.equal (sprintf "glyph %s" name) (kindOfGlyphOracle name)

    testCase "a glyph this FCS does not know degrades to Type, as before" <| fun _ ->
      for name in [ "NotAGlyph"; ""; "class"; " Class" ] do
        RemoteFsiSession.kindOfGlyph name
        |> Expect.equal (sprintf "unknown %A" name) AutoCompletion.CompletionKind.Type

    testCase "a five hundred item list maps its glyphs inside the CPU budget" <| fun _ ->
      let wire = [| for index in 0 .. completionItemsPerList - 1 -> glyphNames.[index % glyphNames.Length] |]
      let cost =
        PerfBudget.minMs timedRuns (fun () ->
          for name in wire do
            RemoteFsiSession.kindOfGlyph name |> ignore)
      printfn "completion kinds: %d items in %.2f ms on %s (budget %.1f)" completionItemsPerList cost (PerfBudget.describe PerfBudget.clock) completionListBudget
      Expect.isLessThanOrEqual (sprintf "%d items took %.2f ms, over %.1f" completionItemsPerList cost completionListBudget) (cost, completionListBudget)
  ]

// --- coveredFiles on the bitmap ---

/// What the code did before: a list of every probe index, filtered by `isSet`. Kept as the oracle.
let coveredFilesOracle (map: InstrumentationMap) (bitmap: CoverageBitmap) : string list =
  [ 0 .. map.Slots.Length - 1 ]
  |> List.filter (fun i -> CoverageBitmap.isSet i bitmap)
  |> List.map (fun i -> map.Slots.[i].File)
  |> List.distinct
  |> List.sort

let slotIn (file: string) (line: int) : SequencePoint =
  { File = file; Line = line; Column = 0; EndLine = line; EndColumn = 0; BranchId = 0 }

let mapOfFiles (files: string array) : InstrumentationMap =
  { Slots = files |> Array.mapi (fun line file -> slotIn file line)
    TotalProbes = files.Length
    TrackerTypeName = "__SageFsCoverage"
    HitsFieldName = "Hits"
    Source = MapSource.none }

/// Slots in runs of one file, the way the instrumenter emits them.
let bigMap : InstrumentationMap =
  mapOfFiles [| for index in 0 .. probesInMap - 1 -> sprintf "/repo/src/File%03d.fs" (index * filesInMap / probesInMap) |]

let sparseBitmap : CoverageBitmap =
  CoverageBitmap.ofBoolArray [| for index in 0 .. probesInMap - 1 -> index % probeHitEvery = 0 |]

[<Tests>]
let coveredFilesTests =
  testSequenced <| testList "InputHashCoverage.coveredFiles against the list version" [

    testProperty "agrees with the old list version for any map and any bitmap, whatever their lengths" <|
      fun (fileOf: byte array) (hits: bool array) ->
        let map = mapOfFiles (fileOf |> Array.map (fun b -> sprintf "F%d.fs" (int b % 7)))
        let bitmap = CoverageBitmap.ofBoolArray hits
        InputHashCoverage.coveredFiles map bitmap = coveredFilesOracle map bitmap

    testCase "bits set past the bitmap's own count in its last word name no file" <| fun _ ->
      // A bitmap read from the wire can carry set bits in the padding of its last word.
      let padded = { Bits = [| System.UInt64.MaxValue |]; Count = 5 }
      let map = mapOfFiles [| for index in 0 .. 63 -> sprintf "F%02d.fs" index |]
      InputHashCoverage.coveredFiles map padded
      |> Expect.equal "only the five counted probes" [ "F00.fs"; "F01.fs"; "F02.fs"; "F03.fs"; "F04.fs" ]

    testCase "a map shorter than the bitmap reads only the map's slots" <| fun _ ->
      let map = mapOfFiles [| "A.fs"; "B.fs" |]
      let bitmap = CoverageBitmap.ofBoolArray [| true; true; true; true; true |]
      InputHashCoverage.coveredFiles map bitmap |> Expect.equal "A and B" [ "A.fs"; "B.fs" ]

    testCase "hashing four hundred tests' coverage fits the CPU budget" <| fun _ ->
      let cost =
        PerfBudget.minMs timedRuns (fun () ->
          for _ in 1 .. testsHashedPerRun do
            InputHashCoverage.coveredFiles bigMap sparseBitmap |> ignore)
      printfn "coveredFiles: %d calls over %d probes in %.1f ms on %s (budget %.1f)" testsHashedPerRun probesInMap cost (PerfBudget.describe PerfBudget.clock) coveredFilesBudget
      Expect.isLessThanOrEqual (sprintf "%d calls took %.1f ms, over %.1f" testsHashedPerRun cost coveredFilesBudget) (cost, coveredFilesBudget)
  ]
