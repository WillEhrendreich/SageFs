module SageFs.Vscode.Extension

open Fable.Core
open Fable.Core.JsInterop
open Vscode
open SageFs.Vscode.JsHelpers

module Client = SageFs.Vscode.SageFsClient
module Diag = SageFs.Vscode.DiagnosticsListener
module Lens = SageFs.Vscode.CodeLensProvider
module Completion = SageFs.Vscode.CompletionProvider
module HotReload = SageFs.Vscode.HotReloadTreeProvider
module SessionCtx = SageFs.Vscode.SessionContextTreeProvider
module LiveTest = SageFs.Vscode.LiveTestingListener
module TestCtrl = SageFs.Vscode.TestControllerAdapter
module TypeExpl = SageFs.Vscode.TypeExplorerProvider
module TestDeco = SageFs.Vscode.TestDecorations
module TestLens = SageFs.Vscode.TestCodeLensProvider
module InlineDeco = SageFs.Vscode.InlineDecorations

open SageFs.Vscode.LiveTestingTypes

// ── Mutable state ──────────────────────────────────────────────

let mutable client: Client.Client option = None
let mutable outputChannel: OutputChannel option = None
let mutable statusBarItem: StatusBarItem option = None
let mutable testStatusBarItem: StatusBarItem option = None
let mutable diagnosticsDisposable: Disposable option = None
let mutable sseDisposable: Disposable option = None
let mutable diagnosticCollection: DiagnosticCollection option = None
let mutable activeSessionId: string option = None
let mutable liveTestListener: LiveTest.LiveTestingListener option = None
let mutable testAdapter: TestCtrl.TestAdapter option = None
let mutable dashboardPanel: WebviewPanel option = None
let mutable typeExplorer: TypeExpl.TypeExplorer option = None

// FSI bindings and pipeline trace — maintained by SSE events (server-side CQRS)
// No client-side parsing; server pushes snapshots via SSE bindings_snapshot/pipeline_trace events

// ── JS Interop ─────────────────────────────────────────────────

[<Emit("require('child_process').spawn($0, $1, $2)")>]
let spawn (cmd: string) (args: string array) (opts: obj) : obj = jsNative

[<Emit("$0.unref()")>]
let unref (proc: obj) : unit = jsNative

[<Emit("$0.kill()")>]
let killProc (proc: obj) : unit = jsNative

[<Emit("$0.stderr")>]
let procStderr (proc: obj) : obj = jsNative

[<Emit("$0.stdout")>]
let procStdout (proc: obj) : obj = jsNative

[<Emit("$0.on('data', function(d) { if (d != null) $1(String(d)) })")>]
let onData (stream: obj) (handler: string -> unit) : unit = jsNative

[<Emit("$0.on('error', function(e) { $1(e.message || String(e)) })")>]
let onProcError (proc: obj) (handler: string -> unit) : unit = jsNative

[<Emit("$0.on('exit', function(code, signal) { $1(code == null ? -1 : code, signal == null ? '' : signal) })")>]
let onProcExit (proc: obj) (handler: int -> string -> unit) : unit = jsNative

let mutable daemonProcess: obj option = None
let mutable isStarting = false
let mutable onDaemonReady: (Client.Client -> unit) option = None

// ── Helpers ────────────────────────────────────────────────────

let getClient () =
  match client with Some c -> c | None -> failwith "SageFs not activated"

let getOutput () =
  match outputChannel with Some o -> o | None -> failwith "SageFs not activated"

let getStatusBar () =
  match statusBarItem with Some s -> s | None -> failwith "SageFs not activated"

let getWorkingDirectory () =
  match Workspace.workspaceFolders () with
  | Some fs when fs.Length > 0 -> Some fs.[0].uri.fsPath
  | _ -> None

let findProject () =
  promise {
    let config = Workspace.getConfiguration "sagefs"
    let configured = config.get("projectPath", "")
    match configured with
    | c when c <> "" -> return Some c
    | _ ->
      let! slnFiles = Workspace.findFiles "**/*.{sln,slnx}" "**/node_modules/**" 5
      let! projFiles = Workspace.findFiles "**/*.fsproj" "**/node_modules/**" 10
      let solutions = slnFiles |> Array.map (fun f -> Workspace.asRelativePath f)
      let projects = projFiles |> Array.map (fun f -> Workspace.asRelativePath f)
      let all = Array.append solutions projects
      match all with
      | [||] -> return None
      | [| single |] -> return Some single
      | _ ->
        let! picked = Window.showQuickPick all "Select a solution or project for SageFs"
        return picked
  }

let getCodeBlock (editor: TextEditor) =
  let doc = editor.document
  let pos = editor.selection.active
  let mutable startLine = int pos.line
  while startLine > 0 && not (doc.lineAt(float (startLine - 1)).text.TrimEnd().EndsWith(";;")) do
    startLine <- startLine - 1
  let mutable endLine = int pos.line
  while endLine < int doc.lineCount - 1 && not (doc.lineAt(float endLine).text.TrimEnd().EndsWith(";;")) do
    endLine <- endLine + 1
  let range = newRange startLine 0 endLine (int (doc.lineAt(float endLine).text.Length))
  doc.getTextRange range

