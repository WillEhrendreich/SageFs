// Oracles and run summaries for the Neovim UI lemmings.
//
// The harness runs these OUTSIDE every sandbox, after the lemming has stopped. They read
// what the harness itself recorded (the driver's call log, the numbered screens, the
// workspace, the shared daemon's own API) and never take the model's word for anything,
// except where the task is "tell me what you saw": then the answer is checked against a
// value the editor had to compute, and against a screen that showed that value.
module LemDrive.NvimOracle

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

// ---------------------------------------------------------------------------
// The tasks, closed.
// ---------------------------------------------------------------------------

type UiTask =
  | UiEval
  | UiEditReeval
  | UiLiveTests
  | UiHotReload
  | UiFindHelp

let taskName (task: UiTask) : string =
  match task with
  | UiEval -> "ui-eval"
  | UiEditReeval -> "ui-edit-reeval"
  | UiLiveTests -> "ui-live-tests"
  | UiHotReload -> "ui-hot-reload"
  | UiFindHelp -> "ui-find-help"

let allTasks = [ UiEval; UiEditReeval; UiLiveTests; UiHotReload; UiFindHelp ]

let tryParseTask (text: string) : Result<UiTask, string> =
  match allTasks |> List.tryFind (fun t -> taskName t = text) with
  | Some t -> Result.Ok t
  | None -> Result.Error(sprintf "'%s' is not a UI task (%s)" text (allTasks |> List.map taskName |> String.concat ", "))

/// The values the tasks are built around. Each is something the editor has to compute or
/// the lemming has to write, so it cannot be copied out of the prompt.
module Expect =
  /// ui-eval: the WindowSpec the project's own parseWindow builds, shown by the plugin.
  let EvalAnswer = @"Width\s*=\s*800"
  /// ui-edit-reeval and ui-live-tests: parseSeed (Some "-1") before and after the fix.
  let SeedBefore = "Some -1"
  let SeedAfter = "None"
  /// ui-hot-reload: the text the route answers with before and after the edit.
  let HelloBefore = "Hello from Falco"
  let HelloAfter = "Hello from Neovim"
  /// ui-find-help: how many real commands the lemming must name.
  let HelpCommandCount = 5

// ---------------------------------------------------------------------------
// Facts about a finished run, read from disk.
// ---------------------------------------------------------------------------

type Call =
  { N: int
    Cmd: string
    Arg: string
    Ok: bool
    Ms: int64
    Screen: string }

type Screen = { File: string; Text: string }

type DaemonSession =
  { Id: string
    WorkingDirectory: string
    Status: string
    EvalCount: int
    TestsEnabled: bool option
    TestsPassed: int
    TestsFailed: int
    TestsTotal: int }

type RunFacts =
  { RunDir: string
    Workspace: string
    FinalText: string
    Calls: Call list
    Screens: Screen list
    Sessions: DaemonSession list option
    ChangedFiles: string list
    PluginCommands: Set<string> }

type Check =
  { Name: string
    Passed: bool
    Detail: string }

let private check name passed detail = { Name = name; Passed = passed; Detail = detail }

let private readIfExists (path: string) : string option =
  if File.Exists path then Some(File.ReadAllText path) else None

let private jsonString (el: JsonElement) (name: string) : string =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj |> Option.defaultValue ""
  | _ -> ""

let private jsonInt (el: JsonElement) (name: string) : int =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
  | _ -> 0

let private jsonBool (el: JsonElement) (name: string) : bool option =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.True -> Some true
  | true, v when v.ValueKind = JsonValueKind.False -> Some false
  | _ -> None

/// The `finalText` of cmdc's closing `result` line.
let finalTextOf (events: string) : string =
  events.Split('\n')
  |> Array.rev
  |> Array.tryPick (fun line ->
    if line.StartsWith "{\"type\":\"result\"" then
      try
        use doc = JsonDocument.Parse line
        Some(jsonString doc.RootElement "finalText")
      with _ -> None
    else None)
  |> Option.defaultValue ""

let parseCalls (jsonl: string) : Call list =
  jsonl.Split('\n')
  |> Array.toList
  |> List.filter (fun l -> l.Trim().Length > 0)
  |> List.choose (fun line ->
    try
      use doc = JsonDocument.Parse line
      let e = doc.RootElement
      let ok = jsonBool e "ok" |> Option.defaultValue false
      Some
        { N = jsonInt e "n"
          Cmd = jsonString e "cmd"
          Arg = jsonString e "arg"
          Ok = ok
          Ms = (match e.TryGetProperty "ms" with | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt64() | _ -> 0L)
          Screen = jsonString e "screen" }
    with _ -> None)

