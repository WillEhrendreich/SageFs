/// The last hop of the patch-confirmation gate: a real FSI compile of a real save, in
/// a real app process, with the outcome read off the surfaces a user reads.
///
/// `InlinedCalleeOutcomeTests` proves the patch probe on the real detour, the
/// real entry-probe stub, the real planner and the real host-side wait, and
/// replaces only the F# compile of the save with IL emission. This closes the
/// rest. The fixture app (`WebAppFixture`) runs in a HotReload session on a
/// daemon. One helper is called by a request (`CalledCallee.fs`) and ends as
/// `Patched`. One helper nothing calls (`UncalledHelper.fs`) ends as
/// `NeverEntered`. Each helper's body is saved on disk.
///
/// An inlined callee is not a case here, because a session cannot produce one:
/// SageFs builds it without optimizations, so `inline` calls stay calls and the JIT
/// does not inline. The unit-level `InlinedCalleeOutcomeTests` covers the planner
/// for an inlined callee from an optimized build.
///
/// What it reads, per case:
///   - the daemon's `/events` stream, which carries every outcome the worker
///     reports for the save, so the waits are on events and nothing is slept on;
///   - `get_session_status` over MCP, whose `lastReload` is what an agent reads;
///   - what the running app serves on the route.
///
/// It runs on net11.0 and net10.0. The runner (`runHotReloadBrowserJourneys`)
/// boots a session for each runtime and hands the journeys their urls and
/// directories through the environment, read lazily so a machine that is not
/// running `--integration-hr` still discovers every other suite.
module SageFs.Tests.HotReloadInlinedCalleeJourneyTests

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open SageFs.Tests.HotReloadStateHarness

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// The environment the runner hands the journeys. The names live here so the
/// runner and the journeys cannot disagree about them.
module Env =
  let mcpPort = "SAGEFS_HR_MCP_PORT"
  let dashboardPort = "SAGEFS_DASHBOARD_PORT"
  let net11AppUrl = "SAGEFS_HR_APP_URL"
  let net11FixtureDir = "SAGEFS_HR_FIXTURE_DIR"
  let net10AppUrl = "SAGEFS_HR_NET10_APP_URL"
  let net10FixtureDir = "SAGEFS_HR_NET10_FIXTURE_DIR"

  let read (name: string) : string =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> failwithf "%s is not set. These journeys run under --integration-hr." name
    | value -> value.TrimEnd('/')

/// Where one runtime's fixture app runs and where its sources are.
type private Target =
  { Runtime: HostRuntime
    AppUrl: string
    FixtureDir: string }

let private targetOf (runtime: HostRuntime) : Target =
  match runtime with
  | HostRuntime.Net11 ->
    { Runtime = runtime
      AppUrl = Env.read Env.net11AppUrl
      FixtureDir = Env.read Env.net11FixtureDir }
  | HostRuntime.Net10 ->
    { Runtime = runtime
      AppUrl = Env.read Env.net10AppUrl
      FixtureDir = Env.read Env.net10FixtureDir }

/// What a save of one helper has to end with.
[<RequireQualifiedAccess>]
type private Ending =
  /// Nothing calls the helper: the patch is in place, the new body never runs, and the
  /// page keeps serving the old value.
  | NeverEntered
  /// A request calls the helper: the new body runs and the page serves it.
  | Patched

module private Ending =
  let outcome (ending: Ending) : SageFs.ReloadCase =
    match ending with
    | Ending.NeverEntered -> SageFs.ReloadCase.NeverEntered
    | Ending.Patched -> SageFs.ReloadCase.Patched

  /// The value the route serves once the save has settled.
  let served (ending: Ending) : string =
    match ending with
    | Ending.NeverEntered -> "A"
    | Ending.Patched -> "B"

/// One helper under test: the file to save, the route that calls it, and the
/// ending the save has to reach.
type private Callee =
  { Name: string
    File: string
    Helper: string
    Route: string
    Find: string
    Replace: string
    Ending: Ending }

let private uncalledCallee =
  { Name = "a helper nothing calls"
    File = "UncalledHelper.fs"
    Helper = "uncalledHelper"
    Route = "/callee/uncalled"
    Find = "let uncalledHelper () : string = \"A\""
    Replace = "let uncalledHelper () : string = \"B\""
    Ending = Ending.NeverEntered }