// ── Status ─────────────────────────────────────────────────────

let updateTestStatusBar (summary: VscTestSummary) =
  match testStatusBarItem with
  | None -> ()
  | Some sb ->
    let text, bg =
      match summary with
      | s when s.Total = 0 ->
        "$(beaker) No tests", None
      | s when s.Failed > 0 ->
        sprintf "$(testing-error-icon) %d/%d failed" s.Failed s.Total,
        Some (newThemeColor "statusBarItem.errorBackground")
      | s when s.Running > 0 ->
        sprintf "$(sync~spin) Running %d/%d" s.Running s.Total, None
      | s when s.Stale > 0 ->
        sprintf "$(warning) %d/%d stale" s.Stale s.Total,
        Some (newThemeColor "statusBarItem.warningBackground")
      | s ->
        sprintf "$(testing-passed-icon) %d/%d passed" s.Passed s.Total, None
    sb.text <- text
    sb.backgroundColor <- bg
    sb.show ()

let refreshStatus () =
  promise {
    let c = getClient ()
    let sb = getStatusBar ()
    try
      let! running = Client.isRunning c
      if not running then
        sb.text <- "$(circle-slash) SageFs: offline"
        sb.backgroundColor <- None
        sb.show ()
        activeSessionId <- None
        HotReload.setSession c None
        SessionCtx.setSession c None
      else
        let! status = Client.getStatus c
        let! sys = Client.getSystemStatus c
        let supervised =
          match sys with Some s when s.supervised -> " $(shield)" | _ -> ""
        let restarts =
          match sys with Some s when s.restartCount > 0 -> sprintf " %d↻" s.restartCount | _ -> ""
        let stripExt (name: string) =
          match name with
          | n when n.EndsWith(".fsproj") -> n.[..n.Length - 8]
          | n when n.EndsWith(".slnx") -> n.[..n.Length - 6]
          | n when n.EndsWith(".sln") -> n.[..n.Length - 5]
          | n -> n
        if status.connected then
          let! sessions = Client.listSessions c
          let session =
            match activeSessionId with
            | Some id -> sessions |> Array.tryFind (fun s -> s.id = id)
            | None -> sessions |> Array.tryHead
          match session with
          | Some s ->
            activeSessionId <- Some s.id
            let projLabel =
              match s.projects with
              | [||] -> "session"
              | ps ->
                ps
                |> Array.map (fun p -> p.Split([|'/'; '\\'|]) |> Array.last |> stripExt)
                |> String.concat ","
            let evalLabel = match s.evalCount with 0 -> "" | n -> sprintf " [%d]" n
            sb.text <- sprintf "$(zap) SageFs: %s%s%s%s" projLabel evalLabel supervised restarts
          | None ->
            activeSessionId <- None
            sb.text <- sprintf "$(zap) SageFs: ready (no session)%s%s" supervised restarts
          sb.backgroundColor <- None
          let activeId = activeSessionId
          HotReload.setSession c activeId
          SessionCtx.setSession c activeId
        else
          sb.text <- "$(loading~spin) SageFs: starting..."
        sb.show ()
    with _ ->
      sb.text <- "$(circle-slash) SageFs: offline"
      sb.show ()
  } |> promiseIgnore

// ── Daemon Lifecycle ───────────────────────────────────────────

