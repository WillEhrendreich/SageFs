/// Thin VSCode API bindings — only the APIs SageFs actually uses.
/// Much simpler than the 723KB Ionide auto-generated bindings.
module Vscode

open Fable.Core
open Fable.Core.JsInterop

// ── Core Types ──────────────────────────────────────────────────

type [<AllowNullLiteral>] Disposable =
  abstract dispose: unit -> obj

type [<AllowNullLiteral>] CancellationToken =
  abstract isCancellationRequested: bool

type [<AllowNullLiteral>] Position =
  abstract line: float
  abstract character: float

type [<AllowNullLiteral>] Range =
  abstract start: Position
  abstract ``end``: Position
  abstract isEmpty: bool

type [<AllowNullLiteral>] Selection =
  abstract start: Position
  abstract ``end``: Position
  abstract active: Position
  abstract anchor: Position
  abstract isEmpty: bool

type [<AllowNullLiteral>] Uri =
  abstract fsPath: string
  abstract scheme: string
  abstract path: string

type [<AllowNullLiteral>] TextLine =
  abstract text: string
  abstract lineNumber: float

type [<AllowNullLiteral>] TextDocument =
  abstract getText: unit -> string
  [<Emit("$0.getText($1)")>]
  abstract getTextRange: range: Range -> string
  abstract lineAt: line: float -> TextLine
  abstract lineCount: float
  abstract fileName: string
  abstract uri: Uri

type [<AllowNullLiteral>] TextEditor =
  abstract document: TextDocument
  abstract selection: Selection
  abstract setDecorations: decorationType: TextEditorDecorationType * rangesOrOptions: ResizeArray<obj> -> unit

and [<AllowNullLiteral>] TextEditorDecorationType =
  abstract dispose: unit -> unit

type [<AllowNullLiteral>] OutputChannel =
  abstract appendLine: value: string -> unit
  abstract show: ?preserveFocus: bool -> unit
  abstract dispose: unit -> unit

type [<AllowNullLiteral>] StatusBarItem =
  abstract text: string with get, set
  abstract tooltip: string option with get, set
  abstract command: string option with get, set
  abstract backgroundColor: obj option with get, set
  abstract show: unit -> unit
  abstract hide: unit -> unit
  abstract dispose: unit -> unit

type [<AllowNullLiteral>] DiagnosticCollection =
  abstract clear: unit -> unit
  abstract set: uri: Uri * diagnostics: ResizeArray<Diagnostic> -> unit
  abstract dispose: unit -> unit

and [<AllowNullLiteral>] Diagnostic =
  abstract range: Range
  abstract message: string
  abstract severity: DiagnosticSeverity

and [<StringEnum>] DiagnosticSeverity =
  | [<CompiledName("Error")>] Error
  | [<CompiledName("Warning")>] Warning
  | [<CompiledName("Information")>] Information
  | [<CompiledName("Hint")>] Hint

type [<AllowNullLiteral>] Command =
  abstract title: string with get, set
  abstract command: string with get, set
  abstract arguments: obj array option with get, set

type [<AllowNullLiteral>] CodeLens =
  abstract range: Range
  abstract command: Command option

type [<AllowNullLiteral>] WorkspaceFolder =
  abstract uri: Uri
  abstract name: string

type [<AllowNullLiteral>] WorkspaceConfiguration =
  abstract get: section: string * ?defaultValue: 'T -> 'T

type [<AllowNullLiteral>] ConfigurationChangeEvent =
  abstract affectsConfiguration: section: string -> bool

type [<AllowNullLiteral>] QuickPickOptions =
  abstract placeHolder: string option with get, set

type [<AllowNullLiteral>] TreeItem =
  interface end

type [<AllowNullLiteral>] EventEmitter<'T> =
  abstract event: obj
  abstract fire: data: 'T -> unit
  abstract dispose: unit -> unit

type [<AllowNullLiteral>] TreeView<'T> =
  abstract dispose: unit -> unit

