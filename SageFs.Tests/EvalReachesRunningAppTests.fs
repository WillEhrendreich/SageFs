/// The eval route to a running app, driven end to end.
///
/// The roadmap says of it: "One writes the evaluated declaration to source and lets save, build
/// and delta carry it, which costs the build. That one is in: an eval that names its file and
/// line writes the declaration back, journaled so it undoes. Nobody has driven it end to end
/// against a running run_app app yet, so I can't tell you how long an eval takes to be served."
///
/// The pieces were each tested apart. The write-back's own tests stop at the decision, and
/// nothing ever wrote a file; the delta's own tests write the file by hand. This test joins
/// them: an eval that names a file and a line writes the declaration back into the file, the
/// file change carries it into the app that `run_app` started, and the running app serves the
/// new code.
///
/// It also answers the roadmap's question. The time from the eval to the app serving the new
/// value is measured and printed, so the page can carry a real figure, and it is bounded, so a
/// change that makes the route slow is caught rather than felt.
///
/// Two things the write-back insists on, and this test has to give it both:
///
///   * the submission declares ONE thing, and it is the declaration at the line it names,
///     because the write-back replaces only what the eval redefined;
///   * the file it names is one the session owns, because the write-back refuses anything else.
module SageFs.Tests.EvalReachesRunningAppTests

open System
open System.Diagnostics
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
module Sample = SageFs.Tests.ConsoleTickerSample

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

