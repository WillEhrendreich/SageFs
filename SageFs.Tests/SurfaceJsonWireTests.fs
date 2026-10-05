/// The exact JSON text the client-facing surfaces write (the Jupyter wire, the SSE events, the
/// daemon client, the dashboard's own endpoints, the update-check cache and the theme file).
/// They all write through `SageFs.Json`, and these pin what each one produces, so a change to a
/// profile or to a site shows up as a changed string here and not as a client that stops parsing.
///
/// Anonymous records write their keys in alphabetical order, so that is the order below.
module SageFs.Tests.SurfaceJsonWireTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Expecto
open Expecto.Flip
open SageFs
open SageFs.JupyterKernel
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.Dashboard

/// F# reads a backslash-u escape even inside triple quotes, so an expected string writes it as
/// `~u` and this turns it back: `~u002B` is the six characters backslash, u, 0, 0, 2, B that
/// System.Text.Json writes for a plus sign.
let private esc (text: string) = text.Replace("~u", "\\u")

// ── Jupyter wire ──

let private fixedHeader : MessageHeader =
  { MsgId = "m-1"
    Session = "s-1"
    Username = "u"
    Date = DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
    MsgType = "execute_request"
    Version = "5.3" }

let private noopComplete : CompleteHandler =
  fun _ _ -> async { return { Matches = []; CursorStart = 0; CursorEnd = 0; Status = "ok" } }
let private noopIsComplete : IsCompleteHandler = fun _ -> async { return CompleteStatus.Complete }

let private executeMessage : JupyterMessage =
  { Header = fixedHeader
    ParentHeader = None
    Metadata = Map.empty
    Content =
      MessageContent.ExecuteRequest { Code = "let x = \"a<b>\"\n"; Silent = false; StoreHistory = true; AllowStdin = false } }

let private iopubOf (events: KernelLifecycle.KernelEvent list) : (string * string) list =
  events |> List.choose (function KernelLifecycle.PublishIOPub (kind, json) -> Some (kind, json) | _ -> None)

