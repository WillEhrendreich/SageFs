/// When a session faulted, every MCP tool (get_fsi_status included) said
/// "Session is faulted. Run reset..." and nothing else. The real reason, e.g.
/// "Not all DLLs are found ...", only showed up in /api/sessions, so an agent
/// had no idea what went wrong. These pin that the reason the daemon recorded
/// travels with the faulted resolution into the text agents read.
module SageFs.Tests.FaultReasonSurfacingTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.McpTools
open SageFs.McpSessionRouting
open SageFs.WorkerProtocol

let private at = DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc)

let private sessionWith (status: SessionLifecycleStatus) : SessionInfo =
  { Id = SessionId.validate "abcd1234" |> Result.defaultWith (fun e -> failwithf "bad test session id: %s" e)
    Name = None
    Projects = []
    WorkingDirectory = "/src"
    SolutionRoot = None
    CreatedAt = at
    LastActivity = at
    Status = status
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = AppRun.AppRunState.NotRunning }

let private reasonOf (resolution: SessionResolution) =
  match resolution with
  | SessionResolution.FaultedSession (_, cause) -> cause
  | other -> failtestf "expected a faulted resolution, got %A" other

[<Tests>]
let tests =
  testList "MCP fault reason surfacing" [

    testCase "WHY: a faulted session's recorded reason reaches the text every tool returns" <| fun _ ->
      let reason = "Initial warm-up failed: Not all DLLs are found (3 missing)."
      let resolution = classifySessionAvailability (Some (sessionWith (SessionLifecycleStatus.Faulted (Some reason)))) false
      reasonOf resolution |> Expect.equal "the cause carries the recorded reason" (FaultCause.Recorded reason)
      formatSessionResolution resolution
      |> Expect.stringContains "get_fsi_status and friends say why, not just 'faulted'" reason

    testCase "a fault with no recorded reason says so instead of pretending" <| fun _ ->
      let resolution = classifySessionAvailability (Some (sessionWith (SessionLifecycleStatus.Faulted None))) false
      reasonOf resolution |> Expect.equal "no reason to carry" FaultCause.NoReasonRecorded
      formatSessionResolution resolution
      |> Expect.stringContains "points at the daemon log" "daemon log"

    testCase "a stopped session is a faulted resolution whose cause is Stopped" <| fun _ ->
      classifySessionAvailability (Some (sessionWith SessionLifecycleStatus.Stopped)) false
      |> reasonOf
      |> Expect.equal "stopped is its own cause" FaultCause.Stopped

    testCase "a blank recorded reason counts as no reason" <| fun _ ->
      FaultCause.ofStatus (SessionLifecycleStatus.Faulted (Some "  "))
      |> Expect.equal "whitespace isn't a reason" FaultCause.NoReasonRecorded

    testProperty "PROPERTY: any non-blank recorded reason appears verbatim in the faulted message" <|
      fun (NonWhiteSpaceString reason) ->
        let resolution = classifySessionAvailability (Some (sessionWith (SessionLifecycleStatus.Faulted (Some reason)))) false
        (formatSessionResolution resolution).Contains reason
  ]
