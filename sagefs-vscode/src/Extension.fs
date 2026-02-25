module SageFs.Vscode.Extension

open Fable.Core
open Fable.Core.JsInterop
open Vscode

module Client = SageFs.Vscode.SageFsClient
module Diag = SageFs.Vscode.DiagnosticsListener
module Lens = SageFs.Vscode.CodeLensProvider
module Completion = SageFs.Vscode.CompletionProvider
module HotReload = SageFs.Vscode.HotReloadTreeProvider
module SessionCtx = SageFs.Vscode.SessionContextTreeProvider
module LiveTest = SageFs.Vscode.LiveTestingListener
module TestCtrl = SageFs.Vscode.TestControllerAdapter

open SageFs.Vscode.LiveTestingTypes

// ── Mutable state ──────────────────────────────────────────────

let mutable private client: Client.Client option = None
let mutable private outputChannel: OutputChannel option = None
let mutable private statusBarItem: StatusBarItem option = None
let mutable private testStatusBarItem: StatusBarItem option = None
let mutable private diagnosticsDisposable: Disposable option = None
let mutable private sseDisposable: Disposable option = None
let mutable private diagnosticCollection: DiagnosticCollection option = None
let mutable private blockDecorations: Map<int, TextEditorDecorationType> = Map.empty
let mutable private activeSessionId: string option = None
let mutable private liveTestListener: LiveTest.LiveTestingListener option = None
let mutable private testAdapter: TestCtrl.TestAdapter option = None

// ── JS Interop ─────────────────────────────────────────────────

[<Emit("require('child_process').spawn($0, $1, $2)")>]
let private spawn (cmd: string) (args: string array) (opts: obj) : obj = jsNative

[<Emit("$0.unref()")>]
let private unref (proc: obj) : unit = jsNative

[<Emit("$0.kill()")>]
let private killProc (proc: obj) : unit = jsNative

[<Emit("$0.stderr")>]
let private procStderr (proc: obj) : obj = jsNative

[<Emit("$0.stdout")>]
let private procStdout (proc: obj) : obj = jsNative

[<Emit("$0.on('data', function(d) { if (d != null) $1(String(d)) })")>]
let private onData (stream: obj) (handler: string -> unit) : unit = jsNative

[<Emit("$0.on('error', function(e) { $1(e.message || String(e)) })")>]
let private onProcError (proc: obj) (handler: string -> unit) : unit = jsNative

[<Emit("$0.on('exit', function(code, signal) { $1(code == null ? -1 : code, signal == null ? '' : signal) })")>]
let private onProcExit (proc: obj) (handler: int -> string -> unit) : unit = jsNative

[<Emit("setInterval($0, $1)")>]
let private setInterval (fn: unit -> unit) (ms: int) : obj = jsNative

[<Emit("clearInterval($0)")>]
let private clearInterval (id: obj) : unit = jsNative

[<Emit("setTimeout($0, $1)")>]
let private setTimeout (fn: unit -> unit) (ms: int) : obj = jsNative

[<Emit("new Promise(resolve => setTimeout(resolve, $0))")>]
let private sleep (ms: int) : JS.Promise<unit> = jsNative

let mutable private daemonProcess: obj option = None

// ── Helpers ────────────────────────────────────────────────────

let private getClient () =
  match client with Some c -> c | None -> failwith "SageFs not activated"

let private getOutput () =
  match outputChannel with Some o -> o | None -> failwith "SageFs not activated"

let private getStatusBar () =
  match statusBarItem with Some s -> s | None -> failwith "SageFs not activated"

let private getWorkingDirectory () =
  match Workspace.workspaceFolders () with
  | Some fs when fs.Length > 0 -> Some fs.[0].uri.fsPath
  | _ -> None