[<Tests>]
let jupyterWireTests =
  testList "Jupyter wire JSON text" [

    test "WHY: the header is written with snake_case keys in this order, because jupyter_client signs these exact bytes" {
      WireProtocol.serializeHeader fixedHeader
      |> Expect.equal
           "header text"
           (esc """{"date":"2026-01-02T03:04:05.0000000~u002B00:00","msg_id":"m-1","msg_type":"execute_request","session":"s-1","username":"u","version":"5.3"}""")
    }

    test "WHY: an execute_request body carries the code, the flags and an empty user_expressions object" {
      WireProtocol.serializeContent (MessageContent.ExecuteRequest { Code = "1+1"; Silent = false; StoreHistory = true; AllowStdin = false })
      |> Expect.equal
           "execute_request text"
           (esc """{"allow_stdin":false,"code":"1~u002B1","silent":false,"stop_on_error":true,"store_history":true,"user_expressions":{}}""")
    }

    test "WHY: complete, shutdown and is_complete bodies are one small object each" {
      WireProtocol.serializeContent (MessageContent.CompleteRequest { Code = "List."; CursorPos = 5 })
      |> Expect.equal "complete_request text" """{"code":"List.","cursor_pos":5}"""
      WireProtocol.serializeContent (MessageContent.ShutdownRequest true)
      |> Expect.equal "shutdown_request text" """{"restart":true}"""
      WireProtocol.serializeContent (MessageContent.CheckCompleteRequest "let x =")
      |> Expect.equal "is_complete_request text" """{"code":"let x ="}"""
    }

    test "WHY: the kernel_info_reply body is snake_case all the way down, with the banner's dash escaped" {
      let reply : KernelInfoReply =
        { ProtocolVersion = "5.3"
          Implementation = "sagefs"
          ImplementationVersion = "0.5.0"
          LanguageInfo =
            { Name = "fsharp"
              Version = "11.0"
              MimeType = "text/x-fsharp"
              FileExtension = ".fsx"
              PygmentsLexer = "fsharp"
              CodemirrorMode = "mllike"
              NbconvertExporter = "script" }
          Banner = "SageFs — F#"
          HelpLinks = [ "Docs", "https://example.test/docs" ] }
      Protocol.serializeKernelInfoReply reply
      |> Expect.equal
           "kernel_info_reply text"
           (esc """{"banner":"SageFs ~u2014 F#","help_links":[{"text":"Docs","url":"https://example.test/docs"}],"implementation":"sagefs","implementation_version":"0.5.0","language_info":{"codemirror_mode":"mllike","file_extension":".fsx","mimetype":"text/x-fsharp","name":"fsharp","nbconvert_exporter":"script","pygments_lexer":"fsharp","version":"11.0"},"protocol_version":"5.3"}""")
    }

    testAsync "WHY: a successful execute publishes busy, the execute_result with escaped text, then idle" {
      let exec : ExecuteHandler = fun code _ -> async { return Ok { Output = code; MimeType = "text/plain" } }
      let! events, _ = KernelLifecycle.processMessage exec noopComplete noopIsComplete KernelState.initial executeMessage
      iopubOf events
      |> Expect.equal
           "iopub text"
           [ "status", """{"execution_state": "busy"}"""
             "execute_result", esc """{"execution_count": 1, "data": {"text/plain": "let x = ~u0022a~u003Cb~u003E~u0022\n"}, "metadata": {}}"""
             "status", """{"execution_state": "idle"}""" ]
    }

    testAsync "WHY: a failed execute publishes the error with its name, value and traceback lines" {
      let exec : ExecuteHandler =
        fun _ _ -> async { return Error { Ename = "FSharpError"; Evalue = "bad \"thing\""; Traceback = [ "  (1,1)-(1,2): oops"; "second" ] } }
      let! events, _ = KernelLifecycle.processMessage exec noopComplete noopIsComplete KernelState.initial executeMessage
      iopubOf events
      |> List.filter (fun (kind, _) -> kind = "error")
      |> Expect.equal
           "error text"
           [ "error", esc """{"ename": "FSharpError", "evalue": "bad ~u0022thing~u0022", "traceback": ["  (1,1)-(1,2): oops", "second"]}""" ]
    }

    test "WHY: the kernelspec file is one line of JSON with the keys jupyter reads" {
      KernelLifecycle.renderKernelSpecJson (KernelSpec.generate "t" "/usr/bin/sagefs")
      |> Expect.equal
           "kernelspec text"
           (esc """{"argv": ["/usr/bin/sagefs", "--jupyter", "{connection_file}"], "display_name": "F# (SageFs ~u2014 t)", "language": "fsharp", "interrupt_mode": "message"}""")
    }
  ]

// ── The daemon bridge and the daemon client ──

[<Tests>]
let bridgeTests =
  testList "JupyterDaemonBridge request bodies" [

    testAsync "WHY: /exec gets code and working_directory, and a missing session gets a create body for the kernel's directory" {
      let execBodies = ResizeArray<string>()
      let createBodies = ResizeArray<string>()
      let execPost : JupyterDaemonBridge.PostJson =
        fun body ->
          async {
            execBodies.Add body
            match execBodies.Count with
            | 1 -> return Ok (404, """{"success":false,"error":"Session not reachable: No active session. Use create_session to create one first.","errorDetails":{"case":"SessionNotRoutable"}}""")
            | _ -> return Ok (200, """{"success":true,"result":"val it: int = 2"}""")
          }
      let createPost : JupyterDaemonBridge.PostJson =
        fun body ->
          async {
            createBodies.Add body
            return Ok (200, """{"success":true,"message":"a1b2c3d4"}""")
          }
      let proxy = JupyterDaemonBridge.makeSessionProxy execPost createPost "/kernel/cwd"
      let! _ = proxy (WorkerProtocol.WorkerMessage.EvalCode("1 + \"x\"", "r1"))
      let execBody = esc """{"code":"1 ~u002B ~u0022x~u0022","working_directory":"/kernel/cwd"}"""
      List.ofSeq execBodies |> Expect.equal "exec bodies" [ execBody; execBody ]
      List.ofSeq createBodies |> Expect.equal "create bodies" [ """{"projects":[],"workingDirectory":"/kernel/cwd"}""" ]
    }
  ]

