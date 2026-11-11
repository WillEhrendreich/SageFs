module SageFs.Tests.DevReloadSeverityTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.Diagnostics

// A diagnostic's severity reaches the browser overlay as one of four words. It was a string field filled from the compiler's
// severity by a second function, so the two could drift. It is a closed type now, and the overlay's word for each case is the
// word the compiler's severity already had on the wire.

let every : DiagnosticSeverity list =
  [ DiagnosticSeverity.Blocking; DiagnosticSeverity.Warning; DiagnosticSeverity.Info; DiagnosticSeverity.Hidden ]

[<Tests>]
let devReloadSeverityTests =
  testList "DevReload severity" [
    testCase "WHY — the overlay is told the same word for every compiler severity as before the field was typed" <| fun _ ->
      for severity in every do
        Features.ReloadBroadcast.severityOf severity
        |> DevReload.DevReloadSeverity.label
        |> Expect.equal (sprintf "the word for %A" severity) (DiagnosticSeverity.label severity)

    testCase "WHY — the wire payload of a diagnostic carries the severity's word" <| fun _ ->
      let diagnostic : DevReload.DevReloadDiagnostic =
        { File = "App.fs"; Line = 1; EndLine = 1; Column = 1; EndColumn = 2
          Severity = DevReload.DevReloadSeverity.Warning; DiagCode = None; Message = "m"
          SourceContext = None; SourceContextStartLine = None }
      (DevReload.DevReloadDiagnostic.toWire diagnostic).Severity |> Expect.equal "warning on the wire" "warning"
  ]
