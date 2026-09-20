/// Pure row shaping for the Sessions tree.
///
/// Split out of SessionsTreeProvider so the decisions that were producing
/// `$(zap) no projectReady` in the sidebar are testable under plain `dotnet
/// fsi` (see tests/SessionsTreeContractTests.fsx). No Fable dependency.
///
/// The three rules this module exists to enforce:
///   * a codicon token (`$(zap)`) belongs in `iconPath`, never in a label —
///     VS Code renders it literally in a TreeItem label;
///   * the label shows what the session ACTUALLY loaded, which is not always
///     what was declared (a session created with `projects=[]` can still load
///     a project the worker discovered);
///   * `Ready` means a worker is alive, not that code is loaded, so the row's
///     icon comes from the DAEMON'S health verdict, never from a local guess.
///
/// WHY the health field, and why the old heuristic is gone: this module used
/// to derive "is it healthy?" from whether `effectiveProjects` was empty. That
/// guess is inverted relative to the daemon's own classifier in BOTH
/// directions — `SageFs.Core/SessionHealth.fs:95-96` states that a bare
/// session (`projectRoles = []`) is Healthy, and `:118-121` says Degraded is
/// "a project resolved but nothing loaded", i.e. `loadedProjects` NON-empty.
/// So the heuristic put a warning triangle on every healthy bare REPL session
/// and a green bolt on exactly the degraded ones it was written to catch. The
/// daemon now ships the verdict on `/api/sessions` as `{status, reason}`; the
/// guess is deleted, and the `reason` — the field that carries the remedy —
/// reaches the user in the row's description and tooltip.
module SageFs.Vscode.SessionsTreePure

/// The daemon's usability verdict for one session, mirroring
/// `SageFs.Core/SessionHealth.fs`'s DU as it arrives on the wire.
/// `Unknown` is the interop case: a daemon older than the verdict sends no
/// `health` field, and claiming either "healthy" or "degraded" for it would be
/// inventing a fact. Every rendering function below is total over this type.
[<RequireQualifiedAccess>]
type SessionHealth =
  /// Lifecycle hasn't reached a judgeable state yet.
  | Starting
  /// Worker is alive and what warmup expected to load, loaded.
  | Healthy
  /// Worker is alive but something the user expects to work will not.
  | Degraded of reason: string
  /// Worker is not usable at all. `reason` names why.
  | Failed of reason: string
  /// No verdict on the wire (daemon predates `SessionHealth`).
  | Unknown

module SessionHealth =

  /// Rebuild the verdict from `/api/sessions`'s `health: {status, reason}`.
  /// An unrecognised status is `Unknown`, not a guess — a wire value this
  /// client does not know about must not be rendered as a judgement.
  let ofWire (status: string) (reason: string) : SessionHealth =
    match status with
    | "Starting" -> SessionHealth.Starting
    | "Healthy" -> SessionHealth.Healthy
    | "Degraded" -> SessionHealth.Degraded reason
    | "Failed" -> SessionHealth.Failed reason
    | _ -> SessionHealth.Unknown

  let label = function
    | SessionHealth.Starting -> "Starting"
    | SessionHealth.Healthy -> "Healthy"
    | SessionHealth.Degraded _ -> "Degraded"
    | SessionHealth.Failed _ -> "Failed"
    | SessionHealth.Unknown -> "Unknown"

  /// The remedy the daemon attached, when the verdict carries one.
  let reason = function
    | SessionHealth.Degraded r
    | SessionHealth.Failed r -> Some r
    | SessionHealth.Starting
    | SessionHealth.Healthy
    | SessionHealth.Unknown -> None

  /// True when the verdict is something the user needs told about. Healthy
  /// and Starting stay quiet; Unknown stays quiet because it is the absence
  /// of a verdict, not a bad one.
  let isNoteworthy = function
    | SessionHealth.Degraded _
    | SessionHealth.Failed _ -> true
    | SessionHealth.Starting
    | SessionHealth.Healthy
    | SessionHealth.Unknown -> false

/// What the daemon reports about one session, in the terms this module needs.
type SessionRowInput = {
  Id: string
  Status: string
  /// The project list the session was created with (may be empty even when a project loaded).
  DeclaredProjects: string array
  /// The projects the worker actually resolved and loaded. Authoritative when non-empty.
  LoadedProjects: string array
  EvalCount: int
  WorkingDirectory: string
  IsActive: bool
  /// The daemon's verdict. The ONLY input to the row's health rendering.
  Health: SessionHealth
}