type private CapturingHandler() =
  inherit HttpMessageHandler()
  member val Bodies = ResizeArray<string>()
  override this.SendAsync(request: HttpRequestMessage, _cancel: CancellationToken) : Task<HttpResponseMessage> =
    task {
      let! text = request.Content.ReadAsStringAsync()
      this.Bodies.Add text
      return new HttpResponseMessage(HttpStatusCode.OK)
    }

[<Tests>]
let daemonClientTests =
  testList "DaemonClient.dispatchAction body" [

    testTask "WHY: an action with a value sends both fields, and one without sends only the action" {
      let handler = new CapturingHandler()
      let client = new HttpClient(handler)
      do! DaemonClient.dispatchAction client "http://127.0.0.1:1" "promptChar" (Some "a")
      do! DaemonClient.dispatchAction client "http://127.0.0.1:1" "promptConfirm" None
      client.Dispose()
      List.ofSeq handler.Bodies
      |> Expect.equal "bodies" [ """{"action":"promptChar","value":"a"}"""; """{"action":"promptConfirm"}""" ]
    }
  ]

// ── SSE events ──

let private sid (text: string) = WorkerProtocol.SessionId.validate text |> Result.defaultWith failwith

let private oneWarmup : WarmupContext =
  { SourceFilesScanned = 5
    AssembliesLoaded = [ { Name = "A"; Path = "/a.dll"; NamespaceCount = 10; ModuleCount = 3 } ]
    NamespacesOpened = [ { Name = "System"; Kind = WarmUp.OpenableKind.Namespace; Source = "auto"; DurationMs = PinnedDurations.surfaceWireOpenedMs } ]
    FailedOpens =
      [ { Name = "Bad"
          Kind = WarmUp.OpenableKind.Module
          ErrorMessage = "err"
          Diagnostics =
            [ { Message = "m"; Severity = "error"; ErrorNumber = 39; FileName = Some "f.fs"; StartLine = 1; EndLine = 2; StartColumn = 3; EndColumn = 4 }
              { Message = "n"; Severity = "warning"; ErrorNumber = 40; FileName = None; StartLine = 5; EndLine = 6; StartColumn = 7; EndColumn = 8 } ]
          RetryCount = 1
          DurationMs = FixtureDurations.instantOpenMs } ]
    PhaseTiming = FixtureDurations.warmupPhaseTiming 1L 2L 3L 6L
    StartedAt = DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero) }

let private finishedReload : SessionReload =
  SessionReload.Finished
    { Case = ReloadCase.Patched
      Patched = 2
      Considered = 3
      Message = "patched 2"
      SuggestedAction = ""
      Mechanism = SageFs.Features.ReloadOutcome.PatchMechanism.MetadataDelta
      Declarations = []
      Callers = SageFs.Features.CallerState.CallersState.CallersCurrent }

