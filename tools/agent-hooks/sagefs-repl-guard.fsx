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

let input = Console.In.ReadToEnd()

let command, cwd =
  try
    use doc = JsonDocument.Parse input
    let root = doc.RootElement
    let str (el: JsonElement) (name: string) =
      match el.TryGetProperty name with
      | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
      | _ -> ""
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

let daemonProbe () =
  let port =
    match Int32.TryParse(Environment.GetEnvironmentVariable "SAGEFS_MCP_PORT") with
    | true, p -> p
    | _ -> 37749
  try
    use http = new HttpClient(Timeout = TimeSpan.FromMilliseconds 800.0)
    use resp = http.GetAsync(sprintf "http://localhost:%d/health" port).GetAwaiter().GetResult()
    match resp.IsSuccessStatusCode with
    | true -> DaemonAnswering
    | false -> DaemonNotAnswering
  with _ -> DaemonNotAnswering

if needsContext command then
  // Only probe once we know the command is a slow-loop dotnet call.
  match projectScope () with
  | NotFSharpWorkspace -> ()
  | FSharpWorkspace ->
    match decide command { Project = FSharpWorkspace; Daemon = daemonProbe () } with
    | Allow -> ()
    | Deny reason ->
      let out =
        {| hookSpecificOutput =
             {| hookEventName = "PreToolUse"
                permissionDecision = "deny"
                permissionDecisionReason = reason |} |}
      Console.Out.Write(JsonSerializer.Serialize out)
