module SageFs.Tests.ErrorNumberClassificationTests

// Roast-5 item #10: the FS error NUMBER (FSharpDiagnostic.ErrorNumber) now
// travels with a Diagnostic/WorkerDiagnostic end to end (FCS → worker →
// daemon), instead of classification guessing from prose. This file proves
// the specific ordering hazard `ErrorMessages.categorize` has — "not found"
// is checked before "type" — is closed once a caller has the number, and
// that ErrorNumber actually survives the wire round-trip that carries it.

open Expecto
open Expecto.Flip
open SageFs
open SageFs.ErrorMessages
open SageFs.Features.Diagnostics
open SageFs.WorkerProtocol

[<Tests>]
let errorNumberClassificationTests =
  testList "ErrorNumber-driven classification (roast-5 #10)" [

    testList "the ordering hazard categorize has, closed by number" [
      test "message says 'not found' but categorize (text-only) wrongly gives NameError" {
        // This is the bug being fixed, pinned as a fact about the fallback
        // path: on message text alone, "not found" wins before "type" ever
        // gets a look, even though the real error is a type error.
        categorize "The type 'Foo' was not found in the target project"
        |> Expect.equal "text-only classification is wrong here — that's the hazard" ErrorCategory.NameError
      }

      test "RED-then-GREEN: the SAME message, with FS0001 attached, classifies as TypeError via the number" {
        // The stub-would-fail-here case: if categorizeByNumber ignored the
        // number and fell through to categorize, this would come back
        // NameError (see the test above) and this assertion would fail —
        // proving the number, not the text, is what decides it.
        categorizeByNumber (Some 1) "The type 'Foo' was not found in the target project"
        |> Expect.equal "FS0001 must win over the 'not found' substring" ErrorCategory.TypeError
      }

      test "a real Diagnostic carrying FS0001 and a 'not found' message classifies as TypeError" {
        let diag : Diagnostic =
          { Message = "The type 'Foo' was not found in the target project"
            Subcategory = "typecheck"
            Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 5 }
            Severity = DiagnosticSeverity.Blocking
            ErrorNumber = 1 }
        categorizeByNumber (Some diag.ErrorNumber) diag.Message
        |> Expect.equal "the Diagnostic's own ErrorNumber field must drive classification" ErrorCategory.TypeError
      }

      test "a real WorkerDiagnostic carrying FS0039 and a 'type' message still classifies as NameError" {
        // The converse hazard: a message containing "type" that a naive
        // fallback would call TypeError, but the number says FS0039.
        let wd : WorkerDiagnostic =
          { Severity = DiagnosticSeverity.Blocking
            Message = "the type-checker could not resolve this symbol"
            StartLine = 1
            StartColumn = 0
            EndLine = 1
            EndColumn = 5
            ErrorNumber = 39 }
        categorizeByNumber (Some wd.ErrorNumber) wd.Message
        |> Expect.equal "FS0039 must win over the 'type' substring" ErrorCategory.NameError
      }
    ]

    testList "ErrorNumber round-trips across the worker<->daemon boundary" [
      test "WorkerDiagnostic.toDiagnostic preserves ErrorNumber" {
        let wd : WorkerDiagnostic =
          { Severity = DiagnosticSeverity.Blocking
            Message = "The value 'x' is not defined"
            StartLine = 2
            StartColumn = 3
            EndLine = 2
            EndColumn = 4
            ErrorNumber = 39 }
        let d = WorkerDiagnostic.toDiagnostic wd
        d.ErrorNumber |> Expect.equal "ErrorNumber should survive wd -> Diagnostic" 39
      }

      test "SageFs.Host.WorkerMain.toWorkerDiagnostic preserves ErrorNumber" {
        let d : Diagnostic =
          { Message = "This expression was expected to have type 'int'"
            Subcategory = "typecheck"
            Range = { StartLine = 5; StartColumn = 1; EndLine = 5; EndColumn = 9 }
            Severity = DiagnosticSeverity.Blocking
            ErrorNumber = 1 }
        let wd = SageFs.Server.WorkerMain.toWorkerDiagnostic d
        wd.ErrorNumber |> Expect.equal "ErrorNumber should survive Diagnostic -> wd" 1
      }

      test "round-trip through both conversions is the identity on ErrorNumber" {
        let original : Diagnostic =
          { Message = "unexpected token in expression"
            Subcategory = "parse"
            Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 3 }
            Severity = DiagnosticSeverity.Blocking
            ErrorNumber = 10 }
        let there = SageFs.Server.WorkerMain.toWorkerDiagnostic original
        let back = WorkerDiagnostic.toDiagnostic there
        back.ErrorNumber |> Expect.equal "ErrorNumber survives Diagnostic -> WorkerDiagnostic -> Diagnostic" original.ErrorNumber
      }
    ]
  ]
