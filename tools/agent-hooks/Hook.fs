/// The IO around ReplGuard.decide: stdin, the disk walk, the /health probe, the lease ask, the JSON out.
/// The decision itself is ReplGuard.fs, the file SageFs.Tests compiles and tests.
///
/// This is a compiled program, not an .fsx, because a Claude Code PreToolUse hook runs before EVERY Bash call
/// and `dotnet fsi` costs over a second of startup each time. Commands that never mention dotnet leave right
/// after reading stdin, before any of the work below.
module SageFs.AgentHooks.Hook

open System
open System.IO
open System.Net.Http
open System.Text.Json
open SageFs.AgentHooks.ReplGuard

/// The daemon's MCP port unless SAGEFS_MCP_PORT says otherwise.
[<Literal>]
let private defaultMcpPort = 37749

/// How long the hook waits on the daemon. A hook that cannot reach the daemon must not block the agent, so
/// both are short and every failure reads as "not answering".
let private healthTimeout = TimeSpan.FromMilliseconds 800.0
let private leaseTimeout = TimeSpan.FromMilliseconds 1500.0

let private str (el: JsonElement) (name: string) =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
  | _ -> ""

/// The command and cwd out of the hook's stdin JSON; empty strings when it is not the shape we know.
let private parseInput (input: string) : string * string =
  try
    use doc = JsonDocument.Parse input
    let root = doc.RootElement
    let cmd =
      match root.TryGetProperty "tool_input" with
      | true, ti when ti.ValueKind = JsonValueKind.Object -> str ti "command"
      | _ -> ""
    cmd, str root "cwd"
  with _ -> "", ""

let rec private hasProjectAtOrAbove (dir: DirectoryInfo) =
  match dir with
  | null -> false
  | d ->
    let found =
      try
        [ "*.fsproj"; "*.slnx"; "*.sln" ]
        |> List.exists (fun pattern -> d.EnumerateFiles(pattern).GetEnumerator().MoveNext())
      with _ -> false
    found || hasProjectAtOrAbove d.Parent

let private projectScope (cwd: string) =
  let dir = if String.IsNullOrWhiteSpace cwd then Environment.CurrentDirectory else cwd
  match Directory.Exists dir && hasProjectAtOrAbove (DirectoryInfo dir) with
  | true -> FSharpWorkspace
  | false -> NotFSharpWorkspace

let private mcpPort () =
  match Int32.TryParse(Environment.GetEnvironmentVariable "SAGEFS_MCP_PORT") with
  | true, p -> p
  | _ -> defaultMcpPort

/// `GET /health`'s own `sessionStates` array already carries every session's `workingDirectory` and lifecycle
/// `status`; this parses just those two fields into `SessionSummary`s for `sessionProbe`, best-effort: a shape
/// it does not recognise yields `[]`, never an exception, so a daemon that answered health at all is never
/// mistaken for a parse failure.
let private parseSessionSummaries (healthBody: string) : SessionSummary list =
  try
    use doc = JsonDocument.Parse healthBody
    match doc.RootElement.TryGetProperty "sessionStates" with
    | true, states when states.ValueKind = JsonValueKind.Array ->
      states.EnumerateArray()
      |> Seq.map (fun s -> { WorkingDirectory = str s "workingDirectory"; Status = str s "status" })
      |> List.ofSeq
    | _ -> []
  with _ -> []

/// Probes `/health` once and reports both whether the daemon answered and what it said about its sessions:
/// one HTTP call feeds both `DaemonProbe` and `SessionProbe`, instead of a second round trip to `/api/sessions`.
let private healthProbe () : bool * SessionSummary list =
  try
    use http = new HttpClient(Timeout = healthTimeout)
    use resp = http.GetAsync(sprintf "http://localhost:%d/health" (mcpPort ())).GetAwaiter().GetResult()
    match resp.IsSuccessStatusCode with
    | false -> false, []
    | true ->
      let body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
      true, parseSessionSummaries body
  with _ -> false, []

/// Ask the daemon for an expensive-work lease for THIS command's own final gate. Best-effort and fail-open:
/// any transport or parse failure here reads as "could not ask" (`None`), never as a refusal.
let private leaseProbe (verb: SlowVerb) : LeaseProbe option =
  try
    let kind =
      match verb with
      | Build -> "full_build"
      | Test -> "test_suite_run"
      | Run -> "run_app"
      | Fsi -> "full_build"
    // Identifies THIS caller across the Wait -> retry sequence a real agent performs: the same holder for the
    // same shell session, so a retry is recognised as the same ask.
    let holder =
      sprintf "dotnet-cli:%s:%d" (Environment.GetEnvironmentVariable "USER" |> Option.ofObj |> Option.defaultValue "unknown") Environment.ProcessId
    use http = new HttpClient(Timeout = leaseTimeout)
    let body = JsonSerializer.Serialize {| holder = holder; kind = kind |}
    use content = new StringContent(body, Text.Encoding.UTF8, "application/json")
    use resp = http.PostAsync(sprintf "http://localhost:%d/api/lease/request" (mcpPort ()), content).GetAwaiter().GetResult()
    match resp.IsSuccessStatusCode with
    | false -> None
    | true ->
      let text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
      use doc = JsonDocument.Parse text
      let root = doc.RootElement
      match root.GetProperty("decision").GetString() with
      | "granted" -> Some LeaseGranted
      | "wait" ->
        let retryAfter = root.GetProperty("retryAfterSeconds").GetDouble()
        let reason = root.GetProperty("reason").GetString()
        Some(LeaseMustWait(retryAfter, reason))
      | "refused" -> Some(LeaseRefused(root.GetProperty("reason").GetString()))
      | _ -> None
  with _ -> None

/// The hook's whole run, given the stdin text; the JSON to print when it denies, nothing when it allows.
let verdict (input: string) : string option =
  let command, cwd = parseInput input
  match needsContext command with
  | false -> None
  | true ->
    match projectScope cwd with
    | NotFSharpWorkspace -> None
    | FSharpWorkspace ->
      match healthProbe () with
      | false, _ -> None
      | true, sessions ->
        let session = Some(sessionProbe cwd sessions)
        let lease =
          match classify command with
          | DeclaredFinalGate verb -> leaseProbe verb
          | SlowLoop _
          | NoSlowLoop -> None
        match decide command { Project = FSharpWorkspace; Daemon = DaemonAnswering; Session = session; Lease = lease } with
        | Allow -> None
        | Deny reason ->
          let out =
            {| hookSpecificOutput =
                 {| hookEventName = "PreToolUse"
                    permissionDecision = "deny"
                    permissionDecisionReason = reason |} |}
          Some(JsonSerializer.Serialize out)

/// stdin as text, read straight off the stream: the Console reader sets up encodings first, which is startup
/// this hook pays on every Bash call.
let private readStdin () : string =
  use reader = new StreamReader(Console.OpenStandardInput(), Text.UTF8Encoding(false))
  reader.ReadToEnd()

[<EntryPoint>]
let main _ =
  // A failure inside the hook never blocks the agent: it is a non-blocking hook error and the command runs.
  let input = readStdin ()
  // Every command that never mentions dotnet is allowed here, before any JSON is parsed.
  match input.Contains("dotnet", StringComparison.Ordinal) with
  | false -> ()
  | true -> verdict input |> Option.iter (fun json -> Console.Out.Write json)
  0