let parseDaemonState (json: string) : DaemonSession list =
  try
    use doc = JsonDocument.Parse json
    match doc.RootElement.TryGetProperty "sessions" with
    | true, arr when arr.ValueKind = JsonValueKind.Array ->
      [ for s in arr.EnumerateArray() ->
          let tests = match s.TryGetProperty "liveTests" with | true, t -> t | _ -> s
          { Id = jsonString s "id"
            WorkingDirectory = jsonString s "workingDirectory"
            Status = jsonString s "status"
            EvalCount = jsonInt s "evalCount"
            TestsEnabled = jsonBool tests "enabled"
            TestsPassed = jsonInt tests "passed"
            TestsFailed = jsonInt tests "failed"
            TestsTotal = jsonInt tests "total" } ]
    | _ -> []
  with _ -> []

/// Two registration forms: a direct nvim_create_user_command call, and an entry in the
/// simple_commands table (`name = "SageFsEval"`).
let private pluginCommandPattern =
  Regex(@"(?:nvim_create_user_command\(\s*|name\s*=\s*)""(SageFs[A-Za-z]+)""", RegexOptions.Compiled)

/// Every user command the plugin registers, read from its source: the ground truth for
/// "list five commands that exist".
let pluginCommands (pluginDir: string) : Set<string> =
  let luaDir = Path.Combine(pluginDir, "lua")
  if Directory.Exists luaDir then
    Directory.EnumerateFiles(luaDir, "*.lua", SearchOption.AllDirectories)
    |> Seq.collect (fun f -> pluginCommandPattern.Matches(File.ReadAllText f) |> Seq.map (fun m -> m.Groups.[1].Value))
    |> Set.ofSeq
  else Set.empty

let private git (workspace: string) (args: string list) : string list =
  match Nvim.runProcess "git" ([ "-C"; workspace ] @ args) [] 30_000 with
  | Result.Ok o when o.ExitCode = 0 -> o.Output.Split('\n') |> Array.toList |> List.filter (fun l -> l.Trim().Length > 0)
  | _ -> []

let changedFiles (workspace: string) : string list =
  (git workspace [ "diff"; "--name-only"; "lem-baseline" ]) @ (git workspace [ "ls-files"; "--others"; "--exclude-standard" ])
  |> List.distinct
  |> List.sort

let loadFacts (runDir: string) (pluginDir: string) : RunFacts =
  let out = Path.Combine(runDir, "out")
  let screens =
    let dir = Path.Combine(out, "screens")
    if Directory.Exists dir then
      Directory.EnumerateFiles(dir, "*.txt") |> Seq.sort |> Seq.map (fun f -> { File = Path.GetFileName f; Text = File.ReadAllText f }) |> List.ofSeq
    else []
  { RunDir = runDir
    Workspace = Path.Combine(runDir, "w")
    FinalText = readIfExists (Path.Combine(out, "events.ndjson")) |> Option.map finalTextOf |> Option.defaultValue ""
    Calls = readIfExists (Path.Combine(out, "ui-calls.jsonl")) |> Option.map parseCalls |> Option.defaultValue []
    Screens = screens
    Sessions = readIfExists (Path.Combine(out, "daemon-state.json")) |> Option.map parseDaemonState
    ChangedFiles = changedFiles (Path.Combine(runDir, "w"))
    PluginCommands = pluginCommands pluginDir }

// ---------------------------------------------------------------------------
// The checks. Pure functions of RunFacts.
// ---------------------------------------------------------------------------

let private firstScreenWith (needle: string) (screens: Screen list) : int option =
  screens |> List.tryFindIndex (fun s -> s.Text.Contains needle)

let private screensAfter (index: int) (screens: Screen list) = screens |> List.skip (index + 1)

let answerContains (needle: string) (facts: RunFacts) : Check =
  check "the lemming's answer states the result"
    (Regex.IsMatch(facts.FinalText, needle))
    (sprintf "looking for '%s' in the final answer (%d chars)" needle facts.FinalText.Length)

let editorShowed (needle: string) (facts: RunFacts) : Check =
  check "the editor screen showed it"
    (facts.Screens |> List.exists (fun s -> Regex.IsMatch(s.Text, needle)))
    (sprintf "looking for '%s' in %d captured screens" needle facts.Screens.Length)