/// One rendered row. `Icon` is a bare theme-icon id (no `$(...)` wrapper).
type SessionRow = {
  Label: string
  Description: string
  Icon: string
  Tooltip: string
  ContextValue: string
  /// Label and description joined for screen readers — they used to be
  /// concatenated with no separator ("no projectReady").
  AccessibleName: string
}

let private stripExtension (name: string) =
  match name with
  | n when n.EndsWith ".fsproj" -> n.[.. n.Length - 8]
  | n when n.EndsWith ".slnx" -> n.[.. n.Length - 6]
  | n when n.EndsWith ".sln" -> n.[.. n.Length - 5]
  | n -> n

let private fileName (path: string) =
  path.Split([| '/'; '\\' |]) |> Array.last

/// What the session holds: loaded wins over declared, because declared can lie.
let effectiveProjects (input: SessionRowInput) : string array =
  match input.LoadedProjects with
  | [||] -> input.DeclaredProjects
  | loaded -> loaded

/// Whether the session has finished starting, i.e. whether "it loaded nothing"
/// is knowable yet.
let private isSettled (status: string) =
  match status with
  | "Starting" | "Restarting" -> false
  | _ -> true

let private projectNames (projects: string array) =
  projects
  |> Array.filter (fun p -> not (System.String.IsNullOrWhiteSpace p))
  |> Array.map (fileName >> stripExtension)
  |> String.concat ", "

let label (input: SessionRowInput) : string =
  match effectiveProjects input with
  | [||] when not (isSettled input.Status) -> "(loading…)"
  | [||] -> "(no project loaded)"
  | projects -> projectNames projects

/// Icon ids for the statuses that carry no health verdict of their own.
/// Only reachable via `SessionHealth.Unknown`, i.e. an older daemon.
let private statusIcon (status: string) =
  match status with
  | "Ready" | "Evaluating" -> "zap"
  | "Starting" | "Restarting" -> "loading~spin"
  | "Faulted" -> "error"
  | "Stopped" -> "circle-slash"
  | _ -> "question"

/// The status icon, total over the daemon's verdict. `Ready` only ever meant
/// "the worker process is alive", so the icon renders `Health`, not `Status` —
/// and never a locally-invented substitute for it.
let icon (input: SessionRowInput) : string =
  match input.Health with
  | SessionHealth.Healthy -> "zap"
  | SessionHealth.Degraded _ -> "warning"
  | SessionHealth.Starting -> "loading~spin"
  // Failed covers both Faulted and Stopped; "stopped on purpose" is not the
  // same picture as "fell over", so the lifecycle status still picks between
  // the two glyphs. The VERDICT is still the daemon's.
  | SessionHealth.Failed _ ->
    match input.Status with
    | "Stopped" -> "circle-slash"
    | _ -> "error"
  | SessionHealth.Unknown -> statusIcon input.Status

let description (input: SessionRowInput) : string =
  [ if input.IsActive then "active"
    input.Status
    // A noteworthy verdict is shown NEXT TO the lifecycle status, not instead
    // of it: "Ready · Degraded" is the whole point — the worker is alive and
    // the session is still unusable.
    match SessionHealth.isNoteworthy input.Health with
    | true -> SessionHealth.label input.Health
    | false -> ()
    match input.EvalCount with
    | 0 -> ()
    | 1 -> "1 eval"
    | n -> sprintf "%d evals" n ]
  |> String.concat " · "

let tooltip (input: SessionRowInput) : string =
  let projects =
    match effectiveProjects input with
    | [||] -> "  (none loaded)"
    | ps -> ps |> Array.map (sprintf "  %s") |> String.concat "\n"
  [ sprintf "Session %s" input.Id
    sprintf "Status: %s" input.Status
    // The reason is why `Degraded` carries a field at all: it names what is
    // wrong AND what to do about it. Dropping it here would leave a warning
    // triangle the user cannot act on.
    match SessionHealth.reason input.Health with
    | Some r -> sprintf "Health: %s — %s" (SessionHealth.label input.Health) r
    | None -> sprintf "Health: %s" (SessionHealth.label input.Health)
    sprintf "Directory: %s" input.WorkingDirectory
    "Projects:"
    projects ]
  |> String.concat "\n"

let contextValue (input: SessionRowInput) : string =
  match input.IsActive, input.Status with
  | true, "Ready" -> "session-active-ready"
  | true, _ -> "session-active"
  | false, "Stopped" -> "session-stopped"
  | false, _ -> "session-inactive"

let renderRow (input: SessionRowInput) : SessionRow =
  let l = label input
  let d = description input
  { Label = l
    Description = d
    Icon = icon input
    Tooltip = tooltip input
    ContextValue = contextValue input
    AccessibleName = sprintf "%s — %s" l d }