let private sseCases : (string * SseEvent * string) list =
  let s = sid "0a000001"
  [ "SessionProgress", SseEvent.SessionProgress, """{"sessionProgress":true}"""
    "SessionReady", SseEvent.SessionReady s, """{"sessionReady":"0a000001"}"""
    "SessionSwitched", SseEvent.SessionSwitched s, """{"sessionSwitched":"0a000001"}"""
    "HotReloadChanged", SseEvent.HotReloadChanged s, """{"hotReloadChanged":true,"sessionId":"0a000001"}"""
    // `declarations` is empty here because this fixture's `finishedReload` declares none — the KEY is
    // what is pinned: the payload carries the field at all, which it did not before c3a7beca, so a
    // client could not tell "nothing needed patching" from "the sender never said". `callers` is pinned the same way: the
    // state of callers in other files is always a field, and it says CallersCurrent rather than saying nothing.
    "ReloadReported finished", SseEvent.ReloadReported (s, finishedReload),
      """{"reloadReported":{"callers":{"state":"CallersCurrent","message":"","suggestedAction":"","pending":[],"notChecked":[]},"considered":3,"declarations":[],"mechanism":"metadata-delta","message":"patched 2","outcome":"Patched","patched":2,"state":"finished","suggestedAction":""},"sessionId":"0a000001"}"""
    "ReloadReported none", SseEvent.ReloadReported (s, SessionReload.NoReloadYet), """{"sessionId":"0a000001"}"""
    "FileReloaded", SseEvent.FileReloaded (s, "a.fs"), """{"fileReloaded":"a.fs","sessionId":"0a000001"}"""
    "SessionFaulted", SseEvent.SessionFaulted (s, "boom \"x\""), esc """{"error":"boom ~u0022x~u0022","sessionFaulted":"0a000001"}"""
    "ModelChanged", SseEvent.ModelChanged (3, 4), """{"diagCount":4,"outputCount":3}"""
    "WarmupProgress", SseEvent.WarmupProgress (s, 1, 4, "m"), """{"sessionId":"0a000001","step":1,"total":4,"warmupProgress":true}"""
    "SystemAlarm", SseEvent.SystemAlarm ("p", "m"), """{"message":"m","phase":"p","systemAlarm":true}"""
    "CohortChanged", SseEvent.CohortChanged (SageFs.CohortScope.Machine), """{"cohortChanged":true,"scope":"machine"}"""
    "WarmupContextSnapshot", SseEvent.WarmupContextSnapshot ("s1", oneWarmup),
      """{"context":{"assembliesLoaded":[{"moduleCount":3,"name":"A","namespaceCount":10,"path":"/a.dll"}],"failedOpens":[{"diagnostics":[{"endColumn":4,"endLine":2,"errorNumber":39,"fileName":"f.fs","message":"m","severity":"error","startColumn":3,"startLine":1},{"endColumn":8,"endLine":6,"errorNumber":40,"message":"n","severity":"warning","startColumn":7,"startLine":5}],"error":"err","isModule":true,"name":"Bad","retryCount":1}],"namespacesOpened":[{"durationMs":1.5,"isModule":false,"name":"System","source":"auto"}],"phaseTiming":{"openNamespacesMs":3,"scanAssembliesMs":2,"scanSourceFilesMs":1,"totalMs":6},"sourceFilesScanned":5,"warmupDurationMs":6},"sessionId":"s1","type":"warmup_context_snapshot"}"""
    "HotReloadSnapshot", SseEvent.HotReloadSnapshot ("s1", [ "a.fs"; "b.fs" ]), """{"sessionId":"s1","type":"hotreload_snapshot","watchedFiles":["a.fs","b.fs"]}"""
    "HotReloadFileToggled", SseEvent.HotReloadFileToggled ("s1", "x.fs", false), """{"file":"x.fs","sessionId":"s1","type":"hotreload_file_toggled","watched":false}"""
    "SessionActivated", SseEvent.SessionActivated "s1", """{"sessionId":"s1","type":"session_activated"}"""
    "SessionCreated", SseEvent.SessionCreated ("s1", [ "P1"; "P2" ]), """{"projectNames":["P1","P2"],"sessionId":"s1","type":"session_created"}"""
    "SessionStopped", SseEvent.SessionStopped "s1", """{"sessionId":"s1","type":"session_stopped"}"""
    "WorkflowSwitching", SseEvent.WorkflowSwitching ("s1", "REPL", "Live"), """{"fromWorkflow":"REPL","sessionId":"s1","toWorkflow":"Live","type":"workflow_switching"}"""
    "WorkflowSwitched", SseEvent.WorkflowSwitched ("s1", "Live", "Full", true), """{"hotReloadActive":true,"replCapability":"Full","sessionId":"s1","type":"workflow_switched","workflowLabel":"Live"}"""
    "SessionHealthChanged healthy", SseEvent.SessionHealthChanged ("s1", SessionHealth.Healthy), """{"health":{"status":"Healthy"},"sessionId":"s1","type":"session_health_changed"}"""
    "SessionHealthChanged degraded", SseEvent.SessionHealthChanged ("s1", SessionHealth.Degraded "why"), """{"health":{"reason":"why","status":"Degraded"},"sessionId":"s1","type":"session_health_changed"}""" ]