/// Commands the lemming ran may legitimately write a new file (an export, a notebook). What a
/// read-only task must not do is change a file the project already had.
let existingFilesUnchanged (facts: RunFacts) : Check =
  let modified = git facts.Workspace [ "diff"; "--name-only"; "lem-baseline" ]
  check "no existing file was modified" modified.IsEmpty (sprintf "modified: %s" (String.concat ", " modified))

let fileChanged (suffix: string) (facts: RunFacts) : Check =
  check (sprintf "%s changed" suffix)
    (facts.ChangedFiles |> List.exists (fun f -> f.EndsWith suffix))
    (sprintf "changed: %s" (String.concat ", " facts.ChangedFiles))

let fileContains (relativePath: string) (needle: string) (facts: RunFacts) : Check =
  let path = Path.Combine(facts.Workspace, relativePath)
  let has = File.Exists path && File.ReadAllText(path).Contains needle
  check (sprintf "%s now says '%s'" relativePath needle) has path

let testsUntouched (facts: RunFacts) : Check =
  let touched = facts.ChangedFiles |> List.filter (fun f -> f.Contains ".Tests/")
  check "the tests were not edited" touched.IsEmpty (sprintf "touched: %s" (String.concat ", " touched))

let usedTheEditor (facts: RunFacts) : Check =
  let keyCalls = facts.Calls |> List.filter (fun c -> c.Cmd = "keys" || c.Cmd = "nvim-type")
  check "the lemming drove the editor" (not keyCalls.IsEmpty) (sprintf "%d key calls of %d driver calls" keyCalls.Length facts.Calls.Length)

let beforeThenAfter (before: string) (after: string) (facts: RunFacts) : Check =
  match firstScreenWith before facts.Screens with
  | None -> check "the screens show the result before and after the change" false (sprintf "no screen ever showed '%s'" before)
  | Some i ->
    let later = screensAfter i facts.Screens |> List.exists (fun s -> s.Text.Contains after)
    check "the screens show the result before and after the change" later
      (sprintf "'%s' first on screen %d; '%s' afterwards: %b" before (i + 1) after later)

let liveTestsGreen (facts: RunFacts) : Check =
  match facts.Sessions with
  | None -> check "the daemon reports the tests green" false "no daemon-state.json: the harness could not read the daemon before cleanup"
  | Some [] -> check "the daemon reports the tests green" false "the daemon had no session under the run directory"
  | Some sessions ->
    let green = sessions |> List.exists (fun s -> s.TestsEnabled = Some true && s.TestsFailed = 0 && s.TestsPassed > 0)
    check "the daemon reports the tests green" green
      (sessions |> List.map (fun s -> sprintf "%s enabled=%A passed=%d failed=%d total=%d" s.Id s.TestsEnabled s.TestsPassed s.TestsFailed s.TestsTotal) |> String.concat "; ")

let sessionAppeared (facts: RunFacts) : Check =
  match facts.Sessions with
  | None -> check "a session appeared in the shared daemon" false "no daemon-state.json"
  | Some sessions ->
    check "a session appeared in the shared daemon" (not sessions.IsEmpty)
      (sprintf "%d session(s) under the run directory" sessions.Length)

let shellCallsContaining (needle: string) (facts: RunFacts) (screens: Map<string, string>) : Call list =
  facts.Calls
  |> List.filter (fun c ->
    c.Cmd = "nvim-shell" && c.Arg.StartsWith "curl" && c.Ok
    && (match Map.tryFind c.Screen screens with Some t -> t.Contains needle | None -> false))

let hotReloadServed (facts: RunFacts) : Check list =
  let byPath = facts.Screens |> List.map (fun s -> "screens/" + s.File, s.Text) |> Map.ofList
  let before = shellCallsContaining Expect.HelloBefore facts byPath
  let after = shellCallsContaining Expect.HelloAfter facts byPath
  let firstAfter = after |> List.tryHead |> Option.map (fun c -> c.N)
  let firstBefore = before |> List.tryHead |> Option.map (fun c -> c.N)
  [ check "curl served the old text before the edit" (not before.IsEmpty) (sprintf "%d curl call(s) showed '%s'" before.Length Expect.HelloBefore)
    check "curl served the new text after the edit"
      (match firstBefore, firstAfter with
       | Some b, Some a -> a > b
       | _ -> false)
      (sprintf "first old at call %A, first new at call %A" firstBefore firstAfter) ]