let private findProject () =
  promise {
    let config = Workspace.getConfiguration "sagefs"
    let configured = config.get("projectPath", "")
    if configured <> "" then
      return Some configured
    else
      let! slnFiles = Workspace.findFiles "**/*.{sln,slnx}" "**/node_modules/**" 5
      let! projFiles = Workspace.findFiles "**/*.fsproj" "**/node_modules/**" 10
      let solutions = slnFiles |> Array.map (fun f -> Workspace.asRelativePath f)
      let projects = projFiles |> Array.map (fun f -> Workspace.asRelativePath f)
      let all = Array.append solutions projects
      if all.Length = 0 then
        return None
      elif all.Length = 1 then
        return Some all.[0]
      else
        let! picked = Window.showQuickPick all "Select a solution or project for SageFs"
        return picked
  }

let private getCodeBlock (editor: TextEditor) =
  let doc = editor.document
  let pos = editor.selection.active
  let mutable startLine = int pos.line
  while startLine > 0 do
    let prevText = doc.lineAt(float (startLine - 1)).text.TrimEnd()
    if prevText.EndsWith(";;") then
      startLine <- startLine
    else
      startLine <- startLine - 1
  let mutable endLine = int pos.line
  while endLine < int doc.lineCount - 1 do
    let lineText = doc.lineAt(float endLine).text.TrimEnd()
    if lineText.EndsWith(";;") then
      endLine <- endLine
    else
      endLine <- endLine + 1
  let range = newRange startLine 0 endLine (int (doc.lineAt(float endLine).text.Length))
  doc.getTextRange range

// ── Decorations ────────────────────────────────────────────────

let private clearBlockDecoration (line: int) =
  match Map.tryFind line blockDecorations with
  | Some deco ->
    deco.dispose () |> ignore
    blockDecorations <- Map.remove line blockDecorations
  | None -> ()

let private clearAllDecorations () =
  blockDecorations |> Map.iter (fun _ deco -> deco.dispose () |> ignore)
  blockDecorations <- Map.empty

let private showInlineResult (editor: TextEditor) (text: string) =
  let trimmed = text.Trim()
  if trimmed = "" then () else
  let line =
    if editor.selection.isEmpty then int editor.selection.active.line
    else int editor.selection.``end``.line
  clearBlockDecoration line
  let lines = trimmed.Split('\n')
  let firstLine = if lines.Length > 0 then lines.[0] else ""
  let contentText =
    if lines.Length <= 1 then
      sprintf "  // → %s" firstLine
    else
      let summary =
        if lines.Length <= 4 then lines |> String.concat "  │  "
        else sprintf "%s  │  ... (%d lines)" firstLine lines.Length
      sprintf "  // → %s" summary
  let opts = createObj [
    "after" ==> createObj [
      "contentText" ==> contentText
      "color" ==> newThemeColor "sagefs.successForeground"
      "fontStyle" ==> "italic"
    ]
  ]
  let deco = Window.createTextEditorDecorationType opts
  let lineText = editor.document.lineAt(float line).text
  let endCol = lineText.Length
  let range = newRange line endCol line endCol
  editor.setDecorations(deco, ResizeArray [| box range |])
  blockDecorations <- Map.add line deco blockDecorations
  setTimeout (fun () -> clearBlockDecoration line) 30000 |> ignore

let private showInlineDiagnostic (editor: TextEditor) (text: string) =
  let firstLine =
    let parts = text.Split('\n')
    if parts.Length > 0 then parts.[0].Trim() else ""
  if firstLine = "" then () else
  let line =
    if editor.selection.isEmpty then int editor.selection.active.line
    else int editor.selection.``end``.line
  clearBlockDecoration line
  let opts = createObj [
    "after" ==> createObj [
      "contentText" ==> sprintf "  // ❌ %s" firstLine
      "color" ==> newThemeColor "sagefs.errorForeground"
      "fontStyle" ==> "italic"
    ]
  ]
  let deco = Window.createTextEditorDecorationType opts
  let lineText = editor.document.lineAt(float line).text
  let endCol = lineText.Length
  let range = newRange line endCol line endCol
  editor.setDecorations(deco, ResizeArray [| box range |])
  blockDecorations <- Map.add line deco blockDecorations
  setTimeout (fun () -> clearBlockDecoration line) 30000 |> ignore

