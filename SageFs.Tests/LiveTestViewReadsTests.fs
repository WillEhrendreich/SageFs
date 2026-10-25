module SageFs.Tests.LiveTestViewReadsTests

/// The reads the dashboard and the daemon health make of live testing answer about the right
/// thing. The defect these pin: the status bar, the failure narratives and the health summary
/// read the PRIMARY session's cycle, so a session with live testing on while the primary had it
/// off was reported as off, with no failures and no tests, while it was running them.

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features
open SageFs.Features.LiveTesting
open SageFs.LiveTestViewReads

let private propConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }

let private update msg model = SageFsUpdate.update msg model |> fst

let private mkSession (index: int) : SessionSnapshot =
  { Id = WorkerProtocol.SessionId.newId ()
    Name = Some (sprintf "s%d" index)
    Projects = [ sprintf "/projects/session-%d/MyProj.fsproj" index ]
    Status = SessionDisplayStatus.Running
    LastActivity = DateTime.UtcNow
    EvalCount = 0
    UpSince = DateTime.UtcNow
    WorkingDirectory = sprintf "/projects/session-%d" index }

let private idOf (snap: SessionSnapshot) = WorkerProtocol.SessionId.value snap.Id

let private mkTest (session: int) (n: int) : TestCase =
  let name = sprintf "Session%d.Tests.test%d" session n
  { TestCase.Id = TestId.create name TestFramework.Expecto
    FullName = name
    DisplayName = name
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

/// One session of a world: whether it has live testing on, and how many tests it discovered.
type SessionSpec =
  { Activation: LiveTestingActivation
    Tests: int }

type WorldSpec =
  { Sessions: SessionSpec list
    Primary: int }

/// Sessions created, the primary switched to, each session's tests discovered, and live testing
/// switched on through the per-session message the daemon sends.
let private buildWorld (spec: WorldSpec) : SageFsModel * SessionSnapshot list =
  let snaps = spec.Sessions |> List.mapi (fun i _ -> mkSession i)
  let created =
    snaps
    |> List.fold (fun m snap -> update (SageFsMsg.Event (TuiEvent.SessionCreated snap)) m) (SageFsModel.initial ())
  let switched =
    update (SageFsMsg.Event (TuiEvent.SessionSwitched (None, idOf snaps.[spec.Primary]))) created
  let discovered =
    List.zip snaps spec.Sessions
    |> List.mapi (fun i (snap, session) -> i, snap, session)
    |> List.fold
      (fun m (i, snap, session) ->
        let tests = [| for n in 1 .. session.Tests -> mkTest i n |]
        update (SageFsMsg.Event (TuiEvent.TestsDiscovered (idOf snap, tests))) m)
      switched
  let model =
    List.zip snaps spec.Sessions
    |> List.fold
      (fun m (snap, session) ->
        match session.Activation with
        | LiveTestingActivation.Active -> update (SageFsMsg.EnableLiveTestingForSession (idOf snap)) m
        | LiveTestingActivation.Inactive -> m)
      discovered
  model, snaps

let private on tests = { Activation = LiveTestingActivation.Active; Tests = tests }
let private off tests = { Activation = LiveTestingActivation.Inactive; Tests = tests }

let private totalsOf (reading: DaemonLiveTesting) : int =
  match reading with
  | DaemonLiveTesting.Totals summary -> summary.TotalTests
  | DaemonLiveTesting.NoSessionHasItOn
  | DaemonLiveTesting.OnWithNoTestsYet -> -1

// ── the daemon-wide summary ─────────────────────────────────────────

[<Tests>]
let daemonSummary =
  testList "live testing view reads: the daemon-wide summary" [

    testCase "WHY — a session with live testing on is counted while the primary session has it off" <| fun _ ->
      let model, _ = buildWorld { Sessions = [ off 2; on 3 ]; Primary = 0 }
      model.LiveTesting.TestState.Activation
      |> Expect.equal "fixture: the primary session has live testing off" LiveTestingActivation.Inactive
      daemonLiveTesting model
      |> totalsOf
      |> Expect.equal "the second session's three tests are counted" 3

    testCase "WHY — the tests of every session that has it on are added together" <| fun _ ->
      let model, _ = buildWorld { Sessions = [ on 2; on 3; off 4 ]; Primary = 1 }
      daemonLiveTesting model
      |> totalsOf
      |> Expect.equal "2 + 3, and the session with it off adds nothing" 5

    testCase "WHY — no session with live testing on reads as none, whatever tests were discovered" <| fun _ ->
      let model, _ = buildWorld { Sessions = [ off 2; off 3 ]; Primary = 0 }
      daemonLiveTesting model
      |> Expect.equal "off everywhere" DaemonLiveTesting.NoSessionHasItOn

    testCase "WHY — live testing on with nothing discovered yet is its own answer, not off" <| fun _ ->
      let model, _ = buildWorld { Sessions = [ off 0; on 0 ]; Primary = 0 }
      daemonLiveTesting model
      |> Expect.equal "on, no tests yet" DaemonLiveTesting.OnWithNoTestsYet

    testPropertyWithConfig propConfig
      "WHY — the summary is the sum over the sessions that have live testing on, whoever is primary"
      <| fun () ->
        let genSession =
          gen {
            let! active = Gen.elements [ LiveTestingActivation.Active; LiveTestingActivation.Inactive ]
            let! tests = Gen.choose (0, 4)
            return { Activation = active; Tests = tests }
          }
        let genWorld =
          gen {
            let! count = Gen.choose (1, 4)
            let! sessions = Gen.listOfLength count genSession
            let! primary = Gen.choose (0, count - 1)
            return { Sessions = sessions; Primary = primary }
          }
        Prop.forAll (Arb.fromGen genWorld) (fun spec ->
          let model, _ = buildWorld spec
          let ons = spec.Sessions |> List.filter (fun s -> s.Activation = LiveTestingActivation.Active)
          let expected =
            match ons with
            | [] -> DaemonLiveTesting.NoSessionHasItOn
            | _ ->
              match ons |> List.sumBy (fun s -> s.Tests) with
              | 0 -> DaemonLiveTesting.OnWithNoTestsYet
              | total -> DaemonLiveTesting.Totals { TotalTests = total; Passed = 0; Failed = 0; Running = 0 }
          // Discovered tests have not run, so nothing is passed, failed or running yet.
          daemonLiveTesting model = expected)
  ]

// ── the viewed session ──────────────────────────────────────────────

/// A world whose second session has one failure narrative and live testing on, while the
/// primary session (the first) has live testing off and no narrative.
let private withNarrativeOnBackground () : SageFsModel * SessionSnapshot * SessionSnapshot * string =
  let model, snaps = buildWorld { Sessions = [ off 0; on 1 ]; Primary = 0 }
  let background = snaps.[1]
  let test = mkTest 1 1
  let cycle = SageFsModel.cycleForSession (idOf background) model
  let withNarrative =
    { cycle with
        TestState =
          { cycle.TestState with
              Cached =
                { cycle.TestState.Cached with
                    FailureNarratives = Map.ofList [ test.Id, FailureNarrative.empty ] } } }
  let model' = { model with PerSessionLiveTesting = model.PerSessionLiveTesting |> Map.add (idOf background) withNarrative }
  model', snaps.[0], background, test.DisplayName

[<Tests>]
let viewedSession =
  testList "live testing view reads: the viewed session" [

    testCase "WHY — the failure narratives are the viewed session's own while the primary session has live testing off" <| fun _ ->
      let model, _, background, displayName = withNarrativeOnBackground ()
      failureNarrativesFor background.Id model
      |> List.map fst
      |> Expect.equal "the background session's failing test is listed" [ displayName ]

    testCase "WHY — the primary session's view does not show the background session's failures" <| fun _ ->
      let model, primary, _, _ = withNarrativeOnBackground ()
      failureNarrativesFor primary.Id model
      |> Expect.isEmpty "the primary session has none of its own"

    testCase "WHY — a viewed session with live testing off has no narratives even if some are cached" <| fun _ ->
      let model, _, background, _ = withNarrativeOnBackground ()
      let cycle = SageFsModel.cycleForSession (idOf background) model
      let switchedOff =
        { cycle with TestState = { cycle.TestState with Activation = LiveTestingActivation.Inactive } }
      let model' = { model with PerSessionLiveTesting = model.PerSessionLiveTesting |> Map.add (idOf background) switchedOff }
      failureNarrativesFor background.Id model'
      |> Expect.isEmpty "off is empty"

    testCase "WHY — the status bar is the viewed session's, not the primary's" <| fun _ ->
      let model, snaps = buildWorld { Sessions = [ off 0; on 2 ]; Primary = 0 }
      statusBarFor snaps.[1].Id model
      |> Expect.equal "the background session's two tests are shown" "Tests: 0/2 ●2"
      statusBarFor snaps.[0].Id model
      |> Expect.equal "the primary session has live testing off and nothing to show" ""
  ]
