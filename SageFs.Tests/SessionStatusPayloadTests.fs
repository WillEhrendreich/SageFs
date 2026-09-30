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
    AverageDurationMs = 0L
    Health = box "healthy"
    LastRestart = restart }

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