/// The eval, with the file and the line that ask for the write.
let private evalWith (client: McpClient) (code: string) (file: string) (line: int) : Task<string> =
  task {
    use cts = new CancellationTokenSource(TestTimeouts.toolCallThatRestarts)
    let! result =
      client.CallToolAsync(
        "send_fsharp_code",
        readOnlyDict
          [ "agentName", box "eval-reaches-app"; "code", box code; "file_path", box file; "block_start_line", box line ],
        null,
        null,
        cts.Token)
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

/// The last few ticker lines in a dashboard text, for a failure message that says what the app WAS
/// printing rather than what the page chrome said.
let private lastTickerLines (text: string) : string =
  Regex.Matches(text, @"#\d+\s+[A-Za-z][^\d]{0,60}")
  |> Seq.cast<Match>
  |> Seq.map (fun m -> m.Value.Trim())
  |> Seq.rev
  |> Seq.truncate 3
  |> Seq.rev
  |> String.concat " | "

/// The ticker line that carries the marker, if the page has one.
///
/// The ticker's line has a fixed shape: a clock, a counter, then the message. That shape is what
/// this looks for, and the message has to follow the counter directly, because the dashboard's
/// markup carries no line breaks once its tags are stripped, and a looser pattern matches the
/// eval's own echo (which names the marker) somewhere else on the page and reports the app as
/// served before it has printed a thing.
let private tickerLine (text: string) (marker: string) : string option =
  Regex.Matches(text, @"\d{2}:\d{2}:\d{2}\.\d\s+#\d+\s+" + Regex.Escape marker)
  |> Seq.cast<Match>
  |> Seq.tryFind (fun _ -> true)
  |> Option.map (fun m -> m.Value.Trim())

/// Poll the dashboard until `predicate` holds, up to `timeout`. Returns the last text seen either
/// way, and the moment the predicate first held.
let private waitForDashboard (http: HttpClient) (dashboardUrl: string) (timeout: TimeSpan) (predicate: string -> bool) : Task<bool * string * TimeSpan> =
  task {
    let started = DateTime.UtcNow
    let mutable last = ""
    let mutable satisfied = false
    let mutable at = TimeSpan.Zero
    while not satisfied && DateTime.UtcNow - started < timeout do
      let! text = dashboardText http dashboardUrl
      last <- text
      satisfied <- predicate text
      if satisfied then at <- DateTime.UtcNow - started
      if not satisfied then do! Task.Delay TestTimeouts.slowPoll
    return satisfied, last, at
  }

/// The 1-based line of the declaration that starts with `let <name>`, so the write-back is told
/// where the thing it replaces lives.
let private lineOfDeclaration (source: string) (name: string) : int =
  let lines = source.Split('\n')
  let index =
    lines
    |> Array.findIndex (fun l -> l.TrimStart().StartsWith(sprintf "let %s " name) || l.TrimStart().StartsWith(sprintf "let %s(" name))
  if index < 0 then failtestf "the sample has no declaration named %s" name
  index + 1

[<Tests>]
let evalReachesRunningAppTests =
  Integration.hostList "eval reaches a running app" [
    testTask "WHY — an eval that names a file and a line reaches the app that run_app started, because the write-back, the save and the delta have never been driven in one go" {
      let workDir = Sample.copy ()
      let project = Path.Combine(workDir, "SageFs.Samples.ConsoleTicker.fsproj")
      let source = Path.Combine(workDir, "Ticker.fs")
      let original = File.ReadAllText source
      original |> Expect.stringContains "the fixture still ships the message this gate changes" Sample.shippedMessage

      // Built through SageFs's own build path, as a session rebuild would: the HTTP create route
      // does not build, and this gives the copy the unoptimized build (with the injected Core
      // reference) that hot reload is specified against.
      let! built = SessionBuild.runBuildAsync [ project ] workDir |> Async.StartAsTask
      match built with
      | Ok _ -> ()
      | Error err -> failtestf "the private ticker copy must build through SageFs's own build path: %s" (SageFsError.describe err)

      let marker = sprintf "evaluated-%s" (Guid.NewGuid().ToString("N").Substring(0, 8))
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
        let! (printing: bool), (before: string), _ =
          waitForDashboard dashboardHttp dashboardUrl TestTimeouts.appOutputAppears (fun t -> (tickerLine t Sample.shippedMessage).IsSome)
        printing
        |> Expect.isTrue (sprintf "the running ticker should print '%s' before any eval. Last lines: %s" Sample.shippedMessage (lastTickerLines before))

        // The eval. It names the file and the line of the declaration it redefines, which is what
        // asks the write-back to do its work. The type is spelled out because the REPL has no
        // `open System`, and an eval that failed would write nothing back at all.
        let line = lineOfDeclaration original "renderLine"
        let code =
          sprintf
            "let renderLine (n: int) (now: System.DateTime) : string =\n  let message = \"%s\"\n  sprintf \"%%s  #%%d  %%s\" (now.ToString \"HH:mm:ss.f\") n message"
            marker

        // The clock starts at the eval: the number the roadmap wants is how long the whole route
        // takes, and the route begins with the eval.
        let sw = Stopwatch.StartNew()
        let! evalText = evalWith client code source line
        evalText
        |> Expect.stringContains
          (sprintf "the eval should have written the declaration back into the file. The tool said: %s" evalText)
          (sprintf "written back to %s:%d" source line)

        // The file now holds the new text: the write-back wrote the source, not just the answer.
        let afterWrite = File.ReadAllText source
        afterWrite |> Expect.stringContains "the file should hold the evaluated text" marker

        // And the app serves it. The clock runs from the eval to the first line that carries the
        // new value, which is the number the roadmap asks for.
        let! (served: bool), (after: string), _ =
          waitForDashboard dashboardHttp dashboardUrl TestTimeouts.appOutputAfterSave (fun t -> (tickerLine t marker).IsSome)
        let servedIn = sw.Elapsed
        printfn
          "eval to running app: %d ms (the eval to the first ticker line carrying the new value). The line was: %s"
          (int servedIn.TotalMilliseconds)
          (tickerLine after marker |> Option.defaultValue "(none)")
        (servedIn, TimeSpan.FromSeconds TestTimeouts.evalToAppServedSeconds)
        |> Expect.isLessThan "the eval route should be quick enough to be worth taking"
        served
        |> Expect.isTrue
          (sprintf "the running app should print the evaluated message '%s' after the eval. Last lines: %s" marker (lastTickerLines after))
      finally
        dashboardHttp.Dispose()
        httpClient.Dispose()
        Http.killDaemon proc
        try Directory.Delete(workDir, true) with _ -> ()
    }
  ]