// ── Status ─────────────────────────────────────────────────────

let updateTestStatusBar (summary: VscTestSummary) =
  match testStatusBarItem with
  | None -> ()
  | Some sb ->
    if summary.Total = 0 then
      sb.text <- "$(beaker) No tests"
      sb.backgroundColor <- None
    elif summary.Failed > 0 then
      sb.text <- sprintf "$(testing-error-icon) %d/%d failed" summary.Failed summary.Total
      sb.backgroundColor <- Some (newThemeColor "statusBarItem.errorBackground")
    elif summary.Running > 0 then
      sb.text <- sprintf "$(sync~spin) Running %d/%d" summary.Running summary.Total
      sb.backgroundColor <- None
    elif summary.Stale > 0 then
      sb.text <- sprintf "$(warning) %d/%d stale" summary.Stale summary.Total
      sb.backgroundColor <- Some (newThemeColor "statusBarItem.warningBackground")
    else
      sb.text <- sprintf "$(testing-passed-icon) %d/%d passed" summary.Passed summary.Total
      sb.backgroundColor <- None
    sb.show ()

let private refreshStatus () =
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
              if s.projects.Length > 0 then
                s.projects
                |> Array.map (fun p ->
                  let name = p.Split([|'/'; '\\'|]) |> Array.last
                  if name.EndsWith(".fsproj") then name.[..name.Length - 8]
                  elif name.EndsWith(".slnx") then name.[..name.Length - 6]
                  elif name.EndsWith(".sln") then name.[..name.Length - 5]
                  else name)
                |> String.concat ","
              else "session"
            let evalLabel = if s.evalCount > 0 then sprintf " [%d]" s.evalCount else ""
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
  } |> ignore

// ── Daemon Lifecycle ───────────────────────────────────────────

let rec private startDaemon () =
  promise {
    let c = getClient ()
    let! running = Client.isRunning c
    if running then
      Window.showInformationMessage "SageFs daemon is already running." [||] |> ignore
      refreshStatus ()
    else
      let! projPath = findProject ()
      match projPath with
      | None ->
        Window.showErrorMessage "No .fsproj or .sln found. Open an F# project first." [||] |> ignore
      | Some proj ->
        let out = getOutput ()
        out.show true
        out.appendLine (sprintf "Starting SageFs daemon with %s..." proj)
        let workDir = getWorkingDirectory () |> Option.defaultValue "."
        let ext =
          let i = proj.LastIndexOf('.')
          if i >= 0 then proj.Substring(i) else ""
        let flag = if ext = ".sln" || ext = ".slnx" then "--sln" else "--proj"
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
        if not (isNull stderr) then
          onData stderr (fun chunk -> out.appendLine chunk)
        let stdout = procStdout proc
        if not (isNull stdout) then
          onData stdout (fun chunk -> out.appendLine chunk)
        unref proc
        daemonProcess <- Some proc
        let sb = getStatusBar ()
        sb.text <- "$(loading~spin) SageFs starting..."
        sb.show ()
        let mutable attempts = 0
        let mutable intervalId: obj option = None
        let id =
          setInterval (fun () ->
            attempts <- attempts + 1
            Client.isRunning c
            |> Promise.iter (fun ready ->
              if ready then
                intervalId |> Option.iter clearInterval
                out.appendLine "SageFs daemon is ready."
                Window.showInformationMessage "SageFs daemon started." [||] |> ignore
                match diagnosticCollection with
                | Some dc ->
                  diagnosticsDisposable |> Option.iter (fun d -> d.dispose () |> ignore)
                  diagnosticsDisposable <- Some (Diag.start c.mcpPort dc)
                | None -> ()
                refreshStatus ()
              elif attempts > 60 then
                intervalId |> Option.iter clearInterval
                out.appendLine "Timed out waiting for SageFs daemon."
                Window.showErrorMessage "SageFs daemon failed to start." [||] |> ignore
                sb.text <- "$(error) SageFs: offline"
            )
          ) 2000
        intervalId <- Some id
  }