type [<AllowNullLiteral>] ExtensionContext =
  abstract subscriptions: ResizeArray<Disposable>

type [<AllowNullLiteral>] Progress<'T> =
  abstract report: value: 'T -> unit

type [<AllowNullLiteral>] ProgressOptions =
  abstract location: int with get, set
  abstract title: string option with get, set

type [<AllowNullLiteral>] Webview =
  abstract html: string with get, set
  abstract onDidReceiveMessage: listener: (obj -> unit) -> Disposable

type [<AllowNullLiteral>] WebviewPanel =
  abstract webview: Webview
  abstract reveal: column: int -> unit
  abstract onDidDispose: listener: (unit -> unit) -> Disposable
  abstract dispose: unit -> unit

// ── Testing API ───────────────────────────────────────────────

type [<AllowNullLiteral>] TestItem =
  abstract id: string
  abstract label: string with get, set
  abstract uri: Uri option with get, set
  abstract range: Range option with get, set
  abstract children: TestItemCollection
  abstract canResolveChildren: bool with get, set
  abstract description: string option with get, set
  abstract sortText: string option with get, set
  abstract tags: obj array with get, set

and [<AllowNullLiteral>] TestItemCollection =
  abstract size: int
  [<Emit("$0.add($1)")>]
  abstract add: item: TestItem -> unit
  [<Emit("$0.delete($1)")>]
  abstract delete: id: string -> unit
  [<Emit("$0.get($1)")>]
  abstract get: id: string -> TestItem option
  [<Emit("$0.replace($1)")>]
  abstract replace: items: TestItem array -> unit

type [<AllowNullLiteral>] TestRunRequest =
  abstract ``include``: TestItem array option
  abstract exclude: TestItem array option

type [<AllowNullLiteral>] TestRun =
  abstract token: CancellationToken
  [<Emit("$0.started($1)")>]
  abstract started: item: TestItem -> unit
  [<Emit("$0.passed($1, $2)")>]
  abstract passed: item: TestItem * duration: float -> unit
  [<Emit("$0.failed($1, $2, $3)")>]
  abstract failed: item: TestItem * message: obj * duration: float -> unit
  [<Emit("$0.skipped($1)")>]
  abstract skipped: item: TestItem -> unit
  [<Emit("$0.end()")>]
  abstract ``end``: unit -> unit

type [<AllowNullLiteral>] TestRunProfile =
  abstract label: string with get, set
  abstract isDefault: bool with get, set

type [<AllowNullLiteral>] TestController =
  abstract id: string
  abstract label: string with get, set
  abstract items: TestItemCollection
  [<Emit("$0.createTestItem($1, $2, $3)")>]
  abstract createTestItem: id: string * label: string * ?uri: Uri -> TestItem
  [<Emit("$0.createRunProfile($1, $2, $3, $4)")>]
  abstract createRunProfile: label: string * kind: int * handler: (TestRunRequest -> CancellationToken -> JS.Promise<unit>) * ?isDefault: bool -> TestRunProfile
  [<Emit("$0.createTestRun($1)")>]
  abstract createTestRun: request: TestRunRequest -> TestRun
  abstract dispose: unit -> unit

[<RequireQualifiedAccess>]
module TestRunProfileKind =
  [<Emit("1")>]
  let Run: int = jsNative
  [<Emit("2")>]
  let Debug: int = jsNative
  [<Emit("3")>]
  let Coverage: int = jsNative

// ── Enums as Ints ────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module StatusBarAlignment =
  [<Emit("1")>]
  let Left: int = jsNative
  [<Emit("2")>]
  let Right: int = jsNative

[<RequireQualifiedAccess>]
module ProgressLocation =
  [<Emit("1")>]
  let SourceControl: int = jsNative
  [<Emit("10")>]
  let Window: int = jsNative
  [<Emit("15")>]
  let Notification: int = jsNative

[<RequireQualifiedAccess>]
module VDiagnosticSeverity =
  [<Emit("0")>]
  let Error: int = jsNative
  [<Emit("1")>]
  let Warning: int = jsNative
  [<Emit("2")>]
  let Information: int = jsNative
  [<Emit("3")>]
  let Hint: int = jsNative

