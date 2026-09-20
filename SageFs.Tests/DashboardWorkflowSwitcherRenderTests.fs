/// Render tests for the dashboard's workflow switcher (`renderWorkflowSwitcher`
/// / `renderWorkflowSwitcherPending`, `DashboardFragments.fs` — sagefs-ux-roast.md
/// §4.1/§4.2/§11 Island B item 4). Pins the picker's actual markup: three
/// options with the current one marked, the POST it fires on change, and the
/// optimistic "switching…" control shown before the restart resolves.
module SageFs.Tests.DashboardWorkflowSwitcherRenderTests

open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

let private html node = node |> renderNode

[<Tests>]
let switcherTests =
  testList "renderWorkflowSwitcher" [
    testCase "WHY — a real session renders a <select> with all three workflows, not a static badge" <| fun _ ->
      let out = renderWorkflowSwitcher "REPL" "0a0b0c0d" |> html
      out |> Expect.stringContains "should be a select, not a span/badge" "<select"
      out |> Expect.stringContains "id carries the shared DOM id for morphing" (sprintf "id=\"%s\"" DomIds.WorkflowSwitcher)
      for label in [ "REPL"; "Live Testing"; "Hot Reload" ] do
        out |> Expect.stringContains (sprintf "option '%s' present" label) label

    testCase "WHY — the current workflow's <option> is marked selected, and only that one" <| fun _ ->
      let out = renderWorkflowSwitcher "Live Testing" "0a0b0c0d" |> html
      // A crude but decisive check: the option carrying value=livetesting is
      // the one with `selected`, not one of the other two.
      let selectedBlock =
        System.Text.RegularExpressions.Regex.Match(out, "<option[^>]*selected[^>]*>([^<]*)</option>")
      selectedBlock.Success |> Expect.isTrue "exactly one selected option should exist"
      selectedBlock.Groups.[1].Value |> Expect.equal "the selected option's text is the current label" "Live Testing"

    testCase "WHY — the control posts to /dashboard/switch-workflow with the picked value on change, not the old read-only badge's static text" <| fun _ ->
      let out = renderWorkflowSwitcher "REPL" "0a0b0c0d" |> html
      out |> Expect.stringContains "posts to the real switch route" "/dashboard/switch-workflow"
      out |> Expect.stringContains "sends the picked value as workflowTarget" "workflowTarget"
      Expect.isFalse "must not still claim to be read-only" (out.Contains "read-only")

    testCase "WHY — a no-session state renders an empty placeholder, never a picker with nothing to switch" <| fun _ ->
      let out = renderWorkflowSwitcher "Interactive" "" |> html
      Expect.isFalse "no <select> when there is no session" (out.Contains "<select")

    testCase "WHY — the control carries an aria-label naming the current workflow, for accessibility parity with every other dashboard control" <| fun _ ->
      let out = renderWorkflowSwitcher "REPL" "0a0b0c0d" |> html
      out |> Expect.stringContains "aria-label present" "aria-label"
      out |> Expect.stringContains "aria-label names the current workflow" "Workflow: REPL"
  ]

[<Tests>]
let pendingTests =
  testList "renderWorkflowSwitcherPending" [
    testCase "WHY — the optimistic control shares the switcher's DOM id, so the SSE morph replaces it in place" <| fun _ ->
      let out = renderWorkflowSwitcherPending "Hot Reload" |> html
      out |> Expect.stringContains "same id as the real switcher" (sprintf "id=\"%s\"" DomIds.WorkflowSwitcher)

    testCase "WHY — the optimistic control names the TARGET workflow being switched to, so the user sees what's happening before the restart completes" <| fun _ ->
      let out = renderWorkflowSwitcherPending "Hot Reload" |> html
      out |> Expect.stringContains "names the target workflow" "Hot Reload"
      out |> Expect.stringContains "carries a status role for a11y" "role=\"status\""
  ]