let private commandMention = Regex(@":?(SageFs[A-Za-z]+)", RegexOptions.Compiled)

let namedRealCommands (facts: RunFacts) : Check =
  let named = commandMention.Matches facts.FinalText |> Seq.map (fun m -> m.Groups.[1].Value) |> Set.ofSeq
  let real = Set.intersect named facts.PluginCommands
  let invented = Set.difference named facts.PluginCommands
  check (sprintf "names %d commands that exist in the plugin" Expect.HelpCommandCount)
    (real.Count >= Expect.HelpCommandCount)
    (sprintf "real: %s; not in the plugin: %s" (String.concat ", " real) (String.concat ", " invented))

let verdictFor (task: UiTask) (facts: RunFacts) : Check list =
  match task with
  | UiEval ->
    [ usedTheEditor facts
      answerContains Expect.EvalAnswer facts
      editorShowed Expect.EvalAnswer facts
      sessionAppeared facts
      existingFilesUnchanged facts ]
  | UiEditReeval ->
    [ usedTheEditor facts
      fileChanged "DemoEnv/DemoEnv.fs" facts
      testsUntouched facts
      beforeThenAfter Expect.SeedBefore Expect.SeedAfter facts
      answerContains Expect.SeedAfter facts
      sessionAppeared facts ]
  | UiLiveTests ->
    [ usedTheEditor facts
      fileChanged "DemoEnv/DemoEnv.fs" facts
      testsUntouched facts
      liveTestsGreen facts ]
  | UiHotReload ->
    [ usedTheEditor facts
      fileChanged "Program.fs" facts
      fileContains "Program.fs" Expect.HelloAfter facts
      sessionAppeared facts
      yield! hotReloadServed facts ]
  | UiFindHelp ->
    [ usedTheEditor facts
      namedRealCommands facts
      existingFilesUnchanged facts ]

let formatVerdict (checks: Check list) : string =
  checks
  |> List.map (fun c -> sprintf "%s  %s  (%s)" (if c.Passed then "PASS" else "FAIL") c.Name c.Detail)
  |> String.concat "\n"

// ---------------------------------------------------------------------------
// What fell over, from the screens and the call log. A closed set of rules.
// ---------------------------------------------------------------------------

type Symptom =
  | DriverRefused
  | LuaError
  | PluginUnreachable
  | StuckAtPrompt
  | NeverConnected
  | NoSession

let symptomText (s: Symptom) : string =
  match s with
  | DriverRefused -> "the driver refused a command"
  | LuaError -> "Neovim showed a Lua error"
  | PluginUnreachable -> "the plugin said SageFs is not available"
  | StuckAtPrompt -> "Neovim sat at a hit-enter or more prompt across several calls"
  | NeverConnected -> "the status line never showed the plugin connected"
  | NoSession -> "no session ever appeared in the shared daemon"

type Finding =
  { Symptom: Symptom
    Evidence: string }

let private luaErrorMarkers = [ "Error executing Lua"; "E5108"; "E5113"; "stack traceback"; "attempt to index"; "attempt to call" ]
let private stuckPromptRun = 3
let private connectedMarkers = [ "⚡"; "SageFs [" ]

let private needsSession (task: UiTask option) : bool =
  match task with
  | Some UiFindHelp -> false
  | Some _ | None -> true

let findings (task: UiTask option) (facts: RunFacts) : Finding list =
  let refused =
    facts.Calls
    |> List.filter (fun c -> not c.Ok)
    |> List.truncate 3
    |> List.map (fun c -> { Symptom = DriverRefused; Evidence = sprintf "call %d: %s %s" c.N c.Cmd c.Arg })
  let lua =
    facts.Screens
    |> List.tryFind (fun s -> luaErrorMarkers |> List.exists s.Text.Contains)
    |> Option.map (fun s -> { Symptom = LuaError; Evidence = sprintf "screens/%s" s.File })
    |> Option.toList
  let unreachable =
    facts.Screens
    |> List.tryFind (fun s -> s.Text.Contains "SageFs not available on port")
    |> Option.map (fun s -> { Symptom = PluginUnreachable; Evidence = sprintf "screens/%s" s.File })
    |> Option.toList
  let stuck =
    facts.Screens
    |> List.map (fun s -> s.Text.Contains "HIT-ENTER PROMPT" || s.Text.Contains "MORE PROMPT")
    |> List.windowed stuckPromptRun
    |> List.tryFindIndex (List.forall id)
    |> Option.map (fun i -> { Symptom = StuckAtPrompt; Evidence = sprintf "screens %d to %d" (i + 1) (i + stuckPromptRun) })
    |> Option.toList
  let connected = facts.Screens |> List.exists (fun s -> connectedMarkers |> List.exists s.Text.Contains)
  let never =
    if facts.Screens.IsEmpty || connected then []
    else [ { Symptom = NeverConnected; Evidence = sprintf "%d screens, none with a connected status" facts.Screens.Length } ]
  let noSession =
    match facts.Sessions with
    | Some [] when needsSession task && not facts.Calls.IsEmpty -> [ { Symptom = NoSession; Evidence = "daemon-state.json lists no session under the run directory" } ]
    | _ -> []
  refused @ lua @ unreachable @ stuck @ never @ noSession