let rec startDaemon () =
  promise {
    match isStarting with
    | true -> ()
    | false ->
    isStarting <- true
    let c = getClient ()
    let! running = Client.isRunning c
    match running with
    | true ->
      isStarting <- false
      Window.showInformationMessage "SageFs daemon is already running." [||] |> ignore
      refreshStatus ()
    | false ->
      let! projPath = findProject ()
      match projPath with
      | None ->
        isStarting <- false
        Window.showErrorMessage "No .fsproj or .sln found. Open an F# project first." [||] |> ignore
      | Some proj ->
        let out = getOutput ()
        out.show true
        out.appendLine (sprintf "Starting SageFs daemon with %s..." proj)
        let workDir = getWorkingDirectory () |> Option.defaultValue "."
        let ext =
          let i = proj.LastIndexOf('.')
          if i >= 0 then proj.Substring(i) else ""
        let flag =
          match ext with
          | ".sln" | ".slnx" -> "--sln"
          | _ -> "--proj"
        let proc = spawn "sagefs" [| flag; proj |] (createObj [
          "cwd" ==> workDir; "detached" ==> true; "stdio" ==> [| box "ignore"; box "pipe"; box "pipe" |]; "shell" ==> true
        ])
        onProcError proc (fun msg ->
          out.appendLine (sprintf "[SageFs spawn error] %s" msg)
          let sb = getStatusBar ()
          sb.text <- "$(error) SageFs: spawn failed"
        )
        onProcExit proc (fun code _signal ->
          out.appendLine (sprintf "[SageFs] process exited (code %d)" code)
        )
        let stderr = procStderr proc
        stderr |> tryOfObj |> Option.iter (fun s -> onData s (fun chunk -> out.appendLine chunk))
        let stdout = procStdout proc
        stdout |> tryOfObj |> Option.iter (fun s -> onData s (fun chunk -> out.appendLine chunk))
        unref proc
        daemonProcess <- Some proc
        let sb = getStatusBar ()
        sb.text <- "$(loading~spin) SageFs starting..."
        sb.show ()
        let mutable attempts = 0
        let mutable intervalId: obj option = None
        let id =
          jsSetInterval (fun () ->
            attempts <- attempts + 1
            sb.text <- sprintf "$(loading~spin) SageFs starting... (%ds)" (attempts * 2)
            promise {
              let! ready = Client.isRunning c
              if ready then
                intervalId |> Option.iter jsClearInterval
                isStarting <- false
                out.appendLine "SageFs daemon is ready."
                Window.showInformationMessage "SageFs daemon started." [||] |> ignore
                onDaemonReady |> Option.iter (fun f -> f c)
                refreshStatus ()
              elif attempts > 60 then
                intervalId |> Option.iter jsClearInterval
                isStarting <- false
                out.appendLine "Timed out waiting for SageFs daemon after 120s."
                Window.showErrorMessage "SageFs daemon failed to start after 120s." [||] |> ignore
                sb.text <- "$(error) SageFs: offline"
            } |> promiseIgnore
          ) 2000
        intervalId <- Some id
  }

and ensureRunning () =
  promise {
    let c = getClient ()
    let! running = Client.isRunning c
    if running then return true
    else
      let! choice = Window.showWarningMessage "SageFs daemon is not running." [| "Start SageFs"; "Cancel" |]
      match choice with
      | Some "Start SageFs" ->
        do! startDaemon ()
        let mutable ready = false
        for _ in 0 .. 14 do
          if not ready then
            do! sleep 2000
            let! r = Client.isRunning c
            if r then ready <- true
        if not ready then
          Window.showErrorMessage "SageFs didn't start in time." [||] |> ignore
        return ready
      | _ ->
        return false
  }

// ── Commands ───────────────────────────────────────────────────

/// Wraps the ensureRunning + getClient boilerplate.
let withClient (action: Client.Client -> JS.Promise<unit>) =
  promise {
    let! ok = ensureRunning ()
    if ok then do! action (getClient ())
  }

/// Fire a client action that returns ApiOutcome, show its message, then refresh.
let simpleCommand (defaultMsg: string) (action: Client.Client -> JS.Promise<Client.ApiOutcome>) =
  withClient (fun c ->
    promise {
      let! result = action c
      let msg = result |> Client.ApiOutcome.messageOrDefault defaultMsg
      Window.showInformationMessage (sprintf "SageFs: %s" msg) [||] |> ignore
      refreshStatus ()
    })

type EvalResult =
  | EvalOk of output: string * elapsed: float
  | EvalError of message: string
  | EvalConnectionError of message: string

let evalCore (code: string) : JS.Promise<EvalResult> =
  promise {
    try
      let c = getClient ()
      let workDir = getWorkingDirectory ()
      let startTime = performanceNow ()
      let! result = Client.evalCode code workDir c
      let elapsed = performanceNow () - startTime
      match result with
      | Client.Failed errMsg -> return EvalError errMsg
      | Client.Succeeded msg ->
        return EvalOk (msg |> Option.defaultValue "", elapsed)
    with err ->
      return EvalConnectionError (string err)
  }

/// Log eval result to output channel. Returns the result for further handling.
let logEvalResult (out: OutputChannel) (result: EvalResult) =
  match result with
  | EvalOk (output, elapsed) ->
    out.appendLine (sprintf "%s  (%s)" output (InlineDeco.formatDuration elapsed))
  | EvalError errMsg ->
    out.appendLine (sprintf "❌ Error:\n%s" errMsg)
  | EvalConnectionError msg ->
    out.appendLine (sprintf "❌ Connection error: %s" msg)
  result

/// Get code from selection or code block, append ;; if needed.
let getEvalCode (ed: TextEditor) =
  let raw =
    if not ed.selection.isEmpty then
      ed.document.getTextRange (newRange (int ed.selection.start.line) (int ed.selection.start.character) (int ed.selection.``end``.line) (int ed.selection.``end``.character))
    else getCodeBlock ed
  match raw.Trim() with
  | "" -> None
  | _ ->
    if raw.TrimEnd().EndsWith(";;") then Some raw
    else Some (raw.TrimEnd() + ";;")

