/// `hard_reset_fsi_session rebuild=true` answers "initiated" and promises that
/// `get_session_status` reports the outcome. The outcome was recorded, and the
/// status payload never read it: a rebuild that failed left the old worker
/// serving, said nothing anywhere, and read exactly like a rebuild that had not
/// finished yet. These pin that the payload carries it.
module SageFs.Tests.SessionStatusPayloadTests

open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs

let private factsWith (restart: SessionStatusPayload.LastRestart) : SessionStatusPayload.Facts =
  { SessionState = SessionState.Ready
    SessionId = "abc12345"
    Target = [ SessionProjectTarget.Bare ]
    LoadedProjects = []
    ReconciledStatus = WorkerProtocol.SessionLifecycleStatus.Stopped
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    CoreVersion = "0.0.0"
    EvalCount = 0
    AverageDurationMs = FixtureDurations.unmeasuredMs
    Health = SessionHealth.toJson SessionHealth.Healthy
    LastRestart = restart
    LastReload = SessionReload.NoReloadYet }

let private allKinds =
  [ SessionStatusPayload.RestartKind.InProgress
    SessionStatusPayload.RestartKind.Succeeded
    SessionStatusPayload.RestartKind.FailedStillServing
    SessionStatusPayload.RestartKind.FailedNotServing ]

[<Tests>]
let lastRestartTests =
  testList "SessionStatusPayload.lastRestart" [

    testCase "WHY — a failed rebuild that left the old worker serving is IN the payload, with its reason, because that state looks identical to a rebuild still running and the caller was told to look here" <| fun _ ->
      let json =
        SessionStatusPayload.serialize
          (factsWith (SessionStatusPayload.LastRestart.Recorded(SessionStatusPayload.RestartKind.FailedStillServing, "Build failed (exit 1): FS0001 in Checkout.fs")))
      use doc = JsonDocument.Parse json
      let restart = doc.RootElement.GetProperty "lastRestart"
      restart.GetProperty("outcome").GetString() |> Expect.equal "the outcome is named" "FailedStillServing"
      restart.GetProperty("message").GetString() |> Expect.stringContains "and carries the reason it failed" "FS0001 in Checkout.fs"

    testCase "WHY — a session nobody has restarted reports no lastRestart, rather than an invented success" <| fun _ ->
      let json = SessionStatusPayload.serialize (factsWith SessionStatusPayload.LastRestart.NoneRecorded)
      use doc = JsonDocument.Parse json
      doc.RootElement.GetProperty("lastRestart").ValueKind
      |> Expect.equal "null, not a made-up outcome" JsonValueKind.Null

    testCase "WHY — every kind of outcome has its own label, because two kinds sharing one would make a failure indistinguishable from a success in the payload" <| fun _ ->
      allKinds
      |> List.map SessionStatusPayload.RestartKind.label
      |> List.distinct
      |> List.length
      |> Expect.equal "four kinds, four labels" (List.length allKinds)
  ]

[<Tests>]
let lastReloadTests =
  testList "SessionStatusPayload.lastReload" [

    testCase "WHY — a save that could not be applied is IN the payload, with the worker's wording and the remedy, because a console app has no browser tab to tell it" <| fun _ ->
      let reload =
        SessionReload.Finished
          { Case = ReloadCase.RestartRequired; Patched = 0; Considered = 3; Message = "3 changed definitions cannot be patched"; SuggestedAction = "restart the app"; Mechanism = SageFs.Features.ReloadOutcome.PatchMechanism.NoPatch }
      let json = SessionStatusPayload.serialize { factsWith SessionStatusPayload.LastRestart.NoneRecorded with LastReload = reload }
      use doc = JsonDocument.Parse json
      let payload = doc.RootElement.GetProperty "lastReload"
      payload.GetProperty("state").GetString() |> Expect.equal "finished" "finished"
      payload.GetProperty("outcome").GetString() |> Expect.equal "the outcome token" "RestartRequired"
      payload.GetProperty("patched").GetInt32() |> Expect.equal "none patched" 0
      payload.GetProperty("considered").GetInt32() |> Expect.equal "of three" 3
      payload.GetProperty("suggestedAction").GetString() |> Expect.equal "the remedy" "restart the app"

    testCase "WHY — a save still compiling says so, with the file" <| fun _ ->
      let json = SessionStatusPayload.serialize { factsWith SessionStatusPayload.LastRestart.NoneRecorded with LastReload = SessionReload.Compiling (Some "/src/Ticker.fs") }
      use doc = JsonDocument.Parse json
      let payload = doc.RootElement.GetProperty "lastReload"
      payload.GetProperty("state").GetString() |> Expect.equal "compiling" "compiling"
      payload.GetProperty("file").GetString() |> Expect.equal "which file" "/src/Ticker.fs"

    testCase "WHY — a session nobody has saved to reports no lastReload, rather than an invented success" <| fun _ ->
      let json = SessionStatusPayload.serialize (factsWith SessionStatusPayload.LastRestart.NoneRecorded)
      use doc = JsonDocument.Parse json
      doc.RootElement.GetProperty("lastReload").ValueKind
      |> Expect.equal "null, not a made-up outcome" JsonValueKind.Null
  ]

[<Tests>]
let targetWireTests =
  testList "SessionStatusPayload target" [

    testCase "WHY — every target is a plain {kind, path} object, never a raw F# union, because System.Text.Json on .NET 10 refuses to serialize one and get_session_status is the first tool everyone calls" <| fun _ ->
      let facts =
        { factsWith SessionStatusPayload.LastRestart.NoneRecorded with
            Target = [ SessionProjectTarget.Project "/src/App/App.fsproj"; SessionProjectTarget.Solution "/src/All.slnx"; SessionProjectTarget.Bare ] }
      let json = SessionStatusPayload.serialize facts
      use doc = JsonDocument.Parse json
      let targets = doc.RootElement.GetProperty("target").EnumerateArray() |> Seq.toList
      targets
      |> List.map (fun t -> t.GetProperty("kind").GetString())
      |> Expect.equal "one kind token per target, in order" [ "Project"; "Solution"; "Bare" ]
      targets
      |> List.map (fun t -> match t.GetProperty("path").ValueKind with JsonValueKind.String -> t.GetProperty("path").GetString() | _ -> "")
      |> Expect.equal "the path for project and solution, none for bare" [ "/src/App/App.fsproj"; "/src/All.slnx"; "" ]
  ]