[<Tests>]
let sseTests =
  testList "SseEvent.toJson text" [
    for (name, event, expected) in sseCases ->
      testCase (sprintf "WHY: %s is written as exactly this payload, because editors and the dashboard parse it by key" name) <| fun _ ->
        SseEvent.toJson event |> Expect.equal "payload text" expected
  ]

// ── Update-check cache and theme file ──

let private freshDir () =
  let dir = SageFs.Tests.RunnerDirs.scratchPath "surface-json-"
  Directory.CreateDirectory dir |> ignore
  dir

let private lf (text: string) = text.Replace("\r\n", "\n")

[<Tests>]
let fileFormatTests =
  testList "JSON files the surfaces keep" [

    test "WHY: the update-check cache is indented, keys as written, and a dismissed version is read back from it" {
      let dir = freshDir ()
      UpdateCheckService.dismissVersion dir (System.Version "0.6.700")
      let text = File.ReadAllText(Path.Combine(dir, "update-check.json")) |> lf
      text
      |> Expect.equal
           "cache file text"
           "{\n  \"LastCheckedUtcTicks\": 0,\n  \"LastKnownLatest\": \"\",\n  \"DismissedVersions\": [\n    \"0.6.700\"\n  ]\n}"
      UpdateCheckService.isDismissed dir (System.Version "0.6.700") |> Expect.isTrue "read back"
    }

    test "WHY: a cache file someone else wrote (indented, pre-existing) is still read" {
      let dir = freshDir ()
      File.WriteAllText(
        Path.Combine(dir, "update-check.json"),
        "{\n  \"LastCheckedUtcTicks\": 5,\n  \"LastKnownLatest\": \"0.6.9\",\n  \"DismissedVersions\": [\n    \"0.6.800\"\n  ]\n}")
      UpdateCheckService.isDismissed dir (System.Version "0.6.800") |> Expect.isTrue "read the dismissed version"
    }

    test "WHY: a garbled cache file reads as an empty cache and does not throw" {
      let dir = freshDir ()
      File.WriteAllText(Path.Combine(dir, "update-check.json"), "not json {")
      UpdateCheckService.isDismissed dir (System.Version "0.6.800") |> Expect.isFalse "empty cache"
    }

    test "WHY: themes.json is an indented object of key to theme, and loads back" {
      let dir = freshDir ()
      let themes = Collections.Concurrent.ConcurrentDictionary<string, string>()
      themes.["/w/a"] <- "dark"
      saveThemes dir themes
      File.ReadAllText(Path.Combine(dir, "themes.json")) |> lf
      |> Expect.equal "themes text" "{\n  \"/w/a\": \"dark\"\n}"
      (loadThemes dir).["/w/a"] |> Expect.equal "loaded" "dark"
    }

    test "WHY: a garbled themes.json loads as no themes and does not throw" {
      let dir = freshDir ()
      File.WriteAllText(Path.Combine(dir, "themes.json"), "[1,2")
      (loadThemes dir).Count |> Expect.equal "no themes" 0
    }
  ]

// ── Dashboard endpoints ──

