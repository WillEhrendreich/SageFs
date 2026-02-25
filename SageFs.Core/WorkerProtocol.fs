namespace SageFs

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

module WorkerProtocol =

  type SessionId = string

  /// Lifecycle state of a managed session — no stringly-typed matching.
  [<RequireQualifiedAccess>]
  type SessionStatus =
    | Starting
    | Ready
    | Evaluating
    | Faulted
    | Restarting
    | Stopped

  module SessionStatus =
    let label = function
      | SessionStatus.Starting -> "Starting"
      | SessionStatus.Ready -> "Ready"
      | SessionStatus.Evaluating -> "Evaluating"
      | SessionStatus.Faulted -> "Faulted"
      | SessionStatus.Restarting -> "Restarting"
      | SessionStatus.Stopped -> "Stopped"

    /// Convert to SessionState for affordance checking.
    let toSessionState = function
      | SessionStatus.Starting -> SessionState.WarmingUp
      | SessionStatus.Ready -> SessionState.Ready
      | SessionStatus.Evaluating -> SessionState.Evaluating
      | SessionStatus.Faulted -> SessionState.Faulted
      | SessionStatus.Restarting -> SessionState.WarmingUp
      | SessionStatus.Stopped -> SessionState.Faulted

    let parse = function
      | "Starting" -> Result.Ok SessionStatus.Starting
      | "Ready" -> Result.Ok SessionStatus.Ready
      | "Evaluating" -> Result.Ok SessionStatus.Evaluating
      | "Faulted" -> Result.Ok SessionStatus.Faulted
      | "Restarting" -> Result.Ok SessionStatus.Restarting
      | "Stopped" -> Result.Ok SessionStatus.Stopped
      | unknown -> Result.Error (sprintf "Unknown session status: '%s'" unknown)

    /// Can accept new work?
    let isOperational = function
      | SessionStatus.Ready -> true
      | _ -> false

    /// Alive (not stopped or faulted)?
    let isAlive = function
      | SessionStatus.Starting | SessionStatus.Ready
      | SessionStatus.Evaluating | SessionStatus.Restarting -> true
      | SessionStatus.Faulted | SessionStatus.Stopped -> false

  [<RequireQualifiedAccess>]
  type WorkerMessage =
    | EvalCode of code: string * replyId: string
    | CheckCode of code: string * replyId: string
    | TypeCheckWithSymbols of code: string * filePath: string * replyId: string
    | GetCompletions of code: string * cursorPos: int * replyId: string
    | CancelEval
    | LoadScript of filePath: string * replyId: string
    | ResetSession of replyId: string
    | HardResetSession of rebuild: bool * replyId: string
    | GetStatus of replyId: string
    | RunTests of tests: Features.LiveTesting.TestCase array * maxParallelism: int * replyId: string
    | Shutdown

  type WorkerDiagnostic = {
    Severity: Features.Diagnostics.DiagnosticSeverity
    Message: string
    StartLine: int
    StartColumn: int
    EndLine: int
    EndColumn: int
  }

  module WorkerDiagnostic =
    let toDiagnostic (wd: WorkerDiagnostic) : Features.Diagnostics.Diagnostic =
      { Message = wd.Message
        Subcategory = ""
        Range = { StartLine = wd.StartLine; StartColumn = wd.StartColumn
                  EndLine = wd.EndLine; EndColumn = wd.EndColumn }
        Severity = wd.Severity }

  type WorkerStatusSnapshot = {
    Status: SessionStatus
    StatusMessage: string option
    EvalCount: int
    AvgDurationMs: int64
    MinDurationMs: int64
    MaxDurationMs: int64
  }

  /// Wire-friendly symbol reference for TypeCheckWithSymbols response
  type WorkerSymbolRef = {
    SymbolFullName: string
    IsFromDefinition: bool
    FilePath: string
    Line: int
  }

  module WorkerSymbolRef =
    let fromDomain (sr: Features.LiveTesting.SymbolReference) : WorkerSymbolRef =
      { SymbolFullName = sr.SymbolFullName
        IsFromDefinition = sr.UseKind = Features.LiveTesting.SymbolUseKind.Definition
        FilePath = sr.FilePath
        Line = sr.Line }

    let toDomain (ws: WorkerSymbolRef) : Features.LiveTesting.SymbolReference =
      { SymbolFullName = ws.SymbolFullName
        UseKind =
          if ws.IsFromDefinition then Features.LiveTesting.SymbolUseKind.Definition
          else Features.LiveTesting.SymbolUseKind.Reference
        UsedInTestId = None
        FilePath = ws.FilePath
        Line = ws.Line }

  [<RequireQualifiedAccess>]
  type WorkerResponse =
    | EvalResult of replyId: string * result: Result<string, SageFsError> * diagnostics: WorkerDiagnostic list * metadata: Map<string, string>
    | CheckResult of replyId: string * diagnostics: WorkerDiagnostic list
    | TypeCheckWithSymbolsResult of replyId: string * hasErrors: bool * diagnostics: WorkerDiagnostic list * symbolRefs: WorkerSymbolRef list
    | CompletionResult of replyId: string * completions: string list
    | StatusResult of replyId: string * status: WorkerStatusSnapshot
    | EvalCancelled of wasRunning: bool
    | ResetResult of replyId: string * result: Result<unit, SageFsError>
    | HardResetResult of replyId: string * result: Result<string, SageFsError>
    | ScriptLoaded of replyId: string * result: Result<string, SageFsError>
    | TestRunResults of replyId: string * results: Features.LiveTesting.TestRunResult array
    | WorkerReady
    | WorkerShuttingDown
    | WorkerError of SageFsError

  /// Transport abstraction — a function, not an interface.
  /// Same signature works for named pipes, HTTP, or in-process.
  type SessionProxy = WorkerMessage -> Async<WorkerResponse>

  type SessionInfo = {
    Id: SessionId
    Name: string option
    Projects: string list
    WorkingDirectory: string
    SolutionRoot: string option
    CreatedAt: DateTime
    LastActivity: DateTime
    Status: SessionStatus
    WorkerPid: int option
  }

  module SessionInfo =
    /// Walk up from dir looking for .git directory.
    let findGitRoot (startDir: string) : string option =
      let rec walk (dir: string) =
        if Directory.Exists(Path.Combine(dir, ".git")) then Some dir
        else
          let parent = Path.GetDirectoryName dir
          if isNull parent || parent = dir then None
          else walk parent
      walk startDir

    let findSolutionRoot (workingDir: string) =
      let rec walk (dir: string) =
        let parent = Path.GetDirectoryName dir
        if isNull parent || parent = dir then None
        else
          let hasSln =
            Directory.GetFiles(dir, "*.sln")
            |> Array.append (Directory.GetFiles(dir, "*.slnx"))
            |> Array.isEmpty
            |> not
          if hasSln then Some dir
          else walk parent
      walk workingDir

    let displayName (info: SessionInfo) =
      let getLastSegment (path: string) =
        let normalized = path.TrimEnd('/', '\\').Replace('\\', '/')
        Path.GetFileName normalized
      match info.SolutionRoot with
      | Some root -> getLastSegment root
      | None -> getLastSegment info.WorkingDirectory

  module Serialization =
    let jsonOptions =
      let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
      opts.Converters.Add(
        JsonFSharpConverter(
          JsonUnionEncoding.AdjacentTag,
          unionTagName = "type",
          unionFieldsName = "value"
        )
      )
      opts

    let serialize<'T> (value: 'T) =
      JsonSerializer.Serialize(value, jsonOptions)

    let deserialize<'T> (json: string) =
      JsonSerializer.Deserialize<'T>(json, jsonOptions)
