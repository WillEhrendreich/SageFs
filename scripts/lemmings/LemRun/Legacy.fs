/// The older harness, for Claude Code: `run-lemming` and `score`. Still here, still working the way
/// it did. It runs the PUBLISHED `sagefs` on a port and data directory of its own, never the shared
/// daemon on 37749, so it can be compared with the cmdc harness (CmdRun.fs), which is the one the
/// README describes.
///
/// Isolation, and what it does and does not guarantee:
///   - The agent runs under bubblewrap. It can read and write only its own run directory and the
///     toolchain (dotnet, the tool store, mise installs, the nuget cache). It cannot see the rest of
///     the home directory, this repo, or any other run.
///   - Its Claude Code config is an empty directory plus the credentials file, with project
///     settings only, CLAUDE.md discovery and auto-memory off, and only the project's MCP config.
///   - The SageFs daemon is the published tool, started by `sagefs mcp` exactly as a new user's
///     client would, on its own port and its own data directory.
///   - Teardown is graceful first (`sagefs stop`), forced only if that fails, and the two are
///     recorded separately.
///   - The network is NOT isolated: the model API and localhost are reachable.
module LemRun.Legacy

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open LemRun.Failure

// ---- score: events.ndjson to summary.json -------------------------------------------------------

/// What the harness measured outside the model, alongside its event stream.
type Measured =
  { Id: string
    Model: string
    ClaudeExit: int
    Seconds: int
    Graceful: string
    Forced: string
    FixtureTests: string
    ChangedFiles: string list
    /// Sessions the dashboard API listed before teardown, or None when it could not be read.
    ResidueSessions: int option }

let private prop (name: string) (el: JsonElement) : JsonElement option =
  match el.ValueKind with
  | JsonValueKind.Object ->
    match el.TryGetProperty name with
    | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
    | _ -> None
  | _ -> None

let private str (name: string) (el: JsonElement) : string option =
  match prop name el with
  | Some v when v.ValueKind = JsonValueKind.String -> Some (v.GetString() |> Option.ofObj |> Option.defaultValue "")
  | _ -> None

let private items (name: string) (el: JsonElement) : JsonElement list =
  match prop name el with
  | Some v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> Seq.toList
  | _ -> []

/// jq's `.[0:n]` on a string counts code points.
let private clip (limit: int) (text: string) : string =
  let runes = text.EnumerateRunes() |> Seq.toArray
  match runes.Length > limit with
  | true -> runes |> Array.take limit |> Array.map _.ToString() |> String.concat ""
  | false -> text

let private typeOf (el: JsonElement) = str "type" el |> Option.defaultValue ""

/// The content blocks of a message of the given event type.
let private blocks (eventType: string) (blockType: string) (events: JsonElement list) : JsonElement list =
  events
  |> List.filter (fun e -> typeOf e = eventType)
  |> List.collect (fun e -> prop "message" e |> Option.map (items "content") |> Option.defaultValue [])
  |> List.filter (fun b -> typeOf b = blockType)

/// A tool result's text: a string, or the texts of its blocks joined with a space.
let private resultText (content: JsonElement option) : string =
  match content with
  | Some c when c.ValueKind = JsonValueKind.String -> c.GetString() |> Option.ofObj |> Option.defaultValue ""
  | Some c when c.ValueKind = JsonValueKind.Array -> c.EnumerateArray() |> Seq.map (fun b -> str "text" b |> Option.defaultValue "") |> String.concat " "
  | _ -> ""

let private dotnetCommand = Regex(@"dotnet (build|test|run|fsi|msbuild)", RegexOptions.Compiled)
let private sleepCommand = Regex(@"(^|[ ;&|])sleep ", RegexOptions.Compiled)