let evalSelection () =
  promise {
    match Window.getActiveTextEditor () with
    | None ->
      Window.showWarningMessage "No active editor." [||] |> ignore
    | Some ed ->
      let! ok = ensureRunning ()
      match ok, getEvalCode ed with
      | false, _ | _, None -> ()
      | true, Some code ->
        let out = getOutput ()
        do! Window.withProgress ProgressLocation.Window "SageFs: evaluating..." (fun _progress _token ->
          promise {
            out.appendLine "──── eval ────"
            out.appendLine code
            out.appendLine ""
            let! result = evalCore code
            match logEvalResult out result with
            | EvalError errMsg ->
              out.show true
              InlineDeco.showInlineDiagnostic ed errMsg
            | EvalOk (output, elapsed) ->
              InlineDeco.showInlineResult ed output (Some elapsed)
            | EvalConnectionError _ ->
              out.show true
              Window.showErrorMessage "Cannot reach SageFs daemon. Is it running?" [||] |> ignore
          }
        )
  }

let evalFile () =
  promise {
    match Window.getActiveTextEditor () with
    | None -> ()
    | Some ed ->
      let! ok = ensureRunning ()
      let code = ed.document.getText ()
      match ok, code.Trim() with
      | false, _ | _, "" -> ()
      | true, _ ->
        let out = getOutput ()
        out.show true
        out.appendLine (sprintf "──── eval file: %s ────" ed.document.fileName)
        let! result = evalCore code
        logEvalResult out result |> ignore
  }

let evalRange (args: obj) =
  promise {
    match Window.getActiveTextEditor () with
    | None -> ()
    | Some ed ->
      let! ok = ensureRunning ()
      let range: Range = unbox args
      let code = ed.document.getTextRange range
      match ok, code.Trim() with
      | false, _ | _, "" -> ()
      | true, _ ->
        let out = getOutput ()
        out.show true
        out.appendLine "──── eval block ────"
        out.appendLine code
        out.appendLine ""
        let! result = evalCore code
        match logEvalResult out result with
        | EvalOk (output, elapsed) ->
          InlineDeco.showInlineResult ed output (Some elapsed)
        | _ -> ()
  }

let resetSessionCmd () =
  simpleCommand "Reset complete" Client.resetSession

let hardResetCmd () =
  simpleCommand "Hard reset complete" (Client.hardReset true)

let createSessionCmd () =
  withClient (fun c ->
    promise {
      let! projPath = findProject ()
      match projPath with
      | None ->
        Window.showErrorMessage "No .fsproj or .sln found. Open an F# project first." [||] |> ignore
      | Some proj ->
        let workDir = getWorkingDirectory () |> Option.defaultValue "."
        do! Window.withProgress ProgressLocation.Notification "SageFs: Creating session..." (fun _p _t ->
          promise {
            let! result = Client.createSession proj workDir c
            match result with
            | Client.Succeeded _ ->
              Window.showInformationMessage (sprintf "SageFs: Session created for %s" proj) [||] |> ignore
            | Client.Failed err ->
              Window.showErrorMessage (sprintf "SageFs: %s" err) [||] |> ignore
            refreshStatus ()
          }
        )
    })

let private formatSessionLabel (s: Client.SessionInfo) =
  let proj =
    match s.projects with
    | [||] -> "no project"
    | ps -> ps |> String.concat ", "
  sprintf "%s (%s) [%s]" s.id proj s.status

let sessionPickCommand (prompt: string) (action: Client.SessionInfo -> Client.Client -> JS.Promise<Client.ApiOutcome>) (onSuccess: Client.SessionInfo -> unit) =
  withClient (fun c ->
    promise {
      let! sessions = Client.listSessions c
      match sessions with
      | [||] ->
        Window.showInformationMessage "No sessions available." [||] |> ignore
      | _ ->
        let items = sessions |> Array.map formatSessionLabel
        let! picked = Window.showQuickPick items prompt
        match picked with
        | Some label ->
          match items |> Array.tryFindIndex ((=) label) with
          | Some i ->
            let sess = sessions.[i]
            let! result = action sess c
            match result with
            | Client.Succeeded _ ->
              onSuccess sess
              Window.showInformationMessage (result |> Client.ApiOutcome.messageOrDefault prompt) [||] |> ignore
            | Client.Failed err ->
              Window.showErrorMessage (sprintf "Failed: %s" err) [||] |> ignore
            refreshStatus ()
          | None -> ()
        | None -> ()
    })

let switchSessionCmd () =
  sessionPickCommand "Select a session"
    (fun sess c -> Client.switchSession sess.id c)
    (fun sess -> activeSessionId <- Some sess.id)

let stopSessionCmd () =
  sessionPickCommand "Select a session to stop"
    (fun sess c -> Client.stopSession sess.id c)
    (fun sess ->
      match activeSessionId with
      | Some id when id = sess.id -> activeSessionId <- None
      | _ -> ())