// ── Window API ──────────────────────────────────────────────────

[<Import("window", "vscode")>]
let private windowExports: obj = jsNative

module Window =
  [<Emit("$0.activeTextEditor")>]
  let activeTextEditor (w: obj) : TextEditor option = jsNative

  let getActiveTextEditor () : TextEditor option =
    activeTextEditor windowExports

  [<Emit("$0.createOutputChannel($1)")>]
  let private _createOutputChannel (w: obj) (name: string) : OutputChannel = jsNative
  let createOutputChannel (name: string) = _createOutputChannel windowExports name

  [<Emit("$0.createStatusBarItem($1, $2)")>]
  let private _createStatusBarItem (w: obj) (alignment: int) (priority: float) : StatusBarItem = jsNative
  let createStatusBarItem (alignment: int) (priority: float) = _createStatusBarItem windowExports alignment priority

  [<Emit("$0.showInformationMessage($1, ...$2)")>]
  let private _showInfoMsg (w: obj) (msg: string) (items: string array) : JS.Promise<string option> = jsNative
  let showInformationMessage (msg: string) (items: string array) = _showInfoMsg windowExports msg items

  [<Emit("$0.showWarningMessage($1, ...$2)")>]
  let private _showWarnMsg (w: obj) (msg: string) (items: string array) : JS.Promise<string option> = jsNative
  let showWarningMessage (msg: string) (items: string array) = _showWarnMsg windowExports msg items

  [<Emit("$0.showErrorMessage($1, ...$2)")>]
  let private _showErrMsg (w: obj) (msg: string) (items: string array) : JS.Promise<string option> = jsNative
  let showErrorMessage (msg: string) (items: string array) = _showErrMsg windowExports msg items

  [<Emit("$0.showQuickPick($1, $2)")>]
  let private _showQuickPick (w: obj) (items: string array) (opts: obj) : JS.Promise<string option> = jsNative
  let showQuickPick (items: string array) (placeHolder: string) =
    _showQuickPick windowExports items (createObj [ "placeHolder" ==> placeHolder ])

  [<Emit("$0.withProgress($1, $2)")>]
  let private _withProgress (w: obj) (opts: obj) (task: obj -> CancellationToken -> JS.Promise<unit>) : JS.Promise<unit> = jsNative
  let withProgress (location: int) (title: string) (task: obj -> CancellationToken -> JS.Promise<unit>) =
    _withProgress windowExports (createObj [ "location" ==> location; "title" ==> title ]) task

  [<Emit("$0.createTextEditorDecorationType($1)")>]
  let private _createDecoType (w: obj) (opts: obj) : TextEditorDecorationType = jsNative
  let createTextEditorDecorationType (opts: obj) = _createDecoType windowExports opts

  [<Emit("$0.createTreeView($1, $2)")>]
  let private _createTreeView (w: obj) (viewId: string) (opts: obj) : TreeView<obj> = jsNative
  let createTreeView (viewId: string) (opts: obj) = _createTreeView windowExports viewId opts

  [<Emit("$0.createWebviewPanel($1, $2, $3, $4)")>]
  let private _createWebviewPanel (w: obj) (viewType: string) (title: string) (column: int) (opts: obj) : WebviewPanel = jsNative
  let createWebviewPanel (viewType: string) (title: string) (column: int) (opts: obj) =
    _createWebviewPanel windowExports viewType title column opts

// ── Commands API ────────────────────────────────────────────────

[<Import("commands", "vscode")>]
let private commandsExports: obj = jsNative