and private ensureRunning () =
  promise {
    let c = getClient ()
    let! running = Client.isRunning c
    if running then
      return true
    else
      let! choice = Window.showWarningMessage "SageFs daemon is not running." [| "Start SageFs"; "Cancel" |]
      if choice = Some "Start SageFs" then
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
      else
        return false
  }

// ── Commands ───────────────────────────────────────────────────

let private evalSelection () =
  promise {
    match Window.getActiveTextEditor () with
    | None ->
      Window.showWarningMessage "No active editor." [||] |> ignore
    | Some ed ->
      let! ok = ensureRunning ()
      if ok then
        let mutable code =
          if not ed.selection.isEmpty then
            ed.document.getTextRange (newRange (int ed.selection.start.line) (int ed.selection.start.character) (int ed.selection.``end``.line) (int ed.selection.``end``.character))
          else
            getCodeBlock ed
        if code.Trim() <> "" then
          if not (code.TrimEnd().EndsWith(";;")) then
            code <- code.TrimEnd() + ";;"
          let workDir = getWorkingDirectory ()
          let out = getOutput ()
          do! Window.withProgress ProgressLocation.Window "SageFs: evaluating..." (fun _progress _token ->
            promise {
              out.appendLine "──── eval ────"
              out.appendLine code
              out.appendLine ""
              try
                let c = getClient ()
                let! result = Client.evalCode code workDir c
                if not result.success then
                  let errMsg =
                    result.error
                    |> Option.orElse result.result
                    |> Option.defaultValue "Unknown error"
                  out.appendLine (sprintf "❌ Error:\n%s" errMsg)
                  out.show true
                  showInlineDiagnostic ed errMsg
                else
                  let output = result.result |> Option.defaultValue ""
                  out.appendLine output
                  showInlineResult ed output
              with err ->
                out.appendLine (sprintf "❌ Connection error: %s" (string err))
                out.show true
                Window.showErrorMessage "Cannot reach SageFs daemon. Is it running?" [||] |> ignore
            }
          )
  }

let private evalFile () =
  promise {
    match Window.getActiveTextEditor () with
    | None -> ()
    | Some ed ->
      let! ok = ensureRunning ()
      if ok then
        let code = ed.document.getText ()
        if code.Trim() <> "" then
          let workDir = getWorkingDirectory ()
          let out = getOutput ()
          out.show true
          out.appendLine (sprintf "──── eval file: %s ────" ed.document.fileName)
          try
            let c = getClient ()
            let! result = Client.evalCode code workDir c
            if not result.success then
              out.appendLine (sprintf "❌ Error:\n%s" (result.error |> Option.orElse result.result |> Option.defaultValue "Unknown error"))
            else
              out.appendLine (result.result |> Option.defaultValue "")
          with err ->
            out.appendLine (sprintf "❌ Connection error: %s" (string err))
  }

let private evalRange (args: obj) =
  promise {
    match Window.getActiveTextEditor () with
    | None -> ()
    | Some ed ->
      let! ok = ensureRunning ()
      if ok then
        let range: Range = unbox args
        let code = ed.document.getTextRange range
        if code.Trim() <> "" then
          let workDir = getWorkingDirectory ()
          let out = getOutput ()
          out.show true
          out.appendLine "──── eval block ────"
          out.appendLine code
          out.appendLine ""
          try
            let c = getClient ()
            let! result = Client.evalCode code workDir c
            if not result.success then
              out.appendLine (sprintf "❌ Error:\n%s" (result.error |> Option.orElse result.result |> Option.defaultValue "Unknown error"))
            else
              out.appendLine (result.result |> Option.defaultValue "")
          with err ->
            out.appendLine (sprintf "❌ Connection error: %s" (string err))
  }

