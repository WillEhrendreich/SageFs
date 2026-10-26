module SageFs.Tests.DependencyGraphRouteTests

/// `GET /api/dependency-graph` answers about the session it is asked about.
///
/// The defect these pin: the route read `model.LiveTesting.DepGraph`, the primary cycle's graph, whoever
/// asked. On a daemon with two sessions, the one running tests was not the primary: its type-check
/// built a graph in its own cycle and the route answered `{"Symbols":[],"TotalSymbols":0}` from the
/// primary's empty one. Switching the primary to the asking session made the same request answer with
/// symbols, so the cycle was filling and the route was reading the wrong one.

open System
open System.Text.Json
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features.LiveTesting

let framework = LiveTestingDefaults.Framework

let mkSession (name: string) (workingDir: string) : SessionSnapshot =
  { Id = WorkerProtocol.SessionId.newId ()
    Name = Some name
    Projects = [ sprintf "%s/MyProj.fsproj" workingDir ]
    Status = SessionDisplayStatus.Running
    LastActivity = DateTime.UtcNow
    EvalCount = 0
    UpSince = DateTime.UtcNow
    WorkingDirectory = workingDir }

let update msg model = SageFsUpdate.update msg model |> fst

let idOf (snap: SessionSnapshot) = WorkerProtocol.SessionId.value snap.Id

/// The test a session's file defines under a `.Tests.` namespace, so the name graph can name it.
let testNameOf (index: int) = sprintf "Mod%d.Tests.adds" index

let symbolOf (index: int) = sprintf "Lib%d.f" index

let testOf (index: int) : TestCase =
  let name = testNameOf index
  { Id = TestId.create name framework
    FullName = name
    DisplayName = name
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = framework
    Category = TestCategory.Unit }

let refsOf (index: int) : SymbolReference list =
  let file = sprintf "/projects/s%d/Tests.fs" index
  [ { SymbolFullName = testNameOf index; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = file; Line = 1 }
    { SymbolFullName = symbolOf index; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = file; Line = 2 } ]

/// How far a session has got: discovered its tests, and had its test file type-checked.
[<RequireQualifiedAccess>]
type Progress =
  | Discovered
  | Analysed

type WorldSpec =
  { Progress: Progress list
    Primary: int }

/// Sessions in distinct directories, each discovered and (for `Analysed`) type-checked, then one
/// switched to as primary.
let buildWorld (spec: WorldSpec) : SageFsModel * SessionSnapshot list =
  let snaps = spec.Progress |> List.mapi (fun i _ -> mkSession (sprintf "s%d" i) (sprintf "/projects/s%d" i))
  let created =
    snaps
    |> List.fold (fun m snap -> update (SageFsMsg.Event (TuiEvent.SessionCreated snap)) m) (SageFsModel.initial ())
  let progressed =
    List.zip snaps spec.Progress
    |> List.indexed
    |> List.fold
      (fun m (i, (snap, progress)) ->
        let sid = idOf snap
        let discovered =
          m
          |> update (SageFsMsg.EnableLiveTestingForSession sid)
          |> update (SageFsMsg.Event (TuiEvent.TestsDiscovered (sid, [| testOf i |])))
        match progress with
        | Progress.Discovered -> discovered
        | Progress.Analysed ->
          update
            (SageFsMsg.FcsTypeCheckCompleted (Some sid, None, FcsTypeCheckResult.Success (sprintf "/projects/s%d/Tests.fs" i, refsOf i)))
            discovered)
      created
  let model = update (SageFsMsg.Event (TuiEvent.SessionSwitched (None, idOf snaps.[spec.Primary]))) progressed
  model, snaps

let symbolsIn (json: string) : Set<string> =
  use doc = JsonDocument.Parse json
  doc.RootElement.GetProperty("Symbols").EnumerateArray()
  |> Seq.map (fun s -> s.GetProperty("Symbol").GetString())
  |> Set.ofSeq

let stateIn (json: string) : string =
  use doc = JsonDocument.Parse json
  doc.RootElement.GetProperty("State").GetString()

let genSpec : Gen<WorldSpec> =
  gen {
    let! count = Gen.choose (1, 4)
    let! progress = Gen.listOfLength count (Gen.elements [ Progress.Discovered; Progress.Analysed ])
    let! primary = Gen.choose (0, count - 1)
    return { Progress = progress; Primary = primary }
  }

let propConfig = { FsCheckConfig.defaultConfig with maxTest = 200 }