module Commands =
  [<Emit("$0.registerCommand($1, $2)")>]
  let private _registerCommand (c: obj) (command: string) (handler: obj -> unit) : Disposable = jsNative
  let registerCommand (command: string) (handler: obj -> unit) = _registerCommand commandsExports command handler

  [<Emit("$0.executeCommand($1)")>]
  let private _executeCommand (c: obj) (command: string) : JS.Promise<obj> = jsNative
  let executeCommand (command: string) = _executeCommand commandsExports command

  [<Emit("$0.getCommands()")>]
  let private _getCommands (c: obj) : JS.Promise<string array> = jsNative
  let getCommands () = _getCommands commandsExports

// ── Workspace API ───────────────────────────────────────────────

[<Import("workspace", "vscode")>]
let private workspaceExports: obj = jsNative

module Workspace =
  [<Emit("$0.workspaceFolders")>]
  let private _workspaceFolders (w: obj) : WorkspaceFolder array option = jsNative
  let workspaceFolders () = _workspaceFolders workspaceExports

  [<Emit("$0.getConfiguration($1)")>]
  let private _getConfiguration (w: obj) (section: string) : WorkspaceConfiguration = jsNative
  let getConfiguration (section: string) = _getConfiguration workspaceExports section

  [<Emit("$0.findFiles($1, $2, $3)")>]
  let private _findFiles (w: obj) (pattern: string) (exclude: string) (maxResults: int) : JS.Promise<Uri array> = jsNative
  let findFiles (pattern: string) (exclude: string) (maxResults: int) = _findFiles workspaceExports pattern exclude maxResults

  [<Emit("$0.asRelativePath($1)")>]
  let private _asRelativePath (w: obj) (uri: Uri) : string = jsNative
  let asRelativePath (uri: Uri) = _asRelativePath workspaceExports uri

  [<Emit("$0.asRelativePath($1)")>]
  let private _asRelativePathStr (w: obj) (path: string) : string = jsNative
  let asRelativePathStr (path: string) = _asRelativePathStr workspaceExports path

  [<Emit("$0.onDidChangeConfiguration($1)")>]
  let private _onDidChangeConfiguration (w: obj) (handler: ConfigurationChangeEvent -> unit) : Disposable = jsNative
  let onDidChangeConfiguration (handler: ConfigurationChangeEvent -> unit) = _onDidChangeConfiguration workspaceExports handler

// ── Languages API ───────────────────────────────────────────────

[<Import("languages", "vscode")>]
let private languagesExports: obj = jsNative

module Languages =
  [<Emit("$0.registerCodeLensProvider($1, $2)")>]
  let private _registerCodeLensProvider (l: obj) (selector: obj) (provider: obj) : Disposable = jsNative
  let registerCodeLensProvider (language: string) (provider: obj) =
    _registerCodeLensProvider languagesExports (createObj [ "language" ==> language ]) provider

  [<Emit("$0.registerCompletionItemProvider($1, $2, ...$3)")>]
  let private _registerCompletionItemProvider (l: obj) (selector: obj) (provider: obj) (triggerChars: string array) : Disposable = jsNative
  let registerCompletionItemProvider (language: string) (provider: obj) (triggerChars: string array) =
    _registerCompletionItemProvider languagesExports (createObj [ "language" ==> language ]) provider triggerChars

  [<Emit("$0.createDiagnosticCollection($1)")>]
  let private _createDiagnosticCollection (l: obj) (name: string) : DiagnosticCollection = jsNative
  let createDiagnosticCollection (name: string) = _createDiagnosticCollection languagesExports name

// ── Tests API ───────────────────────────────────────────────────

[<Import("tests", "vscode")>]
let private testsExports: obj = jsNative

module Tests =
  [<Emit("$0.createTestController($1, $2)")>]
  let private _createTestController (t: obj) (id: string) (label: string) : TestController = jsNative
  let createTestController (id: string) (label: string) = _createTestController testsExports id label

// ── Env API ─────────────────────────────────────────────────────

[<Import("env", "vscode")>]
let private envExports: obj = jsNative

module Env =
  [<Emit("$0.openExternal($1)")>]
  let private _openExternal (e: obj) (uri: Uri) : JS.Promise<bool> = jsNative
  let openExternal (uri: Uri) = _openExternal envExports uri

