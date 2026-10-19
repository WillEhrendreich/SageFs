module SageFs.Tests.EvalFailureTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.Diagnostics
open SageFs.Features.EvalFailure
open SageFs.Features.ReloadOutcome

/// A save whose evaluation failed is reported to the browser, the editors and the MCP agent. `CompileFailed`
/// told every one of them "fix the compile error" even when the failure was SageFs's own (a thread race in
/// the output recorder, an interrupted or superseded eval). These tests pin what each kind of failure is
/// called, and that only a real compiler diagnostic is called a compile failure.

let private at line : Range = { StartLine = line; StartColumn = 0; EndLine = line; EndColumn = 1 }

let private diagnostic severity : Diagnostic =
  { Message = "The value or constructor 'x' is not defined."
    Subcategory = "typecheck"
    Range = at 1
    Severity = severity
    ErrorNumber = 39 }

/// FSI's own words when a submission has an error: the detail is in the diagnostics, not the message.
let private fsiRefusal = Exception "Operation could not be completed due to earlier error"

let private crash : HostCrash = { Exit = ExitedWith 134; Output = "Unhandled exception." }

let private notCompile (fault: EvalFault) = EvalFailureCause.NotACompileError fault

[<Tests>]
let evalFailureTests =
  testList "EvalFailure.classify" [

    testCase "a blocking compiler diagnostic is a compile failure" <| fun _ ->
      classify [| diagnostic DiagnosticSeverity.Blocking |] fsiRefusal
      |> Expect.equal "the diagnostic is what failed" (EvalFailureCause.CompileDiagnostics fsiRefusal.Message)

    testCase "warnings alone never make a compile failure" <| fun _ ->
      match classify [| diagnostic DiagnosticSeverity.Warning |] (InvalidOperationException "boom") with
      | EvalFailureCause.CompileDiagnostics _ -> failtest "a warning is not a compile failure"
      | EvalFailureCause.NotACompileError _ -> ()

    testCase "WHY — SageFs's own fault in the eval path is not a compile failure" <| fun _ ->
      // The shape of the 0.6.910 gate failure: a StringBuilder race surfaced as "internal error" with no diagnostics.
      let fault = EvalInternalFaultException("ArgumentOutOfRangeException", "Index was out of range (Parameter 'chunkLength')")
      classify [||] fault
      |> Expect.equal
           "the exception type and message survive, and the cause is SageFs's"
           (notCompile (EvalFault.InternalFault("ArgumentOutOfRangeException", "Index was out of range (Parameter 'chunkLength')")))

    testCase "an internal fault stays an internal fault even when the submission also had diagnostics" <| fun _ ->
      let fault = EvalInternalFaultException("NullReferenceException", "boom")
      classify [| diagnostic DiagnosticSeverity.Blocking |] fault
      |> Expect.equal "SageFs's fault outranks the diagnostics beside it" (notCompile (EvalFault.InternalFault("NullReferenceException", "boom")))

    testCase "an interrupted evaluation is Interrupted" <| fun _ ->
      classify [||] (OperationCanceledException "the evaluation was interrupted")
      |> Expect.equal "cancellation is its own cause" (notCompile EvalFault.Interrupted)

    testCase "a reset that replaced the session is RefusedByReset" <| fun _ ->
      classify [||] (SageFsErrorException SageFsError.EvalSupersededByReset)
      |> Expect.equal "the structured case decides, not the words" (notCompile EvalFault.RefusedByReset)

    testCase "an eval the session would not take is RefusedBySession, with why" <| fun _ ->
      classify [||] (SageFsErrorException(SageFsError.EvalFailed "Session is resetting"))
      |> Expect.equal "the refusal reason is carried" (notCompile (EvalFault.RefusedBySession "Session is resetting"))

    testCase "a host that crashed is HostCrashed" <| fun _ ->
      classify [||] (SageFsErrorException(SageFsError.FsiHostCrashed crash))
      |> Expect.equal "the crash is described" (notCompile (EvalFault.HostCrashed(HostCrash.describe crash)))

    testCase "code that threw with no compiler diagnostic is EvalThrew" <| fun _ ->
      classify [||] (InvalidOperationException "boom")
      |> Expect.equal "the type and message are carried" (notCompile (EvalFault.EvalThrew("InvalidOperationException", "boom")))
  ]

/// One of every fault, so the wording tests walk the whole closed set.
let private everyFault : EvalFault list =
  [ EvalFault.InternalFault("ArgumentOutOfRangeException", "chunkLength")
    EvalFault.EvalThrew("InvalidOperationException", "boom")
    EvalFault.Interrupted
    EvalFault.RefusedByReset
    EvalFault.RefusedBySession "Session is resetting"
    EvalFault.HostCrashed "the host exited with code 134" ]

[<Tests>]
let evalFailureWordingTests =
  testList "ReloadOutcome.EvalFailed" [

    testCase "WHY — a fault that is not a compile error never tells the user to fix a compile error" <| fun _ ->
      for fault in everyFault do
        let words = ReloadOutcome.describeForUser (ReloadOutcome.EvalFailed fault)
        words.Contains("compile error", StringComparison.OrdinalIgnoreCase)
        |> Expect.isFalse (sprintf "%A must not claim a compile error: %s" fault words)

    testCase "a compile diagnostic still says to fix the compile error, under the CompileFailed token" <| fun _ ->
      let outcome = ReloadOutcome.ofEvalFailure (EvalFailureCause.CompileDiagnostics "x is not defined")
      outcome |> Expect.equal "the existing outcome, unchanged" (ReloadOutcome.CompileFailed "x is not defined")
      (Features.ReloadBroadcast.reportOf outcome).Outcome |> Expect.equal "the wire word is unchanged" "CompileFailed"

    testCase "every fault reports the EvalFailed wire word and names itself as its one reason" <| fun _ ->
      for fault in everyFault do
        let report = Features.ReloadBroadcast.reportOf (ReloadOutcome.ofEvalFailure (notCompile fault))
        report.Outcome |> Expect.equal (sprintf "%A outcome" fault) "EvalFailed"
        report.Reasons
        |> List.map (fun r -> r.Case)
        |> Expect.equal (sprintf "%A reasons" fault) [ EvalFault.caseName fault ]

    testCase "a failed evaluation changes nothing in the running process and never refreshes the page" <| fun _ ->
      for fault in everyFault do
        let outcome = ReloadOutcome.EvalFailed fault
        ReloadOutcome.processChanged outcome |> Expect.isFalse (sprintf "%A processChanged" fault)
        ReloadOutcome.shouldRefreshBrowser outcome |> Expect.isFalse (sprintf "%A refresh" fault)

    testCase "every fault has a distinct case name" <| fun _ ->
      everyFault
      |> List.map EvalFault.caseName
      |> List.distinct
      |> List.length
      |> Expect.equal "no two faults share a token" (List.length everyFault)

    testCase "every fault says what to do, in words that are not the compile-error remedy" <| fun _ ->
      for fault in everyFault do
        ReloadOutcome.remedy (ReloadOutcome.EvalFailed fault)
        |> Expect.isSome (sprintf "%A has a remedy" fault)

    testCase "the EvalFailed token round-trips through the client parser" <| fun _ ->
      ReloadCase.ofToken (ReloadCase.token ReloadCase.EvalFailed)
      |> Expect.equal "known to every reader of the wire" (Result.Ok ReloadCase.EvalFailed)
  ]
