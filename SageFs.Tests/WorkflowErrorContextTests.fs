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
    Gen.constant SessionWorkflow.LiveTesting
    Gen.constant (SessionWorkflow.HotReload BrowserRefreshConfig.defaults)
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
    "HotReload + FS0037 → includes switch hint because type redef is blocked by single-assembly FSI" <| fun _ ->
    let workflow = SessionWorkflow.HotReload BrowserRefreshConfig.defaults
    let error = "error FS0037: Duplicate definition of type 'Foo'"
    let suggestion = "💡 Tip: Type error."

    let enhanced = WorkflowErrorContext.enhance workflow error suggestion

    enhanced
    |> Expect.stringContains "should name the workflow the restriction applies to" "Hot Reload workflow"

    enhanced
    |> Expect.stringContains "should mention switch_workflow tool" "switch_workflow"

    // WHY these two absences are asserted: the shipped hint used to end with "or
    // Ctrl+W in TUI" — a keystroke in a client that no longer ships, so the one
    // actionable half of the hint pointed at nothing. It also said "Live mode",
    // which is the old name AND an alias trap: "live" means Hot Reload, not live
    // testing. Both are easy to reintroduce from muscle memory, so they are pinned.
    enhanced.Contains "TUI"
    |> Expect.isFalse "must not name the deprecated TUI, whose keystroke no user can press"

    enhanced.Contains "Live mode"
    |> Expect.isFalse "must not say 'Live mode' — the workflow is HotReload, and 'live' is an alias trap"

    enhanced
    |> Expect.stringContains "should preserve original suggestion" suggestion

/// WHY: the hint is a list of the places a user can actually switch from. It was written when the dashboard
/// had no switch route and said so in its own comment, so it left the dashboard out. The dashboard now has
/// the workflow dropdown (`/dashboard/switch-workflow`, which calls `POST /api/sessions/{sid}/workflow`), and
/// the hint has to be built from the same list that says which clients switch, so the two cannot drift.
/// Neovim is not on it: `:SageFsWorkflow` takes no argument and only shows the current workflow.
let hintNamesEveryClientThatCanSwitch =
  testCase
    "the hint names every client that can switch a workflow, the dashboard among them" <| fun _ ->
    let workflow = SessionWorkflow.HotReload BrowserRefreshConfig.defaults
    let enhanced =
      WorkflowErrorContext.enhance workflow "error FS0037: Duplicate definition of type 'Foo'" "💡 Tip."

    WorkflowErrorContext.SwitchSurface.all
    |> List.contains WorkflowErrorContext.SwitchSurface.WebDashboard
    |> Expect.isTrue "the dashboard can switch a workflow, so it is one of the surfaces"

    for surface in WorkflowErrorContext.SwitchSurface.all do
      enhanced
      |> Expect.stringContains
           (sprintf "the hint names %A" surface)
           (WorkflowErrorContext.SwitchSurface.describe surface)

    // A client that cannot switch is never offered as the remedy.
    enhanced.Contains "Neovim" |> Expect.isFalse "Neovim cannot switch a workflow yet"
    enhanced.Contains "SageFsWorkflow" |> Expect.isFalse "its :SageFsWorkflow only shows the current workflow"

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
    hintNamesEveryClientThatCanSwitch
    interactiveFs0037NoSwitchHint
  ]
]