/// summary.json for one Claude Code run, read off the stream and the harness's own checks. Nothing
/// in it is the model's claim about itself.
let summary (events: JsonElement list) (m: Measured) : string =
  let init = events |> List.tryFind (fun e -> typeOf e = "system" && str "subtype" e = Some "init")
  let result = events |> List.tryFind (fun e -> typeOf e = "result")
  let uses = blocks "assistant" "tool_use" events
  let results = blocks "user" "tool_result" events
  let nameOf (u: JsonElement) = str "name" u |> Option.defaultValue ""
  let bash =
    uses |> List.filter (fun u -> nameOf u = "Bash") |> List.map (fun u -> prop "input" u |> Option.bind (str "command") |> Option.defaultValue "")
  let errors = results |> List.filter (fun r -> match prop "is_error" r with Some v -> v.ValueKind = JsonValueKind.True | None -> false)
  let tools = init |> Option.map (items "tools") |> Option.defaultValue []
  let toolNames = tools |> List.choose (fun t -> if t.ValueKind = JsonValueKind.String then t.GetString() |> Option.ofObj else None)
  let sagefsPrefix = "mcp__sagefs__"
  use stream = new MemoryStream()
  use w = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true, Encoder = Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
  let strings (name: string) (values: string seq) =
    w.WriteStartArray name
    values |> Seq.iter w.WriteStringValue
    w.WriteEndArray()
  w.WriteStartObject()
  w.WriteString("id", m.Id)
  w.WriteString("model", m.Model)
  w.WriteString("outcome", result |> Option.bind (str "subtype") |> Option.defaultValue "no-result-event")
  w.WriteNumber("claudeExit", m.ClaudeExit)
  w.WriteNumber("seconds", m.Seconds)
  (match result |> Option.bind (prop "num_turns") with
   | Some v -> w.WritePropertyName "turns"; v.WriteTo w
   | None -> w.WriteNull "turns")
  (match result |> Option.bind (prop "total_cost_usd") with
   | Some v -> w.WritePropertyName "costUsd"; v.WriteTo w
   | None -> w.WriteNull "costUsd")
  w.WriteStartObject "isolation"
  strings "mcpServers" (init |> Option.map (items "mcp_servers") |> Option.defaultValue [] |> List.map (fun s -> sprintf "%s:%s" (str "name" s |> Option.defaultValue "null") (str "status" s |> Option.defaultValue "null")))
  w.WriteNumber("toolCount", tools.Length)
  strings "nonSageFsMcpTools" (toolNames |> List.filter (fun t -> t.StartsWith "mcp__" && not (t.StartsWith sagefsPrefix)))
  strings "skills" (init |> Option.map (items "skills") |> Option.defaultValue [] |> List.choose (fun s -> if s.ValueKind = JsonValueKind.String then s.GetString() |> Option.ofObj else None))
  strings "plugins" (init |> Option.map (items "plugins") |> Option.defaultValue [] |> List.map (fun p -> p.GetRawText()))
  w.WriteEndObject()
  (match uses with
   | [] -> w.WriteNull "tools"
   | _ ->
     w.WriteStartObject "tools"
     uses |> List.groupBy nameOf |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b)) |> List.iter (fun (name, group) -> w.WriteNumber(name, group.Length))
     w.WriteEndObject())
  strings "sagefsCalls" (uses |> List.map nameOf |> List.filter (fun n -> n.StartsWith sagefsPrefix) |> List.map (fun n -> n.Substring sagefsPrefix.Length))
  (match uses |> List.tryFindIndex (fun u -> nameOf u = sagefsPrefix + "send_fsharp_code") with
   | Some i -> w.WriteNumber("firstEvalAtCall", i)
   | None -> w.WriteNull "firstEvalAtCall")
  strings "bashDotnet" (bash |> List.filter dotnetCommand.IsMatch)
  strings "bashSleeps" (bash |> List.filter sleepCommand.IsMatch)
  w.WriteNumber("bashOther", bash |> List.filter (fun c -> not (dotnetCommand.IsMatch c) && not (sleepCommand.IsMatch c)) |> List.length)
  w.WriteNumber("toolErrors", errors.Length)
  strings "firstErrors" (errors |> List.map (fun r -> clip 220 (resultText (prop "content" r))) |> List.truncate 4)
  w.WriteString("finalText", result |> Option.bind (str "result") |> Option.defaultValue "" |> clip 900)
  w.WriteStartObject "verification"
  strings "changedFiles" m.ChangedFiles
  w.WriteString("fixtureTests", m.FixtureTests)
  w.WriteEndObject()
  (match m.ResidueSessions with
   | Some n -> w.WriteNumber("residueSessionsBeforeTeardown", n)
   | None -> w.WriteNull "residueSessionsBeforeTeardown")
  w.WriteStartObject "teardown"
  w.WriteString("graceful", m.Graceful)
  w.WriteString("forced", m.Forced)
  w.WriteEndObject()
  w.WriteEndObject()
  w.Flush()
  Encoding.UTF8.GetString(stream.ToArray())

