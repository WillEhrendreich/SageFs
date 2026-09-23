// Claude Code PreToolUse hook for the Bash tool. Reads the hook JSON on
// stdin, and denies `dotnet build/test/run/fsi` in an F# repo while a SageFs
// daemon is up, pointing the agent back at the REPL loop.
//
// The decision is ReplGuard.decide, the same file SageFs.Tests tests. This
// script is only the IO around it: stdin, the disk walk, the /health probe,
// the JSON out. Run it through the sagefs-repl-guard wrapper, which skips
// dotnet fsi entirely for commands that never mention dotnet.

#load "ReplGuard.fs"

open System
open System.IO
open System.Net.Http
open System.Text.Json
open SageFs.AgentHooks.ReplGuard

let private str (el: JsonElement) (name: string) =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
  | _ -> ""

let input = Console.In.ReadToEnd()

let command, cwd =
  try
    use doc = JsonDocument.Parse input
    let root = doc.RootElement
    let cmd =
      match root.TryGetProperty "tool_input" with
      | true, ti when ti.ValueKind = JsonValueKind.Object -> str ti "command"
      | _ -> ""
    cmd, str root "cwd"
  with _ -> "", ""

let rec hasProjectAtOrAbove (dir: DirectoryInfo) =
  match dir with
  | null -> false
  | d ->
    let found =
      try
        [ "*.fsproj"; "*.slnx"; "*.sln" ]
        |> List.exists (fun pattern -> d.EnumerateFiles(pattern).GetEnumerator().MoveNext())
      with _ -> false
    found || hasProjectAtOrAbove d.Parent

let projectScope () =
  let dir = if String.IsNullOrWhiteSpace cwd then Environment.CurrentDirectory else cwd
  match Directory.Exists dir && hasProjectAtOrAbove (DirectoryInfo dir) with
  | true -> FSharpWorkspace
  | false -> NotFSharpWorkspace

let mcpPort () =
  match Int32.TryParse(Environment.GetEnvironmentVariable "SAGEFS_MCP_PORT") with
  | true, p -> p
  | _ -> 37749

/// `GET /health`'s own `sessionStates` array already carries every session's
/// `workingDirectory` and lifecycle `status` — this parses just those two
/// fields into `SessionSummary`s for `sessionProbe`, best-effort: a shape it
/// doesn't recognize yields `[]`, never an exception, so a daemon that
/// answered health at all is never mistaken for a parse failure.
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

/// Probes `/health` once and reports both whether the daemon answered and
/// what it said about its sessions — one HTTP call feeds both `DaemonProbe`
/// and `SessionProbe`, instead of a second round trip to `/api/sessions`.
let healthProbe () : bool * SessionSummary list =
  try
    use http = new HttpClient(Timeout = TimeSpan.FromMilliseconds 800.0)
    use resp = http.GetAsync(sprintf "http://localhost:%d/health" (mcpPort ())).GetAwaiter().GetResult()
    match resp.IsSuccessStatusCode with
    | false -> false, []
    | true ->
      let body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
      true, parseSessionSummaries body
  with _ -> false, []

/// Ask the daemon for an expensive-work lease for THIS command's own final
/// gate. Best-effort and fail-open: any transport/parse failure here must
/// read as "couldn't ask" (`None`), never as a refusal — a hook that can't
/// reach the daemon must not block (see the module doc comment at the top
/// of this file and ReplGuard.decide's own contract).
let leaseProbe (verb: SlowVerb) : LeaseProbe option =
  try
    let kind =
      match verb with
      | Build -> "full_build"
      | Test -> "test_suite_run"
      | Run -> "run_app"
      | Fsi -> "full_build"
    let holder =
      // Identifies THIS caller across the Wait -> retry sequence a real
      // agent would perform. Not a fresh id per call: the same holder for
      // the same shell session, so a retry is recognized as the same ask.
      sprintf "dotnet-cli:%s:%d" (Environment.GetEnvironmentVariable "USER" |> Option.ofObj |> Option.defaultValue "unknown") (Environment.ProcessId)
    use http = new HttpClient(Timeout = TimeSpan.FromMilliseconds 1500.0)
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

if needsContext command then
  match projectScope () with
  | NotFSharpWorkspace -> ()
  | FSharpWorkspace ->
    match healthProbe () with
    | false, _ -> ()
    | true, sessions ->
      let session = Some(sessionProbe cwd sessions)
      let lease =
        match classify command with
        | DeclaredFinalGate verb -> leaseProbe verb
        | SlowLoop _
        | NoSlowLoop -> None
      match decide command { Project = FSharpWorkspace; Daemon = DaemonAnswering; Session = session; Lease = lease } with
      | Allow -> ()
      | Deny reason ->
        let out =
          {| hookSpecificOutput =
               {| hookEventName = "PreToolUse"
                  permissionDecision = "deny"
                  permissionDecisionReason = reason |} |}
        Console.Out.Write(JsonSerializer.Serialize out)
