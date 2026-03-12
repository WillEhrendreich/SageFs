module SageFs.Vscode.Extension

open Fable.Core
open Fable.Core.JsInterop
open Vscode
open SageFs.Vscode.JsHelpers
open SageFs.Vscode.SafeInterop

module Client = SageFs.Vscode.SageFsClient
module Diag = SageFs.Vscode.DiagnosticsListener
module Lens = SageFs.Vscode.CodeLensProvider
module Completion = SageFs.Vscode.CompletionProvider
module HotReload = SageFs.Vscode.HotReloadTreeProvider
module SessionCtx = SageFs.Vscode.SessionContextTreeProvider
module Sessions = SageFs.Vscode.SessionsTreeProvider
module LiveTest = SageFs.Vscode.LiveTestingListener
module TestCtrl = SageFs.Vscode.TestControllerAdapter
module TypeExpl = SageFs.Vscode.TypeExplorerProvider
module TestDeco = SageFs.Vscode.TestDecorations
module TestLens = SageFs.Vscode.TestCodeLensProvider
module InlineDeco = SageFs.Vscode.InlineDecorations
module FileAnno = SageFs.Vscode.FileAnnotationsListener
module Blocks = SageFs.Vscode.CodeBlocks
module Discovery = SageFs.Vscode.DaemonDiscovery

open SageFs.Vscode.LiveTestingTypes
open SageFs.Vscode.FeatureTypes

// ── Mutable state ──────────────────────────────────────────────

let mutable client: Client.Client option = None
let mutable outputChannel: OutputChannel option = None
let mutable statusBarItem: StatusBarItem option = None
let mutable testStatusBarItem: StatusBarItem option = None
let mutable evalPerfStatusBar: StatusBarItem option = None
let mutable diagnosticsDisposable: Disposable option = None
let mutable sseDisposable: Disposable option = None
let mutable diagnosticCollection: DiagnosticCollection option = None
let mutable activeSessionId: string option = None
let mutable liveTestListener: LiveTest.LiveTestingListener option = None
let mutable testAdapter: TestCtrl.TestAdapter option = None
let mutable dashboardPanel: WebviewPanel option = None
let mutable typeExplorer: TypeExpl.TypeExplorer option = None

// Crash detection: track connected→offline transitions
let mutable wasRunning = false
let mutable crashPromptShown = false
let mutable staleDebounceTimer: obj option = None

// Daemon stderr capture for startup failure diagnostics
let mutable daemonStderr = ""

// Eval watchdog: monotonic ID tracks which eval is in flight.
// 0 = idle; >0 = eval in flight (generation counter).
// Prevents phantom "evaluation interrupted" dialogs when a new eval
// starts within the 5-second watchdog window of a previous eval.
let mutable evalId = 0
let mutable evalWatchdogTimer: obj option = None

// Warmup progress state: tracks phase-by-phase warmup for status bar
let mutable warmupPhase: string option = None
let mutable warmupDetail: string option = None

// File annotation decorations (coverage gutters + inline failures)
let mutable private covPassingDecoType: TextEditorDecorationType option = None
let mutable private covFailingDecoType: TextEditorDecorationType option = None
let mutable private covNoneDecoType: TextEditorDecorationType option = None
let mutable private inlineFailureDecoTypes: Map<string, TextEditorDecorationType> = Map.empty
let mutable private fileAnnotationsCache: Map<string, FileAnno.FileAnnotations> = Map.empty
let mutable private daemonConnectionDisposables: Disposable list = []

// Density preset: controls visual annotation verbosity
type Density = Full | Normal | Minimal

let mutable currentDensity = Full

let densityFromString (s: string) =
  match s.ToLowerInvariant() with
  | "normal" -> Normal
  | "minimal" -> Minimal
  | _ -> Full

let densityToString = function
  | Full -> "full"
  | Normal -> "normal"
  | Minimal -> "minimal"

let densityLabel = function
  | Full -> "Full"
  | Normal -> "Normal"
  | Minimal -> "Minimal"

let private replaceOwnedResources dispose current next =
  current
  |> List.rev
  |> List.iter dispose
  next

let private trackDaemonConnectionDisposable (disposable: Disposable) =
  daemonConnectionDisposables <- disposable :: daemonConnectionDisposables

let private disposeDaemonConnectionResources () =
  daemonConnectionDisposables <-
    replaceOwnedResources (fun (d: Disposable) -> d.dispose () |> ignore) daemonConnectionDisposables []
  diagnosticsDisposable <- None
  sseDisposable <- None
  liveTestListener <- None
  testAdapter <- None
  fileAnnotationsCache <- Map.empty
  warmupPhase <- None
  warmupDetail <- None

let cycleDensity () =
  let next =
    match currentDensity with
    | Full -> Normal
    | Normal -> Minimal
    | Minimal -> Full
  currentDensity <- next
  let cfg = Workspace.getConfiguration "sagefs"
  cfg.update("density", densityToString next, 1) |> ignore
  Window.showInformationMessage (sprintf "SageFs density: %s" (densityLabel next)) [||] |> ignore
  match next with
  | Minimal | Normal -> InlineDeco.clearCellHighlight ()
  | Full -> ()

// FSI bindings and test trace — maintained by SSE events (server-side CQRS)
// No client-side parsing; server pushes snapshots via SSE bindings_snapshot/test_trace events

// ── File Annotation Decorations (coverage gutters + inline failures) ──

let initFileAnnotationDecoTypes () =
  covPassingDecoType <- Some (
    Window.createTextEditorDecorationType (
      createObj [
        "isWholeLine" ==> false
        "gutterIconPath" ==> ""
        "overviewRulerColor" ==> newThemeColor "testing.iconPassed"
        "overviewRulerLane" ==> 1
        "before" ==> createObj [
          "contentText" ==> "│"
          "color" ==> newThemeColor "testing.iconPassed"
          "margin" ==> "0 0.3em 0 0"
        ]
      ]))
  covFailingDecoType <- Some (
    Window.createTextEditorDecorationType (
      createObj [
        "isWholeLine" ==> false
        "overviewRulerColor" ==> newThemeColor "testing.iconFailed"
        "overviewRulerLane" ==> 1
        "before" ==> createObj [
          "contentText" ==> "│"
          "color" ==> newThemeColor "testing.iconFailed"
          "margin" ==> "0 0.3em 0 0"
        ]
      ]))
  covNoneDecoType <- Some (
    Window.createTextEditorDecorationType (
      createObj [
        "isWholeLine" ==> false
        "before" ==> createObj [
          "contentText" ==> "│"
          "color" ==> newThemeColor "disabledForeground"
          "margin" ==> "0 0.3em 0 0"
        ]
      ]))

let disposeFileAnnotationDecoTypes () =
  covPassingDecoType |> Option.iter (fun d -> d.dispose ())
  covFailingDecoType |> Option.iter (fun d -> d.dispose ())
  covNoneDecoType |> Option.iter (fun d -> d.dispose ())
  covPassingDecoType <- None
  covFailingDecoType <- None
  covNoneDecoType <- None
  inlineFailureDecoTypes |> Map.iter (fun _ d -> d.dispose ())
  inlineFailureDecoTypes <- Map.empty
  fileAnnotationsCache <- Map.empty

let applyFileAnnotationsToEditor (editor: TextEditor) =
  let filePath = editor.document.fileName
  match Map.tryFind filePath fileAnnotationsCache with
  | None ->
    covPassingDecoType |> Option.iter (fun dt -> editor.setDecorations(dt, ResizeArray<obj>()))
    covFailingDecoType |> Option.iter (fun dt -> editor.setDecorations(dt, ResizeArray<obj>()))
    covNoneDecoType |> Option.iter (fun dt -> editor.setDecorations(dt, ResizeArray<obj>()))
  | Some annotations ->
    let passRanges = ResizeArray<obj>()
    let failRanges = ResizeArray<obj>()
    let noneRanges = ResizeArray<obj>()
    for ann in annotations.CoverageAnnotations do
      let range = newRange (ann.Line - 1) 0 (ann.Line - 1) 0
      let decoObj =
        createObj [
          "range" ==> range
          "hoverMessage" ==>
            (match ann.Health with
             | FileAnno.CoverageHealth.AllPassing -> "Coverage: all tests passing"
             | FileAnno.CoverageHealth.SomeFailing -> "Coverage: some tests failing"
             | FileAnno.CoverageHealth.NoCoverage -> "No coverage")
        ]
      match ann.Health with
      | FileAnno.CoverageHealth.AllPassing -> passRanges.Add decoObj
      | FileAnno.CoverageHealth.SomeFailing -> failRanges.Add decoObj
      | FileAnno.CoverageHealth.NoCoverage -> noneRanges.Add decoObj
    covPassingDecoType |> Option.iter (fun dt -> editor.setDecorations(dt, passRanges))
    covFailingDecoType |> Option.iter (fun dt -> editor.setDecorations(dt, failRanges))
    covNoneDecoType |> Option.iter (fun dt -> editor.setDecorations(dt, noneRanges))
    // Dispose old inline failure decorations for this file
    match Map.tryFind filePath inlineFailureDecoTypes with
    | Some old -> old.dispose ()
    | None -> ()
    // Apply inline failure decorations
    match annotations.InlineFailures with
    | [] ->
      inlineFailureDecoTypes <- Map.remove filePath inlineFailureDecoTypes
    | failures ->
      let deco = Window.createTextEditorDecorationType (
        createObj [
          "after" ==> createObj [
            "color" ==> newThemeColor "testing.iconFailed"
            "fontStyle" ==> "italic"
            "margin" ==> "0 0 0 1.5em"
          ]
        ])
      let ranges = ResizeArray<obj>()
      for f in failures do
        let line = f.Line - 1
        let text =
          match f.Presentation with
          | "" -> sprintf "⊘ %s" f.TestName
          | p -> sprintf "⊘ %s — %s" f.TestName p
        let range = newRange line 0 line 0
        ranges.Add (createObj [
          "range" ==> range
          "renderOptions" ==> createObj [
            "after" ==> createObj [ "contentText" ==> text ]
          ]
        ])
      editor.setDecorations(deco, ranges)
      inlineFailureDecoTypes <- Map.add filePath deco inlineFailureDecoTypes

let applyFileAnnotationsToAllEditors () =
  let editors = Window.getVisibleTextEditors ()
  for editor in editors do
    applyFileAnnotationsToEditor editor

let handleFileAnnotations (data: obj) =
  match FileAnno.parseFileAnnotations data with
  | None -> ()
  | Some annotations ->
    fileAnnotationsCache <- Map.add annotations.FilePath annotations fileAnnotationsCache
    applyFileAnnotationsToAllEditors ()

// ── JS Interop ─────────────────────────────────────────────────

[<Emit("console.debug('[SageFs]', $0)")>]
let logDebug (msg: string) : unit = jsNative

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

[<Emit("new Promise(function(resolve, reject) { require('child_process').execFile($0, $1, function(err, stdout, stderr) { if (err) reject(err); else resolve(stdout) }) })")>]
let execFileAsync (cmd: string) (args: string array) : JS.Promise<string> = jsNative

