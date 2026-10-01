/// The hot-reload promise as a user meets it: start the app with run_app, edit its
/// source, save, and what the RUNNING app does changes.
///
/// Before this file, the "real app" reload tests started their apps inside FSI
/// (`#load` the sources, then `App.run port`), which is where the reload agent
/// lives. None of them saved a file against an app started by `run_app`, which is
/// the documented way to run one. Measured live on 0.6.843, with a build SageFs
/// made itself (optimizations off), for both a console app and a web app: the
/// save reported "Hot reloaded 1 of 1 changed definition(s)" and the running app
/// went on printing (and serving) the old text.
///
/// The reason is a process split. Every session's reload agent lives in the FSI
/// host, and `run_app` runs the app on a thread in the WORKER, so the agent
/// re-pointed the FSI host's copy of the function and the app never called it.
///
/// What this gate does NOT care about is HOW the change reaches the app (an
/// in-place patch or a restart). It cares that a save changes what the app does,
/// and that SageFs never says it did when it did not.
///
/// It works on its OWN COPY of the ticker sample. The save edits a source file,
/// and the shared sample is used by other suites in the same run: an edit there
/// restarts their running app under them.
module SageFs.Tests.RunAppSaveOutcomeTests

open System
open System.IO
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open SageFs

module Integration = SageFs.Tests.TestInfrastructure.Integration
module Http = SageFs.Tests.HttpApiIntegrationTests
module TestTimeouts = SageFs.Tests.TestInfrastructure.TestTimeouts

let private sharedTickerDir = Path.Combine(Http.repoRoot, "samples", "demos", "SageFs.Samples.ConsoleTicker")

/// The files that make the sample buildable. `packages.lock.json` matters: CI
/// restores in locked mode, and a project with no lock file would fail there.
let private sampleFiles =
  [ "SageFs.Samples.ConsoleTicker.fsproj"; "Program.fs"; "Ticker.fs"; "packages.lock.json" ]

/// A private copy beside the real sample, so the fsproj's relative path to the
/// repository (Directory.Build.props, SageFs.Core) still resolves.
let private copySample () : string =
  let dir =
    Path.Combine(Http.repoRoot, "samples", "demos", sprintf "SageFs.Samples.ConsoleTicker.SaveGate.%s" (Guid.NewGuid().ToString("N").Substring(0, 8)))
  Directory.CreateDirectory dir |> ignore
  for name in sampleFiles do
    File.Copy(Path.Combine(sharedTickerDir, name), Path.Combine(dir, name))
  dir

/// The message the ticker prints after its counter, as shipped.
let private shippedMessage = "SageFs keeps ticking"

let private connect (port: int) : Task<McpClient> =
  let opts = HttpClientTransportOptions(Endpoint = Uri(sprintf "http://localhost:%d/" port))
  let transport = HttpClientTransport(opts, (null: Microsoft.Extensions.Logging.ILoggerFactory))
  McpClient.CreateAsync(transport, null, null, CancellationToken.None)

let private textOf (result: CallToolResult) : string =
  result.Content
  |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
  |> Seq.tryHead
  |> Option.defaultValue ""

/// run_app may restart the session into HotReload first, so it needs patience.
let private runApp (client: McpClient) : Task<string> =
  task {
    use cts = new CancellationTokenSource(TestTimeouts.toolCallThatRestarts)
    let! result = client.CallToolAsync("run_app", readOnlyDict [ "project", box "" ], null, null, cts.Token)
    return textOf result
  }

/// What the dashboard shows the app has printed, as plain text.
let private dashboardText (http: HttpClient) (dashboardUrl: string) : Task<string> =
  task {
    try
      let! html = http.GetStringAsync dashboardUrl
      return Regex.Replace(html, "<[^>]*>", "")
    with _ -> return ""
  }

/// The last few ticker lines in a dashboard text, for a failure message that says
/// what the app WAS printing rather than what the page chrome said.
let private lastTickerLines (text: string) : string =
  Regex.Matches(text, @"#\d+\s+[A-Za-z][^\d]{0,60}")
  |> Seq.cast<Match>
  |> Seq.map (fun m -> m.Value.Trim())
  |> Seq.rev
  |> Seq.truncate 3
  |> Seq.rev
  |> String.concat " | "