let private calledCallee =
  { Name = "a helper the caller really calls"
    File = "CalledCallee.fs"
    Helper = "calledHelper"
    Route = "/callee/called"
    Find = "let calledHelper () : string = \"A\""
    Replace = "let calledHelper () : string = \"B\""
    Ending = Ending.Patched }

let private http = new HttpClient(Timeout = TestTimeouts.httpRequest)

/// GET a route of the running app, the body trimmed.
let private appRoute (target: Target) (route: string) : Task<string> =
  task {
    let! body = http.GetStringAsync(target.AppUrl + route)
    return body.Trim()
  }

/// The app answering its first request. The app is started by an init profile
/// on a background task, and nothing tells a client its listener is up, so this
/// asks until it answers, with a bound.
let private firstAnswer (target: Target) (route: string) : Task<string> =
  task {
    use cts = new CancellationTokenSource(TestTimeouts.appFirstAnswer)
    let mutable answer: string option = None
    let mutable lastFailure = ""
    while answer.IsNone && not cts.IsCancellationRequested do
      try
        let! body = appRoute target route
        answer <- Some body
      with ex ->
        lastFailure <- ex.Message
        do! Task.Delay TestTimeouts.pollService
    match answer with
    | Some body -> return body
    | None -> return failwithf "%s%s never answered within %O. Last failure: %s" target.AppUrl route TestTimeouts.appFirstAnswer lastFailure
  }

let private textOf (result: CallToolResult) : string =
  result.Content
  |> Seq.choose (function
    | :? TextContentBlock as t -> Some t.Text
    | _ -> None)
  |> Seq.tryHead
  |> Option.defaultValue ""

let private connect (port: int) : Task<McpClient> =
  let opts = HttpClientTransportOptions(Endpoint = Uri(sprintf "http://localhost:%d/" port))
  let transport = HttpClientTransport(opts, (null: Microsoft.Extensions.Logging.ILoggerFactory))
  McpClient.CreateAsync(transport, null, null, CancellationToken.None)

/// What `get_session_status` says about the session's last save.
[<RequireQualifiedAccess>]
type private LastReload =
  /// `lastReload` is null: no save has resolved for this session yet.
  | NoReloadYet
  | Compiling
  | Finished of outcome: string * message: string

/// `get_session_status` for the session whose working directory is `dir`, over
/// MCP, as an agent calls it.
let private sessionStatus (client: McpClient) (dir: string) : Task<string * LastReload> =
  task {
    use cts = new CancellationTokenSource(TestTimeouts.toolCall)
    let! result = client.CallToolAsync("get_session_status", readOnlyDict [ "working_directory", box dir ], null, null, cts.Token)
    let text = textOf result
    use doc = JsonDocument.Parse text
    let root = doc.RootElement
    let sessionId =
      match root.TryGetProperty "sessionId" with
      | true, id when id.ValueKind = JsonValueKind.String -> id.GetString()
      | _ -> failwithf "get_session_status for %s named no session:\n%s" dir text
    let lastReload =
      match root.TryGetProperty "lastReload" with
      | true, reload when reload.ValueKind = JsonValueKind.Object ->
        match reload.GetProperty("state").GetString() with
        | "compiling" -> LastReload.Compiling
        | "finished" ->
          LastReload.Finished(reload.GetProperty("outcome").GetString(), reload.GetProperty("message").GetString())
        | other -> failwithf "get_session_status carried an unknown lastReload state '%s':\n%s" other text
      | _ -> LastReload.NoReloadYet
    return sessionId, lastReload
  }

/// The daemon's `/events` stream, open before the save so no outcome is missed.
type private Events =
  { Client: HttpClient
    Response: HttpResponseMessage
    Reader: StreamReader }

  interface IDisposable with
    member this.Dispose() =
      this.Reader.Dispose()
      this.Response.Dispose()
      this.Client.Dispose()

let private openEvents (mcpPort: int) : Task<Events> =
  task {
    let client = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)
    let req = new HttpRequestMessage(HttpMethod.Get, sprintf "http://localhost:%d/events" mcpPort)
    req.Headers.Accept.ParseAdd "text/event-stream"
    let! resp = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
    resp.EnsureSuccessStatusCode() |> ignore
    let! stream = resp.Content.ReadAsStreamAsync()
    return { Client = client; Response = resp; Reader = new StreamReader(stream) }
  }

