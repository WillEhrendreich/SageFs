module SageFs.Vscode.DiagnosticsListener

open Fable.Core.JsInterop
open Vscode
open SageFs.Vscode.JsHelpers
open SageFs.Vscode.SafeInterop

let start (port: int) (dc: DiagnosticCollection) (log: (string -> unit) option) =
  let url = sprintf "http://localhost:%d/diagnostics" port

  let onData (data: obj) =
    fieldArray "diagnostics" data
    |> Option.iter (fun diagnostics ->
      let byFile = System.Collections.Generic.Dictionary<string, ResizeArray<Diagnostic>>()

      for diag in diagnostics do
        fieldString "file" diag
        |> Option.iter (fun file ->
          let message = fieldString "message" diag |> Option.defaultValue ""
          let severity =
            match fieldString "severity" diag |> Option.defaultValue "" with
            | "error" -> VDiagnosticSeverity.Error
            | "warning" -> VDiagnosticSeverity.Warning
            | "info" -> VDiagnosticSeverity.Information
            | _ -> VDiagnosticSeverity.Hint
          let startLine = (fieldInt "startLine" diag |> Option.defaultValue 1) - 1 |> max 0
          let startCol = (fieldInt "startColumn" diag |> Option.defaultValue 1) - 1 |> max 0
          let endLine = (fieldInt "endLine" diag |> Option.defaultValue 1) - 1 |> max 0
          let endCol = (fieldInt "endColumn" diag |> Option.defaultValue 1) - 1 |> max 0
          let range = newRange startLine startCol endLine endCol
          let d = newDiagnostic range message severity
          // Names SageFs in the Problems panel, so its diagnostics are never
          // mistaken for the F# language server's as-you-type checks.
          d?source <- "SageFs"

          if not (byFile.ContainsKey file) then
            byFile.[file] <- ResizeArray()
          byFile.[file].Add(d))

      dc.clear ()
      for kv in byFile do
        let uri = uriFile kv.Key
        dc.set (uri, kv.Value))

  subscribeSseWithLogger url onData log
