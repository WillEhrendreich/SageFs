// scripts/smoke-test.fsx   clean-machine end-to-end validation of a SageFs install
// Run with: dotnet fsi scripts/smoke-test.fsx -- [--sample <fsproj>] [--daemon-timeout <s>] [--session-warmup <s>]
//                                                [--port <n>] [--diagnostics-dir <dir>]
//
// Starts a bare daemon from the `sagefs` on PATH, opens a session on a small sample project, and checks that
// the pieces a user touches first answer: the version endpoint, completions, live-testing discovery and run,
// and mark-all-stale. Exits 0 when every check passes, 1 when any fails (64 for bad arguments). A failing run
// leaves everything it saw in the diagnostics directory (default smoke-diagnostics/) so a CI log is not the
// only evidence.
//
// The checks' decisions (what a discovery state or a completions reply means) are SmokeRules.fs, tested in
// SageFs.Tests; this file is the talking: processes, HTTP, the console and the diagnostics files.
#load "SmokeRules.fs"

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open SmokeRules

// ── named values ─────────────────────────────────────────────────────────────

let healthProbeTimeout = TimeSpan.FromSeconds 2.
let versionTimeout = TimeSpan.FromSeconds 10.
let sessionCreateTimeout = TimeSpan.FromSeconds 15.
let sessionsPollTimeout = TimeSpan.FromSeconds 5.
let completionsTimeout = TimeSpan.FromSeconds 15.
let liveTestingEnableTimeout = TimeSpan.FromSeconds 5.
let discoveryPollTimeout = TimeSpan.FromSeconds 5.
let discoveryPollEvery = TimeSpan.FromSeconds 2.
let discoveryPolls = 15
let runTestsTimeout = TimeSpan.FromSeconds 40.
let runTestsBudgetSeconds = 30
let markAllStaleTimeout = TimeSpan.FromSeconds 10.
let diagnosticEndpointTimeout = TimeSpan.FromSeconds 5.
let pollEvery = TimeSpan.FromSeconds 1.
let buildTimeout = TimeSpan.FromMinutes 10.
let probeTimeout = TimeSpan.FromSeconds 30.
/// The text typed into the completions probe, and where its cursor is (the end, just after the dot).
let completionProbe = "let x = List."

// ── console ──────────────────────────────────────────────────────────────────

let say (color: ConsoleColor) (text: string) =
  Console.ForegroundColor <- color
  Console.WriteLine text
  Console.ResetColor()

let section (title: string) =
  Console.WriteLine()
  say ConsoleColor.Cyan (sprintf "── %s" title)

// ── results, in the order the steps first reported ───────────────────────────

let results = ResizeArray<Step * Verdict>()

/// Records a step's verdict and says it. A later verdict for the same step replaces the earlier one in place.
let record (step: Step) (verdict: Verdict) : unit =
  match verdict with
  | Pass m -> say ConsoleColor.Green (sprintf "  ✓ %s" m)
  | Fail m -> say ConsoleColor.Red (sprintf "  ✗ %s" m)
  match results.FindIndex(fun (s, _) -> s = step) with
  | -1 -> results.Add((step, verdict))
  | at -> results[at] <- (step, verdict)

let anyFailed () = results |> Seq.exists (fun (_, v) -> match v with | Fail _ -> true | Pass _ -> false)

let showSummary () : unit =
  section "Summary"
  for (step, verdict) in results do
    match verdict with
    | Pass _ -> say ConsoleColor.Green (sprintf "  ✓ %s" (stepName step))
    | Fail _ -> say ConsoleColor.Red (sprintf "  ✗ %s" (stepName step))
  Console.WriteLine()

/// A check that cannot go on: the diagnostics and the summary are written and the run ends with exit 1.
exception Abort of reason: string

// ── processes ────────────────────────────────────────────────────────────────

