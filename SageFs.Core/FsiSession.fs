/// The seam between SageFs's session logic and the FSI session it drives.
///
/// `IFsiSession` names exactly what the worker needs from a session, in FCS-free terms, so the session can be
/// either in this process (`InProcessFsiSession`, wrapping an FsiEvaluationSession) or in an isolated host
/// process (the remote implementation over FsiProtocol). Nothing above this seam touches a compiler-service type.
module SageFs.FsiSession

open System
open System.Reflection
open System.Threading
open FSharp.Compiler.Interactive.Shell
open SageFs.Features
open SageFs.HostAgent
open SageFs.Utils

/// A boolean feature gate bound in the session (`_SageFsHotReload`, `_SageFsCompExpr`).
type FlagValue =
  | FlagUnbound
  | FlagBound of value: bool
  | FlagNotBool of typeName: string

/// How a submission ended.
type FsiEvalOutcome =
  | FsiSucceeded
  | FsiFailed of exn
  | FsiInterrupted

type FsiEval =
  { Outcome: FsiEvalOutcome
    Diagnostics: Diagnostics.Diagnostic array }

/// Whether the process the session's FSI lives in can end on its own, and how to learn that it did.
type HostLifetime =
  /// FSI is in this process, so it lives and dies with the worker; there is nothing separate to watch.
  | SharesTheWorkerProcess
  /// FSI is in a host process of its own. `ended` completes once, with how that process's life ended.
  | SeparateHost of ended: System.Threading.Tasks.Task<HostEnd>

[<AllowNullLiteral>]
type IFsiSession =
  inherit IDisposable
  /// Where the session's FSI lives, and the way to watch it die.
  abstract HostLifetime: HostLifetime
  /// Evaluate a submission without throwing. Cancelling the token interrupts the eval.
  abstract Eval: code: string * cancellationToken: CancellationToken -> FsiEval
  /// Read a boolean feature gate.
  abstract ReadFlag: name: string -> FlagValue
  /// The session's bound values as a serialized LiveValueSnapshot (increments the generation).
  abstract LiveValuesJson: generation: int64 ref -> string
  abstract Completions: text: string * caret: int * word: string -> AutoCompletion.CompletionItem list
  abstract Diagnose: text: string -> Diagnostics.Diagnostic array
  abstract TypeCheckWithSymbols: filePath: string * text: string -> Diagnostics.TypeCheckWithSymbolsResult
  /// The current value of a bound name, or null when it is not bound.
  abstract BoundValue: name: string -> obj | null
  /// What starting the session's agent found (which project assemblies could not be loaded, and why).
  abstract AgentStarted: AgentReply<AgentStarted>
  /// The agent's work after an eval: redefined methods (detoured when asked) and the tests found. The agent runs where
  /// the user's code runs, so this is an in-process call or a message to the isolated host.
  abstract AfterEval: AfterEval -> AgentReply<AfterEvalReport>
  /// The coverage the instrumented assemblies recorded since the last take (and reset it).
  abstract TakeCoverage: unit -> AgentReply<CoverageReading>
  /// The simple names of the assemblies the session's process has loaded.
  abstract LoadedAssemblyNames: unit -> AgentReply<string list>
  /// Scan what the session's process has loaded for tests.
  abstract DiscoverLoaded: unit -> AgentReply<Discovery>
  /// Where each named module value's reads went, and which readers ran (hot reload rule 2).
  abstract ValueReads: values: string list -> AgentReply<SageFs.Middleware.ValueReads.ValueEvidence list>
  /// Where rule 2's reflection reads stand in the session's process.
  abstract ReflectionReads: unit -> AgentReply<SageFs.Middleware.ValueReads.ReflectionReadsReport>
  /// Switch the reflection read mode of the running app.
  abstract SetReflectionMode: mode: SageFs.Middleware.ValueReads.ReflectionReadMode -> AgentReply<SageFs.Middleware.ValueReads.ReflectionReadsReport>
  /// Wait until every probe has been sighted (the patched function's new body ran, or a newer save replaced it) or the
  /// bound passes, then say what is known. Where the user's code runs, like every other agent call.
  abstract AwaitEntries: probes: int64 list * bound: TimeSpan -> Async<AgentReply<SageFs.Middleware.EntryProbes.EntryReading>>
  /// Run one discovered test where it lives.
  abstract RunTest: test: LiveTesting.TestCase -> Async<AgentReply<LiveTesting.TestResult>>
  /// Hold one test where it lives, for a debugger: the process to attach to and the ticket that releases the test.
  abstract DebugBegin: test: LiveTesting.TestCase -> Async<AgentReply<TestDebug.DebugBegin>>
  /// Release a held test (the editor's debugger is attached) and wait up to `park` for it to finish.
  abstract DebugContinue: ticket: TestDebug.DebugTicket * park: TimeSpan -> Async<AgentReply<TestDebug.DebugProgress>>