// ── Constructors ────────────────────────────────────────────────

[<ImportAll("vscode")>]
let private vscodeAll: obj = jsNative

[<Emit("new $0.Range($1, $2, $3, $4)")>]
let private _newRange (v: obj) (sl: int) (sc: int) (el: int) (ec: int) : Range = jsNative
let newRange (sl: int) (sc: int) (el: int) (ec: int) = _newRange vscodeAll sl sc el ec

[<Emit("new $0.Position($1, $2)")>]
let private _newPosition (v: obj) (l: int) (c: int) : Position = jsNative
let newPosition (l: int) (c: int) = _newPosition vscodeAll l c

[<Emit("new $0.ThemeColor($1)")>]
let private _newThemeColor (v: obj) (id: string) : obj = jsNative
let newThemeColor (id: string) = _newThemeColor vscodeAll id

[<Emit("new $0.ThemeIcon($1)")>]
let private _newThemeIcon (v: obj) (id: string) : obj = jsNative
let newThemeIcon (id: string) = _newThemeIcon vscodeAll id

[<Emit("$0.Uri.parse($1)")>]
let private _uriParse (v: obj) (s: string) : Uri = jsNative
let uriParse (s: string) = _uriParse vscodeAll s

[<Emit("$0.Uri.file($1)")>]
let private _uriFile (v: obj) (path: string) : Uri = jsNative
let uriFile (path: string) = _uriFile vscodeAll path

[<Emit("new $0.Diagnostic($1, $2, $3)")>]
let private _newDiagnostic (v: obj) (range: Range) (message: string) (severity: int) : Diagnostic = jsNative
let newDiagnostic (range: Range) (message: string) (severity: int) = _newDiagnostic vscodeAll range message severity

[<Emit("new $0.TestMessage($1)")>]
let private _newTestMessage (v: obj) (message: string) : obj = jsNative
let newTestMessage (message: string) = _newTestMessage vscodeAll message

[<Emit("new $0.CodeLens($1, $2)")>]
let private _newCodeLens (v: obj) (range: Range) (cmd: obj) : CodeLens = jsNative
let newCodeLens (range: Range) (cmd: obj) = _newCodeLens vscodeAll range cmd

[<Emit("new $0.EventEmitter()")>]
let private _newEventEmitter (v: obj) : EventEmitter<'T> = jsNative
let newEventEmitter<'T> () : EventEmitter<'T> = _newEventEmitter vscodeAll

[<Emit("new $0.CompletionItem($1, $2)")>]
let private _newCompletionItem (v: obj) (label: string) (kind: int) : obj = jsNative
let newCompletionItem (label: string) (kind: int) = _newCompletionItem vscodeAll label kind

[<RequireQualifiedAccess>]
module CompletionItemKind =
  [<Emit("0")>]
  let Method: int = jsNative
  [<Emit("1")>]
  let Function: int = jsNative
  [<Emit("4")>]
  let Field: int = jsNative
  [<Emit("5")>]
  let Variable: int = jsNative
  [<Emit("6")>]
  let Class: int = jsNative
  [<Emit("7")>]
  let Interface: int = jsNative
  [<Emit("8")>]
  let Module: int = jsNative
  [<Emit("9")>]
  let Property: int = jsNative
  [<Emit("13")>]
  let Enum: int = jsNative
  [<Emit("14")>]
  let Keyword: int = jsNative
  [<Emit("20")>]
  let Event: int = jsNative

// TreeItemCollapsibleState
[<RequireQualifiedAccess>]
module TreeItemCollapsibleState =
  [<Emit("0")>]
  let None: int = jsNative
  [<Emit("1")>]
  let Collapsed: int = jsNative
  [<Emit("2")>]
  let Expanded: int = jsNative

[<Emit("new $0.TreeItem($1, $2)")>]
let private _newTreeItem (v: obj) (label: string) (collapsibleState: int) : TreeItem = jsNative
let newTreeItem (label: string) (collapsibleState: int) = _newTreeItem vscodeAll label collapsibleState