/// The ids a residue file lists: an array, or an object with `sessions`, or nothing readable.
let residueCount (json: string) : int option =
  try
    use doc = JsonDocument.Parse json
    match doc.RootElement.ValueKind with
    | JsonValueKind.Array -> Some (doc.RootElement.GetArrayLength())
    | JsonValueKind.Object -> Some (items "sessions" doc.RootElement |> List.length)
    | _ -> Some 0
  with :? JsonException -> None

/// `score <events.ndjson> <residue.sessions.json> --id ID --model M --exit N --seconds N --graceful X
/// --forced X --tests X --diff FILE`: the summary on stdout.
let scoreCommand (argv: string list) : int =
  match argv with
  | eventsFile :: residueFile :: rest ->
    let flags =
      rest |> List.chunkBySize 2 |> List.choose (function [ k; v ] when k.StartsWith "--" -> Some (k.Substring 2, v) | _ -> None) |> Map.ofList
    let get key = Map.tryFind key flags |> Option.defaultValue ""
    let events =
      File.ReadAllLines eventsFile
      |> Array.filter (fun l -> l.Trim() <> "")
      |> Array.map (fun l -> JsonDocument.Parse(l).RootElement.Clone())
      |> Array.toList
    let changed =
      match Map.tryFind "diff" flags with
      | Some diff when File.Exists diff ->
        File.ReadAllLines diff
        |> Array.filter (fun l -> l.StartsWith "< " || l.StartsWith "> ")
        |> Array.map (fun l -> Regex.Replace(l, "^[<>] [0-9a-f]+  ", ""))
        |> Array.distinct
        |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> Array.filter (fun l -> l <> "")
        |> Array.toList
      | _ -> []
    let int' (s: string) = match Int32.TryParse s with | true, n -> n | _ -> 0
    let residue = if File.Exists residueFile then residueCount (File.ReadAllText residueFile) else None
    printfn "%s" (summary events { Id = get "id"; Model = get "model"; ClaudeExit = int' (get "exit"); Seconds = int' (get "seconds"); Graceful = get "graceful"; Forced = get "forced"; FixtureTests = get "tests"; ChangedFiles = changed; ResidueSessions = residue })
    0
  | _ ->
    eprintfn "usage: score <events.ndjson> <residue.sessions.json> --id ID --model M --exit N --seconds N --graceful X --forced X --tests X --diff FILE"
    1

// ---- run-lemming ----------------------------------------------------------------------------------

/// A free pair of ports (the daemon's and the dashboard's) in a range of its own, never 37749.
let private firstPort = 38200
let private portSpan = 600
let private portAttempts = 200

let private listening (port: int) : bool =
  let c = Proc.run (Proc.spec "ss" [ "-ltn"; sprintf "( sport = :%d )" port ]) None
  c.Stdout.Contains "LISTEN"

let private freePortPair () : int =
  let rng = Random()
  let rec pick attempt =
    match attempt >= portAttempts with
    | true -> fail (ToolchainMissing "no free port pair")
    | false ->
      let p = firstPort + rng.Next portSpan
      match listening p || listening (p + 1) with
      | true -> pick (attempt + 1)
      | false -> p
  pick 0

/// A sha256 of every file the lemming could have changed, as `<hash>  ./<path>` lines in path order
/// (the shape of `sha256sum`), leaving out the skill, build output and the MCP file.
let private snapshot (workdir: string) : string list =
  let skip (relative: string) =
    let under = "./" + relative
    relative.StartsWith ".claude/" || under.Contains "/bin/" || under.Contains "/obj/" || Path.GetFileName relative = ".mcp.json"
  Directory.EnumerateFiles(workdir, "*", SearchOption.AllDirectories)
  |> Seq.map (fun f -> Path.GetRelativePath(workdir, f))
  |> Seq.filter (skip >> not)
  |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b))
  |> Seq.map (fun relative ->
    use stream = File.OpenRead(Path.Combine(workdir, relative))
    sprintf "%s  ./%s" (Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()) relative)
  |> Seq.toList

/// Lines that are in one snapshot and not the other, as `< ` and `> ` lines.
let private difference (before: string list) (after: string list) : string list =
  let b, a = Set.ofList before, Set.ofList after
  (before |> List.filter (fun l -> not (a.Contains l)) |> List.map (fun l -> "< " + l))
  @ (after |> List.filter (fun l -> not (b.Contains l)) |> List.map (fun l -> "> " + l))