/// Evaluate and raise on failure: the throwing form, for startup scripts whose failure must abort.
let evalOrThrow (session: IFsiSession) (code: string) (cancellationToken: CancellationToken) : unit =
  match (session.Eval(code, cancellationToken)).Outcome with
  | FsiSucceeded -> ()
  | FsiFailed ex -> raise ex
  | FsiInterrupted -> raise (OperationCanceledException "the evaluation was interrupted")

/// Reflection-walk an FSI session's bound values into a JSON-serialized Features.LiveValueTree.LiveValueSnapshot,
/// for the dashboard's watch window. Pulled on demand AFTER an eval reply, never attached to it — the walk used to
/// sit between the eval finishing and the caller getting its result (roast-4 #2).
let private captureLiveValueSnapshotJson (session: FsiEvaluationSession) (generationRef: int64 ref) : string =
  try
    let boundValues =
      session.GetBoundValues()
      |> List.map (fun bv ->
        let value =
          try bv.Value.ReflectionValue
          with _ -> null
        let typeSig =
          try
            match bv.Value.ReflectionType with
            | null -> ""
            | t -> t.Name
          with _ -> ""
        (bv.Name, typeSig, value))
    let generation = Interlocked.Increment(&generationRef.contents)
    let snap = LiveValueTree.buildSnapshot "" generation boundValues
    // Use WorkerProtocol.Serialization (FSharp.SystemTextJson) so the NodeKind DU and other F# types serialize correctly.
    WorkerProtocol.Serialization.serialize snap
  with ex ->
    Log.warn "[FsiSession] Live value snapshot capture failed: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
    let generation = Interlocked.Increment(&generationRef.contents)
    WorkerProtocol.Serialization.serialize (LiveValueTree.buildSnapshot "" generation [])

/// An FSI session that lives in THIS process: today's behaviour, behind the port.
[<Sealed; AllowNullLiteral>]
type InProcessFsiSession(session: FsiEvaluationSession, init: AgentInit) =
  let agent = Agent(init, currentProcess (fun () -> session.DynamicAssemblies))

  interface IFsiSession with
    member _.HostLifetime = SharesTheWorkerProcess

    member _.Eval(code, cancellationToken) =
      let result, diagnostics = session.EvalInteractionNonThrowing(code, cancellationToken)
      { Outcome =
          match result with
          | Choice1Of2 _ -> FsiSucceeded
          | Choice2Of2 ex -> FsiFailed ex
        Diagnostics = diagnostics |> Array.map Diagnostics.Diagnostic.mkDiagnostic }

    member _.ReadFlag(name) =
      match session.TryFindBoundValue name with
      | None -> FlagUnbound
      | Some bound ->
        match bound.Value.ReflectionValue with
        | :? bool as value -> FlagBound value
        | _ ->
          FlagNotBool(
            match bound.Value.ReflectionType with
            | null -> ""
            | t -> t.Name
          )

    member _.LiveValuesJson(generation) = captureLiveValueSnapshotJson session generation

    member _.Completions(text, caret, word) = AutoCompletion.getCompletions session text caret word

    member _.Diagnose(text) = Diagnostics.getDiagnostics session text

    member _.TypeCheckWithSymbols(filePath, text) = Diagnostics.getTypeCheckWithSymbols session filePath text

    member _.BoundValue(name) =
      session.GetBoundValues()
      |> List.tryFind (fun bound -> bound.Name = name)
      |> Option.map (fun bound -> bound.Value.ReflectionValue)
      |> Option.toObj

    member _.AgentStarted = AgentAnswered agent.Started

    member _.AfterEval(request) = AgentAnswered(agent.AfterEval request)

    member _.TakeCoverage() = AgentAnswered(agent.TakeCoverage())

    member _.LoadedAssemblyNames() = AgentAnswered(agent.LoadedAssemblyNames())

    member _.DiscoverLoaded() = AgentAnswered(agent.DiscoverLoaded())

    member _.ValueReads(values) = AgentAnswered(agent.ValueReads values)

    member _.ReflectionReads() = AgentAnswered(agent.ReflectionReads())

    member _.SetReflectionMode(mode) = AgentAnswered(agent.SetReflectionMode mode)

    member _.AwaitEntries(probes, bound) =
      async {
        let! reading = agent.AwaitEntries(probes, bound)
        return AgentAnswered reading
      }

    member _.RunTest(test) =
      async {
        let! result = agent.RunTest test
        return AgentAnswered result
      }

    member _.DebugBegin(test) = async { return AgentAnswered(agent.DebugBegin test) }

    member _.DebugContinue(ticket, park) =
      async {
        let! progress = agent.DebugContinue(ticket, park)
        return AgentAnswered progress
      }

    member _.Dispose() = (session :> IDisposable).Dispose()
