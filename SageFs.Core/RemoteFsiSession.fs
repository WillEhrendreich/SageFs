/// `IFsiSession` over an isolated FSI host process (FsiHostClient). Nothing here touches a compiler-service
/// type: the user's code runs in the host, next to nothing of SageFs.
///
/// Capability status (kept honest, and mirrored by which contract tests run for this implementation):
///   implemented: Eval, ReadFlag, BoundValue (display text), LiveValuesJson (typed snapshot over the wire), Dispose
///   not yet in the host (return neutral empties): Completions, Diagnose, TypeCheckWithSymbols
///   in-process by nature until the host agent exists: DynamicAssemblies (hot reload / live testing) -> empty
module SageFs.RemoteFsiSession

open System
open System.Reflection
open System.Threading
open SageFs.Features
open SageFs.FsiHost.FsiProtocol
open SageFs.FsiHostClient
open SageFs.FsiSession

let private severityOf (severity: DiagnosticSeverity) : Diagnostics.DiagnosticSeverity =
  match severity with
  | DiagHidden -> Diagnostics.DiagnosticSeverity.Hidden
  | DiagInfo -> Diagnostics.DiagnosticSeverity.Info
  | DiagWarning -> Diagnostics.DiagnosticSeverity.Warning
  | DiagError -> Diagnostics.DiagnosticSeverity.Error

let private toDiagnostic (d: FsiDiagnostic) : Diagnostics.Diagnostic =
  { Message = d.Message
    Subcategory = d.Subcategory
    ErrorNumber = d.ErrorNumber
    Severity = severityOf d.Severity
    Range =
      { StartLine = d.StartLine
        StartColumn = d.StartColumn
        EndLine = d.EndLine
        EndColumn = d.EndColumn } }

/// A session whose FSI lives in an isolated host process.
[<Sealed; AllowNullLiteral>]
type RemoteFsiSession(host: FsiHostSession) =
  // The port is synchronous (in-process evals are), and its callers run on dedicated eval/actor threads, so the
  // remote calls block those threads rather than the thread pool at large. This is the one sync-over-async seam.
  let wait (call: Async<'a>) : 'a = Async.RunSynchronously call

  member _.Host = host

  interface IFsiSession with
    member _.Eval(code, cancellationToken) =
      match wait (host.Eval(code, cancellationToken)) with
      | Completed(outcome, diagnostics) ->
        { Outcome =
            match outcome with
            | EvalSucceeded -> FsiSucceeded
            | EvalFailed message -> FsiFailed(Exception message)
            | EvalInterrupted -> FsiInterrupted
          Diagnostics = diagnostics |> List.map toDiagnostic |> List.toArray }
      | HostLost reason ->
        { Outcome = FsiFailed(InvalidOperationException reason)
          Diagnostics = [||] }

    member _.ReadFlag(name) =
      match wait (host.ReadFlag name) with
      | Answered FlagWasUnbound -> FlagUnbound
      | Answered(FlagWasBool value) -> FlagBound value
      | Answered(FlagWasNotBool typeName) -> FlagNotBool typeName
      // A lost host has no gates set: report unbound, the same as a session that never ran base.fsx.
      | HostGone _ -> FlagUnbound

    member _.BoundValue(name) =
      match wait (host.ReadValue name) with
      | Answered(ValueText(_, text)) -> box text
      | Answered ValueUnbound
      | HostGone _ -> null

    member _.LiveValuesJson(generation) =
      let next = Interlocked.Increment(&generation.contents)
      match wait (host.ReadLiveValues next) with
      | Answered snapshot -> WorkerProtocol.Serialization.serialize snapshot
      // A lost host has no values: an empty snapshot, exactly what a session with no bindings reports.
      | HostGone _ -> WorkerProtocol.Serialization.serialize (LiveValueTree.buildSnapshot "" next [])

    // ---- not in the isolated host yet: neutral results, each a pending case in the contract tests ----
    member _.Completions(_text, _caret, _word) = []

    member _.Diagnose(_text) = [||]

    member _.TypeCheckWithSymbols(_filePath, _text) =
      { Diagnostics.TypeCheckWithSymbolsResult.Diagnostics = [||]
        SymbolRefs = [] }

    // Hot reload and live testing reflect over these IN the user's process: they need the host agent.
    member _.DynamicAssemblies: Assembly[] = [||]

    member _.Dispose() = (host :> IDisposable).Dispose()
