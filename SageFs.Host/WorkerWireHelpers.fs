module SageFs.Server.WorkerWireHelpers

open System
open SageFs
open SageFs.AppState
open SageFs.WorkerProtocol

/// Convert internal Diagnostic to WorkerDiagnostic for transport.
let toWorkerDiagnostic (d: Features.Diagnostics.Diagnostic) : WorkerDiagnostic =
  { Severity = d.Severity
    Message = d.Message
    StartLine = d.Range.StartLine
    StartColumn = d.Range.StartColumn
    EndLine = d.Range.EndLine
    EndColumn = d.Range.EndColumn
    ErrorNumber = d.ErrorNumber }

/// Single-case wrappers: code and name are both strings and passing them in the wrong order would
/// compile cleanly and give a wrong answer. The wrappers make the swap a compile error (SAGE-005).
type CompletionSource = CompletionSource of string
type Candidate = Candidate of string

/// The FCS tooltip closure lives in the process that holds the compiler. Ask for the one named
/// candidate at the caret and send back its text: a CompletionEntry cannot carry a closure over the
/// wire, and computing a tooltip for every candidate in a large set would cost a round trip each.
let describe (actor: AppActor) (CompletionSource code) (cursorPos: int) (Candidate name) : Async<string> =
  async {
    let! answer =
      actor.PostAndAsyncReply(fun rc ->
        Autocomplete(code, cursorPos, name, Features.AutoCompletion.CompletionLimit.EveryMember, rc))
      |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Completion
    return
      match answer with
      | Features.AutoCompletion.CompletionAnswer.Answered(items, _) ->
        items
        |> List.tryFind (fun i -> i.DisplayText = name)
        |> Option.bind (fun i ->
          i.GetDescription
          |> Option.map (fun get -> get () |> Array.map (fun t -> t.Text) |> String.concat ""))
        |> Option.defaultValue ""
      | Features.AutoCompletion.CompletionAnswer.Unavailable _ -> ""
  }
