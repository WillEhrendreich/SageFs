namespace SageFs

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

/// Cross-boundary protocol types shared between daemon and worker processes.
/// This module defines the wire contract — changes here affect all editor integrations.
module WorkerProtocol =

  /// Session identifier — opaque single-case DU enforcing validated 8-char hex format.
  /// Construct via SessionId.newId() or SessionId.validate; extract via SessionId.value.
  [<Struct; CustomComparison; CustomEquality>]
  type SessionId = private SessionId of string
    with
    override x.ToString() = let (SessionId s) = x in s
    override x.GetHashCode() = let (SessionId s) = x in s.GetHashCode()
    override x.Equals(obj) =
      match obj with
      | :? SessionId as other -> let (SessionId a) = x in let (SessionId b) = other in a = b
      | _ -> false
    interface IComparable with
      member x.CompareTo(obj) =
        match obj with
        | :? SessionId as other -> let (SessionId a) = x in let (SessionId b) = other in String.Compare(a, b, StringComparison.Ordinal)
        | _ -> invalidArg "obj" "not a SessionId"
    interface IComparable<SessionId> with
      member x.CompareTo(other) = let (SessionId a) = x in let (SessionId b) = other in String.Compare(a, b, StringComparison.Ordinal)
    interface IEquatable<SessionId> with
      member x.Equals(other) = let (SessionId a) = x in let (SessionId b) = other in a = b

  /// Operations on SessionId values.
  module SessionId =
    /// Compiled regex pattern matching valid session IDs: exactly 8 lowercase hex chars.
    let validPattern = System.Text.RegularExpressions.Regex(@"^[0-9a-f]{8}$", System.Text.RegularExpressions.RegexOptions.Compiled)

    /// Extract the raw string value from a SessionId.
    let value (SessionId s) = s

    /// Generate a new random SessionId (8-char lowercase hex).
    let newId () = SessionId (Guid.NewGuid().ToString("N").[..7])

    /// Validate a session ID from an untrusted source (HTTP, MCP).
    /// Session IDs are 8-char lowercase hex strings (truncated GUID).
    let validate (raw: string) : Result<SessionId, string> =
      match System.String.IsNullOrEmpty(raw) with
      | true -> Error "session ID is empty"
      | false ->
        match validPattern.IsMatch(raw) with
        | true -> Ok (SessionId raw)
        | false -> Error (sprintf "invalid session ID format: '%s'" raw)

  /// Lifecycle state of a managed session — no stringly-typed matching.
  [<RequireQualifiedAccess>]
  type SessionStatus =
    | Starting
    | Ready
    | Evaluating
    /// Worker is running a dotnet build or similar multi-second compilation step.
    /// Sets "Building…" status in the UI so the tool doesn't appear hung.
    | Building of buildReason: string
    | Faulted
    | Restarting
    | Stopped

  /// Conversion and parsing utilities for SessionStatus.
  module SessionStatus =
    /// Convert a SessionStatus to its human-readable label string.
    let label = function
      | SessionStatus.Starting -> "Starting"
      | SessionStatus.Ready -> "Ready"
      | SessionStatus.Evaluating -> "Evaluating"
      | SessionStatus.Building reason -> sprintf "Building (%s)" reason
      | SessionStatus.Faulted -> "Faulted"
      | SessionStatus.Restarting -> "Restarting"
      | SessionStatus.Stopped -> "Stopped"

    /// Convert to SessionState for affordance checking.
    /// Building counts as Evaluating — the session is busy but accepting status queries.
    let toSessionState = function
      | SessionStatus.Starting -> SessionState.WarmingUp
      | SessionStatus.Ready -> SessionState.Ready
      | SessionStatus.Evaluating -> SessionState.Evaluating
      | SessionStatus.Building _ -> SessionState.Evaluating
      | SessionStatus.Faulted -> SessionState.Faulted
      | SessionStatus.Restarting -> SessionState.WarmingUp
      | SessionStatus.Stopped -> SessionState.Faulted

    /// Parse a status label back into a SessionStatus. Handles "Building (reason)" format.
    let parse = function
      | "Starting" -> Result.Ok SessionStatus.Starting
      | "Ready" -> Result.Ok SessionStatus.Ready
      | "Evaluating" -> Result.Ok SessionStatus.Evaluating
      | "Faulted" -> Result.Ok SessionStatus.Faulted
      | "Restarting" -> Result.Ok SessionStatus.Restarting
      | "Stopped" -> Result.Ok SessionStatus.Stopped
      | s when s.StartsWith("Building (", StringComparison.Ordinal) ->
        let reason = s.[10 .. s.Length - 2]
        Result.Ok (SessionStatus.Building reason)
      | unknown -> Result.Error (sprintf "Unknown session status: '%s'" unknown)

    /// Can accept new work?
    let isOperational = function
      | SessionStatus.Ready -> true
      | _ -> false

    /// Alive (not stopped or faulted)?
    let isAlive = function
      | SessionStatus.Starting | SessionStatus.Ready
      | SessionStatus.Evaluating | SessionStatus.Building _
      | SessionStatus.Restarting -> true
      | SessionStatus.Faulted | SessionStatus.Stopped -> false

  /// All messages the daemon can send to a worker process.
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
    | GetTestDiscovery of replyId: string
    | GetInstrumentationMaps of replyId: string
    | Shutdown

  /// F# compiler diagnostic serialized for worker→daemon transport.
  type WorkerDiagnostic = {
    Severity: Features.Diagnostics.DiagnosticSeverity
    Message: string
    StartLine: int
    StartColumn: int
    EndLine: int
    EndColumn: int
  }

  /// Conversion from wire-format WorkerDiagnostic to domain Diagnostic.
  module WorkerDiagnostic =
    /// Convert a WorkerDiagnostic to the rich Features.Diagnostics.Diagnostic type.
    let toDiagnostic (wd: WorkerDiagnostic) : Features.Diagnostics.Diagnostic =
      { Message = wd.Message
        Subcategory = ""
        Range = { StartLine = wd.StartLine; StartColumn = wd.StartColumn
                  EndLine = wd.EndLine; EndColumn = wd.EndColumn }
        Severity = wd.Severity }

  /// Point-in-time snapshot of worker session health and performance metrics.
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

  /// Conversion between wire-format WorkerSymbolRef and domain SymbolReference.
  module WorkerSymbolRef =
    /// Convert a domain SymbolReference to the wire-friendly WorkerSymbolRef format.
    let fromDomain (sr: Features.LiveTesting.SymbolReference) : WorkerSymbolRef =
      { SymbolFullName = sr.SymbolFullName
        IsFromDefinition = sr.UseKind = Features.LiveTesting.SymbolUseKind.Definition
        FilePath = sr.FilePath
        Line = sr.Line }

    /// Convert a wire-friendly WorkerSymbolRef back to the domain SymbolReference.
    /// Definition/Reference distinction is encoded as a bool on the wire.
    let toDomain (ws: WorkerSymbolRef) : Features.LiveTesting.SymbolReference =
      { SymbolFullName = ws.SymbolFullName
        UseKind =
          match ws.IsFromDefinition with
          | true -> Features.LiveTesting.SymbolUseKind.Definition
          | false -> Features.LiveTesting.SymbolUseKind.Reference
        UsedInTestId = None
        FilePath = ws.FilePath
        Line = ws.Line }

  /// All responses a worker process can send back to the daemon.
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
    | InitialTestDiscovery of tests: Features.LiveTesting.TestCase array * providers: Features.LiveTesting.ProviderDescription list
    | InstrumentationMapsResult of replyId: string * maps: Features.LiveTesting.InstrumentationMap array
    | WorkerReady
    | WorkerShuttingDown
    | WorkerError of SageFsError

  /// Transport abstraction — a function, not an interface.
  /// Same signature works for named pipes, HTTP, or in-process.
  type SessionProxy = WorkerMessage -> Async<WorkerResponse>

  /// Metadata for a managed session — displayed in dashboard, stored in persistence.
  type SessionInfo = {
    Id: SessionId
    Name: string option
    Projects: string list
    WorkingDirectory: string
    SolutionRoot: string option
    CreatedAt: DateTime
    LastActivity: DateTime
    Status: SessionStatus
    FaultReason: string option
    WorkerPid: int option
    Workflow: WorkflowTypes.SessionWorkflow
  }

  /// Utilities for deriving display-friendly paths from session metadata.
  module SessionInfo =
    /// Walk up from dir looking for .git directory.
    let findGitRoot (startDir: string) : string option =
      let rec walk (dir: string) =
        match Directory.Exists(Path.Combine(dir, ".git")) with
        | true -> Some dir
        | false ->
          let parent = Path.GetDirectoryName dir
          match isNull parent || parent = dir with
          | true -> None
          | false -> walk parent
      walk startDir

    /// Walk up from workingDir to find the nearest directory containing .sln or .slnx.
    /// Skips directories that do not exist (graceful for tests and missing paths).
    let findSolutionRoot (workingDir: string) =
      let rec walk (dir: string) =
        let parent = Path.GetDirectoryName dir
        match isNull parent || parent = dir with
        | true -> None
        | false ->
          let hasSln =
            Directory.Exists dir &&
            (Directory.GetFiles(dir, "*.sln")
             |> Array.append (Directory.GetFiles(dir, "*.slnx"))
             |> Array.isEmpty
             |> not)
          match hasSln with
          | true -> Some dir
          | false -> walk parent
      walk workingDir

    /// Extract a short display name: last path segment of solution root or working dir.
    let displayName (info: SessionInfo) =
      let getLastSegment (path: string) =
        let normalized = path.TrimEnd('/', '\\').Replace('\\', '/')
        Path.GetFileName normalized
      match info.SolutionRoot with
      | Some root -> getLastSegment root
      | None -> getLastSegment info.WorkingDirectory

  /// JSON serialization configured for F# discriminated unions (adjacent tag encoding).
  module Serialization =
    /// Pre-configured JsonSerializerOptions with camelCase and F# union support.
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

    /// Serialize a value to JSON string using the configured options.
    let serialize<'T> (value: 'T) =
      JsonSerializer.Serialize(value, jsonOptions)

    /// Deserialize a JSON string to a typed value using the configured options.
    let deserialize<'T> (json: string) =
      JsonSerializer.Deserialize<'T>(json, jsonOptions)