let private resetSessionCmd () =
  promise {
    let! ok = ensureRunning ()
    if ok then
      let c = getClient ()
      let! result = Client.resetSession c
      let msg = result.result |> Option.orElse result.error |> Option.defaultValue "Reset complete"
      Window.showInformationMessage (sprintf "SageFs: %s" msg) [||] |> ignore
      refreshStatus ()
  }

let private hardResetCmd () =
  promise {
    let! ok = ensureRunning ()
    if ok then
      let c = getClient ()
      let! result = Client.hardReset true c
      let msg = result.result |> Option.orElse result.error |> Option.defaultValue "Hard reset complete"
      Window.showInformationMessage (sprintf "SageFs: %s" msg) [||] |> ignore
      refreshStatus ()
  }

let private createSessionCmd () =
  promise {
    let! ok = ensureRunning ()
    if ok then
      let! projPath = findProject ()
      match projPath with
      | None ->
        Window.showErrorMessage "No .fsproj or .sln found. Open an F# project first." [||] |> ignore
      | Some proj ->
        let workDir = getWorkingDirectory () |> Option.defaultValue "."
        do! Window.withProgress ProgressLocation.Notification "SageFs: Creating session..." (fun _p _t ->
          promise {
            let c = getClient ()
            let! result = Client.createSession proj workDir c
            if result.success then
              Window.showInformationMessage (sprintf "SageFs: Session created for %s" proj) [||] |> ignore
            else
              Window.showErrorMessage (sprintf "SageFs: %s" (result.error |> Option.defaultValue "Failed to create session")) [||] |> ignore
            refreshStatus ()
          }
        )
  }

let private switchSessionCmd () =
  promise {
    let! ok = ensureRunning ()
    if ok then
      let c = getClient ()
      let! sessions = Client.listSessions c
      if sessions.Length = 0 then
        Window.showInformationMessage "No sessions available." [||] |> ignore
      else
        let items =
          sessions |> Array.map (fun s ->
            let proj =
              if s.projects.Length > 0 then s.projects |> String.concat ", "
              else "no project"
            sprintf "%s (%s) [%s]" s.id proj s.status)
        let! picked = Window.showQuickPick items "Select a session"
        match picked with
        | Some label ->
          let idx = items |> Array.tryFindIndex (fun i -> i = label)
          match idx with
          | Some i ->
            let sess = sessions.[i]
            let! ok = Client.switchSession sess.id c
            if ok then
              activeSessionId <- Some sess.id
              Window.showInformationMessage (sprintf "Switched to session %s" sess.id) [||] |> ignore
            else
              Window.showErrorMessage "Failed to switch session." [||] |> ignore
            refreshStatus ()
          | None -> ()
        | None -> ()
  }

let private stopSessionCmd () =
  promise {
    let! ok = ensureRunning ()
    if ok then
      let c = getClient ()
      let! sessions = Client.listSessions c
      if sessions.Length = 0 then
        Window.showInformationMessage "No sessions available." [||] |> ignore
      else
        let items =
          sessions |> Array.map (fun s ->
            let proj =
              if s.projects.Length > 0 then s.projects |> String.concat ", "
              else "no project"
            sprintf "%s (%s) [%s]" s.id proj s.status)
        let! picked = Window.showQuickPick items "Select a session to stop"
        match picked with
        | Some label ->
          let idx = items |> Array.tryFindIndex (fun i -> i = label)
          match idx with
          | Some i ->
            let sess = sessions.[i]
            let! ok = Client.stopSession sess.id c
            if ok then
              if activeSessionId = Some sess.id then activeSessionId <- None
              Window.showInformationMessage (sprintf "Stopped session %s" sess.id) [||] |> ignore
            else
              Window.showErrorMessage "Failed to stop session." [||] |> ignore
            refreshStatus ()
          | None -> ()
        | None -> ()
  }

