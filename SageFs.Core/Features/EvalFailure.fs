/// Which kind of failure stopped a save's evaluation.
///
/// Every `Error` an eval returned used to be reported as `CompileFailed`, so the browser, the editors and the MCP
/// agent were all told to "fix the compile error" when SageFs's own eval path had thrown, an eval was interrupted or
/// superseded by a reset, or the FSI host had died. The 0.6.910 release gate hit exactly that: a `StringBuilder`
/// race in the output recorder surfaced as `internal error: ... (Parameter 'chunkLength')` and was shown as a
/// compile failure of a file that compiled.
///
/// `classify` is pure and reads only structure: the exception's type, the structured `SageFsError` it carries, and
/// whether the submission had a BLOCKING diagnostic. It never matches on the words of a message.
module SageFs.Features.EvalFailure

open System
open SageFs
open SageFs.Features.Diagnostics
open SageFs.Features.ReloadOutcome

/// A compile failure needs the compiler to have said so: a blocking diagnostic. Warnings, hints and an absence of
/// diagnostics are not one.
let private hasBlockingDiagnostic (diagnostics: Diagnostic[]) : bool =
  diagnostics |> Array.exists (fun d -> d.Severity = DiagnosticSeverity.Blocking)

/// Name what stopped an evaluation, given the diagnostics the submission produced and the exception it failed with.
///
/// The order matters. SageFs's own fault and the structured session errors are decided by what the exception IS, so
/// diagnostics beside them cannot turn them into a compile failure. Cancellation is next. Only then do the
/// diagnostics decide between a compile failure and code that ran and threw.
let classify (diagnostics: Diagnostic[]) (failure: exn) : EvalFailureCause =
  let notCompile fault = EvalFailureCause.NotACompileError fault
  match failure with
  | :? EvalInternalFaultException as fault ->
    notCompile (EvalFault.InternalFault(fault.ExceptionType, fault.FaultMessage))
  | :? SageFsErrorException as structured ->
    match structured.Error with
    | SageFsError.EvalSupersededByReset -> notCompile EvalFault.RefusedByReset
    | SageFsError.FsiHostCrashed crash -> notCompile (EvalFault.HostCrashed(HostCrash.describe crash))
    | SageFsError.EvalFailed reason -> notCompile (EvalFault.RefusedBySession reason)
    | other -> notCompile (EvalFault.EvalThrew(failure.GetType().Name, SageFsError.describe other))
  | :? OperationCanceledException -> notCompile EvalFault.Interrupted
  | _ when hasBlockingDiagnostic diagnostics -> EvalFailureCause.CompileDiagnostics failure.Message
  | _ -> notCompile (EvalFault.EvalThrew(failure.GetType().Name, failure.Message))
