/// The hook's whole run, minus the parts that touch the machine.
///
/// stdin text goes in, the JSON to print (or nothing) comes out. Everything the run needs to look up, the
/// environment, the repository config, the project scan, the daemon, the lease, comes in as a `World`, so
/// SageFs.Tests runs the real code path with a fake daemon. Hook.fs is the compiled program: it builds the real
/// `World` and owns `main`. Nothing in here may be slower than it has to be, it runs before every Bash call
/// that mentions dotnet: no process spawn, and the disk is only touched once a command is a slow-loop verb.
module SageFs.AgentHooks.Verdict

open System
open System.IO
open System.Text.Json
open SageFs.AgentHooks.ReplGuard

/// Everything the hook has to ask the machine.
type World =
  { /// An environment variable's value, "" when it is not set.
    GetEnv: string -> string
    /// The repository config for a hook working directory.
    ReadRepoConfig: string -> RepoConfig
    ProjectScope: string -> ProjectScope
    /// Whether the daemon answered /health, and what it said about its sessions.
    Health: unit -> DaemonProbe * SessionSummary list
    /// Ask for a lease for a declared final gate; `None` when it could not be asked.
    Lease: SlowVerb -> LeaseProbe option
    /// Tell the daemon one use of the escape hatch happened. Best-effort: it can fail and nothing depends on it.
    ReportHatch: SageFs.HatchUse -> unit }

let str (el: JsonElement) (name: string) =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
  | _ -> ""

/// `GET /health`'s own `sessionStates` array already carries every session's `workingDirectory`, lifecycle
/// `status` and, when it is faulted, `faultReason`; this parses those into `SessionSummary`s for `sessionProbe`,
/// best-effort: a shape it does not recognise yields `[]`, never an exception, so a daemon that answered health at
/// all is never mistaken for a parse failure.
let parseSessionSummaries (healthBody: string) : SessionSummary list =
  try
    use doc = JsonDocument.Parse healthBody
    match doc.RootElement.TryGetProperty "sessionStates" with
    | true, states when states.ValueKind = JsonValueKind.Array ->
      states.EnumerateArray()
      |> Seq.map (fun s ->
        { WorkingDirectory = str s "workingDirectory"
          Status = str s "status"
          FaultReason = str s "faultReason" })
      |> List.ofSeq
    | _ -> []
  with _ -> []

/// The command and cwd out of the hook's stdin JSON; empty strings when it is not the shape we know.
let parseInput (input: string) : string * string =
  try
    use doc = JsonDocument.Parse input
    let root = doc.RootElement
    let cmd =
      match root.TryGetProperty "tool_input" with
      | true, ti when ti.ValueKind = JsonValueKind.Object -> str ti "command"
      | _ -> ""
    cmd, str root "cwd"
  with _ -> "", ""

/// Where a hook's working directory sits in a git checkout.
type GitRoot =
  | GitRoot of path: string
  | NoGitRoot

/// The nearest directory at or above `cwd` that has a `.git` entry. A worktree has a `.git` FILE, a plain
/// checkout a directory, and both count. This looks at the hook's cwd from the payload, never the process's own.
let findGitRoot (cwd: string) : GitRoot =
  let rec up (dir: DirectoryInfo) =
    match dir with
    | null -> NoGitRoot
    | d ->
      let dotGit = Path.Combine(d.FullName, ".git")
      match Directory.Exists dotGit || File.Exists dotGit with
      | true -> GitRoot d.FullName
      | false -> up d.Parent
  let start = if String.IsNullOrWhiteSpace cwd then Environment.CurrentDirectory else cwd
  match Directory.Exists start with
  | true -> up (DirectoryInfo start)
  | false -> NoGitRoot

/// Reads `<git root>/.SageFs/repl-guard.json`. A missing file is `NoRepoConfig`; a file that is there but
/// cannot be read or parsed is `RepoConfigMalformed`, never an exception.
let readRepoConfig (cwd: string) : RepoConfig =
  match findGitRoot cwd with
  | NoGitRoot -> NoRepoConfig
  | GitRoot root ->
    let path = Path.Combine(root, ConfigDirectory, ConfigFileName)
    match File.Exists path with
    | false -> NoRepoConfig
    | true ->
      try
        match parseConfig (File.ReadAllText path) with
        | Ok allow -> RepoConfigLoaded allow
        | Error problem -> RepoConfigMalformed(path, problem)
      with e -> RepoConfigMalformed(path, sprintf "it could not be read: %s" e.Message)

let denyJson (reason: string) =
  JsonSerializer.Serialize
    {| hookSpecificOutput =
         {| hookEventName = "PreToolUse"
            permissionDecision = "deny"
            permissionDecisionReason = reason |} |}

/// No `permissionDecision` on purpose: saying "allow" would skip the user's own permission prompts. This only
/// adds context and leaves the normal flow alone.
let adviceJson (reminder: string) =
  JsonSerializer.Serialize
    {| hookSpecificOutput =
         {| hookEventName = "PreToolUse"
            additionalContext = reminder |} |}

/// The hook's whole run, given the stdin text; the JSON to print when it denies or advises, nothing when it allows.
let verdict (world: World) (input: string) : string option =
  let command, cwd = parseInput input
  match classify command with
  | NoSlowLoop -> None
  | SlowLoop _
  | DeclaredFinalGate _ ->
    let policy = Policy.build (world.GetEnv GuardMode.Variable) (world.ReadRepoConfig cwd)
    match needsContextWith policy.Allow command with
    | false -> None
    | true ->
      match world.ProjectScope cwd with
      | NotFSharpWorkspace -> None
      | FSharpWorkspace ->
        match world.Health() with
        | DaemonNotAnswering, _ -> None
        | DaemonAnswering, sessions ->
          let session = Some(sessionProbe cwd sessions)
          let lease =
            match classifyWith policy.Allow command with
            | DeclaredFinalGate verb -> world.Lease verb
            | SlowLoop _
            | NoSlowLoop -> None
          let ctx = { Project = FSharpWorkspace; Daemon = DaemonAnswering; Session = session; Lease = lease }
          match hatchReportWith policy command ctx with
          | Counted hatch -> world.ReportHatch hatch
          | NotCounted -> ()
          match decideWith policy command ctx with
          | Allow -> None
          | AllowWithAdvice reminder -> Some(adviceJson reminder)
          | Deny reason -> Some(denyJson reason)
