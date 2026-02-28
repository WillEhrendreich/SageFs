module SageFs.Vscode.TestCodeLensProvider

open Fable.Core
open Fable.Core.JsInterop
open Vscode

/// Mutable state: current test results per file.
/// Updated by LiveTestingListener callbacks.
let mutable testState : LiveTestingTypes.VscLiveTestState = LiveTestingTypes.VscLiveTestState.empty

/// Event emitter to signal CodeLens refresh
let changeEmitter = newEventEmitter<obj> ()

/// Notify VS Code to refresh CodeLens
let refresh () = changeEmitter.fire (null)

/// Update state and refresh
let updateState (state: LiveTestingTypes.VscLiveTestState) =
  testState <- state
  refresh ()

/// Format a test result as a CodeLens title
let formatTitle (result: LiveTestingTypes.VscTestResult) =
  match result.Outcome with
  | LiveTestingTypes.VscTestOutcome.Passed ->
    match result.DurationMs with
    | Some ms -> sprintf "✓ Passed (%.0fms)" ms
    | None -> "✓ Passed"
  | LiveTestingTypes.VscTestOutcome.Failed msg ->
    let short = if msg.Length > 60 then msg.[..59] + "…" else msg
    sprintf "✗ Failed: %s" short
  | LiveTestingTypes.VscTestOutcome.Running -> "● Running…"
  | LiveTestingTypes.VscTestOutcome.Skipped reason -> sprintf "⊘ Skipped: %s" reason
  | LiveTestingTypes.VscTestOutcome.Errored msg -> sprintf "✗ Error: %s" msg
  | LiveTestingTypes.VscTestOutcome.Stale -> "◌ Stale"
  | LiveTestingTypes.VscTestOutcome.PolicyDisabled -> "⊘ Disabled"

/// Creates a CodeLens provider for test results
let create () =
  createObj [
    "onDidChangeCodeLenses" ==> changeEmitter.event
    "provideCodeLenses" ==> fun (doc: TextDocument) (_token: obj) ->
      let filePath = doc.fileName
      let tests = LiveTestingTypes.VscLiveTestState.testsForFile filePath testState
      let lenses = ResizeArray<CodeLens>()
      for t in tests do
        match t.Line with
        | Some line ->
          let range = newRange (line - 1) 0 (line - 1) 0
          let result = LiveTestingTypes.VscLiveTestState.resultFor t.Id testState
          let title =
            match result with
            | Some r -> formatTitle r
            | None -> "◆ Detected"
          let tooltip =
            match result with
            | Some r ->
              match r.Outcome with
              | LiveTestingTypes.VscTestOutcome.Stale ->
                match testState.Freshness with
                | LiveTestingTypes.VscResultFreshness.StaleCodeEdited ->
                  sprintf "%s — stale: code edited since last run" t.DisplayName
                | LiveTestingTypes.VscResultFreshness.StaleWrongGeneration ->
                  sprintf "%s — stale: generation mismatch (re-run needed)" t.DisplayName
                | _ -> sprintf "%s — stale" t.DisplayName
              | LiveTestingTypes.VscTestOutcome.PolicyDisabled ->
                sprintf "%s — disabled by policy" t.DisplayName
              | _ -> t.DisplayName
            | None -> t.DisplayName
          let cmd = createObj [
            "title" ==> title
            "command" ==> ""
            "tooltip" ==> tooltip
          ]
          lenses.Add(newCodeLens range cmd)
        | None -> ()
      lenses.ToArray()
  ]