/// Runs a program to completion and returns its exit code and combined output.
let run (file: string) (args: string list) (cwd: string) (timeout: TimeSpan) : int * string =
  try
    let psi = ProcessStartInfo(file)
    psi.WorkingDirectory <- cwd
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    args |> List.iter psi.ArgumentList.Add
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEndAsync()
    let err = p.StandardError.ReadToEndAsync()
    match p.WaitForExit timeout with
    | true -> p.ExitCode, out.Result + err.Result
    | false ->
      (try p.Kill true with _ -> ())
      124, "timed out\n" + out.Result + err.Result
  with e -> 127, e.Message

// ── HTTP ─────────────────────────────────────────────────────────────────────

let http = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)

/// One request with its own timeout. Ok carries the status code and body; any failure (refused, timed out, a
/// non-2xx status) is an Error with a reason, the way Invoke-RestMethod threw.
let request (method': HttpMethod) (url: string) (body: string option) (timeout: TimeSpan) : Result<int * string, string> =
  try
    use cts = new CancellationTokenSource(timeout)
    use message = new HttpRequestMessage(method', url)
    body |> Option.iter (fun b -> message.Content <- new StringContent(b, Encoding.UTF8, "application/json"))
    use reply = http.SendAsync(message, cts.Token).GetAwaiter().GetResult()
    let text = reply.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult()
    match reply.IsSuccessStatusCode with
    | true -> Ok(int reply.StatusCode, text)
    | false -> Error(sprintf "%d %s: %s" (int reply.StatusCode) reply.ReasonPhrase text)
  with e -> Error e.Message

let parseJson (text: string) : JsonNode option =
  try Option.ofObj (JsonNode.Parse text) with _ -> None

let getJson (url: string) (timeout: TimeSpan) : Result<JsonNode option, string> =
  request HttpMethod.Get url None timeout |> Result.map (fun (_, text) -> parseJson text)

let postJson (url: string) (body: string) (timeout: TimeSpan) : Result<int * JsonNode option, string> =
  request HttpMethod.Post url (Some body) timeout |> Result.map (fun (code, text) -> code, parseJson text)

let member' (name: string) (node: JsonNode option) : JsonNode option =
  match node with
  | Some (:? JsonObject as o) -> Option.ofObj o[name]
  | _ -> None

let text' (name: string) (node: JsonNode option) : string option =
  match member' name node with
  | Some n -> (try Some(n.GetValue<string>()) with _ -> Some(n.ToJsonString()))
  | None -> None

let whole (name: string) (node: JsonNode option) : int option =
  match member' name node with
  | Some n -> (try Some(n.GetValue<int>()) with _ -> None)
  | None -> None

let hasMember (name: string) (node: JsonNode option) : bool =
  match node with
  | Some (:? JsonObject as o) -> o.ContainsKey name
  | _ -> false

let nowUtc () = DateTime.UtcNow.ToString "o"

// ── the run ──────────────────────────────────────────────────────────────────

let argv = fsi.CommandLineArgs |> Array.toList |> List.tail |> List.filter (fun a -> a <> "--")

let options =
  match parseArgs argv with
  | Ok o -> o
  | Error e ->
    eprintfn "smoke-test: %s" (describeArgError e)
    eprintfn "%s" usage
    exit 64

let baseUrl = sprintf "http://localhost:%d" options.Port
let dashboardBaseUrl = sprintf "http://localhost:%d" (options.Port + 1)
// Locate the repo at RUNTIME, walking up from this script's own location until we
// find the solution file. A build-time constant would bake in the directory the
// script was COMPILED, which is not where it RUNS.
let repoRoot =
  let rec walk (dir: string) (depth: int) : string =
    if depth > 24 then "" else
    let full =
      try
        let f = Path.GetFullPath dir
        let r = Path.GetPathRoot f
        if f = r then f else Path.TrimEndingDirectorySeparator f
      with _ -> dir
    if File.Exists(Path.Combine(full, "SageFs.slnx")) then full
    else
      let parent = Path.GetDirectoryName full
      if String.IsNullOrEmpty parent || parent = full then ""
      else walk parent (depth + 1)
  // Start from the directory this script lives in. A build-time constant only names where it was
  // built, so the walk up to SageFs.slnx is what locates the repo where the code actually runs.
  let start =
    try Path.GetDirectoryName __SOURCE_DIRECTORY__ with _ -> "."
  walk start 0
let repo =
  if repoRoot = "" then
    failwith "Could not locate the SageFs repository (no SageFs.slnx found walking up from this script)."
  else repoRoot
let samplePath = Path.GetFullPath(Path.Combine(repoRoot, options.Sample))
let sampleProjectDir = Path.GetDirectoryName samplePath
let diagnosticsRoot =
  match Path.IsPathRooted options.DiagnosticsDir with
  | true -> Path.GetFullPath options.DiagnosticsDir
  | false -> Path.GetFullPath(Path.Combine(repoRoot, options.DiagnosticsDir))
let daemonStdoutPath = Path.Combine(diagnosticsRoot, "daemon-stdout.log")
let daemonStderrPath = Path.Combine(diagnosticsRoot, "daemon-stderr.log")
let daemonArguments = [ "--mcp-port"; string options.Port; "--no-resume" ]

// What the run has seen, kept for the diagnostics.
let startupHealthPolls = JsonArray()
let sessionWarmupPolls = JsonArray()
let testDiscoveryPolls = JsonArray()
let mutable daemon : Process option = None
let mutable versionResponse : JsonNode option = None
let mutable completionResponse : JsonNode option = None
let mutable latestHealth : JsonNode option = None
let mutable latestSessions : JsonNode option = None
let mutable latestLiveTesting : JsonNode option = None
let mutable runTestsResponse : JsonNode option = None
let mutable markAllStaleResponse : JsonNode option = None
let mutable diagnosticsCaptured = false
let mutable stopped = false

let writeDiagnostic (fileName: string) (content: string) : unit =
  Directory.CreateDirectory diagnosticsRoot |> ignore
  File.WriteAllText(Path.Combine(diagnosticsRoot, fileName), content, UTF8Encoding(false))

let json = JsonSerializerOptions(WriteIndented = true)

let writeDiagnosticJson (fileName: string) (value: JsonNode option) : unit =
  writeDiagnostic fileName (match value with | Some n -> n.ToJsonString json | None -> "null")

/// A copy of a node, so it can sit in a second tree (a JsonNode can have only one parent).
let copy (node: JsonNode option) : JsonNode = match node with | Some n -> n.DeepClone() | None -> null

let captureDiagnostics (reason: string) : unit =
  match diagnosticsCaptured with
  | true -> ()
  | false ->
    diagnosticsCaptured <- true
    Directory.CreateDirectory diagnosticsRoot |> ignore
    say ConsoleColor.DarkGray (sprintf "  Saving diagnostics to %s" diagnosticsRoot)
    let daemonInfo : JsonNode =
      match daemon with
      | None -> null
      | Some p ->
        match p.HasExited with
        | false -> JsonObject([ KeyValuePair("id", JsonValue.Create p.Id :> JsonNode); KeyValuePair("name", JsonValue.Create p.ProcessName :> JsonNode); KeyValuePair("startTime", JsonValue.Create(p.StartTime.ToString "o") :> JsonNode); KeyValuePair("hasExited", JsonValue.Create false :> JsonNode) ])
        | true -> JsonObject([ KeyValuePair("id", JsonValue.Create p.Id :> JsonNode); KeyValuePair("hasExited", JsonValue.Create true :> JsonNode); KeyValuePair("exitCode", JsonValue.Create p.ExitCode :> JsonNode) ])
    let resultsSnapshot = JsonArray()
    for (step, verdict) in results do
      resultsSnapshot.Add(JsonObject([ KeyValuePair("step", JsonValue.Create(stepName step) :> JsonNode); KeyValuePair("result", JsonValue.Create(verdictWord verdict) :> JsonNode); KeyValuePair("message", JsonValue.Create(verdictMessage verdict) :> JsonNode) ]))
    let failed = JsonArray()
    for (step, verdict) in results do
      match verdict with
      | Fail _ -> failed.Add(JsonValue.Create(stepName step))
      | Pass _ -> ()
    let daemonArgs = JsonArray()
    for a in daemonArguments do
      match Int32.TryParse a with
      | true, n -> daemonArgs.Add(JsonValue.Create n)
      | _ -> daemonArgs.Add(JsonValue.Create a)
    let context = JsonObject()
    context["reason"] <- JsonValue.Create reason
    context["timestampUtc"] <- JsonValue.Create(nowUtc ())
    context["baseUrl"] <- JsonValue.Create baseUrl
    context["dashboardBaseUrl"] <- JsonValue.Create dashboardBaseUrl
    context["repoRoot"] <- JsonValue.Create repoRoot
    context["samplePath"] <- JsonValue.Create samplePath
    context["daemonProcess"] <- daemonInfo
    context["daemonCommand"] <-
      JsonObject([ KeyValuePair("fileName", JsonValue.Create "sagefs" :> JsonNode); KeyValuePair("arguments", daemonArgs :> JsonNode)
                   KeyValuePair("stdoutLog", JsonValue.Create daemonStdoutPath :> JsonNode); KeyValuePair("stderrLog", JsonValue.Create daemonStderrPath :> JsonNode) ])
    context["sessionCreate"] <- JsonObject([ KeyValuePair("project", JsonValue.Create samplePath :> JsonNode); KeyValuePair("workingDirectory", JsonValue.Create sampleProjectDir :> JsonNode) ])
    context["failedSteps"] <- failed
    context["results"] <- resultsSnapshot
    context["latestHealth"] <- copy latestHealth
    context["latestSessions"] <- copy latestSessions
    context["latestLiveTestingStatus"] <- copy latestLiveTesting
    context["runTestsResponse"] <- copy runTestsResponse
    context["markAllStaleResponse"] <- copy markAllStaleResponse
    writeDiagnosticJson "context.json" (Some(context :> JsonNode))
    writeDiagnosticJson "startup-health-polls.json" (Some(startupHealthPolls :> JsonNode))
    writeDiagnosticJson "session-warmup-polls.json" (Some(sessionWarmupPolls :> JsonNode))
    writeDiagnosticJson "test-discovery-polls.json" (Some(testDiscoveryPolls :> JsonNode))
    for (file, value) in [ "version-check.json", versionResponse; "completion-response.json", completionResponse; "run-tests-response.json", runTestsResponse; "mark-all-stale-response.json", markAllStaleResponse ] do
      if value.IsSome then writeDiagnosticJson file value
    let endpoints =
      [ "health.json", baseUrl + "/health"
        "version.json", baseUrl + "/version"
        "daemon-info.json", dashboardBaseUrl + "/api/daemon-info"
        "sessions.json", baseUrl + "/api/sessions"
        "live-testing-status.json", baseUrl + "/api/live-testing/status"
        "threadpool.json", baseUrl + "/diag/threadpool" ]
    for (file, url) in endpoints do
      match getJson url diagnosticEndpointTimeout with
      | Ok payload -> writeDiagnosticJson file payload
      | Error why -> writeDiagnostic (sprintf "%s-error.txt" (Path.GetFileNameWithoutExtension file)) why

let poll (fields: (string * JsonNode) list) : JsonNode =
  let o = JsonObject()
  o["timestampUtc"] <- JsonValue.Create(nowUtc ())
  for (k, v) in fields do o[k] <- v
  o :> JsonNode

let str (s: string) : JsonNode = JsonValue.Create s :> JsonNode
let num (n: int) : JsonNode = JsonValue.Create n :> JsonNode
let flag (b: bool) : JsonNode = JsonValue.Create b :> JsonNode

// ── 1. prerequisites ─────────────────────────────────────────────────────────

let prerequisites () : unit =
  section "1. Prerequisites"
  match run "dotnet" [ "--version" ] repoRoot probeTimeout with
  | 0, version -> record Dotnet (dotnetVerdict version)
  | _ -> record Dotnet (Fail "'dotnet' not found in PATH")
  match run "sagefs" [ "--version" ] repoRoot probeTimeout with
  | 0, version -> record SagefsOnPath (Pass(sprintf "sagefs %s found in PATH" (version.Trim())))
  | _ ->
    record SagefsOnPath (Fail "'sagefs' not found in PATH")
    Console.WriteLine()
    say ConsoleColor.Red "ERROR: 'sagefs' not found in PATH."
    say ConsoleColor.Yellow "Install with: dotnet tool install --global SageFs"
    say ConsoleColor.Yellow "Then restart your terminal to update PATH."
    Console.WriteLine()
    raise (Abort "sagefs executable was not available on PATH")

// ── 2. daemon startup ────────────────────────────────────────────────────────

/// Copies a redirected stream into a file as it arrives, so a daemon that outlives this script still has
/// its output on disk, and an unread pipe never stalls it.
let pump (source: StreamReader) (path: string) : unit =
  Tasks.Task.Run(fun () ->
    use target = new StreamWriter(path, false, UTF8Encoding(false), AutoFlush = true)
    let rec loop () =
      match source.ReadLine() with
      | null -> ()
      | line ->
        target.WriteLine line
        loop ()
    try loop () with _ -> ())
  |> ignore

let daemonStartup () : unit =
  section "2. Daemon startup"
  if not (File.Exists samplePath) then
    record DaemonStart (Fail(sprintf "Sample project not found: %s" samplePath))
    raise (Abort "Sample project path was invalid")
  // Build the sample project so its DLL exists for session warmup. CI may only build Release; the session
  // loader defaults to Debug output.
  say ConsoleColor.DarkGray (sprintf "  Building sample project (%s)..." samplePath)
  match run "dotnet" [ "build"; samplePath; "--nologo"; "--verbosity"; "quiet" ] repoRoot buildTimeout with
  | 0, _ -> ()
  | code, output ->
    record DaemonStart (Fail(sprintf "Sample project build failed (exit %d)" code))
    say ConsoleColor.Red output
    raise (Abort "Sample project build failed")
  say ConsoleColor.DarkGray "  Starting bare daemon"
  Directory.CreateDirectory diagnosticsRoot |> ignore
  for stale in [ daemonStdoutPath; daemonStderrPath ] do
    if File.Exists stale then File.Delete stale
  let psi = ProcessStartInfo("sagefs")
  daemonArguments |> List.iter psi.ArgumentList.Add
  psi.WorkingDirectory <- repoRoot
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  let p = Process.Start psi
  daemon <- Some p
  pump p.StandardOutput daemonStdoutPath
  pump p.StandardError daemonStderrPath
  let rec waitForHealth (elapsed: int) : int option =
    match elapsed >= options.DaemonTimeoutSeconds with
    | true -> None
    | false ->
      Thread.Sleep pollEvery
      let elapsed = elapsed + 1
      match getJson (baseUrl + "/health") healthProbeTimeout with
      | Ok payload ->
        latestHealth <- payload
        startupHealthPolls.Add(
          poll [ "elapsedSeconds", num elapsed; "reachable", flag true
                 "status", (match text' "status" payload with | Some s -> str s | None -> null)
                 "healthy", copy (member' "healthy" payload)
                 "sessionCount", (match whole "sessionCount" payload with | Some n -> num n | None -> null)
                 "diagnosticSummary", (match text' "diagnosticSummary" payload with | Some s -> str s | None -> null) ])
        match hasMember "healthy" payload with
        | true -> Some elapsed
        | false -> waitForHealth elapsed
      | Error why ->
        startupHealthPolls.Add(poll [ "elapsedSeconds", num elapsed; "reachable", flag false; "error", str why ])
        waitForHealth elapsed
  match waitForHealth 0 with
  | Some elapsed -> record DaemonStart (Pass(sprintf "Daemon started in %ds" elapsed))
  | None ->
    record DaemonStart (Fail(sprintf "Daemon not reachable after %ds" options.DaemonTimeoutSeconds))
    raise (Abort "Daemon startup timed out")

// ── 3. API version ───────────────────────────────────────────────────────────

let apiVersion () : unit =
  section "3. API version"
  match getJson (baseUrl + "/version") versionTimeout with
  | Ok payload ->
    versionResponse <- payload
    match text' "server" payload, member' "version" payload with
    | Some "sagefs", Some version ->
      let shown name = defaultArg (text' name payload) ""
      record ApiVersion (Pass(sprintf "API version %s (MCP: %s, SSE: %s)" (version.ToJsonString().Trim('"')) (shown "mcp") (shown "sse")))
    | _ -> record ApiVersion (Fail(sprintf "Unexpected /version response: %s" (match payload with | Some n -> n.ToJsonString() | None -> "")))
  | Error why -> record ApiVersion (Fail(sprintf "/version request failed: %s" why))

// ── 4. session warmup and completions ────────────────────────────────────────

let sessionsOf (payload: JsonNode option) : (string * string * string * string) list =
  match member' "sessions" payload with
  | Some (:? JsonArray as items) ->
    [ for item in items do
        let n = Option.ofObj item
        yield (defaultArg (text' "id" n) "", defaultArg (text' "status" n) "", defaultArg (text' "workingDirectory" n) "",
               (match member' "projects" n with
                | Some (:? JsonArray as ps) when ps.Count > 0 -> Path.GetFileName(ps[0].GetValue<string>())
                | _ -> Path.GetFileName((defaultArg (text' "workingDirectory" n) "").TrimEnd('/', '\\'))) ) ]
  | _ -> []

let describeSessions (payload: JsonNode option) : string =
  sessionsOf payload |> List.map (fun (_, status, _, label) -> { Label = label; Status = status }) |> sessionStatusSummary

let sessionAndCompletions () : unit =
  section "4. Session warmup + Completions"
  say ConsoleColor.DarkGray "  Creating session for sample project..."
  let create = JsonObject()
  let projects = JsonArray()
  projects.Add(JsonValue.Create samplePath)
  create["projects"] <- projects
  create["workingDirectory"] <- JsonValue.Create sampleProjectDir
  match postJson (baseUrl + "/api/sessions/create") (create.ToJsonString()) sessionCreateTimeout with
  | Error why -> record Completions (Fail(sprintf "Session/completions request failed: %s" why))
  | Ok (_, reply) when (match member' "success" reply with | Some n -> (try n.GetValue<bool>() with _ -> false) | None -> false) |> not ->
    record Completions (Fail(sprintf "Session creation did not succeed: %s" (match reply with | Some n -> n.ToJsonString() | None -> "")))
  | Ok _ ->
    say ConsoleColor.DarkGray (sprintf "  Waiting for created daemon session to reach Ready (up to %ds)..." options.SessionWarmupSeconds)
    let rec warm (i: int) : bool =
      match i >= options.SessionWarmupSeconds with
      | true -> false
      | false ->
        Thread.Sleep pollEvery
        match getJson (baseUrl + "/api/sessions") sessionsPollTimeout with
        | Ok payload ->
          latestSessions <- payload
          let sessions = sessionsOf payload
          let states = JsonArray()
          match member' "sessions" payload with
          | Some (:? JsonArray as items) ->
            for item in items do
              let n = Option.ofObj item
              states.Add(JsonObject([ KeyValuePair("id", copy (member' "id" n)); KeyValuePair("status", copy (member' "status" n))
                                      KeyValuePair("workingDirectory", copy (member' "workingDirectory" n)); KeyValuePair("evalCount", copy (member' "evalCount" n)) ]))
          | _ -> ()
          sessionWarmupPolls.Add(poll [ "elapsedSeconds", num (i + 1); "sessionCount", num sessions.Length; "sessionStates", states ])
          match warmupLook (sessions |> List.map (fun (id, status, _, _) -> id, status)) with
          | SessionReady id ->
            say ConsoleColor.DarkGray (sprintf "  Session %s ready after %ds" id i)
            true
          | AllFaulted ->
            say ConsoleColor.Yellow (sprintf "  All sessions faulted, aborting warmup early after %ds" i)
            false
          | KeepWaiting -> warm (i + 1)
        | Error why ->
          sessionWarmupPolls.Add(poll [ "elapsedSeconds", num (i + 1); "error", str why ])
          warm (i + 1)
    match warm 0 with
    | false ->
      record Completions (Fail(sprintf "No session reached Ready within %ds. Last statuses: %s" options.SessionWarmupSeconds (describeSessions latestSessions)))
    | true ->
      let body = JsonObject()
      body["code"] <- JsonValue.Create completionProbe
      body["cursorPosition"] <- JsonValue.Create completionProbe.Length
      body["workingDirectory"] <- JsonValue.Create sampleProjectDir
      match request HttpMethod.Post (baseUrl + "/api/completions") (Some(body.ToJsonString())) completionsTimeout with
      | Error why -> record Completions (Fail(sprintf "Session/completions request failed: %s" why))
      | Ok (_, text) ->
        let parsed = parseJson text
        // A reply that is not JSON is kept as the text it was, so the diagnostics show what came back.
        completionResponse <- (match parsed with | Some _ -> parsed | None -> Some(JsonValue.Create text :> JsonNode))
        let reply =
          match parsed with
          | Some (:? JsonArray as items) -> ItemArray items.Count
          | Some (:? JsonObject as o) when o["completions"] |> isNull |> not -> CountedObject(defaultArg (whole "count" parsed) 0)
          | Some (:? JsonValue as v) when (try v.GetValue<string>() |> ignore; true with _ -> false) -> Text(v.GetValue<string>())
          | Some other -> Unexpected(other.ToJsonString())
          | None -> Text text
        record Completions (completionsVerdict reply)

// ── 5. live testing ──────────────────────────────────────────────────────────

let liveTestingFacts (payload: JsonNode option) : LiveTestingFacts option =
  match payload with
  | None -> None
  | Some _ ->
    let summary = member' "summary" payload
    Some { State = text' "DiscoveryState" payload
           Hint = text' "DiscoveryHint" payload
           Counts = [ for name in [ "total"; "passed"; "failed"; "stale"; "running"; "notRun" ] do
                        match whole name summary with
                        | Some n -> yield name, n
                        | None -> () ] }

let tests () : unit =
  section "5. Tests"
  say ConsoleColor.DarkGray "  Enabling live testing..."
  postJson (baseUrl + "/api/live-testing/enable") "" liveTestingEnableTimeout |> ignore
  // Poll for test discovery (up to 30s).
  let rec discover (i: int) (elapsed: int) (discovered: int) (state: DiscoveryState) (hint: string) : int * int * DiscoveryState * string =
    match i >= discoveryPolls with
    | true -> discovered, elapsed, state, hint
    | false ->
      Thread.Sleep discoveryPollEvery
      let elapsed = elapsed + int discoveryPollEvery.TotalSeconds
      match getJson (baseUrl + "/api/live-testing/status") discoveryPollTimeout with
      | Ok payload ->
        latestLiveTesting <- payload
        let discovered = defaultArg (whole "total" (member' "summary" payload)) 0
        let state = text' "DiscoveryState" payload |> parseDiscoveryState
        let hint = defaultArg (text' "DiscoveryHint" payload) hint
        testDiscoveryPolls.Add(
          poll [ "elapsedSeconds", num elapsed; "discoveryState", (match state with Unreported -> null | s -> str (discoveryStateText s))
                 "discoveryHint", str hint; "discovered", num discovered; "summary", copy (member' "summary" payload) ])
        match discoveryFinished state discovered with
        | true -> discovered, elapsed, state, hint
        | false -> discover (i + 1) elapsed discovered state hint
      | Error why ->
        testDiscoveryPolls.Add(poll [ "elapsedSeconds", num elapsed; "error", str why ])
        discover (i + 1) elapsed discovered state hint
  let discovered, elapsed, state, hint = discover 0 0 0 Unreported ""
  say ConsoleColor.DarkGray (sprintf "  Tests discovered: %d (after %ds, state: %s)" discovered elapsed (discoveryStateText state))
  record TestsDiscovery (discoveryVerdict state discovered elapsed hint (liveTestingSummary (liveTestingFacts latestLiveTesting)))
  let runBody = JsonObject()
  runBody["timeout_seconds"] <- JsonValue.Create runTestsBudgetSeconds
  match postJson (baseUrl + "/api/live-testing/run") (runBody.ToJsonString()) runTestsTimeout with
  | Ok (_, reply) ->
    runTestsResponse <- reply
    match member' "success" reply |> Option.map (fun n -> try n.GetValue<bool>() with _ -> false) with
    | Some true -> record RunTests (Pass(sprintf "run_tests accepted (discovered: %d)" discovered))
    | _ -> record RunTests (Fail(sprintf "run_tests response: %s" (match reply with | Some n -> n.ToJsonString() | None -> "")))
  | Error why -> record RunTests (Fail(sprintf "run_tests request failed: %s" why))

let markAllStale () : unit =
  section "5b. mark-all-stale"
  match request HttpMethod.Post (baseUrl + "/api/live-testing/mark-all-stale") (Some "{}") markAllStaleTimeout with
  | Ok (code, content) ->
    let reply = JsonObject()
    reply["statusCode"] <- JsonValue.Create code
    reply["content"] <- JsonValue.Create content
    markAllStaleResponse <- Some(reply :> JsonNode)
    match code with
    | 202 -> record MarkAllStale (Pass "POST /api/live-testing/mark-all-stale returned 202 Accepted")
    | other -> record MarkAllStale (Fail(sprintf "Expected 202, got %d" other))
  | Error why -> record MarkAllStale (Fail(sprintf "mark-all-stale request failed: %s" why))

// ── 6. cleanup ───────────────────────────────────────────────────────────────

/// Kill the whole process tree (daemon plus FSI worker children), so orphaned children do not hold the
/// stdout pipe open and keep a CI step alive.
let stopDaemon (announce: bool) : unit =
  match daemon with
  | Some p when not stopped && not p.HasExited ->
    (try p.Kill true with _ -> ())
    if announce then say ConsoleColor.DarkGray (sprintf "  Daemon process tree stopped (PID %d)" p.Id)
    stopped <- true
  | _ -> ()

let code =
  try
    try
      prerequisites ()
      daemonStartup ()
      apiVersion ()
      sessionAndCompletions ()
      tests ()
      markAllStale ()
      section "6. Cleanup"
      if anyFailed () then captureDiagnostics "One or more smoke steps failed"
      stopDaemon true
      showSummary ()
      match anyFailed () with
      | true ->
        say ConsoleColor.Red "RESULT: FAIL"
        1
      | false ->
        say ConsoleColor.Green "RESULT: PASS"
        0
    with Abort reason ->
      captureDiagnostics reason
      stopDaemon false
      showSummary ()
      1
  finally
    stopDaemon false

exit code
