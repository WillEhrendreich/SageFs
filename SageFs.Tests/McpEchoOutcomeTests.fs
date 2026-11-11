module SageFs.Tests.McpEchoOutcomeTests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.Server.McpTools
open SageFs.McpEcho
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Tests.TestInfrastructure

/// Forty-odd MCP tools answer through `withEcho`, whose body hands back display text. A body that failed wrote
/// "Error: ..." (or a bare `describeForAgent` sentence) and the wrapper sent it as a SUCCESS: the client saw no
/// isError and the friction store recorded the call as clean. These pin the seam that now reads the text once,
/// sets isError, and records the failure.

/// A friction store that keeps every event it is handed, so a test can read the outcome the wrapper recorded.
let private capturingCtx () : McpContext * ConcurrentQueue<FrictionEvent> =
  let events = ConcurrentQueue<FrictionEvent>()
  let store = { tempFrictionStore () with AppendEvent = fun e -> events.Enqueue e; Result.Ok () }
  { sharedCtx () with FrictionStore = Some store }, events

/// Run a wrapper and hand back either its text or the exception it raised.
let private attempt (call: unit -> Task<string>) : Task<Result<string, exn>> =
  task {
    try
      let! text = call ()
      return Result.Ok text
    with ex -> return Result.Error ex
  }

let private isBlocked (e: FrictionEvent) =
  match e.Outcome with
  | FrictionOutcome.EncounteredBlocker _ -> true
  | FrictionOutcome.CompletedCleanly
  | FrictionOutcome.RecoveredVia _
  | FrictionOutcome.AbandonedWithoutResolution -> false

/// One sample of each failure shape the tool bodies really build (grep `"Error` in Mcp.fs and McpTools.fs, and
/// `describeForAgent err` returned bare from Mcp.createSession).
let private failureSamples : (string * string) list =
  [ "an 'Error: ' label (discover_features, manage_local_data, friction reads)",
    "Error: no tool is named 'x'. The tools are: a, b."
    "the 'Error switching workflow: ' sentence (switch_workflow)",
    sprintf "Error switching workflow: %s" (SageFsError.describeForAgent (SageFsError.SessionNotFound "abc"))
    "a bare describeForAgent sentence (create_project_session refusing a path)",
    SageFsError.describeForAgent (SageFsError.UnsafeSessionPath("/nowhere", "the directory does not exist")) ]

[<Tests>]
let tests =
  testSequenced <| testList "withEcho reports a failed tool call as a failure" [

    testList "classifyEcho" [
      for name, text in failureSamples do
        testCase (sprintf "WHY — %s is a failure" name) <| fun _ ->
          match classifyEcho text with
          | EchoResult.Failed _ -> ()
          | EchoResult.Answered _ -> failtestf "%s was read as an answer" text

      testCase "WHY — a describeForAgent sentence keeps its own next step as the suggested action" <| fun _ ->
        let err = SageFsError.UnsafeSessionPath("/nowhere", "the directory does not exist")
        match classifyEcho (SageFsError.describeForAgent err) with
        | EchoResult.Failed failure ->
          SageFsError.suggestedAction failure |> Expect.equal "the original next step" (SageFsError.suggestedAction err)
          SageFsError.describe failure |> Expect.equal "the original what and why" (SageFsError.describe err)
        | EchoResult.Answered text -> failtestf "read as an answer: %s" text

      testCase "WHY — the 'Error: ' label is dropped from the reason, so the client never reads it twice" <| fun _ ->
        match classifyEcho "Error: the session has no running worker." with
        | EchoResult.Failed failure ->
          SageFsError.describe failure |> Expect.equal "the reason alone" "the session has no running worker."
        | EchoResult.Answered text -> failtestf "read as an answer: %s" text

      testCase "WHY — an ordinary reply, even one that quotes an error further down, stays an answer" <| fun _ ->
        let replies =
          [ "Session 'abc' created for /tmp/x."
            "Journal: 2 entries\n[12:00:00] ERR eval Error: boom"
            "Errors: 0"
            """{"case":"Error: inside JSON → Next: never read"}""" ]
        for reply in replies do
          classifyEcho reply |> Expect.equal reply (EchoResult.Answered reply)
    ]

    testList "the wrapper" [
      for name, text in failureSamples do
        testTask (sprintf "WHY — %s reaches the client with isError and the friction store records a failure" name) {
          let ctx, events = capturingCtx ()
          let! (outcome: Result<string, exn>) = attempt (fun () -> withEcho ctx "echo_probe" (Task.FromResult text))
          match outcome with
          | Result.Error (:? SageFsErrorException as ex) ->
            let result = SageFs.Server.McpServer.structuredToolErrorResult ex.Error
            result.IsError |> Expect.equal "the protocol says it failed" (Nullable true)
          | Result.Error other -> failtestf "expected a SageFsErrorException, got %A" other
          | Result.Ok reply -> failtestf "the failure came back as a success: %s" reply
          events |> Seq.toList |> List.exists isBlocked |> Expect.isTrue "the friction store recorded a blocker"
        }

      testTask "WHY — a normal reply stays a success and is recorded as clean (the twin)" {
        let ctx, events = capturingCtx ()
        let! outcome = attempt (fun () -> withEcho ctx "echo_probe" (Task.FromResult "Session 'abc' created."))
        outcome |> Expect.equal "the text comes back" (Result.Ok "Session 'abc' created.")
        events |> Seq.toList |> List.exists isBlocked |> Expect.isFalse "no blocker recorded"
        events |> Seq.isEmpty |> Expect.isFalse "the call was recorded"
      }

      testTask "WHY — withEchoResult keeps a typed failure's own case, never a reading of its text" {
        let ctx, events = capturingCtx ()
        let err = SageFsError.SessionNotFound "abc"
        let! (outcome: Result<string, exn>) = attempt (fun () -> withEchoResult ctx "echo_probe" (Task.FromResult (EchoResult.Failed err)))
        match outcome with
        | Result.Error (:? SageFsErrorException as ex) -> ex.Error |> Expect.equal "the case the body named" err
        | other -> failtestf "expected the typed failure, got %A" other
        events |> Seq.toList |> List.exists isBlocked |> Expect.isTrue "recorded as a blocker"
      }
    ]
  ]