[<Tests>]
let workflowSwitchBodyTests =
  testList "switchWorkflowViaApi body" [

    testTask "WHY: the workflow switch posts one object with the workflow's request value" {
      let probe = new TcpListener(IPAddress.Loopback, 0)
      probe.Start()
      let port = (probe.LocalEndpoint :?> IPEndPoint).Port
      probe.Stop()
      let listener = new HttpListener()
      listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
      listener.Start()
      let served =
        task {
          let! context = listener.GetContextAsync()
          use reader = new StreamReader(context.Request.InputStream)
          let! body = reader.ReadToEndAsync()
          context.Response.StatusCode <- 200
          context.Response.Close()
          return body
        }
      let! _ = switchWorkflowViaApi port (sid "0a000001") WorkflowTypes.SessionWorkflow.Interactive
      let! body = served
      listener.Close()
      body |> Expect.equal "request body" (sprintf """{"workflow":"%s"}""" (WorkflowSwitch.requestValue WorkflowTypes.SessionWorkflow.Interactive))
    }
  ]

let private outgoingReport : SageFs.Features.FrictionSanitize.OutgoingReport =
  { SchemaVersion = 1
    SageFsVersion = "0.6.1"
    SubmittedAtUtc = "2026-01-02T03:04:05.0000000Z"
    TotalEvents = 7
    TotalFeedbackItems = 2
    ToolsWithFriction =
      [ { Tool = "t"; Invocations = 5; Blocked = 1; Abandoned = 2; ExplicitFeedback = 3; SuggestedFix = "fix" } ]
    TopBlockers = [ { Blocker = "b"; Count = 4; AffectedTools = [ "t" ] } ]
    FrequentTransitions = [ { From = "a"; To = "b"; Count = 6 } ]
    RecentFeedback =
      [ { Tool = "t"; Kind = "k"; Count = 1; Reason = "r"; Alternative = Some "alt" }
        { Tool = "u"; Kind = "k"; Count = 2; Reason = "s"; Alternative = None } ]
    RecommendedWorkItems =
      [ { Title = "w"; TargetTool = Some "t"; Reason = "r"; SuggestedAction = "a" }
        { Title = "x"; TargetTool = None; Reason = "r"; SuggestedAction = "a" } ] }

[<Tests>]
let frictionPayloadTests =
  testList "friction send payload" [

    test "WHY: the receiver reads camelCase keys and checks schemaVersion first, so that is how the report is written" {
      frictionPayloadJson outgoingReport
      |> Expect.equal
           "payload text"
           """{"schemaVersion":1,"sageFsVersion":"0.6.1","submittedAtUtc":"2026-01-02T03:04:05.0000000Z","totalEvents":7,"totalFeedbackItems":2,"toolsWithFriction":[{"tool":"t","invocations":5,"blocked":1,"abandoned":2,"explicitFeedback":3,"suggestedFix":"fix"}],"topBlockers":[{"blocker":"b","count":4,"affectedTools":["t"]}],"frequentTransitions":[{"from":"a","to":"b","count":6}],"recentFeedback":[{"tool":"t","kind":"k","count":1,"reason":"r","alternative":"alt"},{"tool":"u","kind":"k","count":2,"reason":"s","alternative":null}],"recommendedWorkItems":[{"title":"w","targetTool":"t","reason":"r","suggestedAction":"a"},{"title":"x","targetTool":null,"reason":"r","suggestedAction":"a"}]}"""
    }
  ]

/// A response body that says when a whole SSE frame (ending in a blank line) has been written.
type private FrameSignalStream() =
  inherit MemoryStream()
  let firstFrame = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
  member _.FirstFrame : Task<string> = firstFrame.Task
  member private this.Check() =
    let text = Encoding.UTF8.GetString(this.ToArray())
    match text.Contains "\n\n" with
    | true -> firstFrame.TrySetResult text |> ignore
    | false -> ()
  override this.WriteAsync(buffer: ReadOnlyMemory<byte>, cancellationToken: CancellationToken) : ValueTask =
    base.Write buffer.Span
    this.Check()
    ValueTask()
  override this.Write(buffer: ReadOnlySpan<byte>) =
    base.Write buffer
    this.Check()