[<Emit("require('os').homedir()")>]
let osHomeDir () : string = jsNative

[<Emit("process.env.LOCALAPPDATA || ''")>]
let localAppData () : string = jsNative

[<Emit("require('fs').existsSync($0)")>]
let fileExists (path: string) : bool = jsNative

[<Emit("require('fs').mkdirSync($0, { recursive: true })")>]
let mkdirRecursive (path: string) : unit = jsNative

[<Emit("require('fs').writeFileSync($0, $1, 'utf8')")>]
let writeUtf8File (path: string) (content: string) : unit = jsNative

[<Emit("require('fs').readFileSync($0, 'utf8')")>]
let readUtf8File (path: string) : string = jsNative

let mutable daemonProcess: obj option = None
let mutable isStarting = false
let mutable onDaemonReady: (Client.Client -> unit) option = None

let private autoOpenNamespacesOptOutTemplate =
  """{ DirectoryConfig.empty with
  AutoOpenNamespaces = false
}
"""

type WarmupAutoOpenConfigResult =
  | Created of string
  | AlreadyDisabled of string
  | RequiresManualEdit of string

let private trimTrailingSeparators (path: string) =
  path.TrimEnd([| '\\'; '/' |])

let private combineWindowsPath (basePath: string) (child: string) =
  sprintf "%s\\%s" (trimTrailingSeparators basePath) child

let private daemonJsonPaths () =
  let homePath = combineWindowsPath (osHomeDir ()) ".SageFs\\daemon.json"
  let localPath =
    match System.String.IsNullOrWhiteSpace (localAppData ()) with
    | true -> None
    | false -> Some (combineWindowsPath (localAppData ()) "SageFs\\daemon.json")

  homePath :: (localPath |> Option.toList)
  |> List.distinct

let private tryReadPersistedDaemonPort () =
  daemonJsonPaths ()
  |> List.tryPick (fun path ->
    match fileExists path with
    | false -> None
    | true ->
      try
        readUtf8File path
        |> Discovery.tryParseDaemonJsonMcpPort
        |> Option.map (fun port -> port, path)
      with _ ->
        None)

let private applyConfiguredPorts (c: Client.Client) =
  let cfg = Workspace.getConfiguration "sagefs"

  Client.applyConfiguredPorts
    (cfg.get("mcpPort", Discovery.defaultMcpPort))
    (cfg.get("dashboardPort", Discovery.deriveDashboardPort Discovery.defaultMcpPort))
    c

let private discoverDaemonPorts (c: Client.Client) =
  promise {
    let configured = applyConfiguredPorts c
    let persistedPort = tryReadPersistedDaemonPort ()

    match persistedPort with
    | Some (port, path) when port <> configured.McpPort ->
      c.log (sprintf "[info] using daemon discovery hint from %s (mcpPort=%d)" path port)
    | _ -> ()

    let candidates =
      Discovery.candidateMcpPorts configured.McpPort (persistedPort |> Option.map fst)

    return! Client.discoverDaemon candidates c
  }

// ── Helpers ────────────────────────────────────────────────────

let getOutput () =
  match outputChannel with
  | Some o -> o
  | None ->
    let o = Window.createOutputChannel "SageFs"
    outputChannel <- Some o
    o

let getStatusBar () =
  match statusBarItem with
  | Some s -> s
  | None ->
    let s = Window.createStatusBarItem StatusBarAlignment.Left 100.0
    statusBarItem <- Some s
    s

let getWorkingDirectory () =
  match Workspace.workspaceFolders () with
  | Some fs when fs.Length > 0 -> Some fs.[0].uri.fsPath
  | _ -> None

let mutable activeProjectPath: string option = None

let scanForProjects () =
  promise {
    let! slnFiles = Workspace.findFiles "**/*.{sln,slnx}" "**/node_modules/**" 5
    let! projFiles = Workspace.findFiles "**/*.fsproj" "**/node_modules/**" 10
    let solutions = slnFiles |> Array.map (fun f -> Workspace.asRelativePath f)
    let projects = projFiles |> Array.map (fun f -> Workspace.asRelativePath f)
    return Array.append solutions projects
  }

let persistProjectChoice (projectPath: string) =
  let config = Workspace.getConfiguration "sagefs"
  config.update("projectPath", box projectPath, 1.) |> ignore
  activeProjectPath <- Some projectPath

let findProject () =
  promise {
    let config = Workspace.getConfiguration "sagefs"
    let configured = config.get("projectPath", "")
    match configured with
    | c when c <> "" ->
      activeProjectPath <- Some c
      return Some c
    | _ ->
      let! all = scanForProjects ()
      match all with
      | [||] -> return None
      | [| single |] ->
        activeProjectPath <- Some single
        return Some single
      | _ ->
        let! picked = Window.showQuickPick all "Select a solution or project for SageFs"
        match picked with
        | Some p -> persistProjectChoice p
        | None -> ()
        return picked
  }

let hasSemiSemiDelimiters = Blocks.hasSemiSemiDelimiters
let getBlockBounds = Blocks.getBlockBounds
let getCodeBlock = Blocks.getCodeBlock
let getAllBlockRanges = Blocks.getAllBlockRanges

// ── Recovery Actions ───────────────────────────────────────────

let showOutputPanel () =
  (getOutput()).show true

let browseForProject () =
  promise {
    let filters = createObj [ "F# Projects" ==> [| "fsproj"; "sln"; "slnx" |] ]
    let! uris = Window.showOpenDialog filters false "Select Project"
    match uris with
    | Some arr when arr.Length > 0 ->
      let uri = arr.[0]
      let config = Workspace.getConfiguration "sagefs"
      do! config.update ("projectPath", uri.fsPath, 1.0)
      Commands.executeCommand "sagefs.start" |> ignore
    | _ -> ()
  }

let openWorkspace () =
  Commands.executeCommand "vscode.openFolder" |> ignore

let checkInstallation () =
  let term = Window.createTerminal "SageFs Version Check"
  terminalShow term
  terminalSendText term "sagefs --version"

let openQuickFile () =
  Commands.executeCommand "workbench.action.quickOpen" |> ignore

let openGettingStarted () =
  promise {
    let content =
      "// ── SageFs Getting Started ─────────────────────────────────\n"
      + "// Welcome! Select each expression and press Alt+Enter (Ctrl+Enter on Mac)\n"
      + "// to evaluate it. Results appear inline, right next to your code.\n"
      + "\n"
      + "// ── Step 1: Simple expressions ──\n"
      + "1 + 1;;\n"
      + "\n"
      + "\"Hello from SageFs!\";;\n"
      + "\n"
      + "// ── Step 2: Let bindings ──\n"
      + "let greeting = \"Welcome to live F# development\";;\n"
      + "greeting.ToUpper();;\n"
      + "\n"
      + "// ── Step 3: Functions and pipelines ──\n"
      + "let square x = x * x;;\n"
      + "square 7;;\n"
      + "\n"
      + "[1..10] |> List.filter (fun n -> n % 2 = 0) |> List.map square;;\n"
      + "\n"
      + "// ── Step 4: Records and pattern matching ──\n"
      + "type Shape =\n"
      + "  | Circle of radius: float\n"
      + "  | Rectangle of width: float * height: float;;\n"
      + "\n"
      + "let area shape =\n"
      + "  match shape with\n"
      + "  | Circle r -> System.Math.PI * r * r\n"
      + "  | Rectangle (w, h) -> w * h;;\n"
      + "\n"
      + "area (Circle 5.0);;\n"
      + "area (Rectangle (3.0, 4.0));;\n"
      + "\n"
      + "// ── Step 5: Try editing! ──\n"
      + "// Change the values above and re-evaluate. SageFs keeps your session\n"
      + "// alive — previous definitions stay available.\n"
      + "//\n"
      + "// Next steps:\n"
      + "//   • Save an .fs file to trigger hot reload + live test updates\n"
      + "//   • Check the SageFs sidebar for test results and sessions\n"
      + "//   • Try 'SageFs: Show Call Graph' from the command palette\n"
      + "//   • Explore samples/ in the SageFs repo for more examples\n"
    let! doc = Workspace.openTextDocument content "fsharp"
    let! _ = Window.showTextDocument doc
    return ()
  }

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

let updateEvalPerfBar (stats: VscTimelineStats) =
  match evalPerfStatusBar with
  | None -> ()
  | Some sb ->
    let text = formatSparklineStatus stats
    match text with
    | "" -> sb.hide ()
    | t ->
      let bg =
        match stats.P50Ms with
        | Some ms when ms > 500.0 -> Some (newThemeColor "statusBarItem.errorBackground")
        | Some ms when ms > 100.0 -> Some (newThemeColor "statusBarItem.warningBackground")
        | _ -> None
      sb.text <- t
      sb.backgroundColor <- bg
      sb.tooltip <- Some (
        [ sprintf "Eval Performance Timeline (%d evals)" stats.Count
          stats.P50Ms |> Option.map (sprintf "P50: %.1f ms") |> Option.defaultValue ""
          stats.P95Ms |> Option.map (sprintf "P95: %.1f ms") |> Option.defaultValue ""
          stats.P99Ms |> Option.map (sprintf "P99: %.1f ms") |> Option.defaultValue ""
          stats.MeanMs |> Option.map (sprintf "Mean: %.1f ms") |> Option.defaultValue "" ]
        |> List.filter (fun s -> s <> "")
        |> String.concat "\n")
      sb.show ()

