module SageFs.Tests.WorkflowErrorContextTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.WorkflowTypes

// ─── Generators ─────────────────────────────────────────────

let private genWorkflow =
  Gen.oneof [
    Gen.constant SessionWorkflow.Interactive
    Gen.constant (SessionWorkflow.WebLive BrowserRefreshConfig.defaults)
  ]

let private genErrorText =
  Gen.oneof [
    Gen.constant "error FS0037: Duplicate definition of type 'Foo'"
    Gen.constant "The type 'Bar' has been defined"
    Gen.constant "Duplicate definition of type 'Baz'"
    Gen.constant "error FS0001: This expression was expected to have type 'int'"
    Gen.constant "error FS0039: The value or constructor 'x' is not defined"
    Gen.constant "unexpected end of file"
    ArbMap.defaults |> ArbMap.generate<string> |> Gen.filter (fun s -> not (isNull s))
  ]

let private genSuggestion =
  Gen.oneof [
    Gen.constant "💡 Tip: Check your types."
    Gen.constant "⚠️ Fix the earlier error."
    ArbMap.defaults |> ArbMap.generate<string> |> Gen.filter (fun s -> not (isNull s))
  ]

type WorkflowErrorArb =
  static member Workflow() = Arb.fromGen genWorkflow
  static member ErrorText() = Arb.fromGen genErrorText
  static member Suggestion() = Arb.fromGen genSuggestion

let private config = {
  FsCheckConfig.defaultConfig with
    arbitrary = [ typeof<WorkflowErrorArb> ]
    maxTest = 200
}

// ─── Property tests ─────────────────────────────────────────

/// WHY: REPL mode has no restrictions — never inject misleading hints.
/// A user in Interactive mode who sees "Duplicate definition" has a TRUE duplicate,
/// not a workflow restriction. Adding "switch to REPL" would be confusing.
let interactiveIsIdentity =
  testPropertyWithConfig config
    "enhancement in Interactive mode is always identity — never misleads" <|
    fun (errorText: string, suggestion: string) ->
      match isNull errorText || isNull suggestion with
      | true -> true
      | false ->
        let enhanced = WorkflowErrorContext.enhance SessionWorkflow.Interactive errorText suggestion
        enhanced = suggestion

/// WHY: Enhancement ADDS context, never removes existing guidance.
/// If ErrorMessages.getSuggestion gave useful advice, we must preserve it.
/// The user should see BOTH the original tip AND the workflow context.
let originalPreserved =
  testPropertyWithConfig config
    "original suggestion is always preserved in enhanced output" <|
    fun (workflow: SessionWorkflow, errorText: string, suggestion: string) ->
      match isNull errorText || isNull suggestion with
      | true -> true
      | false ->
        let enhanced = WorkflowErrorContext.enhance workflow errorText suggestion
        enhanced.Contains(suggestion)

// ─── Scenario tests ─────────────────────────────────────────

/// WHY: This is THE #1 confusion point — the error must guide the user to the fix.
/// A user in Live mode who tries `type Foo = { X: int };;` after defining Foo gets
/// a cryptic FS0037. The enhanced message explains WHY (single-assembly mode) and
/// HOW to fix (switch to REPL via switch_workflow tool).
let webLiveFs0037IncludesSwitchHint =
  testCase
    "WebLive + FS0037 → includes switch hint because type redef is blocked by single-assembly FSI" <| fun _ ->
    let workflow = SessionWorkflow.WebLive BrowserRefreshConfig.defaults
    let error = "error FS0037: Duplicate definition of type 'Foo'"
    let suggestion = "💡 Tip: Type error."

    let enhanced = WorkflowErrorContext.enhance workflow error suggestion

    enhanced
    |> Expect.stringContains "should mention Live mode restriction" "Live mode"

    enhanced
    |> Expect.stringContains "should mention switch_workflow tool" "switch_workflow"

    enhanced
    |> Expect.stringContains "should preserve original suggestion" suggestion

/// WHY: In REPL mode, FS0037 means a genuine duplicate — the user defined the same type
/// name twice in the same ;; block. The workflow switcher is irrelevant here.
/// Adding "switch to REPL" when already in REPL would be nonsensical.
let interactiveFs0037NoSwitchHint =
  testCase
    "Interactive + FS0037 → no switch hint because REPL already has full type redefinition" <| fun _ ->
    let workflow = SessionWorkflow.Interactive
    let error = "error FS0037: Duplicate definition of type 'Foo'"
    let suggestion = "💡 Tip: Type error."

    let enhanced = WorkflowErrorContext.enhance workflow error suggestion

    enhanced
    |> Expect.equal "should be unchanged — REPL needs no workflow guidance" suggestion

// ─── Test list ──────────────────────────────────────────────

[<Tests>]
let tests = testList "WorkflowErrorContext" [
  testList "Properties — enhancement invariants" [
    interactiveIsIdentity
    originalPreserved
  ]
  testList "Scenarios — user-facing error guidance" [
    webLiveFs0037IncludesSwitchHint
    interactiveFs0037NoSwitchHint
  ]
]
