/// `IFsiSession` over an isolated FSI host process (FsiHostClient). Nothing here touches a compiler-service
/// type: the user's code runs in the host, next to nothing of SageFs.
///
/// Capability status (kept honest, and mirrored by which contract tests run for this implementation):
///   implemented: Eval, ReadFlag, BoundValue (display text), LiveValuesJson (typed snapshot over the wire), Dispose
///   also implemented: Completions (candidates from the host, ranked in SageFs; descriptions fetched lazily),
///   Diagnose and TypeCheckWithSymbols (FCS runs in the host and answers with wire types)
///   in-process by nature until the host agent exists: DynamicAssemblies (hot reload / live testing) -> empty
module SageFs.RemoteFsiSession

open System
open System.Reflection
open System.Threading
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Text
open Microsoft.FSharp.Reflection
open SageFs.Features
open SageFs.FsiHost.FsiProtocol
open SageFs.FsiHostClient
open SageFs.FsiSession

let private severityOf (severity: DiagnosticSeverity) : Diagnostics.DiagnosticSeverity =
  match severity with
  | DiagHidden -> Diagnostics.DiagnosticSeverity.Hidden
  | DiagInfo -> Diagnostics.DiagnosticSeverity.Info
  | DiagWarning -> Diagnostics.DiagnosticSeverity.Warning
  | DiagError -> Diagnostics.DiagnosticSeverity.Blocking

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

let private toSymbolReference (symbol: WireSymbolRef) : LiveTesting.SymbolReference =
  { SymbolFullName = symbol.SymbolFullName
    UseKind =
      (match symbol.Use with
       | WireDefinition -> LiveTesting.SymbolUseKind.Definition
       | WireUsage -> LiveTesting.SymbolUseKind.Reference)
    UsedInTestId = None
    FilePath = symbol.FilePath
    Line = symbol.Line }

/// Map FCS's glyph name (sent by the host, taken from ITS FSharpGlyph type) back to SageFs's CompletionKind through
/// the existing exhaustive `ofGlyph`. A glyph this SageFs's FCS does not know (a newer SDK's) degrades to Type.
let private kindOfGlyph (name: string) : AutoCompletion.CompletionKind =
  match FSharpType.GetUnionCases typeof<FSharpGlyph> |> Array.tryFind (fun case -> case.Name = name) with
  | Some case -> AutoCompletion.CompletionKind.ofGlyph (FSharpValue.MakeUnion(case, [||]) :?> FSharpGlyph)
  | None -> AutoCompletion.CompletionKind.Type

let private toCompletionItem (host: FsiHostSession) (completionsId: int64) (index: int) (item: WireCompletion) : AutoCompletion.CompletionItem =
  { DisplayText = item.DisplayText
    ReplacementText = item.ReplacementText
    Kind = kindOfGlyph item.Glyph
    // Fetched on demand, like the in-process closure over FCS's tooltip.
    GetDescription =
      Some(fun () ->
        match Async.RunSynchronously(host.Describe(completionsId, index)) with
        | Answered text when text.Length > 0 -> [| TaggedText.tagText text |]
        | Answered _
        | HostGone _ -> [||]) }

/// A session whose FSI lives in an isolated host process.
[<Sealed; AllowNullLiteral>]
type RemoteFsiSession(host: FsiHostSession, started: HostAgent.AgentStarted) =
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

    member _.Completions(text, caret, word) =
      // F# candidates come from the host, unsorted; SageFs ranks them exactly as it does in-process.
      let fromHost (queryText: string) (queryCaret: int) (_word: string) : AutoCompletion.CompletionItem seq =
        match wait (host.Complete(queryText, queryCaret)) with
        | Answered(completionsId, items) ->
          items |> List.mapi (fun index item -> toCompletionItem host completionsId index item) |> Seq.ofList
        | HostGone _ -> Seq.empty
      AutoCompletion.getCompletionsWith fromHost text caret word

    member _.Diagnose(text) =
      match wait (host.Check text) with
      | Answered diagnostics -> diagnostics |> List.map toDiagnostic |> List.toArray
      | HostGone _ -> [||]

    member _.TypeCheckWithSymbols(filePath, text) =
      match wait (host.CheckWithSymbols(filePath, text)) with
      | Answered(diagnostics, symbols) ->
        { Diagnostics.TypeCheckWithSymbolsResult.Diagnostics = diagnostics |> List.map toDiagnostic |> List.toArray
          SymbolRefs = symbols |> List.map toSymbolReference }
      | HostGone _ ->
        { Diagnostics.TypeCheckWithSymbolsResult.Diagnostics = [||]
          SymbolRefs = [] }

    // Hot reload and live testing act on the user's assemblies, so they run in the host, beside the user's code. A lost
    // host is AgentUnavailable with its reason: never an empty report standing in for "nothing found".
    member _.AgentStarted = HostAgent.AgentAnswered started

    member _.AfterEval(request) =
      match wait (host.AgentAfterEval request) with
      | Answered report -> HostAgent.AgentAnswered report
      | HostGone reason -> HostAgent.AgentUnavailable reason

    member _.TakeCoverage() =
      match wait (host.AgentTakeCoverage()) with
      | Answered coverage -> HostAgent.AgentAnswered coverage
      | HostGone reason -> HostAgent.AgentUnavailable reason

    member _.LoadedAssemblyNames() =
      match wait (host.AgentLoadedAssemblies()) with
      | Answered names -> HostAgent.AgentAnswered names
      | HostGone reason -> HostAgent.AgentUnavailable reason

    member _.DiscoverLoaded() =
      match wait (host.AgentDiscoverLoaded()) with
      | Answered discovery -> HostAgent.AgentAnswered discovery
      | HostGone reason -> HostAgent.AgentUnavailable reason

    member _.RunTest(test) =
      async {
        match! host.AgentRunTest test with
        | Answered result -> return HostAgent.AgentAnswered result
        | HostGone reason -> return HostAgent.AgentUnavailable reason
      }

    member _.Dispose() = (host :> IDisposable).Dispose()

/// Why a session could not be attached to a running host.
type AttachError = AgentDidNotStart of reason: string

let describeAttachError (error: AttachError) : string =
  match error with
  | AgentDidNotStart reason -> sprintf "the isolated FSI host is running but its hot reload / live testing agent did not start: %s" reason

/// Attach a session to a running host. The agent is started FIRST, so a session never exists without one; if it cannot
/// start, the host is disposed rather than left running.
let attach (host: FsiHostSession) (init: HostAgent.AgentInit) : Async<Result<RemoteFsiSession, AttachError>> =
  async {
    match! host.AgentStart init with
    | Answered started -> return Result.Ok(new RemoteFsiSession(host, started))
    | HostGone reason ->
      (host :> IDisposable).Dispose()
      return Result.Error(AgentDidNotStart reason)
  }