// ---------------------------------------------------------------------------
// The shared daemon, read BEFORE cleanup. Never written to.
// ---------------------------------------------------------------------------

module Daemon =
  let Port = 37749
  let DashboardUrl = "http://localhost:37750/dashboard"
  let HttpTimeoutSeconds = 15

let private http = new HttpClient(Timeout = TimeSpan.FromSeconds(float Daemon.HttpTimeoutSeconds))

let private getJson (url: string) : Result<JsonNode, string> =
  try
    let text = http.GetStringAsync(url).GetAwaiter().GetResult()
    match JsonNode.Parse text with
    | null -> Result.Error(sprintf "%s answered with nothing" url)
    | node -> Result.Ok node
  with ex -> Result.Error(sprintf "%s: %s" url ex.Message)

/// Writes out/daemon-state.json: the sessions under the run directory, with each one's
/// live-testing summary read through the per-session endpoint. Read-only.
let daemonState (runDir: string) (port: int) : int =
  let baseUrl = sprintf "http://localhost:%d" port
  match getJson (baseUrl + "/api/sessions") with
  | Result.Error e ->
    eprintfn "daemon-state: %s" e
    1
  | Result.Ok node ->
    let prefix = runDir.TrimEnd('/') + "/"
    let own =
      match node.["sessions"] with
      | :? JsonArray as arr ->
        arr
        |> Seq.choose (fun s -> match s with null -> None | s -> Some s)
        |> Seq.filter (fun s ->
          let wd = match s.["workingDirectory"] with null -> "" | v -> v.GetValue<string>()
          wd = runDir || wd.StartsWith prefix)
        |> List.ofSeq
      | _ -> []
    let sessions = JsonArray()
    for s in own do
      let id = match s.["id"] with null -> "" | v -> v.GetValue<string>()
      let tests =
        match getJson (sprintf "%s/api/live-testing/status?session=%s" baseUrl id) with
        | Result.Ok t ->
          let summary = t.["Summary"]
          let num (name: string) = match summary with null -> 0 | sm -> (match sm.[name] with null -> 0 | v -> v.GetValue<int>())
          let enabled = match t.["Enabled"] with null -> false | v -> v.GetValue<bool>()
          JsonObject(
            [ KeyValuePair("enabled", JsonValue.Create enabled :> JsonNode)
              KeyValuePair("passed", JsonValue.Create(num "Passed") :> JsonNode)
              KeyValuePair("failed", JsonValue.Create(num "Failed") :> JsonNode)
              KeyValuePair("total", JsonValue.Create(num "Total") :> JsonNode) ]
          )
        | Result.Error e -> JsonObject([ KeyValuePair("error", JsonValue.Create e :> JsonNode) ])
      let entry = s.DeepClone().AsObject()
      entry.["liveTests"] <- tests
      sessions.Add entry
    let result = JsonObject([ KeyValuePair("readAt", JsonValue.Create(DateTime.UtcNow.ToString "o") :> JsonNode); KeyValuePair("sessions", sessions :> JsonNode) ])
    File.WriteAllText(Path.Combine(runDir, "out", "daemon-state.json"), result.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
    printfn "daemon-state: %d session(s) under %s" own.Length runDir
    0

// ---------------------------------------------------------------------------
// Commands.
// ---------------------------------------------------------------------------

let private option (args: string list) (name: string) : string option =
  args |> List.pairwise |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd

let private defaultPluginDir = Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, "Work", "sagefs.nvim")

