/// Outcome gate for Island D / outcome-gate-sweep.md §3 Gap K's `run_app`
/// row: "one --integration-host case using samples/demos/SageFs.Samples.
/// ConsoleTicker (74 lines, currently referenced by no test): run_app →
/// assert the process is live and producing output → stop_app → assert it
/// is gone."
///
/// Before this file, AppRunOrchestrationTests.fs (645 lines) tested the
/// run_app/stop_app DECISION ENGINE against a fake `ops` record, and
/// nothing called the run_app/stop_app MCP TOOL FUNCTIONS at all — the
/// tools an agent actually calls (McpTools.fs's run_app/stop_app members)
/// had zero coverage.
///
/// Uses samples/demos/SageFs.Samples.ConsoleTicker exactly as the gap
/// prescribes — read-only; this file never edits it.
///
/// HONEST DEVIATION FROM THE LITERAL PRESCRIPTION. Reading
/// SageFs.Host/AppRunner.fs before writing this test surfaced two real
/// product facts the prescription's author could not have known without
/// reading that file:
///
/// 1. `run_app` does not spawn a separate OS process. AppRunner.fs's
///    `launch` starts the compiled entry point on an in-process background
///    Thread inside the worker — there is no PID to probe for liveness.
/// 2. AppRunner.fs's `Stop` handler has a `ThreadOnly` branch: a console app
///    with no ASP.NET host cannot be stopped in place at all —
///    `reply.Reply(Error "%s has no host to stop, so it cannot be stopped
///    in place. → Hard-reset the session to stop it.")` — and the tool's
///    own doc comment (McpTools.fs's stop_app Description) says the same
///    thing. ConsoleTicker is exactly this shape (a plain `[<EntryPoint>]`
///    with no web host), so stop_app on it can never report "gone" — that
///    would require stop_app to LIE about succeeding.
///
/// So this gate proves the promise the product actually makes for a
/// host-less console app: run_app really starts it (state, entry point,
/// run id), a second run_app call proves it is still the SAME live
/// instance (AlreadyRunning with an unchanged RunId — the only liveness
/// signal available for an in-process thread with no PID), stop_app is
/// HONEST about the one thing it cannot do rather than silently no-op'ing
/// success, and the tool's own documented remedy
/// (hard_reset_fsi_session) really does make it gone — read back through
/// list_runnable_projects's `App` field, the only "what is the app doing
/// right now" MCP read available (there is no dedicated get-app-state tool).
module SageFs.Tests.McpAppRunOutcomeTests

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol

module Integration = SageFs.Tests.TestInfrastructure.Integration
module Http = SageFs.Tests.HttpApiIntegrationTests

let private consoleTickerProject =
  Path.Combine(Http.repoRoot, "samples", "demos", "SageFs.Samples.ConsoleTicker", "SageFs.Samples.ConsoleTicker.fsproj")

let private consoleTickerDir =
  match Path.GetDirectoryName consoleTickerProject with
  | null -> failwith "consoleTickerProject has no directory"
  | dir -> dir

/// Extract the tool's OWN answer — the FIRST TextContentBlock only.
/// Verified live (see McpToolOutcomeTests.fs's textOf for the full story):
/// McpServer.fs's createServerCaptureFilter appends a SECOND
/// TextContentBlock ("📡 SageFs events since last call: ...") to every tool
/// result whenever session events accumulated since the caller's last call.
/// Concatenating every block corrupts a JSON-returning tool's own payload.
let private textOf (result: CallToolResult) : string =
  result.Content
  |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
  |> Seq.tryHead
  |> Option.defaultValue ""

let private connect (port: int) : Task<McpClient> =
  let opts = HttpClientTransportOptions(Endpoint = Uri(sprintf "http://localhost:%d/" port))
  let transport = HttpClientTransport(opts, (null: Microsoft.Extensions.Logging.ILoggerFactory))
  McpClient.CreateAsync(transport, null, null, CancellationToken.None)

