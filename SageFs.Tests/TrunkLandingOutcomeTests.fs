/// Outcome gate for the live loop's second direction: what agents land reaches the running trunk app, live.
///
/// Parallel agents explore in their own checkouts, and what verifies lands in the integration tree through the cohort
/// landing gate. The trunk is the checkout the cohort lands into, and the TRUNK SESSION is a session whose working
/// directory is that checkout and that runs an app. The promise this file holds the daemon to: when a landing lands, the
/// trunk session's running app serves it without a restart and with its in-memory state kept, and the daemon says so on the
/// reload row, naming the mechanism, or says it restarted the app and why.
///
/// The rows run in order against ONE real daemon (its own port and SAGEFS_DATA_DIR, never the user's), three real MCP
/// connections (three cohort members), real git worktrees, the real landing performer, and a real `run_app` app that counts
/// every request it serves in memory:
///
///   1. a trunk session that is not running an app records the landing and says there is no running app to update;
///   2. two agents each land a handler of their own, back to back, and the one process serves both, its counter carrying on;
///   3. a landing that conflicts with what already landed is refused with its reason, and the trunk app is untouched;
///   4. a landing that needs a restart (a virtual member changes its signature) restarts the app, names the cause, and
///      says it landed;
///   5. a landing whose verification fails (a test goes red) does not land, and the trunk app serves the old behavior.
///
/// The rows share the daemon and each leans on the one before it, so they run sequenced. The row for a landing that is refused
/// for a failing test runs last: the verifying session keeps what it evaluated for a landing it blocked, and the landing after
/// it is verified against that.
module SageFs.Tests.TrunkLandingOutcomeTests

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open SageFs

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private sageFsExe = SageFs.Tests.TestInfrastructure.SageFsBinary.path ()

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let private fixtureSourceDir = Path.Combine(__SOURCE_DIRECTORY__, "fixtures", "CohortTrunkFixture")

/// The sources the fixture project compiles, in compile order.
let private fixtureSources = [ "Counter.fs"; "Alice.fs"; "Bob.fs"; "Rude.fs"; "Tests.fs"; "Program.fs" ]

let private fixtureProjectFile = "App.fsproj"

/// The Expecto the repo pins, read from the one place that pins it, so the fixture restores from the same cache the repo does.
let private expectoVersion () : string =
  let props = XDocument.Load(Path.Combine(repoRoot, "Directory.Packages.props"))
  props.Descendants(XName.Get "PackageVersion")
  |> Seq.find (fun e -> e.Attribute(XName.Get "Include").Value = "Expecto")
  |> fun e -> e.Attribute(XName.Get "Version").Value

let private fixtureProject () : string =
  String.concat "\n" [
    "<Project Sdk=\"Microsoft.NET.Sdk.Web\">"
    "  <PropertyGroup>"
    "    <TargetFramework>net11.0</TargetFramework>"
    "    <OutputType>Exe</OutputType>"
    "    <GenerateProgramFile>false</GenerateProgramFile>"
    "  </PropertyGroup>"
    "  <ItemGroup>"
    yield! fixtureSources |> List.map (sprintf "    <Compile Include=\"%s\" />")
    "  </ItemGroup>"
    "  <ItemGroup>"
    sprintf "    <PackageReference Include=\"Expecto\" Version=\"%s\" />" (expectoVersion ())
    "  </ItemGroup>"
    "</Project>"
    "" ]