let refreshStatus () =
  promise {
    match client, statusBarItem with
    | Some c, Some sb ->
    try
      let! status = Client.getStatus c
      match status.connected with
      | false ->
        // Detect daemon crash: was running, now offline
        match wasRunning && not crashPromptShown with
        | true ->
          crashPromptShown <- true
          let! choice = Window.showWarningMessage "SageFs daemon has stopped." [| "Restart"; "Dismiss" |]
          match choice with
          | Some "Restart" ->
            crashPromptShown <- false
            Commands.executeCommand "sagefs.start" |> promiseIgnoreLog (fun msg -> (getOutput()).appendLine msg)
          | _ -> ()
        | false -> ()
        wasRunning <- false
        sb.text <- "$(circle-slash) SageFs: offline"
        sb.backgroundColor <- None
        sb.show ()
        activeSessionId <- None
        liveTestListener |> Option.iter (fun l -> l.SetSessionFilter None)
        HotReload.setSession c None
        SessionCtx.setSession c None
        Sessions.setSession c None
      | true ->
        wasRunning <- true
        crashPromptShown <- false
        let! sys = Client.getSystemStatus c
        let supervised =
          match sys with Some s when s.supervised -> " $(shield)" | _ -> ""
        let restarts =
          match sys with Some s when s.restartCount > 0 -> sprintf " %d↻" s.restartCount | _ -> ""
        let stripExt (name: string) =
          match jsIsNullOrUndefined (box name) with
          | true -> ""
          | false ->
          match name with
          | n when n.EndsWith(".fsproj") -> n.[..n.Length - 8]
          | n when n.EndsWith(".slnx") -> n.[..n.Length - 6]
          | n when n.EndsWith(".sln") -> n.[..n.Length - 5]
          | n -> n
        match status.status with
        | Some "Ready" | Some "Evaluating" ->
          warmupPhase <- None
          warmupDetail <- None
          let! sessions = Client.listSessions c
          let session =
            match activeSessionId with
            | Some id -> sessions |> Array.tryFind (fun s -> s.id = id)
            | None -> sessions |> Array.tryHead
          match session with
          | Some s ->
            activeSessionId <- Some s.id
            liveTestListener |> Option.iter (fun l -> l.SetSessionFilter (Some s.id))
            let projLabel =
              match s.projects with
              | [||] -> "session"
              | ps ->
                ps
                |> Array.choose (fun p ->
                  match jsIsNullOrUndefined (box p) with
                  | true -> None
                  | false -> p.Split([|'/'; '\\'|]) |> Array.last |> stripExt |> Some)
                |> String.concat ","
                |> fun s -> match s with "" -> "session" | x -> x
            let projFile =
              match activeProjectPath with
              | Some p -> p.Split([|'/'; '\\'|]) |> Array.last
              | None ->
                match s.projects with
                | [||] -> ""
                | ps ->
                  ps
                  |> Array.choose (fun p ->
                    match jsIsNullOrUndefined (box p) with
                    | true -> None
                    | false -> p.Split([|'/'; '\\'|]) |> Array.last |> Some)
                  |> Array.tryHead |> Option.defaultValue ""
            let sessionCount = sessions.Length
            let evalLabel = match s.evalCount with 0 -> "" | n -> sprintf " [%d]" n
            sb.text <- sprintf "$(zap) SageFs: %s%s%s%s" projLabel evalLabel supervised restarts
            let tooltipText =
              match projFile with
              | "" -> sprintf "SageFs — %d session(s) — click for session menu" sessionCount
              | f -> sprintf "SageFs: %s — %d session(s) — click for session menu" f sessionCount
            sb.tooltip <- Some tooltipText
          | None ->
            activeSessionId <- None
            liveTestListener |> Option.iter (fun l -> l.SetSessionFilter None)
            sb.text <- sprintf "$(zap) SageFs: ready (no session)%s%s" supervised restarts
          sb.backgroundColor <- None
          let activeId = activeSessionId
          HotReload.setSession c activeId
          SessionCtx.setSession c activeId
          Sessions.setSession c activeId
          TypeExpl.setClient (Some c)
        | Some "Starting" | Some "Restarting" ->
          match warmupPhase with
          | Some phase ->
            let phaseLabel =
              match phase with
              | "creating_fsi" -> "Creating FSI..."
              | "scanning_sources" -> "Scanning sources..."
              | "loading_assemblies" -> "Loading assemblies..."
              | "opening_namespaces" ->
                match warmupDetail with
                | Some d -> sprintf "Opening namespaces (%s)" d
                | None -> "Opening namespaces..."
              | "finalizing" -> "Finalizing..."
              | _ -> "Warming up..."
            sb.text <- sprintf "$(loading~spin) SageFs: %s" phaseLabel
          | None ->
            sb.text <- "$(loading~spin) SageFs: warming up..."
          sb.backgroundColor <- None
        | Some "Faulted" | Some "Stopped" | Some "error" ->
          match status.error with
          | Some err ->
            sb.text <- "$(error) SageFs: session error"
            sb.tooltip <- Some err.message
            let! choice =
              Window.showErrorMessage
                err.message
                [| err.suggestedAction; "Show Output" |]
            match choice with
            | Some action when action = err.suggestedAction ->
              (getOutput()).appendLine (sprintf "[SageFs] Suggested action: %s" err.suggestedAction)
            | Some "Show Output" -> showOutputPanel ()
            | _ -> ()
          | None ->
            sb.text <- "$(error) SageFs: session error"
          sb.backgroundColor <-
            Some (newThemeColor "statusBarItem.errorBackground")
        | Some "no session" ->
          sb.text <- "$(circle-slash) SageFs: no session"
          sb.backgroundColor <- None
        | _ ->
          sb.text <- "$(loading~spin) SageFs: starting..."
        sb.show ()
    with ex ->
      c.log (sprintf "[warn] refreshStatus: %O" ex)
      sb.text <- "$(circle-slash) SageFs: offline"
      sb.show ()
    | _ -> ()
  } |> promiseIgnoreLog (fun msg -> (getOutput()).appendLine msg)

// ── Daemon Lifecycle ───────────────────────────────────────────

let rec startDaemon () =
  promise {
    match isStarting with
    | true -> ()
    | false ->
    isStarting <- true
    match client with
    | None ->
      isStarting <- false
      let! choice = Window.showErrorMessage "SageFs not activated." [| "Retry"; "Show Output" |]
      match choice with
      | Some "Retry" -> Commands.executeCommand "sagefs.start" |> ignore
      | Some "Show Output" -> showOutputPanel ()
      | _ -> ()
    | Some c ->
    let! _ = discoverDaemonPorts c
    let! running = Client.isRunning c
    match running with
    | true ->
      isStarting <- false
      refreshStatus ()
    | false ->
      let! projPath = findProject ()
      match projPath with
      | None ->
        isStarting <- false
        let! choice = Window.showErrorMessage "No .fsproj or .sln found. Open an F# project first." [| "Browse for Project"; "Open Workspace" |]
        match choice with
        | Some "Browse for Project" -> browseForProject () |> promiseIgnoreLog (fun msg -> (getOutput()).appendLine msg)
        | Some "Open Workspace" -> openWorkspace ()
        | _ -> ()
      | Some proj ->
        let out = getOutput ()
        out.show true
        out.appendLine (sprintf "Starting SageFs daemon with %s on mcpPort=%d..." proj c.mcpPort)
        daemonStderr <- ""
        let workDir = getWorkingDirectory () |> Option.defaultValue "."
        let startArgs = Discovery.buildDaemonStartArgs proj c.mcpPort
        let proc = spawn "sagefs" startArgs (createObj [
          "cwd" ==> workDir; "detached" ==> true; "stdio" ==> [| box "ignore"; box "pipe"; box "pipe" |]; "shell" ==> true
        ])
        onProcError proc (fun msg ->
          out.appendLine (sprintf "[SageFs spawn error] %s" msg)
          daemonStderr <- daemonStderr + msg + "\n"
          isStarting <- false
          let sb = getStatusBar ()
          sb.text <- "$(error) SageFs: spawn failed"
        )
        onProcExit proc (fun code _signal ->
          out.appendLine (sprintf "[SageFs] process exited (code %d)" code)
          isStarting <- false
        )
        let stderr = procStderr proc
        stderr |> tryOfObj |> Option.iter (fun s -> onData s (fun chunk ->
          out.appendLine chunk
          daemonStderr <- daemonStderr + chunk + "\n"))
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
            sb.text <- sprintf "$(loading~spin) SageFs starting... (%ds)" attempts
            promise {
              let! ready = Client.isRunning c
              if ready then
                intervalId |> Option.iter jsClearInterval
                isStarting <- false
                out.appendLine "SageFs daemon is ready."
                onDaemonReady |> Option.iter (fun f -> f c)
                refreshStatus ()
              elif attempts > 120 then
                intervalId |> Option.iter jsClearInterval
                isStarting <- false
                let stderrSnippet =
                  match daemonStderr.Trim() with
                  | "" -> ""
                  | s -> sprintf "\n\nDaemon output:\n%s" (if s.Length > 500 then s.Substring(0, 500) + "…" else s)
                out.appendLine (sprintf "Timed out waiting for SageFs daemon after 120s.%s" stderrSnippet)
                out.show false
                let! choice = Window.showErrorMessage (sprintf "SageFs daemon failed to start after 120s.%s" stderrSnippet) [| "Retry"; "Show Full Output"; "Check Installation" |]
                match choice with
                | Some "Retry" -> Commands.executeCommand "sagefs.restart" |> ignore
                | Some "Show Full Output" -> showOutputPanel ()
                | Some "Check Installation" -> checkInstallation ()
                | _ -> ()
                sb.text <- "$(error) SageFs: offline"
            } |> promiseIgnoreLog (fun msg -> out.appendLine msg)
          ) 1000
        intervalId <- Some id
  }

and ensureRunning () =
  promise {
    match client with
    | None -> return false
    | Some c ->
    let! _ = discoverDaemonPorts c
    let! running = Client.isRunning c
    if running then return true
    else
      let! choice = Window.showWarningMessage "SageFs daemon is not running." [| "Start SageFs"; "Cancel" |]
      match choice with
      | Some "Start SageFs" ->
        do! startDaemon ()
        let mutable ready = false
        let mutable attempts = 0
        while not ready && attempts < 30 do
          do! sleep 1000
          let! r = Client.isRunning c
          ready <- r
          attempts <- attempts + 1
        if not ready then
          let! choice = Window.showErrorMessage "SageFs didn't start in time." [| "Retry"; "Show Output"; "Check Installation" |]
          match choice with
          | Some "Retry" -> Commands.executeCommand "sagefs.restart" |> ignore
          | Some "Show Output" -> showOutputPanel ()
          | Some "Check Installation" -> checkInstallation ()
          | _ -> ()
        return ready
      | _ ->
        return false
  }

// ── Commands ───────────────────────────────────────────────────

/// Wraps the ensureRunning + getClient boilerplate.
let withClient (action: Client.Client -> JS.Promise<unit>) =
  promise {
    let! ok = ensureRunning ()
    match ok, client with
    | true, Some c -> do! action c
    | _ -> ()
  }

/// Fire a client action that returns ApiOutcome, show brief status bar flash, then refresh.
let simpleCommand (defaultMsg: string) (action: Client.Client -> JS.Promise<Client.ApiOutcome>) =
  withClient (fun c ->
    promise {
      let! result = action c
      let msg = result |> Client.ApiOutcome.messageOrDefault defaultMsg
      match statusBarItem with
      | Some sb ->
        sb.text <- sprintf "$(check) %s" msg
        jsSetTimeout (fun () -> refreshStatus () |> ignore) 3000 |> ignore
      | None ->
        Window.showInformationMessage (sprintf "SageFs: %s" msg) [||] |> ignore
      refreshStatus ()
    })

type EvalResult =
  | EvalOk of output: string * elapsed: float
  | EvalError of message: string
  | EvalConnectionError of message: string

/// Wait for session to reach Ready state (up to ~60s with 2s intervals).
/// Returns true if ready, false if timed out.
let waitForSessionReady () : JS.Promise<bool> =
  promise {
    match client with
    | None -> return false
    | Some c ->
    let mutable ready = false
    let mutable attempts = 0
    while not ready && attempts < 30 do
      let! r = Client.isReady c
      ready <- r
      if not ready then
        do! sleep 2000
        attempts <- attempts + 1
    return ready
  }