/// Poll the dashboard until `predicate` holds, up to `timeout`. Returns the last
/// text seen either way.
let private waitForDashboard (http: HttpClient) (dashboardUrl: string) (timeout: TimeSpan) (predicate: string -> bool) : Task<bool * string> =
  task {
    let started = DateTime.UtcNow
    let mutable last = ""
    let mutable satisfied = false
    while not satisfied && DateTime.UtcNow - started < timeout do
      let! text = dashboardText http dashboardUrl
      last <- text
      satisfied <- predicate text
      if not satisfied then do! Task.Delay TestTimeouts.slowPollInterval
    return satisfied, last
  }

[<Tests>]
let runAppSaveOutcomeTests =
  Integration.hostList "run_app save outcome" [
    testTask "WHY — a save to a file of an app started with run_app changes what the RUNNING app prints, because the reload agent and the app are in different processes and an in-place patch reported success while the app kept printing the old text" {
      let workDir = copySample ()
      let project = Path.Combine(workDir, "SageFs.Samples.ConsoleTicker.fsproj")
      let source = Path.Combine(workDir, "Ticker.fs")
      let original = File.ReadAllText source
      original |> Expect.stringContains "the fixture still ships the message this gate edits" shippedMessage

      // Built through SageFs's own build path, as a session rebuild would: the HTTP
      // create route does not build, and this gives the copy the unoptimized build
      // (with the injected Core reference) that hot reload is specified against.
      let! built = SessionBuild.runBuildAsync [ project ] workDir |> Async.StartAsTask
      match built with
      | Ok _ -> ()
      | Error err -> failtestf "the private ticker copy must build through SageFs's own build path: %s" (SageFsError.describe err)

      let marker = sprintf "saved-%s" (Guid.NewGuid().ToString("N").Substring(0, 8))
      let port = Http.reserveLoopbackPort ()
      let dashboardUrl = sprintf "http://localhost:%d/dashboard" (port + 1)
      let! proc, httpClient = Http.startDaemonWithArgs port workDir [ "--no-resume" ]
      let dashboardHttp = new HttpClient(Timeout = TestTimeouts.dashboardProbe)

      try
        let! createStatus, createBody = Http.createSession httpClient project workDir
        createStatus |> Expect.equal (sprintf "session create should succeed: %s" createBody) 200

        // A cold build (restore, compile) happens before the session is Ready.
        let! ready, sessionsBody = Http.waitForReadySession httpClient workDir TestTimeouts.sessionReadyColdBuild
        ready |> Expect.isTrue (sprintf "the ticker session should reach Ready. Sessions: %s" sessionsBody)

        use! client = connect port
        let! runRaw = runApp client
        runRaw |> Expect.stringContains "run_app should have started the ticker" "Running"

        // Baseline: the app is live and printing the shipped message.
        let! (printing: bool), (before: string) =
          waitForDashboard dashboardHttp dashboardUrl TestTimeouts.appOutputAppears (fun t -> t.Contains shippedMessage)
        printing
        |> Expect.isTrue (sprintf "the running ticker should print '%s' before any edit. Last lines: %s" shippedMessage (lastTickerLines before))

        // The save. The only thing that changes is the message text.
        File.WriteAllText(source, original.Replace(sprintf "\"%s\"" shippedMessage, sprintf "\"%s\"" marker))

        // A rebuild and restart is allowed; being told "Patched" while nothing changes is not.
        let! (changed: bool), (after: string) =
          waitForDashboard dashboardHttp dashboardUrl TestTimeouts.appOutputAfterSave (fun t -> t.Contains marker)
        changed
        |> Expect.isTrue
          (sprintf "the running app should print the edited message '%s' after the save. Last lines: %s" marker (lastTickerLines after))
      finally
        dashboardHttp.Dispose()
        httpClient.Dispose()
        Http.killDaemon proc
        try Directory.Delete(workDir, true) with _ -> ()
    }
  ]