/// run_app restarts an Interactive session into HotReload before launching
/// (AppRunOrchestration.fs's startPhaseFor), and hard_reset_fsi_session
/// respawns the worker — both bounded by Timeouts.warmupReadyPollMax
/// (120s), so this tool call needs real patience, unlike a plain read tool.
let private callToolPatient (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> =
  task {
    use cts = new CancellationTokenSource(TimeSpan.FromSeconds 150.0)
    let! result = client.CallToolAsync(name, readOnlyDict args, null, null, cts.Token)
    return textOf result
  }

let private callTool (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> =
  task {
    use cts = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)
    let! result = client.CallToolAsync(name, readOnlyDict args, null, null, cts.Token)
    return textOf result
  }

[<Tests>]
let mcpAppRunOutcomeTests =
  Integration.hostList "MCP run_app / stop_app outcome gates" [
    testTask "WHY — run_app starts the console ticker as the same live instance across repeated calls, and stop_app is honest about the app it cannot stop in place (Gap K)" {
      let port = Http.reserveLoopbackPort (Some (38950 + Random().Next(100)))
      let! proc, httpClient = Http.startDaemonWithArgs port consoleTickerDir [ "--no-resume" ]

      try
        let! createStatus, createBody = Http.createSession httpClient consoleTickerProject consoleTickerDir
        createStatus |> Expect.equal "session create should succeed" 200

        let! ready, sessionsBody = Http.waitForReadySession httpClient consoleTickerDir (TimeSpan.FromSeconds 60.0)
        ready
        |> Expect.isTrue (
          sprintf "console ticker session should reach Ready. Create: %s Sessions: %s" createBody sessionsBody)

        use! client = connect port

        // ── run_app: an Interactive session first restarts into HotReload,
        //    then launches the entry point on an in-process background
        //    thread (AppRunner.fs — there is no separate OS process) ──
        let! runRaw = callToolPatient client "run_app" [ "project", box "" ]
        let runDoc = JsonDocument.Parse(runRaw: string)
        let runRoot = runDoc.RootElement
        runRoot.GetProperty("State").GetString()
        |> Expect.equal "run_app should report the ticker Running" "Running"

        let runId = runRoot.GetProperty("RunId").GetString()
        String.IsNullOrWhiteSpace runId
        |> Expect.isFalse "a running app must carry a RunId"

        runRoot.GetProperty("EntryPoint").GetString()
        |> Expect.stringContains "run_app should report the ticker's real entry point" "ConsoleTicker"

        // ── run_app again: AlreadyRunning with the SAME RunId is the only
        //    liveness signal available for an in-process thread with no
        //    PID — it proves the ticker is still the original instance,
        //    not a silently-crashed-and-restarted one ──
        let! rerunRaw = callTool client "run_app" [ "project", box "" ]
        let rerunDoc = JsonDocument.Parse(rerunRaw: string)
        let rerunRoot = rerunDoc.RootElement
        rerunRoot.GetProperty("State").GetString()
        |> Expect.equal "a second run_app call should still see the app Running" "Running"
        rerunRoot.GetProperty("RunId").GetString()
        |> Expect.equal "the second run_app call should observe the SAME run, not a new one" runId

        // ── list_runnable_projects: cross-tool consistency — its App field
        //    must agree with what run_app itself just reported ──
        let! runnableRaw = callTool client "list_runnable_projects" []
        let runnableDoc = JsonDocument.Parse(runnableRaw: string)
        runnableDoc.RootElement.GetProperty("App").GetString()
        |> Expect.notEqual "list_runnable_projects should agree the app is running, not report it stopped" "Not running"

        // ── stop_app: HONESTLY refused for a host-less console app — not a
        //    silent no-op that lies about success (AppRunner.fs's
        //    ThreadOnly branch) ──
        let! stopRaw = callTool client "stop_app" []
        stopRaw
        |> Expect.stringContains
          "stop_app must tell the truth: a console app with no host cannot be stopped in place"
          "cannot be stopped in place"
        stopRaw
        |> Expect.stringContains "stop_app's honest refusal must point at the real remedy" "Hard-reset"

        // ── the app is still alive after the refused stop — the refusal is
        //    truthful precisely because nothing actually stopped it ──
        let! runnableAfterStopRaw = callTool client "list_runnable_projects" []
        let runnableAfterStopDoc = JsonDocument.Parse(runnableAfterStopRaw: string)
        runnableAfterStopDoc.RootElement.GetProperty("App").GetString()
        |> Expect.notEqual "a refused stop_app must not have silently ended the app" "Not running"

        // ── the tool's own documented remedy really does end it ──
        let! resetRaw = callToolPatient client "hard_reset_fsi_session" [ "rebuild", box false ]
        String.IsNullOrWhiteSpace resetRaw
        |> Expect.isFalse "hard_reset_fsi_session should report something"

        let! readyAfterReset, sessionsAfterReset =
          Http.waitForReadySession httpClient consoleTickerDir (TimeSpan.FromSeconds 90.0)
        readyAfterReset
        |> Expect.isTrue (sprintf "session should come back Ready after hard reset. Sessions: %s" sessionsAfterReset)

        let! runnableAfterResetRaw = callTool client "list_runnable_projects" []
        let runnableAfterResetDoc = JsonDocument.Parse(runnableAfterResetRaw: string)
        runnableAfterResetDoc.RootElement.GetProperty("App").GetString()
        |> Expect.equal
          "hard-reset is the documented way to actually end a host-less app — it must now read Not running"
          "Not running"
      finally
        httpClient.Dispose()
        Http.killDaemon proc
    }
  ]