let evalCore (code: string) (filePath: string option) (evalMode: string option) (blockStartLine: int option) : JS.Promise<EvalResult> =
  promise {
    evalId <- evalId + 1
    let myId = evalId
    try
      match client with
      | None ->
        if evalId = myId then evalId <- 0
        return EvalConnectionError "SageFs not activated"
      | Some c ->
      let! ready = Client.isReady c
      if not ready then
        (getOutput()).appendLine "Session not ready, waiting for warmup..."
        let! becameReady = waitForSessionReady ()
        if not becameReady then
          if evalId = myId then evalId <- 0
          return EvalError "Session did not become ready in time. Check the dashboard for status."
        else
          (getOutput()).appendLine "Session ready, evaluating..."
          let workDir = getWorkingDirectory ()
          let startTime = performanceNow ()
          let! result = Client.evalCode code workDir filePath evalMode blockStartLine c
          if evalId = myId then evalId <- 0
          let elapsed = performanceNow () - startTime
          match result with
          | Client.Failed errMsg -> return EvalError errMsg
          | Client.Succeeded msg ->
            return EvalOk (msg |> Option.defaultValue "", elapsed)
      else
        let workDir = getWorkingDirectory ()
        let startTime = performanceNow ()
        let! result = Client.evalCode code workDir filePath evalMode blockStartLine c
        if evalId = myId then evalId <- 0
        let elapsed = performanceNow () - startTime
        match result with
        | Client.Failed errMsg -> return EvalError errMsg
        | Client.Succeeded msg ->
          return EvalOk (msg |> Option.defaultValue "", elapsed)
    with err ->
      if evalId = myId then evalId <- 0
      return EvalConnectionError (string err)
  }

/// Log eval result to output channel. Auto-shows output on error.
let logEvalResult (out: OutputChannel) (result: EvalResult) =
  match result with
  | EvalOk (output, elapsed) ->
    out.appendLine (sprintf "%s  (%s)" output (InlineDeco.formatDuration elapsed))
  | EvalError errMsg ->
    out.appendLine (sprintf "❌ Error:\n%s" errMsg)
    out.show true
  | EvalConnectionError msg ->
    out.appendLine (sprintf "❌ Connection error: %s" msg)
    out.show true
  result

/// Get code from selection or code block, append ;; if needed.
/// Returns (code, startLine, endLine) — server handles module context.
let getEvalCode (ed: TextEditor) =
  let doc = ed.document
  let raw, blockStartLine, blockEndLine =
    if not ed.selection.isEmpty then
      let startLine = int ed.selection.start.line
      let endLine = int ed.selection.``end``.line
      let text = doc.getTextRange (newRange startLine (int ed.selection.start.character) endLine (int ed.selection.``end``.character))
      text, startLine, endLine
    else
      getCodeBlock ed
  match raw.Trim() with
  | "" -> None
  | _ ->
    let code = if raw.TrimEnd().EndsWith(";;") then raw else raw.TrimEnd() + ";;"
    Some (code, blockStartLine, blockEndLine)