/// `nvim oracle <task> --run <run-dir> [--plugin <dir>]`: exit 0 only when every check passes.
let run (args: string list) : int =
  match args with
  | taskText :: rest ->
    match tryParseTask taskText, option rest "--run" with
    | Result.Error e, _ ->
      eprintfn "%s" e
      2
    | _, None ->
      eprintfn "oracle needs --run <run-dir>"
      2
    | Result.Ok task, Some runDir ->
      let plugin = option rest "--plugin" |> Option.defaultValue defaultPluginDir
      let checks = verdictFor task (loadFacts runDir plugin)
      printfn "%s" (formatVerdict checks)
      if checks |> List.forall (fun c -> c.Passed) then 0 else 1
  | [] ->
    eprintfn "usage: oracle <task> --run <run-dir>"
    2

let private jsonOut = JsonSerializerOptions(WriteIndented = true)

/// `nvim summarize --run <run-dir>`: writes out/fellover.extra.json (read by lem_score) and
/// out/ui-stats.json. Run it before lem_score.
let summarize (args: string list) : int =
  match option args "--run" with
  | None ->
    eprintfn "summarize needs --run <run-dir>"
    2
  | Some runDir ->
    let facts = loadFacts runDir (option args "--plugin" |> Option.defaultValue defaultPluginDir)
    let out = Path.Combine(runDir, "out")
    let extra =
      findings (option args "--task" |> Option.bind (fun t -> match tryParseTask t with Result.Ok task -> Some task | Result.Error _ -> None)) facts
      |> List.map (fun f -> {| stage = "Editor"; symptom = symptomText f.Symptom; evidence = f.Evidence |})
    File.WriteAllText(Path.Combine(out, "fellover.extra.json"), JsonSerializer.Serialize(extra, jsonOut))
    let byCommand = facts.Calls |> List.countBy (fun c -> c.Cmd) |> List.map (fun (k, v) -> k, v) |> dict
    let stats =
      {| driverCalls = facts.Calls.Length
         driverRefusals = facts.Calls |> List.filter (fun c -> not c.Ok) |> List.length
         byCommand = byCommand
         screens = facts.Screens.Length
         sessionsSeenInDashboard = (match facts.Sessions with Some s -> s.Length | None -> -1)
         dashboardUrl = Daemon.DashboardUrl
         workingDirectory = facts.Workspace |}
    File.WriteAllText(Path.Combine(out, "ui-stats.json"), JsonSerializer.Serialize(stats, jsonOut))
    printfn "summarize: %d driver calls, %d finding(s)" facts.Calls.Length extra.Length
    0

/// The generic scorer was written for lemmings that call SageFs over MCP. A Neovim lemming has no MCP
/// server by design, so "never called a SageFs MCP tool" is not a finding about SageFs and would bury the
/// real ones. It is removed, and the MCP-only clauses are cut from the budget finding.
let private mcpOnlyRegistration = "never called a SageFs MCP tool"
let private mcpOnlyBudgetClause = "; first SageFs call at turn never; first successful eval at turn never"

let dropMcpOnlyFindings (summary: JsonObject) : unit =
  match summary.["fellOver"] with
  | :? JsonArray as findings ->
    let kept = JsonArray()
    for item in findings |> Seq.toList do
      match item with
      | :? JsonObject as f ->
        let text (name: string) = match f.[name] with null -> "" | v -> v.GetValue<string>()
        if text "stage" = "Registration" && text "symptom" = mcpOnlyRegistration then ()
        else
          if text "stage" = "Budget" then f.["evidence"] <- JsonValue.Create((text "evidence").Replace(mcpOnlyBudgetClause, ""))
          kept.Add(f.DeepClone())
      | _ -> ()
    summary.["fellOver"] <- kept
  | _ -> ()

/// `nvim annotate --run <run-dir>`: folds out/ui-stats.json into summary.json under "ui".
let annotate (args: string list) : int =
  match option args "--run" with
  | None ->
    eprintfn "annotate needs --run <run-dir>"
    2
  | Some runDir ->
    let summaryPath = Path.Combine(runDir, "out", "summary.json")
    let statsPath = Path.Combine(runDir, "out", "ui-stats.json")
    match readIfExists summaryPath, readIfExists statsPath with
    | Some summary, Some stats ->
      match JsonNode.Parse summary with
      | :? JsonObject as obj ->
        obj.["ui"] <- JsonNode.Parse stats
        dropMcpOnlyFindings obj
        File.WriteAllText(summaryPath, obj.ToJsonString jsonOut)
        0
      | _ ->
        eprintfn "summary.json is not an object"
        1
    | _ ->
      eprintfn "annotate: summary.json or ui-stats.json missing"
      1
