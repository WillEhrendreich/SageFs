module SageFs.Tests.SageFsErrorSeamTests

open System
open Expecto
open Expecto.Flip
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.McpRouteError
open SageFs.Features.MessageJournal

/// Where a typed error used to be flattened or misfiled on its way to an agent (roast-2, the errors section).

/// The text Mcp.fs builds when a call reaches a session that is restarting on purpose.
let private restartMessage =
  "Session 's1' is Restarting — transport is temporarily unavailable by design. Call get_session_status with wait_seconds=60 to wait for it; do NOT retry hard_reset_fsi_session or create a new session."

let private allKinds : CohortFailureKind list =
  FSharpType.GetUnionCases(typeof<CohortFailureKind>)
  |> Array.map (fun c -> FSharpValue.MakeUnion(c, [||]) :?> CohortFailureKind)
  |> Array.toList

let private entry (at: DateTimeOffset) (level: JournalLevel) (message: string) : JournalEntry =
  { Timestamp = at; Level = level; Source = "eval"; Message = message }

[<Tests>]
let tests =
  testList "SageFsError seams" [

    testList "a restart in progress" [
      testCase "WHY — RestartInProgress is its own case, so its advice is to wait" <| fun _ ->
        routeErrorToSageFsError "s1" (RestartInProgress restartMessage)
        |> Expect.equal "the restart case" (SageFsError.SessionRestarting("s1", restartMessage))

      testCase "WHY — the next step says wait with get_session_status and never says to run a hard reset" <| fun _ ->
        let err = routeErrorToSageFsError "s1" (RestartInProgress restartMessage)
        let next = SageFsError.suggestedAction err
        next |> Expect.stringContains "names the wait" "get_session_status with wait_seconds"
        next.StartsWith("Run hard_reset", StringComparison.Ordinal) |> Expect.isFalse "never recommends the reset"
        (SageFsError.describeForAgent err).Contains("The worker may have crashed") |> Expect.isFalse "never calls the restart a crash"

      testCase "WHY — a restart is a wait-and-retry family (503), never a gateway failure" <| fun _ ->
        let err = SageFsError.SessionRestarting("s1", restartMessage)
        SageFsError.category err |> Expect.equal "overload family" ErrorCategory.Overload
        SageFsError.toHttpStatus err |> Expect.equal "service unavailable" 503

      testCase "WHY — a real transport failure still maps to the worker case and its reset advice (the twin)" <| fun _ ->
        match routeErrorToSageFsError "s1" (TransportFailure "connection refused") with
        | SageFsError.WorkerCommunicationFailed("s1", "connection refused") -> ()
        | other -> failtestf "expected WorkerCommunicationFailed, got %A" other
    ]

    testList "the message journal" [
      testCase "WHY — an entry recorded from history keeps its own time and level, never the clock at read" <| fun _ ->
        let earlier = DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
        let journal =
          Journal.create 8
          |> Journal.recordEntries
               [ entry earlier JournalLevel.Info "let x = 1"
                 entry (earlier.AddMinutes 1.0) JournalLevel.Failure "let y = boom" ]
        Journal.entries journal
        |> List.map (fun e -> e.Timestamp, e.Level)
        |> Expect.equal "newest first, each with its own time and level"
             [ earlier.AddMinutes 1.0, JournalLevel.Failure
               earlier, JournalLevel.Info ]
        (Journal.stats journal).ErrorCount |> Expect.equal "the failure is counted" 1
        Journal.filterByMinLevel JournalLevel.Failure journal
        |> List.map (fun e -> e.Message)
        |> Expect.equal "min_level=error finds it" [ "let y = boom" ]

      testCase "WHY — a failed eval's stored result reads as a failure, and a value as info" <| fun _ ->
        JournalLevel.ofEvalResult "Error: The value or constructor 'boom' is not defined."
        |> Expect.equal "failed eval" JournalLevel.Failure
        JournalLevel.ofEvalResult "val x: int = 1" |> Expect.equal "a value" JournalLevel.Info
    ]

    testList "a hard reset's caught error" [
      testCase "WHY — a typed error raised inside the reset reaches the reply whole" <| fun _ ->
        let built = SageFsError.BuildFailed(1, [ BuildDiagnostic.ofLine "src/A.fs(1,1): error FS0039: nope" ])
        SageFsErrorException.recoverOr SageFsError.HardResetFailed (SageFsErrorException built)
        |> Expect.equal "the build failure with its diagnostics" built

      testCase "WHY — any other exception is a hard reset failure with its message (the twin)" <| fun _ ->
        SageFsErrorException.recoverOr SageFsError.HardResetFailed (InvalidOperationException "disk full")
        |> Expect.equal "the fallback case" (SageFsError.HardResetFailed "disk full")
    ]

    testList "a cohort refusal's kind" [
      testCase "WHY — the kind is in the structured payload, so an agent tells ClaimConflict from NotConductor" <| fun _ ->
        let fieldsOf kind = (SageFsError.toJson (SageFsError.CohortRefused(kind, "r", "s"))).fields
        fieldsOf CohortFailureKind.ClaimConflict |> fun f -> f.["kind"] |> Expect.equal "claim conflict" (box "ClaimConflict")
        fieldsOf CohortFailureKind.NotConductor |> fun f -> f.["kind"] |> Expect.equal "not conductor" (box "NotConductor")

      testCase "WHY — every kind has its own wire name" <| fun _ ->
        allKinds |> List.map CohortFailureKind.label |> List.distinct |> List.length
        |> Expect.equal "one name per kind" allKinds.Length

      testCase "WHY — there is one kind per CohortError constructor" <| fun _ ->
        let errorCases =
          FSharpType.GetUnionCases(typedefof<Cohort.CohortError<string>>.MakeGenericType(typeof<string>))
          |> Array.map (fun c -> c.Name)
          |> Set.ofArray
        allKinds |> List.map CohortFailureKind.label |> Set.ofList
        |> Expect.equal "the same names" errorCases

      testCase "WHY — ofError names the refusal the core gave" <| fun _ ->
        CohortFailureKind.ofError (Cohort.CohortError.NotConductor "m1")
        |> Expect.equal "not conductor" CohortFailureKind.NotConductor
        CohortFailureKind.ofError (Cohort.CohortError<string>.OwnerStepFailed "ledger")
        |> Expect.equal "owner step" CohortFailureKind.OwnerStepFailed
    ]
  ]