let evalSelection () =
  promise {
    match Window.getActiveTextEditor () with
    | None ->
      let! choice = Window.showWarningMessage "No active editor." [| "Open File" |]
      match choice with
      | Some "Open File" -> openQuickFile ()
      | _ -> ()
    | Some ed ->
      let! ok = ensureRunning ()
      match ok, getEvalCode ed with
      | false, _ | _, None -> ()
      | true, Some (code, blockStart, blockEnd) ->
        let filePath = Some ed.document.fileName
        let blockLine = Some (blockStart + 1) // VS Code is 0-based, server is 1-based
        InlineDeco.flashEvalRange ed blockStart blockEnd
        let out = getOutput ()
        do! Window.withProgress ProgressLocation.Window "SageFs: evaluating..." (fun _progress _token ->
          promise {
            out.appendLine "──── eval ────"
            out.appendLine code
            out.appendLine ""
            let! result = evalCore code filePath (Some "block") blockLine
            match logEvalResult out result with
            | EvalError errMsg ->
              out.show true
              InlineDeco.showInlineDiagnostic ed errMsg (Some blockEnd)
            | EvalOk (output, elapsed) ->
              InlineDeco.showInlineResult ed output (Some elapsed) (Some blockEnd)
            | EvalConnectionError _ ->
              out.show true
              let! choice = Window.showErrorMessage "Cannot reach SageFs daemon. Is it running?" [| "Show Output"; "Restart" |]
              match choice with
              | Some "Restart" -> Commands.executeCommand "sagefs.restart" |> promiseIgnoreLog (fun msg -> (getOutput()).appendLine msg)
              | _ -> ()
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
        let filePath = Some ed.document.fileName
        let out = getOutput ()
        out.show true
        out.appendLine (sprintf "──── eval file: %s ────" ed.document.fileName)
        let! result = evalCore code filePath (Some "file") None
        logEvalResult out result |> ignore
  }

let evalRange (args: obj) =
  promise {
    match Window.getActiveTextEditor () with
    | None -> ()
    | Some ed ->
      let! ok = ensureRunning ()
      let range: Range = unbox args
      let raw = ed.document.getTextRange range
      let endLine = int range.``end``.line
      match ok, raw.Trim() with
      | false, _ | _, "" -> ()
      | true, _ ->
        let code = if raw.TrimEnd().EndsWith(";;") then raw else raw.TrimEnd() + ";;"
        let startLine = int range.start.line
        let filePath = Some ed.document.fileName
        let blockLine = Some (startLine + 1) // 0-based → 1-based
        let out = getOutput ()
        out.show true
        out.appendLine "──── eval block ────"
        out.appendLine code
        out.appendLine ""
        let! result = evalCore code filePath (Some "block") blockLine
        match logEvalResult out result with
        | EvalOk (output, elapsed) ->
          InlineDeco.showInlineResult ed output (Some elapsed) (Some endLine)
        | EvalError errMsg ->
          InlineDeco.showInlineDiagnostic ed errMsg (Some endLine)
        | _ -> ()
  }

let resetSessionCmd () =
  simpleCommand "Reset complete" Client.resetSession

/// Evaluate all code blocks in the file sequentially (top to bottom).
let evalAllBlocks () =
  promise {
    match Window.getActiveTextEditor () with
    | None ->
      let! choice = Window.showWarningMessage "No active editor." [| "Open File" |]
      match choice with
      | Some "Open File" -> openQuickFile ()
      | _ -> ()
    | Some ed ->
      let! ok = ensureRunning ()
      match ok with
      | false -> ()
      | true ->
        let doc = ed.document
        let lineCount = int doc.lineCount
        let out = getOutput ()
        out.appendLine (sprintf "──── eval all blocks: %s ────" doc.fileName)
        let blocks = getAllBlockRanges doc
        // Evaluate each block in sequence
        let mutable errorCount = 0
        for blockStart, blockEnd in blocks do
          let range = newRange blockStart 0 blockEnd (int (doc.lineAt(float blockEnd).text.Length))
          let raw = doc.getTextRange range
          match raw.Trim() with
          | "" -> ()
          | _ ->
            let code = if raw.TrimEnd().EndsWith(";;") then raw else raw.TrimEnd() + ";;"
            let filePath = Some doc.fileName
            let blockLine = Some (blockStart + 1) // 0-based → 1-based
            InlineDeco.flashEvalRange ed blockStart blockEnd
            let! result = evalCore code filePath (Some "block") blockLine
            match logEvalResult out result with
            | EvalOk (output, elapsed) ->
              InlineDeco.showInlineResult ed output (Some elapsed) (Some blockEnd)
            | EvalError errMsg ->
              errorCount <- errorCount + 1
              InlineDeco.showInlineDiagnostic ed errMsg (Some blockEnd)
            | EvalConnectionError _ ->
              errorCount <- errorCount + 1
        let summary =
          match errorCount with
          | 0 -> sprintf "✓ All %d blocks evaluated" blocks.Length
          | n -> sprintf "⚠ %d of %d blocks had errors" n blocks.Length
        out.appendLine summary
        Window.showInformationMessage summary [||] |> ignore
  }

let hardResetCmd () =
  simpleCommand "Hard reset complete" (Client.hardReset true)

let createSessionCmd () =
  withClient (fun c ->
    promise {
      let! projPath = findProject ()
      match projPath with
      | None ->
        let! choice = Window.showErrorMessage "No .fsproj or .sln found. Open an F# project first." [| "Browse for Project"; "Open Workspace" |]
        match choice with
        | Some "Browse for Project" -> browseForProject () |> promiseIgnoreLog (fun msg -> (getOutput()).appendLine msg)
        | Some "Open Workspace" -> openWorkspace ()
        | _ -> ()
      | Some proj ->
        let workDir = getWorkingDirectory () |> Option.defaultValue "."
        do! Window.withProgress ProgressLocation.Notification "SageFs: Creating session..." (fun _p _t ->
          promise {
            let! result = Client.createSession proj workDir c
            match result with
            | Client.Succeeded _ ->
              Window.showInformationMessage (sprintf "SageFs: Session created for %s" proj) [||] |> ignore
            | Client.Failed err ->
              let! choice = Window.showErrorMessage (sprintf "SageFs: %s" err) [| "Show Output"; "Retry" |]
              match choice with
              | Some "Show Output" -> showOutputPanel ()
              | Some "Retry" -> Commands.executeCommand "sagefs.createSession" |> ignore
              | _ -> ()
            refreshStatus ()
          }
        )
    })

let configureWarmupAutoOpenCmd () =
  promise {
    match getWorkingDirectory () with
    | None ->
      let! choice = Window.showErrorMessage "Open an F# project or workspace first." [| "Browse for Project"; "Open Workspace" |]
      match choice with
      | Some "Browse for Project" -> browseForProject () |> promiseIgnoreLog (fun msg -> (getOutput()).appendLine msg)
      | Some "Open Workspace" -> openWorkspace ()
      | _ -> ()
    | Some workDir ->
      let configDir = combineWindowsPath workDir ".SageFs"
      let configPath = combineWindowsPath configDir "config.fsx"
      let result =
        match fileExists configPath with
        | false ->
          mkdirRecursive configDir
          writeUtf8File configPath autoOpenNamespacesOptOutTemplate
          Created configPath
        | true ->
          let content = readUtf8File configPath
          match content.Contains("AutoOpenNamespaces = false") || content.Contains("AutoOpenNamespaces=false") with
          | true -> AlreadyDisabled configPath
          | false -> RequiresManualEdit configPath
      let! doc = Workspace.openTextDocumentUri (uriFile configPath)
      let! _ = Window.showTextDocument doc
      match result with
      | Created path ->
        Window.showInformationMessage (sprintf "Created %s with AutoOpenNamespaces = false." path) [||] |> ignore
      | AlreadyDisabled path ->
        Window.showInformationMessage (sprintf "Warmup auto-open is already disabled in %s." path) [||] |> ignore
      | RequiresManualEdit path ->
        Window.showWarningMessage (sprintf "Existing config opened at %s. Set AutoOpenNamespaces = false; it was not overwritten." path) [||] |> ignore
  }

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
        let! choice = Window.showInformationMessage "No sessions available." [| "Create Session"; "Start Daemon" |]
        match choice with
        | Some "Create Session" -> Commands.executeCommand "sagefs.createSession" |> ignore
        | Some "Start Daemon" -> Commands.executeCommand "sagefs.start" |> ignore
        | _ -> ()
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
              let! choice = Window.showErrorMessage (sprintf "Failed: %s" err) [| "Show Diagnostics"; "Show Output" |]
              match choice with
              | Some "Show Diagnostics" -> showOutputPanel ()
              | Some "Show Output" -> showOutputPanel ()
              | _ -> ()
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

/// Context-aware session menu — the primary entry point from the status bar.
let sessionMenu () =
  promise {
    match client with
    | None ->
      let! choice = Window.showWarningMessage "SageFs is not connected." [| "Start SageFs"; "Show Output" |]
      match choice with
      | Some "Start SageFs" -> Commands.executeCommand "sagefs.start" |> ignore
      | Some "Show Output" -> showOutputPanel ()
      | _ -> ()
    | Some c ->
      let! status = Client.getStatus c
      let items = ResizeArray<string>()
      // State-aware top items
      match status.connected with
      | false ->
        items.Add "$(play) Start SageFs"
      | true ->
        let! sessions = Client.listSessions c
        match sessions.Length with
        | 0 -> items.Add "$(add) Create New Session"
        | _ ->
          // Show sessions with status
          for s in sessions do
            let isActive =
              match activeSessionId with
              | Some id -> id = s.id
              | None -> false
            let icon =
              match isActive with
              | true -> "$(star-full)"
              | false -> "$(terminal)"
            let proj =
              match s.projects with
              | [||] -> "no project"
              | ps ->
                ps
                |> Array.map (fun p ->
                  let parts = p.Split([|'/'; '\\'|])
                  let name = parts |> Array.last
                  match name with
                  | n when n.EndsWith(".fsproj") -> n.[..n.Length - 8]
                  | n -> n)
                |> String.concat ", "
            let evals =
              match s.evalCount with
              | 0 -> ""
              | n -> sprintf " [%d]" n
            let label = sprintf "%s %s — %s%s" icon proj s.status evals
            items.Add label
          items.Add "──────────"
          items.Add "$(add) Create New Session"
        // Always-available actions
        match status.status with
        | Some "Ready" | Some "Evaluating" ->
          items.Add "$(debug-restart) Reset Session"
          items.Add "$(refresh) Hard Reset (Rebuild)"
        | _ -> ()
        items.Add "$(dashboard) Open Dashboard"
        items.Add "$(gear) Cycle Density"

      let! picked = Window.showQuickPick (items.ToArray()) "SageFs"
      match picked with
      | None -> ()
      | Some choice ->
        match choice with
        | s when s.Contains "Start SageFs" ->
          do! startDaemon ()
        | s when s.Contains "Create New Session" ->
          do! createSessionCmd ()
        | s when s.Contains "Reset Session" && not (s.Contains "Hard") ->
          do! resetSessionCmd ()
        | s when s.Contains "Hard Reset" ->
          do! hardResetCmd ()
        | s when s.Contains "Open Dashboard" ->
          Commands.executeCommand "sagefs.openDashboard" |> ignore
        | s when s.Contains "Cycle Density" ->
          cycleDensity ()
        | s when s.Contains "──" -> () // separator
        | sessionItem ->
          // Clicked a session line — switch to it
          match client with
          | None -> ()
          | Some c2 ->
            let! sessions = Client.listSessions c2
            // Match by project label in the picked string
            let picked =
              sessions
              |> Array.tryFind (fun sess ->
                sessionItem.Contains (
                  match sess.projects with
                  | [||] -> "no project"
                  | ps ->
                    let name = ps.[0].Split([|'/'; '\\'|]) |> Array.last
                    match name with
                    | n when n.EndsWith(".fsproj") -> n.[..n.Length - 8]
                    | n -> n))
            match picked with
            | Some sess ->
              let! _ = Client.switchSession sess.id c2
              activeSessionId <- Some sess.id
              Window.showInformationMessage (sprintf "Switched to %s" sess.id) [||] |> ignore
              refreshStatus ()
            | None -> ()
  }

let stopDaemon () =
  match daemonProcess with
  | Some proc ->
    killProc proc
    daemonProcess <- None
    disposeDaemonConnectionResources ()
  | None -> ()
  Window.showInformationMessage "SageFs: stop the daemon from its terminal or use `sagefs stop`." [||] |> ignore
  refreshStatus ()

let switchProject () =
  promise {
    let! all = scanForProjects ()
    match all with
    | [||] ->
      Window.showWarningMessage "No .fsproj or .sln files found in workspace." [||] |> ignore
    | _ ->
      let! picked = Window.showQuickPick all "Switch SageFs to a different project"
      match picked with
      | Some p ->
        persistProjectChoice p
        let out = getOutput ()
        out.appendLine (sprintf "Switching to project: %s" p)
        stopDaemon ()
        do! sleep 1000
        do! startDaemon ()
      | None -> ()
  }

let openDashboard () =
  promise {
    match client with
    | None -> ()
    | Some c ->
      let! _ = discoverDaemonPorts c
      let dashUrl = Client.dashboardUrl c
      let dashboardHtml =
        sprintf """<!DOCTYPE html>
<html style="height:100%%;margin:0;padding:0">
<head><meta http-equiv="Content-Security-Policy" content="default-src 'none'; frame-src http://localhost:*; style-src 'unsafe-inline'"></head>
<body style="height:100%%;margin:0;padding:0">
<iframe src="%s" style="width:100%%;height:100%%;border:none"></iframe>
</body>
</html>""" dashUrl

      match dashboardPanel with
      | Some panel ->
        panel.webview.html <- dashboardHtml
        panel.reveal 1
      | None ->
        let panel =
          Window.createWebviewPanel
            "sagefsDashboard"
            "SageFs Dashboard"
            2  // ViewColumn.Beside
            (createObj [ "enableScripts" ==> true ])
        panel.webview.html <- dashboardHtml
        panel.onDidDispose (fun () -> dashboardPanel <- None) |> ignore
        dashboardPanel <- Some panel
  }

let evalAdvance () =
  promise {
    match Window.getActiveTextEditor () with
    | None ->
      let! choice = Window.showWarningMessage "No active editor." [| "Open File" |]
      match choice with
      | Some "Open File" -> openQuickFile ()
      | _ -> ()
    | Some ed ->
      let! ok = ensureRunning ()
      match ok, getEvalCode ed with
      | false, _ | _, None -> ()
      | true, Some (code, blockStart, blockEnd) ->
        let filePath = Some ed.document.fileName
        let blockLine = Some (blockStart + 1) // 0-based → 1-based
        InlineDeco.flashEvalRange ed blockStart blockEnd
        let out = getOutput ()
        let! result = evalCore code filePath (Some "block") blockLine
        match logEvalResult out result with
        | EvalError errMsg ->
          InlineDeco.showInlineDiagnostic ed errMsg (Some blockEnd)
        | EvalOk (output, elapsed) ->
          InlineDeco.showInlineResult ed output (Some elapsed) (Some blockEnd)
          // Move cursor to next non-blank line after the block end
          let lineCount = int ed.document.lineCount
          let mutable nextLine = blockEnd + 1
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

/// Navigate to the next code block.
let nextBlock () =
  match Window.getActiveTextEditor () with
  | None -> ()
  | Some ed ->
    let doc = ed.document
    let curLine = int ed.selection.active.line
    let _, blockEnd = getBlockBounds doc curLine
    let lineCount = int doc.lineCount
    let mutable next = blockEnd + 1
    // Skip blank lines between blocks
    while next < lineCount && doc.lineAt(float next).text.Trim() = "" do
      next <- next + 1
    match next < lineCount with
    | true ->
      let pos = newPosition next 0
      setEditorSelection ed (newSelection pos pos)
      revealEditorRange ed (newRange next 0 next 0)
    | false -> ()

/// Navigate to the previous code block.
let prevBlock () =
  match Window.getActiveTextEditor () with
  | None -> ()
  | Some ed ->
    let doc = ed.document
    let curLine = int ed.selection.active.line
    let blockStart, _ = getBlockBounds doc curLine
    match blockStart > 0 with
    | false -> ()
    | true ->
      let mutable prev = blockStart - 1
      // Skip blank lines between blocks
      while prev > 0 && doc.lineAt(float prev).text.Trim() = "" do
        prev <- prev - 1
      let prevStart, _ = getBlockBounds doc prev
      let pos = newPosition prevStart 0
      setEditorSelection ed (newSelection pos pos)
      revealEditorRange ed (newRange prevStart 0 prevStart 0)

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
          let! choice = Window.showErrorMessage err [| "Show Diagnostics"; "Show Output" |]
          match choice with
          | Some "Show Diagnostics" -> showOutputPanel ()
          | Some "Show Output" -> showOutputPanel ()
          | _ -> ()
      | _ ->
        let! choice = Window.showWarningMessage "Open an .fsx file to load it as a script." [| "Open File" |]
        match choice with
        | Some "Open File" -> openQuickFile ()
        | _ -> ()
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
      | Some "Open Dashboard" -> do! openDashboard ()
      | _ -> ()
  }

let checkHealth () =
  promise {
    try
      let! version = execFileAsync "sagefs" [| "--version" |]
      let trimmed = version.Trim()
      Window.showInformationMessage (sprintf "SageFs CLI found: %s" trimmed) [||] |> ignore
    with _ ->
      Window.showErrorMessage
        "SageFs CLI not found. Install it with: dotnet tool install --global SageFs"
        [||]
      |> ignore
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
    with ex ->
      logDebug (sprintf "Could not hijack %s: %s" cmd (string ex))

// ── Activate / Deactivate ──────────────────────────────────────

let activate (context: ExtensionContext) =
  let config = Workspace.getConfiguration "sagefs"
  let mcpPort = config.get("mcpPort", 37749)
  let dashboardPort = config.get("dashboardPort", 37750)

  let c = Client.create mcpPort dashboardPort (fun msg -> (getOutput()).appendLine msg)
  client <- Some c

  currentDensity <- densityFromString (config.get("density", "full"))

  let out = Window.createOutputChannel "SageFs"
  outputChannel <- Some out

  // Log unhandled promise rejections with stack traces to the output channel
  installRejectionHandler (fun msg -> out.appendLine msg)

  let sb = Window.createStatusBarItem StatusBarAlignment.Left 50.
  sb.command <- Some "sagefs.sessionMenu"
  sb.tooltip <- Some "Click for SageFs session menu"
  statusBarItem <- Some sb
  context.subscriptions.Add (sb :> obj :?> Disposable)

  let tsb = Window.createStatusBarItem StatusBarAlignment.Left 49.
  tsb.text <- "$(beaker) No tests"
  tsb.tooltip <- Some "SageFs live testing — click to enable"
  tsb.command <- Some "sagefs.enableLiveTesting"
  testStatusBarItem <- Some tsb
  context.subscriptions.Add (tsb :> obj :?> Disposable)

  let esb = Window.createStatusBarItem StatusBarAlignment.Left 48.
  esb.tooltip <- Some "SageFs eval performance"
  evalPerfStatusBar <- Some esb
  context.subscriptions.Add (esb :> obj :?> Disposable)

  let dc = Languages.createDiagnosticCollection "sagefs"
  diagnosticCollection <- Some dc
  context.subscriptions.Add (dc :> obj :?> Disposable)

  // Mark inline results as stale when F# documents change (debounced)
  let docChangeSub = Workspace.onDidChangeTextDocument (fun _evt ->
    staleDebounceTimer |> Option.iter jsClearTimeout
    staleDebounceTimer <- Some (jsSetTimeout (fun () ->
      match Window.getActiveTextEditor () with
      | Some ed when ed.document.fileName.EndsWith(".fs") || ed.document.fileName.EndsWith(".fsx") ->
        if not (Map.isEmpty InlineDeco.blockDecorations) then
          InlineDeco.markDecorationsStale ed
        // Clear binding-value ghost text: source lines may have shifted
        InlineDeco.clearBindingValueDecorations ()
      | _ -> ()
    ) 300))
  context.subscriptions.Add docChangeSub

  // Hot Reload TreeView
  HotReload.register context
  HotReload.setSession c None

  // Session Context TreeView
  SessionCtx.register context
  SessionCtx.setSession c None

  // Sessions TreeView
  Sessions.register context
  Sessions.setSession c None

  // Type Explorer TreeView
  typeExplorer <- Some (TypeExpl.create context client (fun () -> activeSessionId))

  let reg cmd handler =
    context.subscriptions.Add (Commands.registerCommand cmd handler)
  let logToOutput msg = (getOutput()).appendLine msg

  reg "sagefs.eval" (fun _ -> evalSelection () |> promiseIgnoreLog logToOutput)
  reg "sagefs.evalFile" (fun _ -> evalFile () |> promiseIgnoreLog logToOutput)
  reg "sagefs.evalRange" (fun args -> evalRange args |> promiseIgnoreLog logToOutput)
  reg "sagefs.evalAdvance" (fun _ -> evalAdvance () |> promiseIgnoreLog logToOutput)
  reg "sagefs.evalAllBlocks" (fun _ -> evalAllBlocks () |> promiseIgnoreLog logToOutput)
  reg "sagefs.cancelEval" (fun _ -> cancelEvalCmd () |> promiseIgnoreLog logToOutput)
  reg "sagefs.nextBlock" (fun _ -> nextBlock ())
  reg "sagefs.prevBlock" (fun _ -> prevBlock ())
  reg "sagefs.loadScript" (fun _ -> loadScriptCmd () |> promiseIgnoreLog logToOutput)
  reg "sagefs.start" (fun _ -> startDaemon () |> promiseIgnoreLog logToOutput)
  reg "sagefs.stop" (fun _ -> stopDaemon ())
  reg "sagefs.restart" (fun _ ->
    promise {
      let out = getOutput ()
      out.appendLine "Restarting SageFs daemon..."
      stopDaemon ()
      do! sleep 1000
      do! startDaemon ()
    } |> promiseIgnoreLog logToOutput)
  reg "sagefs.openDashboard" (fun _ -> openDashboard () |> promiseIgnoreLog logToOutput)
  reg "sagefs.switchProject" (fun _ -> switchProject () |> promiseIgnoreLog logToOutput)
  reg "sagefs.checkHealth" (fun _ -> checkHealth () |> promiseIgnoreLog logToOutput)
  reg "sagefs.openGettingStarted" (fun _ -> openGettingStarted () |> promiseIgnoreLog logToOutput)
  reg "sagefs.sessionMenu" (fun _ -> sessionMenu () |> promiseIgnoreLog logToOutput)
  reg "sagefs.resetSession" (fun _ -> resetSessionCmd () |> promiseIgnoreLog logToOutput)
  reg "sagefs.hardReset" (fun _ -> hardResetCmd () |> promiseIgnoreLog logToOutput)
  reg "sagefs.createSession" (fun _ -> createSessionCmd () |> promiseIgnoreLog logToOutput)
  reg "sagefs.configureWarmupAutoOpen" (fun _ -> configureWarmupAutoOpenCmd () |> promiseIgnoreLog logToOutput)
  reg "sagefs.switchSession" (fun _ -> switchSessionCmd () |> promiseIgnoreLog logToOutput)
  reg "sagefs.stopSession" (fun _ -> stopSessionCmd () |> promiseIgnoreLog logToOutput)

  // Tree view inline actions for Sessions panel
  reg "sagefs.switchToSession" (fun args ->
    promise {
      match client with
      | None -> ()
      | Some c ->
        let sid: string = try args?sessionId with _ -> ""
        match sid with
        | "" -> ()
        | id ->
          let! _ = Client.switchSession id c
          activeSessionId <- Some id
          refreshStatus ()
    } |> promiseIgnoreLog logToOutput)
  reg "sagefs.stopSessionInline" (fun args ->
    promise {
      match client with
      | None -> ()
      | Some c ->
        let sid: string = try args?sessionId with _ -> ""
        match sid with
        | "" -> ()
        | id ->
          let! _ = Client.stopSession id c
          match activeSessionId with
          | Some aid when aid = id -> activeSessionId <- None
          | _ -> ()
          refreshStatus ()
    } |> promiseIgnoreLog logToOutput)
  reg "sagefs.resetSessionInline" (fun _ ->
    resetSessionCmd () |> promiseIgnoreLog logToOutput)

  reg "sagefs.clearResults" (fun _ -> InlineDeco.clearAllDecorations ())
  reg "sagefs.cycleDensity" (fun _ -> cycleDensity ())
  reg "sagefs.enableLiveTesting" (fun _ ->
    simpleCommand "Live testing enabled" Client.enableLiveTesting |> promiseIgnoreLog logToOutput)
  reg "sagefs.disableLiveTesting" (fun _ ->
    simpleCommand "Live testing disabled" Client.disableLiveTesting |> promiseIgnoreLog logToOutput)
  reg "sagefs.runTests" (fun _ ->
    simpleCommand "Tests queued" (Client.runTests "") |> promiseIgnoreLog logToOutput)
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
      }) |> promiseIgnoreLog logToOutput)
  reg "sagefs.showHistory" (fun _ ->
    withClient (fun c ->
      promise {
        let! bodyOpt = Client.getRecentEvents 30 c
        match bodyOpt with
        | Some body ->
          let lines = body.Split('\n') |> Array.filter (fun l -> l.Trim().Length > 0)
          match lines with
          | [||] -> Window.showInformationMessage "No recent events" [||] |> ignore
          | _ -> Window.showQuickPick lines "Recent SageFs events" |> promiseIgnoreLog logToOutput
        | None ->
          let! choice = Window.showWarningMessage "Could not fetch events" [| "Start SageFs"; "Show Output" |]
          match choice with
          | Some "Start SageFs" -> Commands.executeCommand "sagefs.start" |> ignore
          | Some "Show Output" -> showOutputPanel ()
          | _ -> ()
      }) |> promiseIgnoreLog logToOutput)
  reg "sagefs.showCallGraph" (fun _ ->
    withClient (fun c ->
      promise {
        let! overviewOpt = Client.getDependencyGraph "" c
        match overviewOpt with
        | None ->
          let! choice = Window.showWarningMessage "Could not fetch dependency graph" [| "Start SageFs"; "Show Output" |]
          match choice with
          | Some "Start SageFs" -> Commands.executeCommand "sagefs.start" |> ignore
          | Some "Show Output" -> showOutputPanel ()
          | _ -> ()
        | Some body ->
          let parsed = jsonParse body
          let total = fieldInt "TotalSymbols" parsed |> Option.defaultValue 0
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
                let! choice = Window.showWarningMessage "Could not fetch graph" [| "Start SageFs"; "Show Output" |]
                match choice with
                | Some "Start SageFs" -> Commands.executeCommand "sagefs.start" |> ignore
                | Some "Show Output" -> showOutputPanel ()
                | _ -> ()
              | Some detail ->
                let parsed2 = jsonParse detail
                let tests = fieldArray "Tests" parsed2 |> Option.defaultValue [||]
                match tests with
                | [||] ->
                  Window.showInformationMessage (sprintf "No tests cover '%s'" sym) [||] |> ignore
                | _ ->
                  let items =
                    tests |> Array.map (fun t ->
                      let name = fieldString "TestName" t |> Option.defaultValue "?"
                      let status = fieldString "Status" t |> Option.defaultValue "unknown"
                      let icon = match status with "passed" -> "✓" | "failed" -> "✗" | _ -> "●"
                      sprintf "%s %s [%s]" icon name status)
                  Window.showQuickPick items (sprintf "Tests covering '%s'" sym) |> promiseIgnoreLog logToOutput
            | _ -> ()
      }) |> promiseIgnoreLog logToOutput)
  reg "sagefs.showBindings" (fun _ ->
    match liveTestListener |> Option.map (fun l -> l.Bindings ()) with
    | Some [||] | None ->
      Window.showInformationMessage "No FSI bindings yet" [||] |> ignore
    | Some bindings ->
      let items =
        bindings |> Array.choose (fun b ->
          match fieldString "Name" b, fieldString "TypeSig" b with
          | Some name, Some typeSig ->
            let shadow = fieldInt "ShadowCount" b |> Option.defaultValue 0
            let shadowLabel = match shadow with n when n > 1 -> sprintf " (×%d)" n | _ -> ""
            Some (sprintf "%s : %s%s" name typeSig shadowLabel)
          | _ -> None)
      Window.showQuickPick items "FSI Bindings"
      |> promiseIgnoreLog logToOutput)
  reg "sagefs.showTestTrace" (fun _ ->
    match liveTestListener |> Option.bind (fun l -> l.TestTrace ()) with
    | Some trace ->
      let get name = fieldInt name trace |> Option.defaultValue 0
      let items = [|
        sprintf "Enabled: %b" (fieldBool "Enabled" trace |> Option.defaultValue false)
        sprintf "Running: %b" (fieldBool "IsRunning" trace |> Option.defaultValue false)
        sprintf "Total: %d | Passed: %d | Failed: %d"
          (fieldObj "Summary" trace |> Option.bind (fieldInt "Total") |> Option.defaultValue 0)
          (fieldObj "Summary" trace |> Option.bind (fieldInt "Passed") |> Option.defaultValue 0)
          (fieldObj "Summary" trace |> Option.bind (fieldInt "Failed") |> Option.defaultValue 0)
      |]
      Window.showQuickPick items "test trace" |> promiseIgnoreLog logToOutput
    | None -> Window.showInformationMessage "No test trace data yet" [||] |> ignore)

  reg "sagefs.exportSession" (fun _ ->
    withClient (fun c ->
      promise {
        match activeSessionId with
        | None ->
          let! choice = Window.showInformationMessage "No active session" [| "Create Session"; "Start Daemon" |]
          match choice with
          | Some "Create Session" -> Commands.executeCommand "sagefs.createSession" |> ignore
          | Some "Start Daemon" -> Commands.executeCommand "sagefs.start" |> ignore
          | _ -> ()
        | Some sid ->
          let! result = Client.exportSessionAsFsx sid c
          match result with
          | None ->
            let! choice = Window.showErrorMessage "Failed to export session" [| "Show Output" |]
            match choice with
            | Some "Show Output" -> showOutputPanel ()
            | _ -> ()
          | Some r ->
            match r.evalCount with
            | 0 -> Window.showInformationMessage "No evaluations to export" [||] |> ignore
            | _ ->
              let! doc = Workspace.openTextDocument r.content "fsharp"
              let! _ = Window.showTextDocument doc
              ()
      }) |> promiseIgnoreLog logToOutput)

  reg "sagefs.explainTestFailure" (fun args ->
    promise {
      let requestedId =
        try
          match args with
          | null -> None
          | _ ->
            let arr = args :?> obj array
            match arr.Length with
            | 0 -> None
            | _ -> arr.[0] |> tryCastString
        with _ -> None
      let narratives = TestLens.narrativeState
      let failedWithNarrative =
        narratives
        |> Map.toArray
        |> Array.filter (fun (_, n) -> n.Summary <> "")
      match failedWithNarrative with
      | [||] ->
        Window.showInformationMessage "No failure narratives available yet. Run tests with live testing enabled." [||] |> ignore
      | _ ->
        let items =
          failedWithNarrative |> Array.map (fun (id, n) ->
            let label =
              match requestedId with
              | Some rid when rid = id -> sprintf "★ %s" n.Summary
              | _ -> n.Summary
            label, n)
        let labels = items |> Array.map fst
        let! labelOpt = Window.showQuickPick labels "Select failed test to explain"
        match labelOpt with
        | None -> ()
        | Some label ->
          match items |> Array.tryFind (fun (l, _) -> l = label) with
          | None -> ()
          | Some (_, n) ->
            let out = getOutput ()
            out.show true
            out.appendLine ""
            out.appendLine (sprintf "═══ Why failed: %s ═══" n.TestId)
            out.appendLine (sprintf "  Summary  : %s" n.Summary)
            out.appendLine (sprintf "  Since    : %s" n.TimeSinceLastPass)
            match n.CausalChanges with
            | [||] -> out.appendLine "  Causes   : (no changes detected)"
            | changes ->
              out.appendLine "  Causes   :"
              changes |> Array.iter (fun c ->
                out.appendLine (sprintf "    • [%s] %s" c.Kind c.Name))
            out.appendLine ""
    } |> promiseIgnoreLog logToOutput)

  reg "sagefs.suggestRepair" (fun args ->
    promise {
      let requestedId =
        try
          match args with
          | null -> None
          | _ ->
            let arr = args :?> obj array
            match arr.Length with
            | 0 -> None
            | _ -> arr.[0] |> tryCastString
        with _ -> None
      let diagEntries =
        TestLens.diagnosisState
        |> Map.toArray
        |> Array.filter (fun (_, symbols) -> symbols.Length > 0)
      match diagEntries with
      | [||] ->
        Window.showInformationMessage
          "No repair suggestions yet. Run tests with live testing enabled to generate diagnosis." [||]
        |> ignore
      | _ ->
        let items =
          diagEntries |> Array.map (fun (testName, symbols) ->
            let sym = symbols |> String.concat ", "
            let label =
              match requestedId with
              | Some rid when (testName.Contains rid) -> sprintf "★ %s" testName
              | _ -> testName
            sprintf "%s  ←  %s changed" label sym, testName, symbols)
        let labels = items |> Array.map (fun (l, _, _) -> l)
        let! labelOpt = Window.showQuickPick labels "Select test to repair"
        match labelOpt with
        | None -> ()
        | Some label ->
          match items |> Array.tryFind (fun (l, _, _) -> l = label) with
          | None -> ()
          | Some (_, testName, symbols) ->
            let out = getOutput ()
            out.show true
            out.appendLine ""
            out.appendLine (sprintf "═══ Repair Suggestion: %s ═══" testName)
            out.appendLine (sprintf "  Caused by: %s" (String.concat ", " symbols))
            out.appendLine ""
            out.appendLine "  Suggested actions:"
            out.appendLine "    1. Check the changed symbols above for unintended mutations"
            out.appendLine "    2. Use sagefs-explain_test_failure in MCP for full narrative"
            out.appendLine "    3. Use sagefs-preview_what_if to explore hypothetical fixes"
            out.appendLine "    4. Eval the corrected code and watch live tests go green"
            out.appendLine ""
            Window.showInformationMessage
              (sprintf "Repair guidance for '%s' written to output." testName)
              [||]
            |> ignore
    } |> promiseIgnoreLog logToOutput)

  // ── Failing test navigation ────────────────────────────────────
  let mutable failingTestIndex = 0

  let getFailingTests () =
    match liveTestListener with
    | None -> [||]
    | Some l ->
      let st = l.State ()
      st.Results
      |> Map.toArray
      |> Array.choose (fun (tid, r) ->
        match r.Outcome with
        | VscTestOutcome.Failed _ | VscTestOutcome.Errored _ ->
          Map.tryFind tid st.Tests
          |> Option.bind (fun info ->
            match info.FilePath, info.Line with
            | Some fp, Some ln -> Some (info, fp, ln)
            | _ -> None)
        | _ -> None)
      |> Array.sortBy (fun (info, fp, ln) -> fp, ln)

  let navigateToFailingTest (delta: int) =
    promise {
      let tests = getFailingTests ()
      match tests.Length with
      | 0 ->
        Window.showInformationMessage "No failing tests with source locations" [||] |> ignore
      | count ->
        failingTestIndex <- (failingTestIndex + delta) % count
        match failingTestIndex < 0 with
        | true -> failingTestIndex <- failingTestIndex + count
        | false -> ()
        let (info, filePath, line) = tests.[failingTestIndex]
        let uri = uriFile filePath
        let! doc = Workspace.openTextDocumentUri uri
        let! ed = Window.showTextDocument doc
        let pos = newPosition (line - 1) 0
        let sel = newSelection pos pos
        setEditorSelection (ed :?> TextEditor) sel
        revealEditorRange (ed :?> TextEditor) (newRange (line - 1) 0 (line - 1) 0)
        Window.showInformationMessage
          (sprintf "Failing test %d/%d: %s" (failingTestIndex + 1) count info.DisplayName)
          [||]
        |> ignore
    }

  reg "sagefs.nextFailingTest" (fun _ ->
    navigateToFailingTest 1 |> promiseIgnoreLog logToOutput)
  reg "sagefs.prevFailingTest" (fun _ ->
    navigateToFailingTest -1 |> promiseIgnoreLog logToOutput)

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
  let rec connectToRunningDaemon (c: Client.Client) =
    c.log "connectToRunningDaemon: disposing existing daemon-owned resources..."
    disposeDaemonConnectionResources ()
    c.log "connectToRunningDaemon: establishing fresh SSE connections..."
    // Establish fresh connection resources
    let diagLogger = Some (fun (msg: string) -> (getOutput()).appendLine (sprintf "[Diagnostics SSE] %s" msg))
    let diagDisposable = Diag.start c.mcpPort dc diagLogger
    diagnosticsDisposable <- Some diagDisposable
    trackDaemonConnectionDisposable diagDisposable
    // TestController for VS Code Test Explorer
    let adapter = TestCtrl.create (fun () -> client) (fun () ->
      liveTestListener
      |> Option.map (fun l -> (l.State ()).FailureNarratives)
      |> Option.defaultValue Map.empty)
    testAdapter <- Some adapter
    trackDaemonConnectionDisposable {
      new Disposable with
        member _.dispose () =
          adapter.Dispose ()
          null
    }
    // Initialize inline test decorations
    TestDeco.initialize ()
    trackDaemonConnectionDisposable {
      new Disposable with
        member _.dispose () =
          TestDeco.dispose ()
          null
    }
    initFileAnnotationDecoTypes ()
    trackDaemonConnectionDisposable {
      new Disposable with
        member _.dispose () =
          disposeFileAnnotationDecoTypes ()
          null
    }
    // Live testing listener — handles test_summary, test_results_batch, and state events
    let refreshAllDecorations () =
      liveTestListener
      |> Option.map (fun l -> l.State ())
      |> Option.defaultValue VscLiveTestState.empty
      |> fun state ->
        TestDeco.applyToAllEditors state
        TestDeco.applyCoverageToAllEditors state
        applyFileAnnotationsToAllEditors ()
        state
    let liveTestCallbacks: LiveTest.LiveTestingCallbacks = {
      OnStateChange = fun changes ->
        adapter.Refresh changes
        let state = refreshAllDecorations ()
        TestDeco.updateDiagnostics state
        TestLens.updateState state
        // Auto-focus output on test failure
        let hasFailure =
          changes |> List.exists (fun c ->
            match c with
            | VscStateChange.TestsCompleted results ->
              results |> Array.exists (fun r ->
                match r.Outcome with
                | VscTestOutcome.Failed _ | VscTestOutcome.Errored _ -> true
                | _ -> false)
            | _ -> false)
        match hasFailure with
        | true -> match outputChannel with Some out -> out.show true | None -> ()
        | false -> ()
      OnSummaryUpdate = fun summary -> updateTestStatusBar summary
      OnStatusRefresh = fun () -> refreshStatus ()
      OnBindingsUpdate = fun _ -> ()
      OnTestTraceUpdate = fun _ -> ()
      OnFeatureEvent = Some {
        OnEvalDiff = fun _ -> ()
        OnCellGraph = fun _ -> ()
        OnBindingScope = fun _ -> ()
        OnTimeline = fun stats -> updateEvalPerfBar stats
      }
      OnEvalResult = fun filePath blockStartLine output durationMs ->
        let line = blockStartLine - 1 // server is 1-based, VS Code is 0-based
        Window.getVisibleTextEditors ()
        |> Array.tryFind (fun ed -> ed.document.fileName = filePath)
        |> Option.iter (fun ed ->
          InlineDeco.clearEvalInProgress ed
          InlineDeco.showInlineResult ed output (Some durationMs) (Some line))
      OnEvalStarted = fun filePath blockStartLine ->
        let line = blockStartLine - 1
        Window.getVisibleTextEditors ()
        |> Array.tryFind (fun ed -> ed.document.fileName = filePath)
        |> Option.iter (fun ed ->
          InlineDeco.markDecorationsStale ed
          InlineDeco.showEvalInProgress ed line)
      OnEvalHeartbeat = fun filePath blockStartLine elapsedMs ->
        let line = blockStartLine - 1
        Window.getVisibleTextEditors ()
        |> Array.tryFind (fun ed -> ed.document.fileName = filePath)
        |> Option.iter (fun ed ->
          InlineDeco.updateEvalInProgressElapsed ed line elapsedMs)
      OnSourceLocationsUpdate = fun locations ->
        adapter.UpdateSourceLocations locations
      OnFileAnnotations = fun data ->
        handleFileAnnotations data
      OnFailureNarratives = fun narratives ->
        let narrativeMap = narratives |> Array.fold (fun m n -> Map.add n.TestId n m) Map.empty
        TestLens.updateNarratives narrativeMap
        testAdapter |> Option.iter (fun a -> a.RefreshNarratives())
      OnWarmupProgress = fun step total message _progress phase ->
        warmupPhase <- Some phase
        let detail =
          match total > 5 with
          | true -> Some (sprintf "%d/%d" step total)
          | false -> None
        warmupDetail <- detail
        refreshStatus ()
        match phase with
        | "finalizing" ->
          warmupPhase <- None
          warmupDetail <- None
        | _ -> ()
      OnWarmupCompleted = fun projectName ->
        Window.showInformationMessage
          (sprintf "SageFs: warmup complete for %s — session ready" projectName)
          [||]
        |> ignore
      OnFileReloaded = fun filePath ->
        let shortName =
          let parts = filePath.Split([| '/'; '\\' |])
          if parts.Length > 0 then parts.[parts.Length - 1] else filePath
        (getOutput()).appendLine (sprintf "[SageFs] File reloaded: %s" shortName)
      OnSessionFaulted = fun reason ->
        promise {
          let! choice =
            Window.showWarningMessage
              (sprintf "SageFs session faulted: %s. Use Restart Session to recover." reason)
              [| "Restart Session"; "Show Output" |]
          match choice with
          | Some "Restart Session" -> Commands.executeCommand "sagefs.restart" |> ignore
          | Some "Show Output" -> showOutputPanel ()
          | _ -> ()
        } |> promiseIgnore
      OnDomainModel = fun _data ->
        // Domain model visualization data — available via sagefs.visualize_domain_model MCP tool.
        // Future: render in a dedicated webview panel.
        ()
      OnDiagnosisReady = fun data ->
        let severity = fieldString "Severity" data |> Option.defaultValue "unknown"
        let summary = fieldString "Summary" data |> Option.defaultValue ""
        (getOutput()).appendLine (sprintf "[SageFs Diagnostics] %s: %s" severity summary)
        // Parse per-failure causal symbols and feed them into the repair CodeLens
        let failures : LiveTestingTypes.VscDiagnosisFailure array =
          try
            let arr : obj array = unbox (data?failures)
            arr |> Array.map (fun f ->
              { LiveTestingTypes.VscDiagnosisFailure.TestName =
                  (try unbox<string>(f?testName) with _ -> "")
                CausalSymbols =
                  try unbox<string array>(f?causalSymbols)
                  with _ -> [||] })
          with _ -> [||]
        match failures.Length with
        | 0 -> ()
        | _ -> TestLens.updateDiagnosis failures
      OnBindingValuesUpdate = fun blockStartLine bindingValues ->
        // Show persistent inline binding value ghost text in the active editor.
        // blockStartLine is 1-based from server; VS Code setDecorations is 0-based.
        let line = max 0 (blockStartLine - 1)
        Window.getActiveTextEditor ()
        |> Option.iter (fun ed -> InlineDeco.showBindingValues ed line bindingValues)
    }
    let reconnectHandler = Some (fun () ->
      c.log "SSE reconnected — refreshing status..."
      // Cancel eval watchdog timer on reconnect
      evalWatchdogTimer |> Option.iter jsClearTimeout
      evalWatchdogTimer <- None
      match statusBarItem with
      | Some sb ->
        sb.text <- "$(check) SageFs: connected"
        sb.backgroundColor <- None
        sb.show ()
      | None -> ()
      refreshStatus ()
    )
    let disconnectHandler = Some (fun () ->
      c.log "SSE disconnected — reconnecting..."
      match statusBarItem with
      | Some sb ->
        sb.text <- "$(sync~spin) SageFs: reconnecting..."
        sb.backgroundColor <- Some (newThemeColor "statusBarItem.warningBackground")
        sb.show ()
      | None -> ()
      // Eval watchdog: if an eval is in flight, fire a synthetic error after 5s
      match evalId with
      | 0 -> ()
      | activeEvalId ->
        evalWatchdogTimer |> Option.iter jsClearTimeout
        evalWatchdogTimer <- Some (jsSetTimeout (fun () ->
          match evalId = activeEvalId with
          | true ->
            evalId <- 0
            evalWatchdogTimer <- None
            let out = getOutput ()
            out.appendLine "⚠ Evaluation interrupted: daemon connection lost"
            out.show true
            match Window.getActiveTextEditor () with
            | Some ed -> InlineDeco.showInlineDiagnostic ed "⚠ Evaluation interrupted: daemon connection lost" None
            | None -> ()
            Window.showWarningMessage "Evaluation interrupted: SageFs daemon connection lost." [| "Reconnect"; "Show Output" |]
            |> Promise.map (fun choice ->
              match choice with
              | Some "Reconnect" -> Commands.executeCommand "sagefs.reconnect" |> promiseIgnoreLog (fun msg -> (getOutput()).appendLine msg)
              | Some "Show Output" -> showOutputPanel ()
              | _ -> ())
            |> promiseIgnore
          | false ->
            evalWatchdogTimer <- None
        ) 5000)
    )
    let sseLogger = Some (fun (msg: string) -> (getOutput()).appendLine (sprintf "[SSE] %s" msg))
    let listener = LiveTest.start c.mcpPort liveTestCallbacks reconnectHandler disconnectHandler sseLogger
    liveTestListener <- Some listener
    c.log "connectToRunningDaemon: SSE streams established."
    let listenerDisposable = {
      new Disposable with member _.dispose () = listener.Dispose(); null
    }
    sseDisposable <- Some listenerDisposable
    trackDaemonConnectionDisposable listenerDisposable
    // Re-apply decorations when editors change
    Window.onDidChangeVisibleTextEditors (fun _editors -> refreshAllDecorations () |> ignore)
    |> trackDaemonConnectionDisposable
    Window.onDidChangeActiveTextEditor (fun _editor -> refreshAllDecorations () |> ignore)
    |> trackDaemonConnectionDisposable
    // Auto-discover and create session if none exists (delay to let daemon stabilize)
    promise {
      do! sleep 2000
      try
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
      with _ -> ()
    } |> promiseIgnoreLog logToOutput

  let checkAndConnect (c: Client.Client) =
    promise {
      let! _ = discoverDaemonPorts c
      let! sysOpt = Client.getSystemStatus c
      match sysOpt with
      | None -> connectToRunningDaemon c
      | Some sys ->
        match Client.checkVersion sys with
        | Result.Ok () -> connectToRunningDaemon c
        | Result.Error msg ->
          (getOutput()).appendLine (sprintf "[SageFs] Version mismatch: %s" msg)
          let! choice = Window.showErrorMessage msg [| "Update Now"; "Show Output"; "Ignore" |]
          match choice with
          | Some "Update Now" ->
            let term = Window.createTerminal "SageFs Update"
            terminalShow term
            terminalSendText term "dotnet tool update --global SageFs"
          | Some "Show Output" -> showOutputPanel ()
          | _ -> ()
    } |> promiseIgnoreLog logToOutput

  // Wire up daemon-ready callback for startDaemon lifecycle
  onDaemonReady <- Some checkAndConnect

  // Single health check: connect to running daemon OR auto-start
  let autoStart = config.get("autoStart", true)
  let out = getOutput ()
  let extVersion =
    try fieldString "version" context?extension?packageJSON |> Option.defaultValue "?"
    with _ -> "?"
  out.appendLine (sprintf "SageFs v%s activating (mcpPort=%d, dashboardPort=%d, autoStart=%b)" extVersion c.mcpPort c.dashboardPort autoStart)
  promise {
    try
      out.appendLine "Checking for running daemon..."
      let! _ = discoverDaemonPorts c
      let! running = Client.isRunning c
      match running with
      | true ->
        out.appendLine "Daemon found, connecting SSE streams..."
        try
          checkAndConnect c
        with ex ->
          out.appendLine (sprintf "SSE connection error: %s" (string ex))
          match statusBarItem with
          | Some sb -> sb.text <- "$(warning) SageFs: SSE error"; sb.show ()
          | None -> ()
      | false ->
        match autoStart with
        | true ->
          out.appendLine "Daemon not found, auto-starting..."
          let! projPath = findProject ()
          match projPath with
          | Some proj ->
            out.appendLine (sprintf "Starting daemon for %s" proj)
            do! startDaemon ()
          | None ->
            out.appendLine "No .fsproj/.sln found, skipping auto-start."
        | false ->
          out.appendLine "Daemon not running (autoStart=false, waiting for manual start)."
    with ex ->
      out.appendLine (sprintf "SageFs activation error: %s" (string ex))
      out.show false
      match statusBarItem with
      | Some sb ->
        sb.text <- "$(error) SageFs: activation failed"
        sb.show ()
      | None -> ()
  } |> promiseIgnoreLog logToOutput

  // Config change listener
  context.subscriptions.Add (
    Workspace.onDidChangeConfiguration (fun e ->
      match e.affectsConfiguration "sagefs" with
      | true ->
        let cfg = Workspace.getConfiguration "sagefs"
        Client.applyConfiguredPorts
          (cfg.get("mcpPort", Discovery.defaultMcpPort))
          (cfg.get("dashboardPort", Discovery.deriveDashboardPort Discovery.defaultMcpPort))
          c
        |> ignore
        discoverDaemonPorts c |> promiseIgnoreLog logToOutput
        currentDensity <- densityFromString (cfg.get("density", "full"))
      | false -> ()
    )
  )

   // Cell highlight: update on cursor move / editor switch (respects density)
  let updateCellHighlightForEditor (ed: TextEditor) =
    match currentDensity with
    | Minimal | Normal -> InlineDeco.clearCellHighlight ()
    | Full ->
      let langId: string = try ed.document?languageId with _ -> ""
      match langId with
      | "fsharp" ->
        let curLine = int ed.selection.active.line
        let s, e = getBlockBounds ed.document curLine
        InlineDeco.updateCellHighlight ed s e
      | _ -> ()
  context.subscriptions.Add (
    Window.onDidChangeTextEditorSelection (fun ed -> updateCellHighlightForEditor ed))
  context.subscriptions.Add (
    Window.onDidChangeActiveTextEditor (fun edOpt ->
      match edOpt with
      | Some ed -> updateCellHighlightForEditor ed
      | None -> InlineDeco.clearCellHighlight ()))

  // Status polling (15s for responsive crash detection)
  refreshStatus ()
  let statusInterval = jsSetInterval refreshStatus 15000
  context.subscriptions.Add (
    { new Disposable with member _.dispose () = jsClearInterval statusInterval; null }
  )

let deactivate () =
  disposeDaemonConnectionResources ()
  typeExplorer |> Option.iter (fun te -> te.dispose ())
  typeExplorer <- None
  dashboardPanel |> Option.iter (fun p -> p.dispose () |> ignore)
  dashboardPanel <- None
  HotReload.stopAutoRefresh ()
  SessionCtx.stopAutoRefresh ()
  InlineDeco.clearAllDecorations ()
