/// The dashboard's session keys.
///
/// `x`, `Delete` and `X` used to stop sessions with no confirmation, on a daemon-wide selection
/// the page never draws, and they also fired from a <select>'s type-ahead. The bindings are now
/// data: the script is built from them and the help table is read from them.
module SageFs.Tests.DashboardKeyBindingTests

open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs.Server.Dashboard

let private destructive (b: KeyBindings.Binding) =
  match b.Effect with
  | KeyBindings.Effect.StopViewing
  | KeyBindings.Effect.StopOthers -> true
  | KeyBindings.Effect.MoveSelection _
  | KeyBindings.Effect.SelectCurrent
  | KeyBindings.Effect.SelectIndex
  | KeyBindings.Effect.NewSession
  | KeyBindings.Effect.ClientOnly -> false

[<Tests>]
let tests =
  testList "dashboard key bindings" [
    testCase "WHY — every key that stops a session asks first, because a keystroke is the cheapest way to lose one" <| fun _ ->
      for b in KeyBindings.all |> List.filter destructive do
        match b.Confirm with
        | KeyBindings.Confirmation.AskFirst prompt ->
          prompt |> Expect.isNotEmpty (sprintf "%s names what it will do" b.Shown)
        | KeyBindings.Confirmation.NoPrompt ->
          failtestf "%s stops sessions with no confirmation" b.Shown

    testCase "WHY — a key that only moves a selection never asks, so navigation stays instant" <| fun _ ->
      for b in KeyBindings.all |> List.filter (destructive >> not) do
        b.Confirm |> Expect.equal (sprintf "%s does not prompt" b.Shown) KeyBindings.Confirmation.NoPrompt

    testCase "WHY — no key is bound twice, so which action a key runs is never a matter of order" <| fun _ ->
      let keys = KeyBindings.all |> List.collect (fun b -> b.Keys)
      keys |> List.distinct |> List.length |> Expect.equal "every key appears once" keys.Length

    testCase "WHY — every binding says what it does, so the help table can list all of them" <| fun _ ->
      for b in KeyBindings.all do
        b.Help |> Expect.isNotEmpty (sprintf "%s has help text" b.Shown)
        b.Shown |> Expect.isNotEmpty "and a label to show"

    testCase "WHY — keystrokes aimed at a field the viewer is typing into or choosing from are never read as commands" <| fun _ ->
      KeyBindings.textEntryTags |> Expect.containsAll "input, textarea and select all count" [ "input"; "textarea"; "select" ]
      let script = keyboardHandlerScript () |> renderNode
      for tag in KeyBindings.textEntryTags do
        script |> Expect.stringContains (sprintf "the script ignores <%s>" tag) (sprintf "\"%s\"" tag)
      script |> Expect.stringContains "contenteditable regions count too" "isContentEditable"

    testCase "WHY — the destructive keys act on the session THIS tab views, not a daemon-wide selection nobody can see" <| fun _ ->
      let script = keyboardHandlerScript () |> renderNode
      Expect.isFalse "sessionDelete is no longer dispatched from the keyboard" (script.Contains "sessionDelete")
      Expect.isFalse "nor is sessionStopOthers" (script.Contains "sessionStopOthers")
  ]
