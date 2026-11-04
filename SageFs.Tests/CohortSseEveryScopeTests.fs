/// The daemon runs one cohort per scope, but `/events` followed only the one it was launched from, and the
/// matrix frame did not say which cohort it was about. An editor in a second repository was told
/// "cohortChanged scope=B" and, on reconnect, shown repository A's matrix.
///
/// Every cohort's matrix is pushed and replayed now, and each frame names its cohort. The registry gained a
/// hook that, unlike `SetOnOwnerStarted`, adds to the others and covers the owners that already exist: that
/// hook is replaced by whoever sets it last, and a subscriber that arrives after an owner was created never
/// hears of it.

module SageFs.Tests.CohortSseEveryScopeTests

open System
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Features
open SageFs.Features.CohortLedger
open SageFs.Server
open SageFs.Server.McpServer

let private scopeOfPath (p: string) = SageFs.Scope.ofWorkingDirectory SageFs.Scope.defaultStrategy p

let private silentLogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

let private registry () : CohortOwners.Owners =
  let deps : CohortOwners.Deps =
    { Ledger = CohortLedger.InMemory.create ()
      Clock = fun () -> DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc)
      Entropy = CohortOwner.productionEntropy
      GetSessionTestOutcomes = fun _ -> ([], [], [], 0L)
      Performer = CohortOwner.LandingPerformer.stub
      Logger = silentLogger }
  CohortOwners.create (CohortOwners.factoryOf deps)

let private repoA = scopeOfPath "/repos/alpha"
let private repoB = scopeOfPath "/repos/beta"

let private join (owner: CohortOwner.Handle) (scope: CohortScope) (name: string) =
  owner.Commit(CohortCommand.Join(MemberId.Minted name, JoinableRole.Implementer, None, scope)).Result
  |> ignore

let private sseContext () : SseContext =
  { GetElmModel = None
    GetWarmupContext = None
    GetHotReloadState = None
    SseJsonOpts = JsonSerializerOptions()
    TestEventBroadcast = Event<SseFrame>()
    SessionEventBroadcast = Event<SseFrame>()
    Log = SseLog.startNew ()
    ServerTracker = McpServerTracker()
    CohortOwner = None }

/// The `CohortScope` a `cohort_matrix` frame names, or none for any other frame.
let private matrixScopeOf (frame: SseFrame) : string option =
  match frame.Wire.StartsWith "event: cohort_matrix" with
  | false -> None
  | true ->
    let data = frame.Wire.Split('\n') |> Array.find (fun l -> l.StartsWith "data: ")
    use doc = JsonDocument.Parse(data.Substring 6)
    match doc.RootElement.TryGetProperty "CohortScope" with
    | true, p -> Some (p.GetString())
    | _ -> Some ""

[<Tests>]
let tests =
  testList "cohort events for every scope" [

    test "OnEachOwner is told of an owner that existed before it was registered" {
      let owners = registry ()
      owners.OwnerFor repoA |> ignore
      let heard = ResizeArray<CohortScope>()
      owners.OnEachOwner(fun scope _ -> heard.Add scope)
      List.ofSeq heard |> Expect.equal "the seeded owner is not missed" [ repoA ]
    }

    test "OnEachOwner is told of owners created after it, once each" {
      let owners = registry ()
      let heard = ResizeArray<CohortScope>()
      owners.OnEachOwner(fun scope _ -> heard.Add scope)
      owners.OwnerFor repoA |> ignore
      owners.OwnerFor repoB |> ignore
      owners.OwnerFor repoA |> ignore
      List.ofSeq heard |> Expect.equal "each scope once, in creation order" [ repoA; repoB ]
    }

    test "WHY — OnEachOwner does not displace the hook SetOnOwnerStarted holds, and the reverse" {
      let owners = registry ()
      let setHeard = ResizeArray<CohortScope>()
      let eachHeard = ResizeArray<CohortScope>()
      owners.SetOnOwnerStarted(fun scope _ -> setHeard.Add scope)
      owners.OnEachOwner(fun scope _ -> eachHeard.Add scope)
      owners.OwnerFor repoA |> ignore
      List.ofSeq setHeard |> Expect.equal "the single hook still fires" [ repoA ]
      List.ofSeq eachHeard |> Expect.equal "and so does the added one" [ repoA ]
    }

    test "the matrix names the cohort it is about" {
      let owners = registry ()
      let owner = owners.OwnerFor repoB
      join owner repoB "bea"
      use doc = JsonDocument.Parse(SageFs.SseWriter.cohortFrameJson (JsonSerializerOptions()) (owner.ReadFrame()))
      doc.RootElement.GetProperty("CohortScope").GetString()
      |> Expect.equal "the label of the frame's own scope" (SageFs.Scope.label repoB)
    }

    testTask "WHY — a cohort other than the launch one pushes its matrix, labelled, to /events" {
      let owners = registry ()
      owners.OwnerFor repoA |> ignore
      let ctx = sseContext ()
      let seen = TaskCompletionSource<string>()
      ctx.SessionEventBroadcast.Publish.Add(fun frame ->
        match matrixScopeOf frame with
        | Some label when label = SageFs.Scope.label repoB -> seen.TrySetResult label |> ignore
        | _ -> ())
      let _subs = wireCohortEventSubscriptions (CohortOwners.Wiring.Wired(owners, repoA)) ctx
      // The second repository's cohort is created AFTER the wiring, as it is in the daemon.
      join (owners.OwnerFor repoB) repoB "bea"
      let! label = seen.Task.WaitAsync TestTimeouts.patienceInProcess
      label |> Expect.equal "repository B's matrix reached the stream" (SageFs.Scope.label repoB)
    }

    testTask "the launch cohort's matrix is pushed too, and a cohort that already existed is not missed" {
      let owners = registry ()
      let seeded = owners.OwnerFor repoA
      let ctx = sseContext ()
      let seen = TaskCompletionSource<string>()
      ctx.SessionEventBroadcast.Publish.Add(fun frame ->
        match matrixScopeOf frame with
        | Some label when label = SageFs.Scope.label repoA -> seen.TrySetResult label |> ignore
        | _ -> ())
      let _subs = wireCohortEventSubscriptions (CohortOwners.Wiring.Wired(owners, repoA)) ctx
      join seeded repoA "ann"
      let! label = seen.Task.WaitAsync TestTimeouts.patienceInProcess
      label |> Expect.equal "the seeded owner has a subscriber" (SageFs.Scope.label repoA)
    }

    testTask "a connecting client is replayed the matrix of every other cohort that exists" {
      let owners = registry ()
      owners.OwnerFor repoA |> ignore
      join (owners.OwnerFor repoB) repoB "bea"
      let ctx = sseContext ()
      let stream = new IO.MemoryStream()
      do! replayOtherCohortMatrices (CohortOwners.Wiring.Wired(owners, repoA)) ctx stream
      let text = Encoding.UTF8.GetString(stream.ToArray())
      text.Contains "event: cohort_matrix" |> Expect.isTrue "a matrix frame was written"
      text.Contains (SageFs.Scope.label repoB) |> Expect.isTrue "it is repository B's"
      text.Contains (SageFs.Scope.label repoA) |> Expect.isFalse "the launch cohort is replayed by replayCohortMatrix, not here"
    }
  ]