[<Tests>]
let tests =
  testList "dependency-graph route answers about the asked session" [

    test "WHY — the reported case: a session that is not primary is answered from its own graph, not the primary's empty one" {
      let model, snaps = buildWorld { Progress = [ Progress.Discovered; Progress.Analysed ]; Primary = 0 }
      model.LiveTesting.DepGraph.SymbolToTests
      |> Expect.isEmpty "fixture: the primary cycle's graph is empty"
      let json = DependencyGraphRoute.overview (idOf snaps.[1]) model
      symbolsIn json
      |> Expect.equal "session 1's own symbol" (Set.singleton (symbolOf 1))
      stateIn json |> Expect.equal "mapped" "mapped"
    }

    test "WHY — REPRODUCED: reading the primary cycle, as the route did, answers the reported case empty" {
      let model, snaps = buildWorld { Progress = [ Progress.Discovered; Progress.Analysed ]; Primary = 0 }
      let primaryReader = SymbolTests.ofCycle model.LiveTesting
      primaryReader.SymbolToTests
      |> Expect.isEmpty "the primary's map has nothing of session 1's"
      (SymbolTests.ofCycle (SageFsModel.cycleForSession (idOf snaps.[1]) model)).SymbolToTests
      |> Map.isEmpty
      |> Expect.isFalse "session 1's own cycle does have its map, so the route was reading the wrong cycle"
    }

    test "WHY — the tests listed for a symbol are the discovered ones, under their discovered names" {
      let model, snaps = buildWorld { Progress = [ Progress.Discovered; Progress.Analysed ]; Primary = 0 }
      use doc = JsonDocument.Parse (DependencyGraphRoute.forSymbol (idOf snaps.[1]) model (symbolOf 1))
      let tests = doc.RootElement.GetProperty("Tests").EnumerateArray() |> Seq.toList
      tests
      |> List.map (fun t -> t.GetProperty("TestName").GetString(), t.GetProperty("TestId").GetString())
      |> Expect.equal "the test discovery gave" [ testNameOf 1, TestId.value (testOf 1).Id ]
    }

    test "WHY — a session that has analysed nothing is answered empty and says nothing has been analysed" {
      let model, snaps = buildWorld { Progress = [ Progress.Analysed; Progress.Discovered ]; Primary = 0 }
      let json = DependencyGraphRoute.overview (idOf snaps.[1]) model
      symbolsIn json |> Expect.isEmpty "no symbols"
      stateIn json |> Expect.equal "named reason" "nothing_analysed"
    }

    test "WHY — the instrumentation a background session reports lands in that session's cycle, where its runs read it" {
      let model, snaps = buildWorld { Progress = [ Progress.Discovered; Progress.Discovered ]; Primary = 0 }
      let background = idOf snaps.[1]
      let maps = [| { InstrumentationMap.empty with TotalProbes = 3 } |]
      let after = update (SageFsMsg.Event (TuiEvent.InstrumentationMapsReady (background, maps))) model
      (SageFsModel.cycleForSession background after).InstrumentationMaps
      |> Map.tryFind background
      |> Expect.equal "the session's own cycle holds its instrumentation" (Some maps)
      after.LiveTesting.InstrumentationMaps
      |> Map.containsKey background
      |> Expect.isFalse "and the primary cycle does not hold another session's"
    }

    testPropertyWithConfig propConfig
      "WHY — over any world, each session's instrumentation lands in its own cycle and in no other"
      (Prop.forAll (Arb.fromGen genSpec) (fun spec ->
        let model, snaps = buildWorld spec
        let maps = [| { InstrumentationMap.empty with TotalProbes = 3 } |]
        let after =
          snaps
          |> List.fold (fun m snap -> update (SageFsMsg.Event (TuiEvent.InstrumentationMapsReady (idOf snap, maps))) m) model
        snaps
        |> List.forall (fun snap ->
          let own = (SageFsModel.cycleForSession (idOf snap) after).InstrumentationMaps
          let foreign = snaps |> List.filter (fun other -> other <> snap) |> List.exists (fun other -> own.ContainsKey (idOf other))
          own.ContainsKey (idOf snap) && not foreign)))

    test "WHY — a session the daemon does not know is answered empty, never from the primary" {
      let model, _ = buildWorld { Progress = [ Progress.Analysed ]; Primary = 0 }
      let json = DependencyGraphRoute.overview "ffffffff" model
      symbolsIn json |> Expect.isEmpty "an unknown session has no symbols of its own"
    }

    testPropertyWithConfig propConfig
      "WHY — over any world, the answer for a session is that session's own graph, never another's and never empty once analysed"
      (Prop.forAll (Arb.fromGen genSpec) (fun spec ->
        let model, snaps = buildWorld spec
        snaps
        |> List.mapi (fun i snap ->
          let symbols = symbolsIn (DependencyGraphRoute.overview (idOf snap) model)
          match spec.Progress.[i] with
          | Progress.Analysed -> symbols = Set.singleton (symbolOf i)
          | Progress.Discovered -> Set.isEmpty symbols)
        |> List.forall id))
  ]
