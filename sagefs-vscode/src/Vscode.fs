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

type [<AllowNullLiteral>] ExtensionContext =
  abstract subscriptions: ResizeArray<Disposable>

type [<AllowNullLiteral>] Progress<'T> =
  abstract report: value: 'T -> unit

type [<AllowNullLiteral>] ProgressOptions =
  abstract location: int with get, set
  abstract title: string option with get, set

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

  [<Emit("$0.createDiagnosticCollection($1)")>]
  let private _createDiagnosticCollection (l: obj) (name: string) : DiagnosticCollection = jsNative
  let createDiagnosticCollection (name: string) = _createDiagnosticCollection languagesExports name

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

[<Emit("$0.Uri.parse($1)")>]
let private _uriParse (v: obj) (s: string) : Uri = jsNative
let uriParse (s: string) = _uriParse vscodeAll s

[<Emit("$0.Uri.file($1)")>]
let private _uriFile (v: obj) (path: string) : Uri = jsNative
let uriFile (path: string) = _uriFile vscodeAll path

[<Emit("new $0.Diagnostic($1, $2, $3)")>]
let private _newDiagnostic (v: obj) (range: Range) (message: string) (severity: int) : Diagnostic = jsNative
let newDiagnostic (range: Range) (message: string) (severity: int) = _newDiagnostic vscodeAll range message severity

[<Emit("new $0.CodeLens($1, $2)")>]
let private _newCodeLens (v: obj) (range: Range) (cmd: obj) : CodeLens = jsNative
let newCodeLens (range: Range) (cmd: obj) = _newCodeLens vscodeAll range cmd
