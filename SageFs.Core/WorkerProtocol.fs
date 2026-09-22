namespace SageFs

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open SageFs.ProjectLoading

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

  /// A daemon-tracked worker process's id and, once it reports one, its HTTP port.
  type WorkerHandle = { Pid: int; Port: int option }

  /// The daemon's own live status for one managed session (SessionInfo.Status).
  /// Distinct from the worker's simpler self-reported SessionStatus (used in
  /// WorkerStatusSnapshot) — a worker process has no notion of "my own pid as
  /// tracked by my supervisor" or "the fault reason the daemon chose to record
  /// for me," so folding those into the wire type would force a worker to
  /// fabricate data it doesn't own. This type instead folds what used to be
  /// three independent optional fields on SessionInfo (FaultReason/WorkerPid/
  /// WorkerPort) into the status itself, so illegal combinations — Ready with
  /// no pid, a stale fault reason surviving into Ready, a stale port
  /// surviving into Faulted — become unrepresentable.
  [<RequireQualifiedAccess>]
  type SessionLifecycleStatus =
    | Starting of WorkerHandle
    | Ready of WorkerHandle
    | Evaluating of WorkerHandle
    /// Worker is running a dotnet build or similar multi-second compilation step.
    | Building of buildReason: string * worker: WorkerHandle
    | Faulted of reason: string option
    /// A restart in flight. Carries the OLD worker's pid ONLY so a late
    /// exit/ready event from that dying process can be recognized as stale
    /// and ignored (see the WorkerExited/WorkerReady stale-pid guards in
    /// SessionManager) — it is not a live worker. None when there was no
    /// prior worker (a cold restart after a Faulted/Stopped session).
    | Restarting of previousWorkerPid: int option
    | Stopped

  /// Conversion and query utilities for SessionLifecycleStatus.
  module SessionLifecycleStatus =
    let workerPid = function
      | SessionLifecycleStatus.Starting w
      | SessionLifecycleStatus.Ready w
      | SessionLifecycleStatus.Evaluating w -> Some w.Pid
      | SessionLifecycleStatus.Building(_, w) -> Some w.Pid
      | SessionLifecycleStatus.Restarting pid -> pid
      | SessionLifecycleStatus.Faulted _ | SessionLifecycleStatus.Stopped -> None

    let workerPort = function
      | SessionLifecycleStatus.Starting w
      | SessionLifecycleStatus.Ready w
      | SessionLifecycleStatus.Evaluating w -> w.Port
      | SessionLifecycleStatus.Building(_, w) -> w.Port
      | SessionLifecycleStatus.Faulted _
      | SessionLifecycleStatus.Restarting _
      | SessionLifecycleStatus.Stopped -> None

    let faultReason = function
      | SessionLifecycleStatus.Faulted reason -> reason
      | _ -> None

    /// Update the port on a status that carries a worker handle; a no-op on
    /// any status that doesn't (Faulted/Restarting/Stopped never do).
    let withWorkerPort (port: int option) = function
      | SessionLifecycleStatus.Starting w -> SessionLifecycleStatus.Starting { w with Port = port }
      | SessionLifecycleStatus.Ready w -> SessionLifecycleStatus.Ready { w with Port = port }
      | SessionLifecycleStatus.Evaluating w -> SessionLifecycleStatus.Evaluating { w with Port = port }
      | SessionLifecycleStatus.Building(reason, w) -> SessionLifecycleStatus.Building(reason, { w with Port = port })
      | other -> other

    let label = function
      | SessionLifecycleStatus.Starting _ -> "Starting"
      | SessionLifecycleStatus.Ready _ -> "Ready"
      | SessionLifecycleStatus.Evaluating _ -> "Evaluating"
      | SessionLifecycleStatus.Building(reason, _) -> sprintf "Building (%s)" reason
      | SessionLifecycleStatus.Faulted _ -> "Faulted"
      | SessionLifecycleStatus.Restarting _ -> "Restarting"
      | SessionLifecycleStatus.Stopped -> "Stopped"

    /// Convert to SessionState for affordance checking.
    let toSessionState = function
      | SessionLifecycleStatus.Starting _ -> SessionState.WarmingUp
      | SessionLifecycleStatus.Ready _ -> SessionState.Ready
      | SessionLifecycleStatus.Evaluating _ -> SessionState.Evaluating
      | SessionLifecycleStatus.Building _ -> SessionState.Evaluating
      | SessionLifecycleStatus.Faulted _ -> SessionState.Faulted
      | SessionLifecycleStatus.Restarting _ -> SessionState.WarmingUp
      | SessionLifecycleStatus.Stopped -> SessionState.Faulted

    /// Can accept new work?
    let isOperational = function
      | SessionLifecycleStatus.Ready _ -> true
      | _ -> false

    /// Alive (not stopped or faulted)?
    let isAlive = function
      | SessionLifecycleStatus.Starting _ | SessionLifecycleStatus.Ready _
      | SessionLifecycleStatus.Evaluating _ | SessionLifecycleStatus.Building _
      | SessionLifecycleStatus.Restarting _ -> true
      | SessionLifecycleStatus.Faulted _ | SessionLifecycleStatus.Stopped -> false

    /// Reconcile the daemon's own lifecycle status with what the worker
    /// itself just self-reported (WorkerStatusSnapshot.Status, the simpler
    /// wire-protocol SessionStatus). The worker's report carries no pid/port
    /// — it has no notion of how the daemon is tracking it — so those are
    /// carried over from whatever the daemon currently has recorded.
    let ofWorkerReport (current: SessionLifecycleStatus) (reported: SessionStatus) : SessionLifecycleStatus =
      let handle () : WorkerHandle =
        { Pid = workerPid current |> Option.defaultValue 0
          Port = workerPort current }
      match reported with
      | SessionStatus.Starting -> SessionLifecycleStatus.Starting (handle ())
      | SessionStatus.Ready -> SessionLifecycleStatus.Ready (handle ())
      | SessionStatus.Evaluating -> SessionLifecycleStatus.Evaluating (handle ())
      | SessionStatus.Building reason -> SessionLifecycleStatus.Building (reason, handle ())
      | SessionStatus.Faulted -> SessionLifecycleStatus.Faulted (faultReason current)
      | SessionStatus.Restarting -> SessionLifecycleStatus.Restarting (workerPid current)
      | SessionStatus.Stopped -> SessionLifecycleStatus.Stopped

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
    /// Pulled by the daemon on demand, AFTER an eval reply — never attached
    /// to one. Building the live-value snapshot (reflection walk + JSON) used
    /// to sit between the eval finishing and the caller getting its result;
    /// this request lets the daemon ask for it separately (roast-4 #2).
    | GetLiveValues of replyId: string
    | RunTests of tests: Features.LiveTesting.TestCase array * maxParallelism: int * replyId: string
    | GetTestDiscovery of replyId: string
    /// Identity-preserving whole-file eval of a (possibly unsaved) editor
    /// buffer for as-you-type live testing (live-testing-asyoutype-plan.md
    /// Brief 3): `content` is evaluated through the same `EvalMode.File`
    /// CompilationContext transform the file-watcher's on-disk reload uses,
    /// with eval-time test discovery FORCED (Brief 2's `liveTestRediscover`
    /// flag) so a brand-new or edited `[<Tests>]` value is scanned even when
    /// it detours no existing method. `filePath` is used only for parsing/
    /// identity (module-path derivation, diagnostics); the file on disk is
    /// never read or written by this message.
    | EvalLiveTestFile of filePath: string * content: string * replyId: string
    | GetInstrumentationMaps of replyId: string
    | RunApp of project: string * previous: AppRun.PreviousAddress * replyId: string
    | StopApp of scope: AppRun.StopScope * replyId: string
    /// Long poll: answered when the app is no longer Running with this run id.
    | AwaitAppChange of runId: string * replyId: string
    | Shutdown

  /// F# compiler diagnostic serialized for worker→daemon transport.
  type WorkerDiagnostic = {
    Severity: Features.Diagnostics.DiagnosticSeverity
    Message: string
    StartLine: int
    StartColumn: int
    EndLine: int
    EndColumn: int
    /// The FCS diagnostic's stable `ErrorNumber` (the "39" in "FS0039"), or 0
    /// when not derived from a compiler diagnostic. See roast-5 item #10.
    ErrorNumber: int
  }

  /// Conversion from wire-format WorkerDiagnostic to domain Diagnostic.
  module WorkerDiagnostic =
    /// Convert a WorkerDiagnostic to the rich Features.Diagnostics.Diagnostic type.
    let toDiagnostic (wd: WorkerDiagnostic) : Features.Diagnostics.Diagnostic =
      { Message = wd.Message
        Subcategory = ""
        Range = { StartLine = wd.StartLine; StartColumn = wd.StartColumn
                  EndLine = wd.EndLine; EndColumn = wd.EndColumn }
        Severity = wd.Severity
        ErrorNumber = wd.ErrorNumber }

  /// Point-in-time snapshot of worker session health and performance metrics.
  type WorkerStatusSnapshot = {
    Status: SessionStatus
    StatusMessage: string option
    EvalCount: int
    AvgDurationMs: int64
    MinDurationMs: int64
    MaxDurationMs: int64
    /// The loaded projects as the worker classified them (only it has the MSBuild properties).
    Projects: ClassifiedProject list
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
    | TypeCheckWithSymbolsResult of replyId: string * diagnostics: WorkerDiagnostic list * symbolRefs: WorkerSymbolRef list
    | CompletionResult of replyId: string * completions: string list
    | StatusResult of replyId: string * status: WorkerStatusSnapshot
    /// Reply to GetLiveValues: the same JSON a Features.LiveValueTree.LiveValueSnapshot
    /// serializes to today (the daemon already deserializes that type).
    | LiveValuesResult of replyId: string * snapshotJson: string
    | EvalCancelled of wasRunning: bool
    | ResetResult of replyId: string * result: Result<unit, SageFsError>
    | HardResetResult of replyId: string * result: Result<string, SageFsError>
    | ScriptLoaded of replyId: string * result: Result<string, SageFsError>
    | TestRunResults of replyId: string * results: Features.LiveTesting.TestRunResult array
    | InitialTestDiscovery of tests: Features.LiveTesting.TestCase array * providers: Features.LiveTesting.ProviderDescription list
    /// Reply to EvalLiveTestFile. Mirrors InitialTestDiscovery's `(tests,
    /// providers)` shape — same wire vocabulary, not a forked one — but
    /// wrapped in a Result so a parse/eval failure is a first-class DU case
    /// (fail-closed, Invariant 4 of live-testing-asyoutype-plan.md §2)
    /// instead of an exception: on Error, the worker's dynamic discovery and
    /// run-test slots are left untouched and the caller must retain its
    /// last-good state rather than wipe to a false-empty or false-green view.
    /// On Ok, `tests` is already the LIVE MERGE (TestDiscoveryMerge.merge) of
    /// the compiled baseline with the freshly eval'd dynamic discovery.
    | EvalLiveTestFileResult of
        replyId: string *
        result: Result<Features.LiveTesting.TestCase array * Features.LiveTesting.ProviderDescription list, SageFsError>
    | InstrumentationMapsResult of replyId: string * maps: Features.LiveTesting.InstrumentationMap array
    | AppRunResult of replyId: string * result: Result<AppRun.AppRunState, SageFsError>
    | WorkerReady
    | WorkerShuttingDown
    | WorkerError of SageFsError

  /// Transport abstraction — a function, not an interface.
  /// Same signature works for named pipes, HTTP, or in-process.
  type SessionProxy = WorkerMessage -> Async<WorkerResponse>

  /// Which messages count as the session actually being USED, as opposed to
  /// merely being checked on. `SessionInfo.LastActivity` (and therefore the
  /// dashboard's idle detection, `SessionDisplay.displayStatus`) is only ever
  /// as honest as this classification — a `GetStatus` poll must never count,
  /// or a genuinely idle session could never show idle again.
  [<RequireQualifiedAccess>]
  module WorkerMessage =
    let isActivity = function
      | WorkerMessage.EvalCode _
      | WorkerMessage.CheckCode _
      | WorkerMessage.TypeCheckWithSymbols _
      | WorkerMessage.GetCompletions _
      | WorkerMessage.LoadScript _
      | WorkerMessage.RunTests _
      | WorkerMessage.EvalLiveTestFile _
      | WorkerMessage.RunApp _
      | WorkerMessage.StopApp _ -> true
      | WorkerMessage.CancelEval
      | WorkerMessage.ResetSession _
      | WorkerMessage.HardResetSession _
      | WorkerMessage.GetStatus _
      | WorkerMessage.GetLiveValues _
      | WorkerMessage.GetTestDiscovery _
      | WorkerMessage.GetInstrumentationMaps _
      | WorkerMessage.AwaitAppChange _
      | WorkerMessage.Shutdown -> false

  /// Wrapping helpers for `SessionProxy` — kept next to the type so every
  /// place that resolves a proxy from worker URLs (`SessionManagementOps.GetProxy`,
  /// `EffectDeps.GetProxy`) can apply the SAME behavior instead of hand-rolling it.
  module SessionProxy =
    /// Wrap a proxy so every activity-bearing message (see `WorkerMessage.isActivity`)
    /// calls `touch` before being sent. This is the one place `TouchSession`
    /// gets posted — no route into a worker can forget it, because none of
    /// them call the worker any other way than through a `SessionProxy`.
    let touching (touch: unit -> unit) (proxy: SessionProxy) : SessionProxy =
      fun msg ->
        match WorkerMessage.isActivity msg with
        | true -> touch ()
        | false -> ()
        proxy msg

  /// Metadata for a managed session — displayed in dashboard, stored in persistence.
  type SessionInfo = {
    Id: SessionId
    Name: string option
    Projects: string list
    WorkingDirectory: string
    SolutionRoot: string option
    CreatedAt: DateTime
    LastActivity: DateTime
    Status: SessionLifecycleStatus
    Workflow: WorkflowTypes.SessionWorkflow
    /// The project currently in focus for "Run App" operations.
    ActiveProject: string option
    /// Classification of all projects loaded in this session.
    ProjectRoles: ClassifiedProject list
    /// The app this session runs ("Run App"), as the user should see it.
    App: AppRun.AppRunState
  }

  /// Utilities for deriving display-friendly paths from session metadata.
  module SessionInfo =
    /// Walk up from `startDir` looking for the checkout root — a directory
    /// carrying a checkout marker (`.git`, directory OR file — a worktree's
    /// `.git` is a file). Delegates to `Checkout.classify`/`Checkout.root`;
    /// kept as its own function (same name/signature as before) because
    /// every existing caller resolves a plain `string option` root and does
    /// not need the fuller `Checkout` classification (worktree branch, "not
    /// a git checkout" vs "no marker found") — see `SessionInfo.checkout`
    /// below for that. FIXED: this used to check `Directory.Exists ".git"`
    /// only, so from inside a git worktree it walked past the worktree's
    /// `.git` FILE straight to the main checkout's `.git` DIRECTORY further
    /// up and returned the WRONG (main checkout's) root — see
    /// sagefs-multiagent-vision.md §1.6 #2.
    let findGitRoot (startDir: string) : string option =
      Checkout.root (Checkout.classify startDir)

    /// The checkout a session's working directory sits in — a MAIN checkout,
    /// a git WORKTREE (with its own branch), or no git checkout at all.
    /// Computed on demand from `WorkingDirectory` rather than stored on
    /// `SessionInfo`: `SessionInfo` is constructed as a full record literal
    /// at ~50 call sites across the daemon and test suite, and `Checkout`
    /// classification is a few cheap filesystem stats, so storing it would
    /// only risk staleness for zero benefit over computing it where needed
    /// (routing, list_sessions, the sidebar).
    let checkout (info: SessionInfo) : Checkout.Checkout =
      Checkout.classify info.WorkingDirectory

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
