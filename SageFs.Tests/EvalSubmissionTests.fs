module SageFs.Tests.EvalSubmissionTests

open Expecto
open Expecto.Flip
open SageFs.AppState
open SageFs.Middleware.EvaluableSubmission
open SageFs.ErrorMessages

// ─────────────────────────────────────────────────────────────────
// Measured defect (roast, 2026-09): sending a bare `module Foo.Bar.Baz`
// header to FSI as a standalone submission fails with a compile error that
// blames the user's code, on a session's very FIRST eval. classify/message
// must recognize "nothing to run here" instead of forwarding the header.
// ─────────────────────────────────────────────────────────────────

[<Tests>]
let classifyTests =
  testList "EvaluableSubmission.classify" [

    test "bare module header classifies as NothingToEvaluate ModuleOrNamespaceHeaderOnly" {
      EvaluableSubmission.classify "module Foo.Bar.Baz"
      |> Expect.equal
        "a lone module header has nothing to run"
        (EvaluableSubmission.NothingToEvaluate NothingToEvaluateReason.ModuleOrNamespaceHeaderOnly)
    }

    test "bare namespace header classifies as NothingToEvaluate ModuleOrNamespaceHeaderOnly" {
      EvaluableSubmission.classify "namespace Foo.Bar"
      |> Expect.equal
        "a lone namespace header has nothing to run"
        (EvaluableSubmission.NothingToEvaluate NothingToEvaluateReason.ModuleOrNamespaceHeaderOnly)
    }

    test "comments only classifies as NothingToEvaluate BlankOrCommentsOnly" {
      EvaluableSubmission.classify "// just a comment\n// another comment"
      |> Expect.equal
        "comment-only text has nothing to run"
        (EvaluableSubmission.NothingToEvaluate NothingToEvaluateReason.BlankOrCommentsOnly)
    }

    test "comment followed by module header classifies as ModuleOrNamespaceHeaderOnly" {
      EvaluableSubmission.classify "// a comment\nmodule Foo.Bar.Qux"
      |> Expect.equal
        "a comment ahead of the header changes nothing — still just a header"
        (EvaluableSubmission.NothingToEvaluate NothingToEvaluateReason.ModuleOrNamespaceHeaderOnly)
    }

    test "empty submission classifies as NothingToEvaluate" {
      match EvaluableSubmission.classify "" with
      | EvaluableSubmission.NothingToEvaluate _ -> ()
      | EvaluableSubmission.Executable -> failtest "empty code has nothing to run"
    }

    test "whitespace-only submission classifies as NothingToEvaluate" {
      match EvaluableSubmission.classify "   \n\t\n   " with
      | EvaluableSubmission.NothingToEvaluate _ -> ()
      | EvaluableSubmission.Executable -> failtest "whitespace-only code has nothing to run"
    }

    test "module header followed by a real binding stays Executable" {
      EvaluableSubmission.classify "module Foo.Bar\nlet x = 1"
      |> Expect.equal "a header wrapping real code must still run" EvaluableSubmission.Executable
    }

    test "a plain let binding is Executable" {
      EvaluableSubmission.classify "let x = 1"
      |> Expect.equal "an ordinary binding must run" EvaluableSubmission.Executable
    }

    test "a bare expression is Executable" {
      EvaluableSubmission.classify "1 + 1"
      |> Expect.equal "an expression must run" EvaluableSubmission.Executable
    }

    test "open System is Executable" {
      // Opens are excluded from CompilationContext's own "declaration range"
      // notion (they don't need a wrapping context), but they are NOT an
      // error in FSI today and must not become a swallowed no-op.
      EvaluableSubmission.classify "open System"
      |> Expect.equal "an open directive must still be sent to FSI" EvaluableSubmission.Executable
    }

    test "an #r directive is Executable" {
      EvaluableSubmission.classify "#r \"nuget: FSharp.Data\""
      |> Expect.equal "a hash directive must still be sent to FSI" EvaluableSubmission.Executable
    }

    test "malformed code falls back to Executable rather than being swallowed" {
      // Deliberately broken syntax: must never be misclassified as
      // "nothing to evaluate" — that would hide a real compile error from
      // the user instead of letting FSI report it.
      EvaluableSubmission.classify "let x = "
      |> Expect.equal "unparseable code must fall back to Executable, never swallowed" EvaluableSubmission.Executable
    }
  ]