let stopDaemon () =
  daemonProcess |> Option.iter killProc
  daemonProcess <- None
  Window.showInformationMessage "SageFs: stop the daemon from its terminal or use `sagefs stop`." [||] |> ignore
  refreshStatus ()

let openDashboard () =
  let c = getClient ()
  let dashUrl = Client.dashboardUrl c
  match dashboardPanel with
  | Some panel ->
    panel.reveal 1
  | None ->
    let panel =
      Window.createWebviewPanel
        "sagefsDashboard"
        "SageFs Dashboard"
        2  // ViewColumn.Beside
        (createObj [ "enableScripts" ==> true ])
    panel.webview.html <-
      sprintf """<!DOCTYPE html>
<html style="height:100%%;margin:0;padding:0">
<body style="height:100%%;margin:0;padding:0">
<iframe src="%s" style="width:100%%;height:100%%;border:none"></iframe>
</body>
</html>""" dashUrl
    panel.onDidDispose (fun () -> dashboardPanel <- None) |> ignore
    dashboardPanel <- Some panel

let evalAdvance () =
  promise {
    match Window.getActiveTextEditor () with
    | None ->
      Window.showWarningMessage "No active editor." [||] |> ignore
    | Some ed ->
      let! ok = ensureRunning ()
      match ok, getEvalCode ed with
      | false, _ | _, None -> ()
      | true, Some code ->
        let out = getOutput ()
        let! result = evalCore code
        match logEvalResult out result with
        | EvalError errMsg ->
          InlineDeco.showInlineDiagnostic ed errMsg
        | EvalOk (output, elapsed) ->
          InlineDeco.showInlineResult ed output (Some elapsed)
          let curLine = int ed.selection.``end``.line
          let lineCount = int ed.document.lineCount
          let mutable nextLine = curLine + 1
          while nextLine < lineCount && ed.document.lineAt(float nextLine).text.Trim() = "" do
            nextLine <- nextLine + 1
          match nextLine < lineCount with
          | true ->
            let pos = newPosition nextLine 0
            let sel = newSelection pos pos
            setEditorSelection ed sel
            revealEditorRange ed (newRange nextLine 0 nextLine 0)
          | false -> ()
        | EvalConnectionError _ -> ()
  }

let cancelEvalCmd () =
  simpleCommand "Eval cancelled" Client.cancelEval

let loadScriptCmd () =
  withClient (fun c ->
    promise {
      match Window.getActiveTextEditor () with
      | Some ed when ed.document.fileName.EndsWith(".fsx") ->
        let! result = Client.loadScript ed.document.fileName c
        match result with
        | Client.Succeeded _ ->
          let name = ed.document.fileName.Split([|'/'; '\\'|]) |> Array.last
          Window.showInformationMessage (sprintf "Script loaded: %s" name) [||] |> ignore
        | Client.Failed err ->
          Window.showErrorMessage err [||] |> ignore
      | _ ->
        Window.showWarningMessage "Open an .fsx file to load it as a script." [||] |> ignore
    })

let promptAutoStart () =
  promise {
    let! projPath = findProject ()
    match projPath with
    | None -> ()
    | Some proj ->
      let! choice =
        Window.showInformationMessage
          (sprintf "SageFs daemon is not running. Start it for %s?" proj)
          [| "Start SageFs"; "Open Dashboard"; "Not Now" |]
      match choice with
      | Some "Start SageFs" -> do! startDaemon ()
      | Some "Open Dashboard" -> openDashboard ()
      | _ -> ()
  }

let hijackIonideSendToFsi (subs: ResizeArray<Disposable>) =
  for cmd in [| "fsi.SendSelection"; "fsi.SendLine"; "fsi.SendFile" |] do
    try
      let disp =
        Commands.registerCommand cmd (fun _ ->
          match cmd with
          | "fsi.SendFile" ->
            Commands.executeCommand "sagefs.evalFile" |> promiseIgnore
          | _ ->
            Commands.executeCommand "sagefs.eval" |> promiseIgnore
        )
      subs.Add disp
    with _ -> ()

// ── Activate / Deactivate ──────────────────────────────────────