let private mcpJson (port: int) (dataDir: string) : string =
  sprintf "{ \"mcpServers\": { \"sagefs\": { \"command\": \"sagefs\", \"args\": [\"mcp\"],\n  \"env\": { \"SAGEFS_MCP_PORT\": \"%d\", \"SAGEFS_DATA_DIR\": \"%s\" } } } }\n" port dataDir

let private claudeTimeoutSeconds = 1500
let private maxBudgetUsd = 4
let private stopSeconds = 60
let private settle = TimeSpan.FromSeconds 2.0
let private suiteSeconds = 300

/// `run-lemming <fixture> <task> <model> <run-id> [max-turns]`
let runCommand (argv: string list) : int =
  match argv with
  | fixture :: task :: model :: id :: rest ->
    let turns = match rest with [ t ] -> t | _ -> "60"
    let run = Path.Combine(Env.lemRoot, id)
    if Directory.Exists run || File.Exists run then fail (Refused (sprintf "run %s already exists: pick a new id" id))
    let w, cfg, data, out = Path.Combine(run, "w"), Path.Combine(run, "cfg"), Path.Combine(run, "data"), Path.Combine(run, "out")
    for d in [ w; cfg; data; out; Path.Combine(run, "dotnethome") ] do Directory.CreateDirectory d |> ignore
    // The workspace: the fixture, the skill installed where a user installs it, and the MCP
    // registration a user would write.
    let fixtureDir = Path.Combine(Env.lemDir, "fixtures", fixture)
    (match Store.copyTree fixtureDir w with Ok () -> () | Error e -> fail (Refused e))
    Directory.CreateDirectory(Path.Combine(w, ".claude", "skills")) |> ignore
    (match Store.copyTree (Path.Combine(Env.repoRoot, "skills", "sagefs")) (Path.Combine(w, ".claude", "skills", "sagefs")) with Ok () -> () | Error e -> fail (Refused e))
    let port = freePortPair ()
    File.WriteAllText(Path.Combine(w, ".mcp.json"), mcpJson port data)
    let before = snapshot w
    File.WriteAllLines(Path.Combine(out, "files.before"), before)
    let prompt = File.ReadAllText(Path.Combine(Env.lemDir, "tasks", task)).TrimEnd('\n')
    let home = Env.home
    let path = sprintf "%s/.dotnet:%s/.dotnet/tools:%s/.local/share/mise/installs/claude/latest:%s/.local/share/mise/installs/node/26.7.0/bin:/usr/bin:/bin" home home home home
    let args =
      [ "--die-with-parent"
        "--ro-bind"; "/usr"; "/usr"; "--ro-bind"; "/etc"; "/etc"
        "--symlink"; "usr/bin"; "/bin"; "--symlink"; "usr/sbin"; "/sbin"; "--symlink"; "usr/lib"; "/lib"; "--symlink"; "usr/lib64"; "/lib64"
        "--ro-bind"; "/run/systemd/resolve"; "/run/systemd/resolve"
        "--proc"; "/proc"; "--dev"; "/dev"; "--tmpfs"; "/tmp"; "--tmpfs"; home
        "--ro-bind"; home + "/.dotnet"; home + "/.dotnet"
        "--ro-bind"; home + "/.local/share/mise"; home + "/.local/share/mise"
        "--bind"; home + "/.nuget"; home + "/.nuget"
        "--bind"; run; run
        "--bind"; home + "/.claude/.credentials.json"; cfg + "/.credentials.json"
        "--chdir"; w
        "--setenv"; "HOME"; home
        "--setenv"; "DOTNET_ROOT"; home + "/.dotnet"
        "--setenv"; "DOTNET_CLI_HOME"; run + "/dotnethome"
        "--setenv"; "DOTNET_CLI_TELEMETRY_OPTOUT"; "1"; "--setenv"; "DOTNET_NOLOGO"; "1"; "--setenv"; "DOTNET_SKIP_FIRST_TIME_EXPERIENCE"; "1"
        "--setenv"; "PATH"; path
        "--setenv"; "CLAUDE_CONFIG_DIR"; cfg
        "--setenv"; "CLAUDE_CODE_DISABLE_CLAUDE_MDS"; "1"; "--setenv"; "CLAUDE_CODE_DISABLE_AUTO_MEMORY"; "1"
        "timeout"; string claudeTimeoutSeconds; "claude"; "-p"; prompt
        "--model"; model; "--max-turns"; turns; "--max-budget-usd"; string maxBudgetUsd
        "--output-format"; "stream-json"; "--verbose"
        "--strict-mcp-config"; "--mcp-config"; Path.Combine(w, ".mcp.json")
        "--setting-sources"; "project"; "--no-session-persistence"; "--dangerously-skip-permissions" ]
    let clock = Stopwatch.StartNew()
    let claudeExit = Proc.runToFiles (Proc.spec "bwrap" args) (Path.Combine(out, "events.ndjson")) (Path.Combine(out, "claude.stderr"))
    clock.Stop()
    // Residue: what the agent left running, read BEFORE any teardown. The sessions list is served on
    // the MCP port; the dashboard port answers 404 for it.
    let residueFile = Path.Combine(out, "residue.sessions.json")
    let sessions = Proc.run (Proc.spec "curl" [ "-s"; "--max-time"; "5"; sprintf "http://localhost:%d/api/sessions" port ]) None
    File.WriteAllText(residueFile, (match sessions.ExitCode, sessions.Stdout with | 0, body when body.Trim() <> "" -> body | _ -> "null"))
    // Teardown, graceful first.
    let sagefsEnv = Proc.spec "sagefs" [ "stop" ] |> Proc.withEnv "SAGEFS_MCP_PORT" (string port) |> Proc.withEnv "SAGEFS_DATA_DIR" data
    let stop = Proc.run sagefsEnv (Some (TimeSpan.FromSeconds (float stopSeconds)))
    File.WriteAllText(Path.Combine(out, "stop.out"), stop.Stdout + stop.Stderr)
    let graceful = if stop.ExitCode = 0 then "stopped" else "stop-failed"
    Threading.Thread.Sleep settle
    let dash = port + 1
    let forced =
      match listening port || listening dash with
      | false -> "no"
      | true ->
        let ps = Proc.run (Proc.spec "ss" [ "-ltnp"; sprintf "( sport = :%d or sport = :%d )" port dash ]) None
        Regex.Matches(ps.Stdout, @"pid=(\d+)") |> Seq.map (fun m -> Int32.Parse m.Groups[1].Value) |> Seq.distinct |> Seq.iter (fun pid -> Proc.sendSignal pid Proc.Signal.term)
        Threading.Thread.Sleep settle
        "yes"
    // Verification the harness does itself, outside the sandbox: never the model's claim.
    let after = snapshot w
    File.WriteAllLines(Path.Combine(out, "files.after"), after)
    let diff = difference before after
    let diffFile = Path.Combine(out, "files.diff")
    File.WriteAllLines(diffFile, diff)
    let tests =
      match fixture with
      | "demoenv" ->
        let ran = Proc.run (Proc.spec "dotnet" [ "run"; "--project"; "DemoEnv.Tests" ] |> Proc.inDir w) (Some (TimeSpan.FromSeconds (float suiteSeconds)))
        File.WriteAllText(Path.Combine(out, "verify.tests.out"), ran.Stdout + ran.Stderr)
        if ran.ExitCode = 0 then "pass" else "fail"
      | _ -> "skipped"
    let events =
      File.ReadAllLines(Path.Combine(out, "events.ndjson"))
      |> Array.filter (fun l -> l.Trim() <> "")
      |> Array.map (fun l -> JsonDocument.Parse(l).RootElement.Clone())
      |> Array.toList
    let changed =
      diff |> List.map (fun l -> Regex.Replace(l, "^[<>] [0-9a-f]+  ", "")) |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
    let json =
      summary events
        { Id = id; Model = model; ClaudeExit = claudeExit; Seconds = int clock.Elapsed.TotalSeconds; Graceful = graceful; Forced = forced
          FixtureTests = tests; ChangedFiles = changed; ResidueSessions = residueCount (File.ReadAllText residueFile) }
    File.WriteAllText(Path.Combine(out, "summary.json"), json + "\n")
    printfn "%s" json
    0
  | _ -> fail (MissingArgument "usage: run-lemming <fixture> <task> <model> <run-id> [max-turns]")
