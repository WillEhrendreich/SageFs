module SageFs.Vscode.CoverageViewCodeLensProvider

// WHY — this provider renders one CodeLens per CoverageView for the
// active document. It is the user-facing fix for the "20-100 tests
// above the function" problem: instead of one decoration per test,
// the editor renders one badge line per function. The pure projection
// logic (filter the store by file, project each view to a CodeLens
// shape) is in `PureProvider` and is covered by
// CoverageViewProviderContractTests.fsx. This Fable module only wires
// the pure shape to the VSCode CodeLens API.

open Fable.Core
open Fable.Core.JsInterop
open Vscode
open SageFs.Vscode.CoverageViewPure

/// Mutable config: read from VSCode settings on each refresh.
/// WHY mutable: the editor settings change at runtime; the provider
/// re-reads on each provideCodeLenses call.
let mutable config : CoverageViewConfig = CoverageViewConfig.defaults

/// Coverage views per file. Updated by LiveTestingListener via the
/// `coverage_view` SSE event handler. Keyed by file path.
let mutable coverageViews : Map<string, CoverageView array> = Map.empty

/// Event emitter to signal CodeLens refresh.
let changeEmitter = newEventEmitter<obj> ()

/// Notify VS Code to refresh CodeLens.
let refresh () = changeEmitter.fire (null)

/// Replace the coverage views for a single file. Called by the listener
/// when a `coverage_view` event arrives.
let updateFile (filePath: string) (views: CoverageView array) =
  coverageViews <- Map.add filePath views coverageViews
  refresh ()

/// Update config from editor settings. Called by the extension when the
/// user changes `sagefs.coverageView.inlineCollapseAt`.
let updateConfig (newConfig: CoverageViewConfig) =
  config <- newConfig
  refresh ()

/// Build a single VSCode CodeLens from a PureCodeLens. Wraps the pure
/// shape with the Fable `createObj` + `newCodeLens` API.
let private buildCodeLens (lens: PureCodeLens) : CodeLens =
  let line = max 0 (lens.Line - 1)
  let range = newRange line 0 line 0
  let cmd = createObj [
    "title" ==> lens.Title
    "command" ==> lens.CommandLabel
    "tooltip" ==> lens.Tooltip
  ]
  newCodeLens range cmd

/// Creates a CodeLens provider for coverage views.
let create () =
  createObj [
    "onDidChangeCodeLenses" ==> changeEmitter.event
    "provideCodeLenses" ==> fun (doc: TextDocument) (_token: obj) ->
      let filePath = doc.fileName
      PureProvider.lensesForFile config coverageViews filePath
      |> Array.map buildCodeLens
  ]