/// The next outcome the worker reports for session `sessionId`, as the daemon
/// pushes it. Waits on the stream, not on a clock: the budget only bounds how
/// long a stream that never says anything is listened to, and what the stream
/// did say comes back in the failure.
let private nextOutcome (events: Events) (sessionId: string) (budget: TimeSpan) : Task<string> =
  task {
    use cts = new CancellationTokenSource(budget)
    let seen = ResizeArray<string>()
    let mutable outcome: string option = None
    try
      while outcome.IsNone do
        let! line = events.Reader.ReadLineAsync(cts.Token).AsTask()
        match line with
        | null -> failwithf "the /events stream closed. Reload events seen for the session: %A" (List.ofSeq seen)
        | line when line.StartsWith("data: ", StringComparison.Ordinal) ->
          use doc = JsonDocument.Parse(line.Substring "data: ".Length)
          let root = doc.RootElement
          let forThisSession =
            match root.TryGetProperty "sessionId" with
            | true, id -> id.GetString() = sessionId
            | _ -> false
          match forThisSession, root.TryGetProperty "reloadReported" with
          | true, (true, reload) when reload.ValueKind = JsonValueKind.Object ->
            match reload.GetProperty("state").GetString() with
            | "finished" ->
              let token = reload.GetProperty("outcome").GetString()
              seen.Add token
              outcome <- Some token
            | state -> seen.Add state
          | _ -> ()
        | _ -> ()
    with :? OperationCanceledException ->
      failwithf "no further outcome for session %s within %O. Reload events seen for it: %A" sessionId budget (List.ofSeq seen)
    return outcome.Value
  }

/// Write a fixture file, retrying while something still holds it.
let private writeFixtureFile (path: string) (content: string) : Task<unit> =
  task {
    use cts = new CancellationTokenSource(TestTimeouts.fileLockRetryPatience)
    let mutable written = false
    while not written && not cts.IsCancellationRequested do
      try
        do! File.WriteAllTextAsync(path, content)
        written <- true
      with :? IOException ->
        do! Task.Delay TestTimeouts.pollPage
    written |> Expect.isTrue (sprintf "%s should be writable within %O" path TestTimeouts.fileLockRetryPatience)
  }

let private postJson (url: string) (body: string) : Task<int * string> =
  task {
    use content = new StringContent(body, Encoding.UTF8, "application/json")
    let! resp = http.PostAsync(url, content)
    let! text = resp.Content.ReadAsStringAsync()
    return int resp.StatusCode, text
  }

/// Put the file watcher on every source file of the session, the way the
/// dashboard's Watch All button does, and read back that it took.
let private watchAll (sessionId: string) : Task<unit> =
  task {
    let dashboard = sprintf "http://localhost:%s/api/sessions/%s/hotreload" (Env.read Env.dashboardPort) sessionId
    let! status, body = postJson (dashboard + "/watch-all") "{}"
    status |> Expect.equal (sprintf "watch-all should succeed: %s" body) 200
    let! watched = http.GetStringAsync dashboard
    watched.Contains "\"watchedCount\":0"
    |> Expect.isFalse (sprintf "watch-all should leave files watched, the worker said: %s" watched)
  }

/// Take the file watcher off again. The dashboard journeys that run after these start from a
/// session that is not watching, and expect to arm it themselves.
let private unwatchAll (runtime: HostRuntime) : Task<unit> =
  task {
    let target = targetOf runtime
    use! client = connect (int (Env.read Env.mcpPort))
    let! sessionId, _ = sessionStatus client target.FixtureDir
    let dashboard = sprintf "http://localhost:%s/api/sessions/%s/hotreload" (Env.read Env.dashboardPort) sessionId
    let! status, body = postJson (dashboard + "/unwatch-all") "{}"
    status |> Expect.equal (sprintf "unwatch-all should succeed: %s" body) 200
  }