[<Tests>]
let apiStateTests =
  testList "GET /api/state payload" [

    testTask "WHY: the JSON state stream writes the session facts, with a region's cursor and completions unwrapped and its annotations listed" {
      let region : RenderRegion =
        { Id = "output"
          Flags = RegionFlags.None
          Content = "hello"
          Affordances = []
          Cursor = Some { Line = 1; Col = 2 }
          Completions = Some { Items = [ "a"; "b" ]; SelectedIndex = 1 }
          LineAnnotations = [| { Line = 3; Icon = Features.LiveTesting.GutterIcon.TestPassed; Tooltip = "ok" } |] }
      let bare : RenderRegion = { region with Id = "input"; Content = ""; Cursor = None; Completions = None; LineAnnotations = [||] }
      let queries =
        { DashboardRenderGateTests.mkQueries (ref 0) [] with
            GetElmRegionsForSession = fun _ -> Some [ region; bare ]
            GetLiveTestingStatus = fun () -> "running"
            GetTestSourceLocations =
              fun () -> [ { CellId = 0; TestName = "t1"; FilePath = "f.fs"; StartLine = 9; EndLine = 10 } ] }
      let body = new FrameSignalStream()
      let ctx = DefaultHttpContext()
      ctx.Response.Body <- body
      ctx.Request.QueryString <- QueryString("?sessionId=0a000001")
      let cts = new CancellationTokenSource()
      ctx.RequestAborted <- cts.Token
      let running = createApiStateHandler queries (DashboardRenderGateTests.mkInfra ()) ctx
      let! frame = body.FirstFrame
      cts.Cancel()
      do! running
      cts.Dispose()
      lf frame
      |> Expect.equal
           "state frame"
           ("data: " + """{"activeWorkingDir":"/w","avgMs":0,"evalCount":0,"hotReloadActive":false,"liveTestingStatus":"running","regions":[{"completions":{"items":["a","b"],"selectedIndex":1},"content":"hello","cursor":{"col":2,"line":1},"id":"output","lineAnnotations":[{"icon":"TestPassed","line":3,"tooltip":"ok"}]},{"completions":null,"content":"","cursor":null,"id":"input","lineAnnotations":[]}],"replCapability":"Full","sessionId":"0a000001","sessionState":"Ready","testSourceLocations":[{"filePath":"f.fs","startLine":9,"testName":"t1"}],"watchedCount":0,"workflowLabel":"REPL"}""" + "\n\n")
    }
  ]

[<Tests>]
let apiDispatchTests =
  testList "POST /api/dispatch body" [

    let post (json: string) =
      task {
        let dispatched = ResizeArray<SageFsMsg>()
        let ctx = DefaultHttpContext()
        ctx.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes json)
        ctx.Request.ContentLength <- Nullable(int64 json.Length)
        let response = new MemoryStream()
        ctx.Response.Body <- response
        do! createApiDispatchHandler dispatched.Add ctx
        return ctx.Response.StatusCode, Encoding.UTF8.GetString(response.ToArray()), dispatched.Count
      }

    testTask "WHY: an action with no value is dispatched once and answered ok" {
      let! status, text, count = post """{"action":"sessionCycleNext"}"""
      (status, text, count) |> Expect.equal "result" (200, """{"ok":true}""", 1)
    }

    testTask "WHY: an action with a string value is dispatched once and answered ok" {
      let! status, text, count = post """{"action":"promptChar","value":"a"}"""
      (status, text, count) |> Expect.equal "result" (200, """{"ok":true}""", 1)
    }

    testTask "WHY: an unknown action is refused with 400 and nothing is dispatched" {
      let! status, text, count = post """{"action":"nope"}"""
      (status, count) |> Expect.equal "result" (400, 0)
      text |> Expect.stringContains "names the action" "nope"
    }

    testTask "WHY: a body with no action is refused with 400 and says so" {
      let! status, text, count = post """{"value":"a"}"""
      (status, text, count) |> Expect.equal "result" (400, """{"error":"Missing action"}""", 0)
    }

    testTask "WHY: a body that is not JSON is refused with 400 and nothing is dispatched" {
      let! status, text, count = post "not json"
      (status, text, count) |> Expect.equal "result" (400, """{"error":"Request failed"}""", 0)
    }
  ]