let private gitAvailable () : bool =
  try
    let psi = ProcessStartInfo("git", "--version", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
    use proc = Process.Start psi
    proc.WaitForExit(TestTimeouts.childExit) |> ignore
    proc.HasExited && proc.ExitCode = 0
  with _ -> false

// -- git: fixture setup and every oracle go through plain git, never through CohortGit ---------------------------------

let private git (dir: string) (args: string list) : Task<string> =
  task {
    let psi = ProcessStartInfo("git", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir)
    for a in args do psi.ArgumentList.Add a
    use proc = new Process(StartInfo = psi)
    proc.Start() |> ignore
    let! stdout = proc.StandardOutput.ReadToEndAsync()
    let! stderr = proc.StandardError.ReadToEndAsync()
    do! proc.WaitForExitAsync()
    match proc.ExitCode with
    | 0 -> return stdout.Trim()
    | code -> return failwithf "git %s failed (%d) in %s: %s" (String.concat " " args) code dir stderr
  }

/// Replace `find` with `replace` in a file of a checkout, which must hold `find` exactly once, and commit it.
let private editAndCommit (dir: string) (relPath: string) (edits: (string * string) list) (message: string) : Task<string> =
  task {
    let full = Path.Combine(dir, relPath)
    let before = File.ReadAllText full
    let after =
      edits
      |> List.fold
        (fun (text: string) (find, replace) ->
          let occurrences = text.Split([| find |], StringSplitOptions.None).Length - 1
          occurrences |> Expect.equal (sprintf "the edit anchor must appear exactly once in %s: %s" relPath find) 1
          text.Replace(find, replace))
        before
    File.WriteAllText(full, after)
    let! _ = git dir [ "add"; relPath ]
    let! _ = git dir [ "commit"; "--quiet"; "-m"; message ]
    return! git dir [ "rev-parse"; "HEAD" ]
  }

// -- the daemon and its MCP clients -----------------------------------------------------------------------------------

let private killProcess (proc: Process) =
  try
    if not proc.HasExited then
      proc.Kill(entireProcessTree = true)
      proc.WaitForExit TestTimeouts.childExit |> ignore
  with _ -> ()

let private startIsolatedDaemon (workingDir: string) (dataDir: string) : Task<Process * int> = task {
  let port, _dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
  let psi = ProcessStartInfo()
  psi.FileName <- sageFsExe
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.WorkingDirectory <- workingDir
  psi.ArgumentList.Add "--mcp-port"
  psi.ArgumentList.Add(string port)
  psi.ArgumentList.Add "--no-resume"
  let self = Process.GetCurrentProcess()
  psi.ArgumentList.Add "--owner-pid"
  psi.ArgumentList.Add(string self.Id)
  psi.ArgumentList.Add "--owner-start"
  psi.ArgumentList.Add(string (self.StartTime.ToUniversalTime().Ticks))
  psi.ArgumentList.Add "--ttl"
  psi.ArgumentList.Add "30m"
  psi.Environment["SAGEFS_DATA_DIR"] <- dataDir
  let proc = Process.Start psi
  use client = new HttpClient()
  client.BaseAddress <- Uri(sprintf "http://localhost:%d" port)
  client.Timeout <- TestTimeouts.httpProbe
  let! ready =
    SageFs.Tests.TestInfrastructure.waitForAsync
      (int Timeouts.integrationDaemonReady.TotalMilliseconds)
      (fun () -> task {
        try
          let! resp = client.GetAsync "/health"
          return int resp.StatusCode > 0
        with _ -> return false
      })
  match ready with
  | true -> return proc, port
  | false ->
    killProcess proc
    proc.Dispose()
    return failwithf "the trunk landing daemon failed to start on port %d within %.0fs" port Timeouts.integrationDaemonReady.TotalSeconds
}

/// The tool's own answer: the FIRST text block. The server appends a second block of session events to a result when
/// any piled up since the caller's last call, and reading both would corrupt a JSON answer.
let private textOf (result: CallToolResult) : string =
  result.Content
  |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
  |> Seq.tryHead
  |> Option.defaultValue ""

let private connect (port: int) : Task<McpClient> =
  let opts = HttpClientTransportOptions(Endpoint = Uri(sprintf "http://localhost:%d/" port))
  let transport = HttpClientTransport(opts, (null: Microsoft.Extensions.Logging.ILoggerFactory))
  McpClient.CreateAsync(transport, null, null, CancellationToken.None)

let private callToolWithin (budget: TimeSpan) (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> =
  task {
    use cts = new CancellationTokenSource(budget)
    let! result = client.CallToolAsync(name, readOnlyDict args, null, null, cts.Token)
    return textOf result
  }

let private callTool = callToolWithin TestTimeouts.toolCall

/// A tool that restarts or builds, so it needs real patience.
let private callToolPatient = callToolWithin TestTimeouts.toolCallThatRestarts

let private claimIdAndFence (text: string) : string * int64 =
  let m = Regex.Match(text, @"Acquired claim (\S+) over .*\(fence=(\d+)\)")
  match m.Success with
  | true -> m.Groups[1].Value, Int64.Parse m.Groups[2].Value
  | false -> failwithf "acquire_claim did not report a claim: %s" text

let private landingIdOf (text: string) : string =
  let m = Regex.Match(text, @"Landing (\S+) queued")
  match m.Success with
  | true -> m.Groups[1].Value
  | false -> failwithf "request_landing did not queue a landing: %s" text

let private field (marker: string) (text: string) : string =
  let m = Regex.Match(text, Regex.Escape marker + @"(\S+)")
  match m.Success with
  | true -> m.Groups[1].Value
  | false -> failwithf "the reply names no '%s': %s" marker text

// -- the world ---------------------------------------------------------------------------------------------------------

type private Agent = { Name: string; Client: McpClient; Worktree: string }

type private World = {
  Repo: string
  Daemon: Process
  Port: int
  BaseSha: string
  Alice: Agent
  Bob: Agent
  Carol: Agent
  /// The ref the cohort lands into.
  IntegrationBranch: string
  /// The checkout the cohort lands into, and the working directory of the trunk session.
  TrunkPath: string
  TrunkSession: string
  /// The integration session that verifies landings, for saying what a blocked landing failed on.
  IntegrationSession: string
  /// The trunk app's own HTTP client.
  App: HttpClient
}

let private appClient =
  let client = new HttpClient()
  client.Timeout <- TestTimeouts.httpRequest
  client

let private statusOf (w: World) : Task<string> = callTool w.Alice.Client "get_cohort_status" []

/// The single `get_cohort_status` line for a landing in the landings list.
let private landingLine (status: string) (landingId: string) : string option =
  let lines = status.Split('\n')
  match lines |> Array.tryFindIndex (fun (l: string) -> l.TrimStart().StartsWith(sprintf "- %s " landingId, StringComparison.Ordinal)) with
  | None -> None
  | Some first ->
    // A long state is printed over several lines, so the entry runs until the next list item or the next heading.
    let continuation =
      lines
      |> Array.skip (first + 1)
      |> Array.takeWhile (fun (l: string) -> l.Length > 0 && Char.IsWhiteSpace l.[0] && not (l.TrimStart().StartsWith("- ", StringComparison.Ordinal)))
    Some (String.Join("\n", Array.append [| lines.[first] |] continuation))

/// The trunk line for a landing: what the trunk did with it. Absent until the landing has landed and the trunk has finished with
/// it: a landing the trunk is still following, or has queued, is not yet something it did.
let private trunkLine (status: string) (landingId: string) : string option =
  status.Split('\n')
  |> Array.tryFind (fun (l: string) ->
    l.TrimStart().StartsWith(sprintf "trunk %s:" landingId, StringComparison.Ordinal)
    && not (l.Contains ": following (")
    && not (l.Contains ": queued behind"))

/// How many landings the trunk has said something about.
let private trunkLineCount (status: string) : int =
  status.Split('\n') |> Array.filter (fun (l: string) -> l.TrimStart().StartsWith("trunk ", StringComparison.Ordinal)) |> Array.length

let private waitFor (budget: TimeSpan) (describe: unit -> string) (probe: unit -> Task<bool>) : Task<unit> =
  task {
    let! ok = SageFs.Tests.TestInfrastructure.waitForAsync (int budget.TotalMilliseconds) probe
    match ok with
    | true -> ()
    | false -> failtestf "not satisfied within %.0fs: %s" budget.TotalSeconds (describe ())
  }

/// Wait until a landing is no longer in flight, and return its line.
let private awaitSettled (w: World) (landingId: string) : Task<string> =
  task {
    let last = ref ""
    do!
      waitFor TestTimeouts.heavyVerdictBudget (fun () -> sprintf "landing %s to settle. Last status:\n%s" landingId last.Value) (fun () -> task {
        let! status = statusOf w
        last.Value <- status
        match landingLine status landingId with
        | Some line -> return line.Contains "state=Landed" || line.Contains "state=Blocked"
        | None -> return false })
    let line = (landingLine last.Value landingId).Value
    match line.Contains "state=Blocked" with
    | false -> return line
    | true ->
      // A blocked landing says why it was blocked, and what the verifying session's tests were doing says what to look at.
      use probe = new HttpClient(Timeout = TestTimeouts.httpRequest)
      let! live =
        task {
          try return! probe.GetStringAsync(sprintf "http://localhost:%d/api/live-testing/status?session=%s" w.Port w.IntegrationSession)
          with ex -> return sprintf "(live testing status unreadable: %s)" ex.Message
        }
      return sprintf "%s\n  verifying session's live testing status: %s" line live
  }

/// Wait until the trunk has said what it did with a landing that landed, and return that line.
let private awaitTrunkLine (w: World) (landingId: string) (accept: string -> bool) : Task<string> =
  task {
    let last = ref ""
    do!
      waitFor TestTimeouts.saveVerdict (fun () -> sprintf "the trunk to report landing %s. Last status:\n%s" landingId last.Value) (fun () -> task {
        let! status = statusOf w
        last.Value <- status
        match trunkLine status landingId with
        | Some line -> return accept line
        | None -> return false })
    return (trunkLine last.Value landingId).Value
  }

/// Claim `relPath` for an agent, commit the edit on a branch of its own off `startPoint`, and queue the landing.
let private land (w: World) (agent: Agent) (relPath: string) (startPoint: string) (edits: (string * string) list) (message: string) : Task<string> =
  task {
    let! claimReply = callTool agent.Client "acquire_claim" [ "agentName", box agent.Name; "scope", box (sprintf "file:%s" relPath); "purpose", box message ]
    let claimId, fence = claimIdAndFence claimReply
    let branch = sprintf "%s-%s" agent.Name (Guid.NewGuid().ToString("N").Substring(0, 8))
    let! _ = git agent.Worktree [ "checkout"; "--quiet"; "-b"; branch; startPoint ]
    let! sha = editAndCommit agent.Worktree relPath edits message
    let! queued =
      callTool agent.Client "request_landing"
        [ "agentName", box agent.Name; "claims", box (sprintf "%s:%d" claimId fence); "commits", box sha; "statement", box message ]
    return landingIdOf queued
  }

let private integrationTip (w: World) : Task<string> = git w.Repo [ "rev-parse"; sprintf "refs/heads/%s" w.IntegrationBranch ]

let private appGet (w: World) (route: string) : Task<string> =
  w.App.GetStringAsync(sprintf "%s/%s" (w.App.BaseAddress.ToString().TrimEnd('/')) route)

/// Wait for the trunk's line about a landing while the app keeps being asked. A reload that no request enters is judged
/// NeverEntered once its grace window passes, and that verdict is final, so a waiter that only reads status lets a loaded
/// machine run the window out before the new body ever runs. Asking the app on every poll enters the body the moment the
/// reload applies.
let private awaitTrunkLineServing (w: World) (landingId: string) (routes: string list) (accept: string -> bool) : Task<string> =
  task {
    let last = ref ""
    do!
      waitFor TestTimeouts.saveVerdict (fun () -> sprintf "the trunk to report landing %s while the app served %A. Last status:\n%s" landingId routes last.Value) (fun () -> task {
        for route in routes do
          let! _ = appGet w route
          ()
        let! status = statusOf w
        last.Value <- status
        match trunkLine status landingId with
        | Some line -> return accept line
        | None -> return false })
    return (trunkLine last.Value landingId).Value
  }

/// "alice:v3#5" -> ("alice:v3", 5): what a handler said and how many requests the process had served by then.
let private splitCounted (served: string) : string * int =
  match served.LastIndexOf '#' with
  | -1 -> failwithf "the app answered %A, which carries no request count" served
  | i -> served.Substring(0, i), int (served.Substring(i + 1))

let private trunkReload (w: World) : Task<JsonElement> =
  task {
    let! text = callTool w.Alice.Client "get_session_status" [ "session_id", box w.TrunkSession ]
    use doc = JsonDocument.Parse text
    return doc.RootElement.GetProperty("lastReload").Clone()
  }

/// Wait until a session is Ready and, when `notWorker` names a worker process, until the worker is another one. Answers the worker's
/// process id.
let private awaitSessionReady (client: McpClient) (session: string) (notWorker: int64 option) : Task<int64> =
  task {
    let last = ref ""
    let pid = ref 0L
    do!
      waitFor TestTimeouts.workerSessionReady (fun () -> sprintf "session %s to be Ready (not worker %A). Last status: %s" session notWorker last.Value) (fun () -> task {
        let! text = callTool client "get_session_status" [ "session_id", box session; "wait_seconds", box (int TestTimeouts.readyBudget.TotalSeconds) ]
        last.Value <- text
        use doc = JsonDocument.Parse text
        let root = doc.RootElement
        let workerPid =
          match root.TryGetProperty "workerPid" with
          | true, p when p.ValueKind = JsonValueKind.Number -> p.GetInt64()
          | _ -> 0L
        pid.Value <- workerPid
        let anotherWorker =
          match notWorker with
          | Some before -> workerPid <> before
          | None -> true
        return root.GetProperty("lifecycle").GetString() = "Ready" && anotherWorker })
    return pid.Value
  }

let private newWorld () : Task<World> =
  task {
    let repo = Directory.CreateTempSubdirectory("trunk-landing-main-").FullName
    let dataDir = Directory.CreateTempSubdirectory("trunk-landing-data-").FullName
    let agents = Directory.CreateTempSubdirectory("trunk-landing-agents-").FullName
    let! _ = git repo [ "init"; "--quiet"; "-b"; "main" ]
    let! _ = git repo [ "config"; "user.email"; "trunk-landing@example.com" ]
    let! _ = git repo [ "config"; "user.name"; "Trunk Landing Test" ]
    for source in fixtureSources do
      File.Copy(Path.Combine(fixtureSourceDir, source), Path.Combine(repo, source))
    File.WriteAllText(Path.Combine(repo, fixtureProjectFile), fixtureProject ())
    File.Copy(Path.Combine(repoRoot, "global.json"), Path.Combine(repo, "global.json"))
    File.WriteAllText(Path.Combine(repo, ".gitignore"), "bin/\nobj/\n")
    let! _ = git repo [ "add"; "-A" ]
    let! _ = git repo [ "commit"; "--quiet"; "-m"; "base: the trunk fixture app" ]
    let! baseSha = git repo [ "rev-parse"; "HEAD" ]
    let agentIn (name: string) : Task<string> =
      task {
        let dir = Path.Combine(agents, name)
        let! _ = git repo [ "worktree"; "add"; "--quiet"; "--detach"; dir; baseSha ]
        return dir
      }
    let! aliceDir = agentIn "alice"
    let! bobDir = agentIn "bob"
    let! carolDir = agentIn "carol"

    let! daemon, port = startIsolatedDaemon repo dataDir
    AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> killProcess daemon)
    let! aliceClient = connect port
    let! bobClient = connect port
    let! carolClient = connect port
    let alice = { Name = "alice"; Client = aliceClient; Worktree = aliceDir }
    let bob = { Name = "bob"; Client = bobClient; Worktree = bobDir }
    let carol = { Name = "carol"; Client = carolClient; Worktree = carolDir }
    for agent in [ alice; bob; carol ] do
      let! joined = callTool agent.Client "join_cohort" [ "agentName", box agent.Name; "role", box "Implementer" ]
      joined |> Expect.stringContains (sprintf "%s joins the cohort" agent.Name) "Joined cohort"

    // Alice is first in, so she is the conductor, and sets where landings land.
    let! configured = callToolPatient alice.Client "set_integration_ref" [ "agentName", box alice.Name; "integrationRef", box "HEAD" ]
    configured |> Expect.stringContains "the integration is configured" "Integration configured: head="
    configured |> Expect.stringContains "it reports the trunk checkout the cohort lands into" "trunk="
    let branch = field "branch=" configured
    let trunkPath = field "trunk=" configured
    let integrationSession = field "session=" configured
    Directory.Exists trunkPath |> Expect.isTrue "the trunk checkout exists on disk"

    // The trunk session: a hot reload session whose working directory is the trunk checkout. Nothing runs in it yet.
    let! created =
      callToolPatient alice.Client "create_project_session"
        [ "project", box (Path.Combine(trunkPath, fixtureProjectFile))
          "working_directory", box trunkPath
          "workflow", box "hotreload" ]
    let trunkSession = created.Split('\n').[0].Trim()
    let! _ = awaitSessionReady alice.Client trunkSession None
    return
      { Repo = repo
        Daemon = daemon
        Port = port
        BaseSha = baseSha
        Alice = alice
        Bob = bob
        Carol = carol
        IntegrationBranch = branch
        TrunkPath = trunkPath
        TrunkSession = trunkSession
        IntegrationSession = integrationSession
        App = appClient }
  }

let private world = lazy (newWorld ())

let private getWorld () : Task<World> = world.Value

/// What the trunk app has been told to serve at the moment, as an address: set when run_app answers.
let private startTrunkApp (w: World) : Task<World> =
  task {
    let! switched = callTool w.Alice.Client "switch_session" [ "session_id", box w.TrunkSession ]
    ignore switched
    // The session was built before anything landed, and run_app starts the build it holds. The trunk says so when it finds no app
    // to update, so the session is rebuilt from the trunk checkout before the app starts. The reset answers once it is scheduled,
    // so the session is ready again when it reports a worker other than the one it had.
    let! workerBefore = awaitSessionReady w.Alice.Client w.TrunkSession None
    let! reset = callToolPatient w.Alice.Client "hard_reset_fsi_session" [ "rebuild", box true ]
    ignore reset
    let! _ = awaitSessionReady w.Alice.Client w.TrunkSession (Some workerBefore)
    let! ran = callToolPatient w.Alice.Client "run_app" [ "project", box "" ]
    let m = Regex.Match(ran, @"http://[^\s""\\/]+:(\d+)")
    match m.Success with
    | false -> return failwithf "run_app did not report where the trunk app listens: %s" ran
    | true ->
      let url = sprintf "http://127.0.0.1:%s" m.Groups[1].Value
      let client = new HttpClient(BaseAddress = Uri url, Timeout = TestTimeouts.httpRequest)
      return { w with App = client }
  }

let private mutableApp : World option ref = ref None

let private appWorld () : Task<World> =
  task {
    match mutableApp.Value with
    | Some w -> return w
    | None ->
      let! w = getWorld ()
      let! started = startTrunkApp w
      mutableApp.Value <- Some started
      return started
  }

[<Tests>]
let tests =
  Integration.hostList "TrunkLanding" [
    if not (gitAvailable ()) then
      testCase "git must be available on PATH" <| fun () -> failtest "git is not available on PATH, and the trunk landing gate needs a real git"
    else
    testSequenced
    <| testList "a landing reaches the running trunk app" [

      testTask "WHY: a trunk session that runs no app records a landing and says there is no running app to update" {
        let! w = getWorld ()
        let! landingId = land w w.Alice "Alice.fs" w.BaseSha [ "alice:v1", "alice:v2" ] "alice: v2"
        let! landed = awaitSettled w landingId
        landed |> Expect.stringContains "the landing landed" "state=Landed"
        let! line = awaitTrunkLine w landingId (fun _ -> true)
        line |> Expect.stringContains "the trunk says what it found" "no running app to update"
        File.ReadAllText(Path.Combine(w.TrunkPath, "Alice.fs"))
        |> Expect.stringContains "the trunk checkout holds the landed file, so a run_app that starts later serves it" "alice:v2"
      }

      testTask "WHY: two agents each land a handler of their own and the one trunk process serves both, its counter carrying on" {
        let! started = appWorld ()
        let! first = appGet started "alice"
        splitCounted first |> fst |> Expect.equal "the app starts from the trunk checkout, so it serves what already landed" "alice:v2"
        let! pidBefore = appGet started "pid"
        let! _ = appGet started "bob"

        // Both landings are queued before either has settled, so they run back to back through the one queue.
        let! tip = integrationTip started
        let! aliceLanding = land started started.Alice "Alice.fs" tip [ "alice:v2", "alice:v3" ] "alice: v3"
        let! bobLanding = land started started.Bob "Bob.fs" tip [ "bob:v1", "bob:v2" ] "bob: v2"
        let! aliceSettled = awaitSettled started aliceLanding
        let! bobSettled = awaitSettled started bobLanding
        aliceSettled |> Expect.stringContains "alice's landing landed" "state=Landed"
        bobSettled |> Expect.stringContains "bob's landing landed" "state=Landed"

        let! aliceTrunk = awaitTrunkLineServing started aliceLanding [ "alice"; "bob" ] (fun l -> l.Contains "Patched")
        let! bobTrunk = awaitTrunkLineServing started bobLanding [ "alice"; "bob" ] (fun l -> l.Contains "Patched")
        aliceTrunk |> Expect.stringContains "alice's landing names the mechanism that carried it" "metadata-delta"
        bobTrunk |> Expect.stringContains "bob's landing names the mechanism that carried it" "metadata-delta"

        // One process serves both new behaviors, and the counter it holds in memory never went back.
        let! aliceServed = appGet started "alice"
        let! bobServed = appGet started "bob"
        let aliceSaid, aliceCount = splitCounted aliceServed
        let bobSaid, bobCount = splitCounted bobServed
        aliceSaid |> Expect.equal "the app serves alice's landed behavior" "alice:v3"
        bobSaid |> Expect.equal "the app serves bob's landed behavior" "bob:v2"
        (aliceCount < bobCount) |> Expect.isTrue "the counter carried on from the requests before the landings"
        (snd (splitCounted first) < aliceCount) |> Expect.isTrue "the counter never went back to zero"
        let! pidAfter = appGet started "pid"
        pidAfter |> Expect.equal "the same process served the whole time" pidBefore

        // The new bodies ran, so the reload row says Patched, by the mechanism that did it.
        bobTrunk |> Expect.stringContains "once the new body has run the trunk line says Patched" "Patched"
        let! (reload: JsonElement) = trunkReload started
        reload.GetProperty("outcome").GetString() |> Expect.equal "the session's own reload row agrees" "Patched"
        reload.GetProperty("mechanism").GetString() |> Expect.equal "and names the mechanism" "metadata-delta"
      }

      testTask "WHY: a landing that conflicts with what landed is refused with its reason and the trunk app is untouched" {
        let! w = appWorld ()
        let! before = statusOf w
        let! tipBefore = integrationTip w
        let! pidBefore = appGet w "pid"
        // Carol's change starts from the original base, and changes the very line alice's landings changed.
        let! landingId = land w w.Carol "Alice.fs" w.BaseSha [ "alice:v1", "alice:carol" ] "carol: the same line"
        let! settled = awaitSettled w landingId
        settled |> Expect.stringContains "the landing is refused" "state=Blocked"
        settled |> Expect.stringContains "with the reason the cohort already gives" "RebaseConflict"
        settled |> Expect.stringContains "naming the file" "Alice.fs"
        let! after = statusOf w
        (trunkLine after landingId) |> Expect.isNone "the trunk was told nothing about a landing that did not land"
        trunkLineCount after |> Expect.equal "no new trunk line appeared" (trunkLineCount before)
        let! tipAfter = integrationTip w
        tipAfter |> Expect.equal "the integration branch did not move" tipBefore
        File.ReadAllText(Path.Combine(w.TrunkPath, "Alice.fs"))
        |> Expect.stringContains "the trunk checkout still holds alice's landed file" "alice:v3"
        let! served = appGet w "alice"
        splitCounted served |> fst |> Expect.equal "the app still serves what landed" "alice:v3"
        let! pidAfter = appGet w "pid"
        pidAfter |> Expect.equal "in the same process" pidBefore
      }

      testTask "WHY: a landing that needs a restart restarts the app, names the cause, and says it landed" {
        let! w = appWorld ()
        let! pidBefore = appGet w "pid"
        let! tip = integrationTip w
        let! landingId =
          land w w.Bob "Rude.fs" tip
            [ "abstract Name: unit -> string", "abstract Name: int -> string"
              "override _.Name() : string = \"rude:v1\"", "override _.Name(n: int) : string = \"rude:v2\" + string n"
              "shape.Name()", "shape.Name(1)" ]
            "bob: the virtual member takes an argument"
        let! settled = awaitSettled w landingId
        settled |> Expect.stringContains "the landing landed" "state=Landed"
        let! line = awaitTrunkLine w landingId (fun l -> l.Contains "Restarted")
        line |> Expect.stringContains "the trunk restarted the app" "Restarted"
        line |> Expect.stringContains "and names what could not be patched" "Shape"
        do!
          waitFor TestTimeouts.heavyVerdictBudget (fun () -> "the restarted app to answer from a new process") (fun () -> task {
            try
              let! pid = appGet w "pid"
              return pid <> pidBefore
            with _ -> return false })
        let! served = appGet w "rude"
        let said, count = splitCounted served
        said |> Expect.stringStarts "the restarted app serves the landed behavior" "rude:v2"
        count |> Expect.equal "its in-memory state started over, which is what a restart is" 1
        let! bob = appGet w "bob"
        splitCounted bob |> fst |> Expect.equal "and it still serves everything that landed before" "bob:v2"
      }

      // Last, because a landing the gate blocks leaves what the verifying session evaluated for it in that session, and the next
      // landing is verified against that. The trunk follows only what lands, so this row's own claims hold wherever it runs.
      testTask "WHY: a landing whose verification fails does not land, and the trunk app serves the old behavior" {
        let! w = appWorld ()
        let! tipBefore = integrationTip w
        let! servedBefore = appGet w "bob"
        // Carol changes bob's handler so it no longer speaks as bob, and the test that says what a handler's message looks like
        // goes red in the verifying session.
        let! landingId = land w w.Carol "Bob.fs" tipBefore [ "bob:v2", "broken" ] "carol: bob stops speaking as bob"
        let! settled = awaitSettled w landingId
        settled |> Expect.stringContains "the landing is blocked" "state=Blocked"
        settled |> Expect.stringContains "because a test failed" "FailingTests"
        let! status = statusOf w
        (trunkLine status landingId) |> Expect.isNone "the trunk was told nothing about it"
        File.ReadAllText(Path.Combine(w.TrunkPath, "Bob.fs"))
        |> Expect.stringContains "the trunk checkout still holds bob's landed file" "bob:v2"
        let! servedAfter = appGet w "bob"
        splitCounted servedAfter |> fst |> Expect.equal "the app serves the old behavior" (fst (splitCounted servedBefore))
      }
    ]
  ]