/// Make the primary (net11) session the one the dashboard shows. The dashboard journeys that run
/// after these click Watch All and save into the net11 fixture, so they need the panel on that
/// session. Which session is active otherwise depends on what the earlier cases touched, and left
/// to that it was the net10 one in a clean run: the click armed net10, the saves went to an
/// unwatched net11, and nothing reloaded.
let private activatePrimary () : Task<unit> =
  task {
    let primary = targetOf HostRuntime.Net11
    use! client = connect (int (Env.read Env.mcpPort))
    let! sessionId, _ = sessionStatus client primary.FixtureDir
    let url = sprintf "http://localhost:%s/api/sessions/switch" (Env.read Env.mcpPort)
    let! status, body = postJson url (sprintf "{\"sessionId\":\"%s\"}" sessionId)
    status |> Expect.equal (sprintf "switching the dashboard to the primary session should succeed: %s" body) 200
  }

/// One journey: save the helper's body, follow the save to its outcome, and
/// check that the outcome and the page agree.
let private journey (runtime: HostRuntime) (callee: Callee) : Task<unit> =
  task {
    let target = targetOf runtime
    let ending = callee.Ending
    let endingOutcome = SageFs.ReloadCase.token (Ending.outcome ending)

    let! before = firstAnswer target callee.Route
    before |> Expect.equal (sprintf "before the save %s serves the old value" callee.Route) "A"

    use! client = connect (int (Env.read Env.mcpPort))
    let! sessionId, _ = sessionStatus client target.FixtureDir
    do! watchAll sessionId

    use! events = openEvents (int (Env.read Env.mcpPort))

    let path = Path.Combine(target.FixtureDir, callee.File)
    let original = File.ReadAllText path
    let occurrences = original.Split([| callee.Find |], StringSplitOptions.None).Length - 1
    occurrences |> Expect.equal (sprintf "the edit anchor has to appear exactly once in %s: %s" callee.File callee.Find) 1
    do! writeFixtureFile path (original.Replace(callee.Find, callee.Replace))

    // The save lands as a patch that is applied and not yet seen running. That
    // is the first thing the worker can honestly say, for either helper.
    let! applied = nextOutcome events sessionId TestTimeouts.saveVerdict
    applied
    |> Expect.equal
      (sprintf "%s on %s: a save of %s lands as applied-and-unconfirmed first" callee.Name (HostRuntime.moniker runtime) callee.Helper)
      (SageFs.ReloadCase.token SageFs.ReloadCase.PatchPending)

    // Exercise the route, the way the refreshed page would. Only a caller that
    // really enters the helper runs the new body.
    let! firstServed = appRoute target callee.Route
    firstServed
    |> Expect.equal (sprintf "%s: what %s serves right after the patch lands" callee.Name callee.Route) (Ending.served ending)

    let! settled = nextOutcome events sessionId TestTimeouts.patchOutcome
    settled
    |> Expect.equal
      (sprintf "%s on %s: the save of %s has to end as %s" callee.Name (HostRuntime.moniker runtime) callee.Helper endingOutcome)
      endingOutcome

    let! served = appRoute target callee.Route
    served
    |> Expect.equal
      (sprintf "%s: the page and the outcome have to agree about %s" callee.Name callee.Route)
      (Ending.served ending)

    // The surface an agent reads says the same thing.
    let! _, lastReload = sessionStatus client target.FixtureDir
    match lastReload with
    | LastReload.Finished(outcome, message) ->
      outcome |> Expect.equal (sprintf "%s: get_session_status lastReload" callee.Name) endingOutcome
      match ending with
      | Ending.NeverEntered ->
        message
        |> Expect.stringContains "the message names the helper whose new body never ran" callee.Helper
      | Ending.Patched -> ()
    | other -> failtestf "%s: get_session_status lastReload should be finished as %s, it was %A" callee.Name endingOutcome other
  }

let private journeyCase (runtime: HostRuntime) (callee: Callee) =
  Integration.dedicatedCaseTask
    "--integration-hr"
    (sprintf "HR patch confirmation on %s: a save of %s ends as %s" (HostRuntime.moniker runtime) callee.Name (SageFs.ReloadCase.token (Ending.outcome callee.Ending)))
    (fun () ->
      task {
        let! outcome =
          task {
            try
              do! journey runtime callee
              return Ok ()
            with ex ->
              return Error ex
          }
        do! unwatchAll runtime
        do! activatePrimary ()
        match outcome with
        | Ok () -> ()
        | Error ex -> System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
      })

[<Tests>]
let tests =
  testSequenced
  <| testList "Hot-reload patch confirmation journeys" [
    for runtime in HostRuntime.all do
      for callee in [ uncalledCallee; calledCallee ] do
        journeyCase runtime callee
  ]