let private stopDaemon () =
  daemonProcess |> Option.iter killProc
  daemonProcess <- None
  Window.showInformationMessage "SageFs: stop the daemon from its terminal or use `sagefs stop`." [||] |> ignore
  refreshStatus ()

let private openDashboard () =
  let c = getClient ()
  Env.openExternal (uriParse (Client.dashboardUrl c)) |> ignore

let private promptAutoStart () =
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

let private hijackIonideSendToFsi (subs: ResizeArray<Disposable>) =
  for cmd in [| "fsi.SendSelection"; "fsi.SendLine"; "fsi.SendFile" |] do
    try
      let disp =
        Commands.registerCommand cmd (fun _ ->
          if cmd = "fsi.SendFile" then
            Commands.executeCommand "sagefs.evalFile" |> ignore
          else
            Commands.executeCommand "sagefs.eval" |> ignore
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
  tsb.tooltip <- Some "SageFs live testing — click to toggle"
  tsb.command <- Some "sagefs.toggleLiveTesting"
  testStatusBarItem <- Some tsb
  context.subscriptions.Add (tsb :> obj :?> Disposable)

  let dc = Languages.createDiagnosticCollection "sagefs"
  diagnosticCollection <- Some dc
  context.subscriptions.Add (dc :> obj :?> Disposable)

  // Hot Reload TreeView
  HotReload.register context
  HotReload.setSession c None

  // Session Context TreeView
  SessionCtx.register context
  SessionCtx.setSession c None

  let reg cmd handler =
    context.subscriptions.Add (Commands.registerCommand cmd handler)

  reg "sagefs.eval" (fun _ -> evalSelection () |> ignore)
  reg "sagefs.evalFile" (fun _ -> evalFile () |> ignore)
  reg "sagefs.evalRange" (fun args -> evalRange args |> ignore)
  reg "sagefs.start" (fun _ -> startDaemon () |> ignore)
  reg "sagefs.stop" (fun _ -> stopDaemon ())
  reg "sagefs.openDashboard" (fun _ -> openDashboard ())
  reg "sagefs.resetSession" (fun _ -> resetSessionCmd () |> ignore)
  reg "sagefs.hardReset" (fun _ -> hardResetCmd () |> ignore)
  reg "sagefs.createSession" (fun _ -> createSessionCmd () |> ignore)
  reg "sagefs.switchSession" (fun _ -> switchSessionCmd () |> ignore)
  reg "sagefs.stopSession" (fun _ -> stopSessionCmd () |> ignore)
  reg "sagefs.clearResults" (fun _ -> clearAllDecorations ())
  reg "sagefs.toggleLiveTesting" (fun _ ->
    match client with
    | Some c ->
      Client.toggleLiveTesting c
      |> Promise.iter (fun result ->
        match result.result with
        | Some msg -> Window.showInformationMessage msg [||] |> ignore
        | None -> ())
    | None -> Window.showWarningMessage "SageFs is not connected" [||] |> ignore)
  reg "sagefs.runTests" (fun _ ->
    match client with
    | Some c ->
      Client.runTests "" c
      |> Promise.iter (fun result ->
        match result.result with
        | Some msg -> Window.showInformationMessage msg [||] |> ignore
        | None -> ())
    | None -> Window.showWarningMessage "SageFs is not connected" [||] |> ignore)
  reg "sagefs.setRunPolicy" (fun _ ->
    match client with
    | Some c ->
      Window.showQuickPick
        [| "unit"; "integration"; "browser"; "benchmark"; "architecture"; "property" |]
        "Select test category"
      |> Promise.iter (fun catOpt ->
        match catOpt with
        | Some cat ->
          Window.showQuickPick
            [| "every"; "save"; "demand"; "disabled" |]
            (sprintf "Set policy for %s tests" cat)
          |> Promise.iter (fun polOpt ->
            match polOpt with
            | Some pol ->
              Client.setRunPolicy cat pol c
              |> Promise.iter (fun result ->
                match result.result with
                | Some msg -> Window.showInformationMessage msg [||] |> ignore
                | None -> ())
            | None -> ())
        | None -> ())
    | None -> Window.showWarningMessage "SageFs is not connected" [||] |> ignore)

  // CodeLens
  let lensProvider = Lens.create ()
  context.subscriptions.Add (Languages.registerCodeLensProvider "fsharp" lensProvider)

  // Code completion
  let getWorkDir () =
    Workspace.workspaceFolders ()
    |> Option.bind (fun folders ->
      if folders.Length > 0 then Some folders.[0].uri.fsPath
      else None)
  let completionProvider =
    Completion.create (fun () -> client) getWorkDir
  context.subscriptions.Add (
    Languages.registerCompletionItemProvider "fsharp" completionProvider [| "." |])

  // Ionide hijack
  hijackIonideSendToFsi context.subscriptions

  // Diagnostics SSE + session resume + live state updates
  Client.isRunning c
  |> Promise.iter (fun running ->
    if running then
      diagnosticsDisposable <- Some (Diag.start c.mcpPort dc)
      // TestController for VS Code Test Explorer
      let adapter = TestCtrl.create (fun () -> client)
      testAdapter <- Some adapter
      // Live testing listener — handles test_summary, test_results_batch, and state events
      let listener = LiveTest.start c.mcpPort {
        OnStateChange = fun changes -> adapter.Refresh changes
        OnSummaryUpdate = fun summary -> updateTestStatusBar summary
        OnStatusRefresh = fun () -> refreshStatus ()
      }
      liveTestListener <- Some listener
      sseDisposable <- Some {
        new Disposable with member _.dispose () = listener.Dispose(); null
      }
      // Auto-discover and create session if none exists
      Client.listSessions c
      |> Promise.iter (fun sessions ->
        if sessions.Length = 0 then
          findProject ()
          |> Promise.iter (fun projOpt ->
            match projOpt with
            | Some proj ->
              let workDir = getWorkingDirectory () |> Option.defaultValue "."
              Window.showInformationMessage
                (sprintf "SageFs is running but has no session. Create one for %s?" proj)
                [| "Create Session"; "Not Now" |]
              |> Promise.iter (fun choice ->
                match choice with
                | Some "Create Session" ->
                  Client.createSession proj workDir c
                  |> Promise.iter (fun result ->
                    if result.success then
                      Window.showInformationMessage (sprintf "SageFs: Session created for %s" proj) [||] |> ignore
                    refreshStatus ()
                  )
                | _ -> ()
              )
            | None -> ()
          )
      )
  )

  // Config change listener
  context.subscriptions.Add (
    Workspace.onDidChangeConfiguration (fun e ->
      if e.affectsConfiguration "sagefs" then
        let cfg = Workspace.getConfiguration "sagefs"
        Client.updatePorts (cfg.get("mcpPort", 37749)) (cfg.get("dashboardPort", 37750)) c
    )
  )

  // Status polling
  refreshStatus ()
  let statusInterval = setInterval refreshStatus 5000
  context.subscriptions.Add (
    { new Disposable with member _.dispose () = clearInterval statusInterval; null }
  )

  // Auto-start
  let autoStart = config.get("autoStart", true)
  if autoStart then
    Client.isRunning c
    |> Promise.iter (fun running ->
      if not running then promptAutoStart () |> ignore
    )

let deactivate () =
  diagnosticsDisposable |> Option.iter (fun d -> d.dispose () |> ignore)
  sseDisposable |> Option.iter (fun d -> d.dispose () |> ignore)
  liveTestListener |> Option.iter (fun l -> l.Dispose ())
  liveTestListener <- None
  testAdapter |> Option.iter (fun a -> a.Dispose ())
  testAdapter <- None
  clearAllDecorations ()