[<Tests>]
let messageTests =
  testList "EvaluableSubmission.message" [
    test "ModuleOrNamespaceHeaderOnly names the situation and the next action" {
      let msg = EvaluableSubmission.message NothingToEvaluateReason.ModuleOrNamespaceHeaderOnly
      msg |> Expect.stringContains "should name the situation" "header"
      msg |> Expect.stringContains "should suggest a next action" "Evaluate Entire File"
    }

    test "ModuleOrNamespaceHeaderOnly never implies the user's code is broken" {
      let msg = EvaluableSubmission.message NothingToEvaluateReason.ModuleOrNamespaceHeaderOnly
      msg |> Expect.stringContains "should not sound like an error" "Nothing to evaluate"
      Expect.isFalse "should never say 'error'" (msg.ToLowerInvariant().Contains "error")
    }

    test "BlankOrCommentsOnly names the situation" {
      let msg = EvaluableSubmission.message NothingToEvaluateReason.BlankOrCommentsOnly
      msg |> Expect.stringContains "should mention blank/comments" "blank"
    }
  ]

// ─────────────────────────────────────────────────────────────────
// The middleware itself: an Executable submission must be a complete,
// byte-for-byte pass-through to `next` (no other test in the suite can
// regress because of this middleware); a NothingToEvaluate submission must
// short-circuit WITHOUT calling `next` at all, and succeed.
// ─────────────────────────────────────────────────────────────────

let private dummyRequest code = { Code = code; Args = Map.empty }

[<Tests>]
let middlewareTests =
  testList "nothingToEvaluateMiddleware" [

    test "a module header short-circuits without calling next" {
      let mutable nextCalled = false
      let next: MiddlewareNext =
        fun (req, st) ->
          nextCalled <- true
          { EvaluationResult = Ok "should not reach here"
            Diagnostics = [||]
            EvaluatedCode = req.Code
            Metadata = Map.empty }, st

      let response, _ =
        nothingToEvaluateMiddleware next (dummyRequest "module Foo.Bar.Baz", Unchecked.defaultof<_>)

      nextCalled |> Expect.isFalse "next must never be called for a header-only submission"
      match response.EvaluationResult with
      | Ok msg -> msg |> Expect.stringContains "should explain the header situation" "header"
      | Error ex -> failtestf "expected Ok, got Error %s" ex.Message
    }

    test "a module header short-circuit succeeds, it is not an error" {
      let next: MiddlewareNext = fun (req, st) -> failtest "next must not be called"
      let response, _ =
        nothingToEvaluateMiddleware next (dummyRequest "module Foo.Bar.Baz", Unchecked.defaultof<_>)
      match response.EvaluationResult with
      | Ok _ -> ()
      | Error ex -> failtestf "a module header must succeed as a no-op, not fail: %s" ex.Message
    }

    test "a plain binding passes through to next completely unmodified" {
      let mutable seenCode = None
      let next: MiddlewareNext =
        fun (req, st) ->
          seenCode <- Some req.Code
          { EvaluationResult = Ok "ran"
            Diagnostics = [||]
            EvaluatedCode = req.Code
            Metadata = Map.empty }, st

      let response, _ =
        nothingToEvaluateMiddleware next (dummyRequest "let x = 1", Unchecked.defaultof<_>)

      seenCode |> Expect.equal "next must see the exact same code" (Some "let x = 1")
      response.EvaluationResult |> Expect.equal "the pass-through response is untouched" (Ok "ran")
    }

    test "open System passes through to next" {
      let mutable nextCalled = false
      let next: MiddlewareNext =
        fun (req, st) ->
          nextCalled <- true
          { EvaluationResult = Ok ""; Diagnostics = [||]; EvaluatedCode = req.Code; Metadata = Map.empty }, st
      nothingToEvaluateMiddleware next (dummyRequest "open System", Unchecked.defaultof<_>) |> ignore
      nextCalled |> Expect.isTrue "open must reach next exactly as before"
    }
  ]

// ─────────────────────────────────────────────────────────────────
// The false "earlier error" lecture (roast, 2026-09): the guidance claimed a
// PREVIOUS statement failed, as fact, with no session history available to
// this pure function — false on a session's first eval. It must still warn
// against resetting and point at real evidence, but must not assert history
// it cannot know.
// ─────────────────────────────────────────────────────────────────

[<Tests>]
let earlierErrorMessageTests =
  testList "ErrorMessages EarlierError — no fabricated history" [
    test "does not assert a PREVIOUS statement failed as fact" {
      let msg = getSuggestion ErrorCategory.EarlierError
      Expect.isFalse
        "must not claim certain history it cannot know"
        (msg.Contains "This 'earlier error' means a PREVIOUS statement had a compile error")
    }

    test "still directs the user to the real evidence (Diagnostics)" {
      getSuggestion ErrorCategory.EarlierError
      |> Expect.stringContains "should point at Diagnostics as the real cause" "Diagnostics"
    }

    test "still warns against resetting the session" {
      getSuggestion ErrorCategory.EarlierError
      |> Expect.stringContains "should still warn against reset" "NOT"
    }

    test "still mentions earlier error" {
      getSuggestion ErrorCategory.EarlierError
      |> Expect.stringContains "should still name the situation" "earlier error"
    }
  ]
