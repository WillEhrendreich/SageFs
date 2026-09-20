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
///   * `Ready` means a worker is alive, not that code is loaded, so a Ready
///     session holding nothing is warned about rather than shown as healthy.
module SageFs.Vscode.SessionsTreePure

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

/// The status icon. A settled session with nothing loaded is a warning, not a
/// green light: "Ready" only ever meant "the worker process is alive".
let icon (input: SessionRowInput) : string =
  let empty = Array.isEmpty (effectiveProjects input)
  match input.Status with
  | "Ready" | "Evaluating" when empty -> "warning"
  | "Ready" | "Evaluating" -> "zap"
  | "Starting" | "Restarting" -> "loading~spin"
  | "Faulted" -> "error"
  | "Stopped" -> "circle-slash"
  | _ -> "question"

let description (input: SessionRowInput) : string =
  [ if input.IsActive then "active"
    input.Status
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