let activate (context: ExtensionContext) =
  let config = Workspace.getConfiguration "sagefs"
  let mcpPort = config.get("mcpPort", 37749)
  let dashboardPort = config.get("dashboardPort", 37750)

  let c = Client.create mcpPort dashboardPort
  client <- Some c

  let out = Window.createOutputChannel "SageFs"
  outputChannel <- Some out

  let sb = Window.createStatusBarItem StatusBarAlignment.Left 50.
  sb.command <- Some "sagefs.openDashboard"
  sb.tooltip <- Some "Click to open SageFs dashboard"
  statusBarItem <- Some sb
  context.subscriptions.Add (sb :> obj :?> Disposable)

  let tsb = Window.createStatusBarItem StatusBarAlignment.Left 49.
  tsb.text <- "$(beaker) No tests"
  tsb.tooltip <- Some "SageFs live testing — click to enable"
  tsb.command <- Some "sagefs.enableLiveTesting"
  testStatusBarItem <- Some tsb
  context.subscriptions.Add (tsb :> obj :?> Disposable)

  let dc = Languages.createDiagnosticCollection "sagefs"
  diagnosticCollection <- Some dc
  context.subscriptions.Add (dc :> obj :?> Disposable)

  // Mark inline results as stale when F# documents change
  let docChangeSub = Workspace.onDidChangeTextDocument (fun _evt ->
    match Window.getActiveTextEditor () with
    | Some ed when ed.document.fileName.EndsWith(".fs") || ed.document.fileName.EndsWith(".fsx") ->
      if not (Map.isEmpty InlineDeco.blockDecorations) then
        InlineDeco.markDecorationsStale ed
    | _ -> ())
  context.subscriptions.Add docChangeSub

  // Hot Reload TreeView
  HotReload.register context
  HotReload.setSession c None

  // Session Context TreeView
  SessionCtx.register context
  SessionCtx.setSession c None

  // Type Explorer TreeView
  typeExplorer <- Some (TypeExpl.create context client)

  let reg cmd handler =
    context.subscriptions.Add (Commands.registerCommand cmd handler)

  reg "sagefs.eval" (fun _ -> evalSelection () |> promiseIgnore)
  reg "sagefs.evalFile" (fun _ -> evalFile () |> promiseIgnore)
  reg "sagefs.evalRange" (fun args -> evalRange args |> promiseIgnore)
  reg "sagefs.evalAdvance" (fun _ -> evalAdvance () |> promiseIgnore)
  reg "sagefs.cancelEval" (fun _ -> cancelEvalCmd () |> promiseIgnore)
  reg "sagefs.loadScript" (fun _ -> loadScriptCmd () |> promiseIgnore)
  reg "sagefs.start" (fun _ -> startDaemon () |> promiseIgnore)
  reg "sagefs.stop" (fun _ -> stopDaemon ())
  reg "sagefs.openDashboard" (fun _ -> openDashboard ())
  reg "sagefs.resetSession" (fun _ -> resetSessionCmd () |> promiseIgnore)
  reg "sagefs.hardReset" (fun _ -> hardResetCmd () |> promiseIgnore)
  reg "sagefs.createSession" (fun _ -> createSessionCmd () |> promiseIgnore)
  reg "sagefs.switchSession" (fun _ -> switchSessionCmd () |> promiseIgnore)
  reg "sagefs.stopSession" (fun _ -> stopSessionCmd () |> promiseIgnore)
  reg "sagefs.clearResults" (fun _ -> InlineDeco.clearAllDecorations ())
  reg "sagefs.enableLiveTesting" (fun _ ->
    simpleCommand "Live testing enabled" Client.enableLiveTesting |> promiseIgnore)
  reg "sagefs.disableLiveTesting" (fun _ ->
    simpleCommand "Live testing disabled" Client.disableLiveTesting |> promiseIgnore)
  reg "sagefs.runTests" (fun _ ->
    simpleCommand "Tests queued" (Client.runTests "") |> promiseIgnore)
  reg "sagefs.setRunPolicy" (fun _ ->
    withClient (fun c ->
      promise {
        let! catOpt = Window.showQuickPick
                        [| "unit"; "integration"; "browser"; "benchmark"; "architecture"; "property" |]
                        "Select test category"
        match catOpt with
        | Some cat ->
          let! polOpt = Window.showQuickPick
                          [| "every"; "save"; "demand"; "disabled" |]
                          (sprintf "Set policy for %s tests" cat)
          match polOpt with
          | Some pol ->
            let! result = Client.setRunPolicy cat pol c
            result
            |> Client.ApiOutcome.message
            |> Option.iter (fun msg -> Window.showInformationMessage msg [||] |> ignore)
          | None -> ()
        | None -> ()
      }) |> promiseIgnore)
  reg "sagefs.showHistory" (fun _ ->
    withClient (fun c ->
      promise {
        let! bodyOpt = Client.getRecentEvents 30 c
        match bodyOpt with
        | Some body ->
          let lines = body.Split('\n') |> Array.filter (fun l -> l.Trim().Length > 0)
          match lines with
          | [||] -> Window.showInformationMessage "No recent events" [||] |> ignore
          | _ -> Window.showQuickPick lines "Recent SageFs events" |> promiseIgnore
        | None -> Window.showWarningMessage "Could not fetch events" [||] |> ignore
      }) |> promiseIgnore)
  reg "sagefs.showCallGraph" (fun _ ->
    withClient (fun c ->
      promise {
        let! overviewOpt = Client.getDependencyGraph "" c
        match overviewOpt with
        | None ->
          Window.showWarningMessage "Could not fetch dependency graph" [||] |> ignore
        | Some body ->
          let parsed = jsonParse body
          let total = tryField<int> "TotalSymbols" parsed |> Option.defaultValue 0
          match total with
          | 0 ->
            Window.showInformationMessage "No dependency graph available yet" [||] |> ignore
          | _ ->
            let! inputOpt = Window.showInputBox (sprintf "Enter symbol name (%d symbols tracked)" total)
            match inputOpt with
            | Some sym when sym.Trim().Length > 0 ->
              let! detailOpt = Client.getDependencyGraph (sym.Trim()) c
              match detailOpt with
              | None ->
                Window.showWarningMessage "Could not fetch graph" [||] |> ignore
              | Some detail ->
                let parsed2 = jsonParse detail
                let tests = tryField<obj array> "Tests" parsed2 |> Option.defaultValue [||]
                match tests with
                | [||] ->
                  Window.showInformationMessage (sprintf "No tests cover '%s'" sym) [||] |> ignore
                | _ ->
                  let items =
                    tests |> Array.map (fun t ->
                      let name = tryField<string> "TestName" t |> Option.defaultValue "?"
                      let status = tryField<string> "Status" t |> Option.defaultValue "unknown"
                      let icon = match status with "passed" -> "✓" | "failed" -> "✗" | _ -> "●"
                      sprintf "%s %s [%s]" icon name status)
                  Window.showQuickPick items (sprintf "Tests covering '%s'" sym) |> promiseIgnore
            | _ -> ()
      }) |> promiseIgnore)
  reg "sagefs.showBindings" (fun _ ->
    match liveTestListener |> Option.map (fun l -> l.Bindings ()) with
    | Some [||] | None ->
      Window.showInformationMessage "No FSI bindings yet" [||] |> ignore
    | Some bindings ->
      let items =
        bindings |> Array.choose (fun b ->
          match tryField<string> "Name" b, tryField<string> "TypeSig" b with
          | Some name, Some typeSig ->
            let shadow = tryField<int> "ShadowCount" b |> Option.defaultValue 0
            let shadowLabel = match shadow with n when n > 1 -> sprintf " (×%d)" n | _ -> ""
            Some (sprintf "%s : %s%s" name typeSig shadowLabel)
          | _ -> None)
      Window.showQuickPick items "FSI Bindings"
      |> promiseIgnore)
  reg "sagefs.showPipelineTrace" (fun _ ->
    match liveTestListener |> Option.bind (fun l -> l.PipelineTrace ()) with
    | Some trace ->
      let get name = tryField<int> name trace |> Option.defaultValue 0
      let items = [|
        sprintf "Enabled: %b" (tryField<bool> "Enabled" trace |> Option.defaultValue false)
        sprintf "Running: %b" (tryField<bool> "IsRunning" trace |> Option.defaultValue false)
        sprintf "Total: %d | Passed: %d | Failed: %d"
          (tryField "Summary" trace |> Option.bind (tryField<int> "Total") |> Option.defaultValue 0)
          (tryField "Summary" trace |> Option.bind (tryField<int> "Passed") |> Option.defaultValue 0)
          (tryField "Summary" trace |> Option.bind (tryField<int> "Failed") |> Option.defaultValue 0)
      |]
      Window.showQuickPick items "Pipeline Trace" |> promiseIgnore
    | None -> Window.showInformationMessage "No pipeline trace data yet" [||] |> ignore)

  reg "sagefs.exportSession" (fun _ ->
    withClient (fun c ->
      promise {
        match activeSessionId with
        | None ->
          Window.showInformationMessage "No active session" [||] |> ignore
        | Some sid ->
          let! result = Client.exportSessionAsFsx sid c
          match result with
          | None ->
            Window.showErrorMessage "Failed to export session" [||] |> ignore
          | Some r ->
            match r.evalCount with
            | 0 -> Window.showInformationMessage "No evaluations to export" [||] |> ignore
            | _ ->
              let! doc = Workspace.openTextDocument r.content "fsharp"
              let! _ = Window.showTextDocument doc
              ()
      }) |> promiseIgnore)
  let lensProvider = Lens.create ()
  context.subscriptions.Add (Languages.registerCodeLensProvider "fsharp" lensProvider)
  let testLensProvider = TestLens.create ()
  context.subscriptions.Add (Languages.registerCodeLensProvider "fsharp" testLensProvider)

  // Code completion
  let getWorkDir () =
    Workspace.workspaceFolders ()
    |> Option.bind (fun folders ->
      match folders with
      | [||] -> None
      | _ -> Some folders.[0].uri.fsPath)
  let completionProvider =
    Completion.create (fun () -> client) getWorkDir
  context.subscriptions.Add (
    Languages.registerCompletionItemProvider "fsharp" completionProvider [| "." |])

  // Ionide hijack
  hijackIonideSendToFsi context.subscriptions

  // Diagnostics SSE + session resume + live state updates
  let connectToRunningDaemon (c: Client.Client) =
    // Dispose existing connection resources for idempotency
    sseDisposable |> Option.iter (fun d -> d.dispose () |> ignore)
    sseDisposable <- None
    liveTestListener |> Option.iter (fun l -> l.Dispose ())
    liveTestListener <- None
    testAdapter |> Option.iter (fun a -> a.Dispose ())
    testAdapter <- None
    diagnosticsDisposable |> Option.iter (fun d -> d.dispose () |> ignore)
    diagnosticsDisposable <- None
    // Establish fresh connection resources
    diagnosticsDisposable <- Some (Diag.start c.mcpPort dc)
    // TestController for VS Code Test Explorer
    let adapter = TestCtrl.create (fun () -> client)
    testAdapter <- Some adapter
    // Initialize inline test decorations
    TestDeco.initialize ()
    // Live testing listener — handles test_summary, test_results_batch, and state events
    let refreshAllDecorations () =
      liveTestListener
      |> Option.map (fun l -> l.State ())
      |> Option.defaultValue VscLiveTestState.empty
      |> fun state ->
        TestDeco.applyToAllEditors state
        TestDeco.applyCoverageToAllEditors state
        state
    let listener = LiveTest.start c.mcpPort {
      OnStateChange = fun changes ->
        adapter.Refresh changes
        let state = refreshAllDecorations ()
        TestDeco.updateDiagnostics state
        TestLens.updateState state
      OnSummaryUpdate = fun summary -> updateTestStatusBar summary
      OnStatusRefresh = fun () -> refreshStatus ()
      OnBindingsUpdate = fun _ -> ()
      OnPipelineTraceUpdate = fun _ -> ()
      OnFeatureEvent = None
    }
    liveTestListener <- Some listener
    sseDisposable <- Some {
      new Disposable with member _.dispose () = listener.Dispose(); null
    }
    // Re-apply decorations when editors change
    context.subscriptions.Add (
      Window.onDidChangeVisibleTextEditors (fun _editors -> refreshAllDecorations () |> ignore))
    context.subscriptions.Add (
      Window.onDidChangeActiveTextEditor (fun _editor -> refreshAllDecorations () |> ignore))
    // Auto-discover and create session if none exists
    promise {
      let! sessions = Client.listSessions c
      match sessions with
      | [||] ->
        let! projOpt = findProject ()
        match projOpt with
        | Some proj ->
          let workDir = getWorkingDirectory () |> Option.defaultValue "."
          let! choice =
            Window.showInformationMessage
              (sprintf "SageFs is running but has no session. Create one for %s?" proj)
              [| "Create Session"; "Not Now" |]
          match choice with
          | Some "Create Session" ->
            let! result = Client.createSession proj workDir c
            match result with
            | Client.Succeeded _ ->
              Window.showInformationMessage (sprintf "SageFs: Session created for %s" proj) [||] |> ignore
            | Client.Failed _ -> ()
            refreshStatus ()
          | _ -> ()
        | None -> ()
      | _ -> ()
    } |> promiseIgnore

  // Wire up daemon-ready callback for startDaemon lifecycle
  onDaemonReady <- Some connectToRunningDaemon

  promise {
    let! running = Client.isRunning c
    if running then connectToRunningDaemon c
  } |> promiseIgnore

  // Config change listener
  context.subscriptions.Add (
    Workspace.onDidChangeConfiguration (fun e ->
      match e.affectsConfiguration "sagefs" with
      | true ->
        let cfg = Workspace.getConfiguration "sagefs"
        Client.updatePorts (cfg.get("mcpPort", 37749)) (cfg.get("dashboardPort", 37750)) c
      | false -> ()
    )
  )

  // Status polling
  refreshStatus ()
  let statusInterval = jsSetInterval refreshStatus 5000
  context.subscriptions.Add (
    { new Disposable with member _.dispose () = jsClearInterval statusInterval; null }
  )

  // Auto-start (silently — no prompt dialog)
  let autoStart = config.get("autoStart", true)
  match autoStart with
  | true ->
    promise {
      let! running = Client.isRunning c
      if not running then
        let! projPath = findProject ()
        match projPath with
        | Some _ -> do! startDaemon ()
        | None -> ()
    } |> promiseIgnore
  | false -> ()

let deactivate () =
  diagnosticsDisposable |> Option.iter (fun d -> d.dispose () |> ignore)
  sseDisposable |> Option.iter (fun d -> d.dispose () |> ignore)
  liveTestListener |> Option.iter (fun l -> l.Dispose ())
  liveTestListener <- None
  testAdapter |> Option.iter (fun a -> a.Dispose ())
  testAdapter <- None
  typeExplorer |> Option.iter (fun te -> te.dispose ())
  typeExplorer <- None
  dashboardPanel |> Option.iter (fun p -> p.dispose () |> ignore)
  dashboardPanel <- None
  TestDeco.dispose ()
  InlineDeco.clearAllDecorations ()
