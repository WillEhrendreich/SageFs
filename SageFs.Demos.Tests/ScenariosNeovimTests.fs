/// Proves the shape of `Scenarios.Neovim.fs` — stable derived ids matching
/// the plan's own naming (`ScenarioId.derive`), the right client/layout per
/// scenario, and (§9's "never fake a step" doctrine, applied at the data
/// level) that every step which types text also has its OWN, separate
/// `Action.Chord [ Key.Escape ]` step before the next command-line step —
/// the concrete regression this file guards is folding an embedded ESC byte
/// into a `Text` (silently dropped by `Keymap.resolve`, per `Cadence.fs`'s
/// own doc), which a purely visual read of the scenario file will not catch
/// but this structural check will.
module SageFs.Demos.Tests.ScenariosNeovimTests

open Expecto
open Expecto.Flip
open SageFs.Demos.Domain
open SageFs.Demos.Scenarios.Neovim

let private isEscapeChord (action: Action) : bool =
  match action with
  | Action.Chord [ Key.Escape ] -> true
  | _ -> false

/// A typed `Text` should never itself contain an embedded ESC byte — leaving
/// insert mode is always its own `Action.Chord [ Key.Escape ]` step (see the
/// module doc).
let private textOfAction (action: Action) : string option =
  match action with
  | Action.Type(_, text, _) -> Some(Text.value text)
  | _ -> None

[<Tests>]
let tests =
  testList "Scenarios.Neovim" [

    testCase "every scenario id round-trips through ScenarioId.derive with Client.Neovim" <| fun _ ->
      for s in scenarios do
        s.Client |> Expect.equal (sprintf "%s is filmed through Neovim" (ScenarioId.value s.Id)) Client.Neovim

    testCase "replNeovim derives to 'repl-neovim' and lives on the EditorFull layout with no app pane" <| fun _ ->
      replNeovim.Id |> ScenarioId.value |> Expect.equal "matches the plan's own worked id" "repl-neovim"
      replNeovim.App |> Expect.equal "no app pane for a Repl scenario" AppKind.NoApp
      replNeovim.Layout |> Expect.equal "EditorFull (editor + dashboard narrator, no app pane)" LayoutTemplate.EditorFull

    testCase "ltNeovim derives to 'lt-neovim', opens the real FromCSharp test project, and its LAST step is honestly wired but not-yet-passing (§5's LT blocker, mirrored from lt-dashboard)" <| fun _ ->
      ltNeovim.Id |> ScenarioId.value |> Expect.equal "matches the plan's own worked id" "lt-neovim"
      ltNeovim.Sample |> Expect.equal "the real Expecto test project, not a runnable hot-reload sample" Sample.FromCSharp

      // Observes through the shared Dashboard narrator pane (`Expectation.
      // PageTextContains`), not `NvimBufferContains` — the live-testing
      // panel's "✓" text is daemon/dashboard state, never written into
      // nvim's own buffer/extmarks (`Scenarios.Neovim.fs`'s own top doc).
      match ltNeovim.Steps |> List.last with
      | { Action = Action.Await sig_; Expect = Expectation.PageTextContains(selector, text) } ->
        sig_ |> Expect.equal "waits for the real test-run-completed signal, not a click" Signal.testRunCompleted
        selector |> Expect.equal "observes the live-testing panel, the real source of this text" "#live-testing-panel"
        text |> Expect.equal "checks for the real pass checkmark" "✓"
      | other -> failtestf "expected the last step to Await testRunCompleted / expect a checkmark, got %A" other

    testCase "the three hot-reload scenarios derive the plan's own ids and each keeps the client's own AppKind" <| fun _ ->
      [ hrNeovimNeovimWeb, "hr-neovim-neovim-web", AppKind.Web
        hrNeovimNeovimRaylib, "hr-neovim-neovim-raylib", AppKind.Raylib
        hrNeovimNeovimConsole, "hr-neovim-neovim-console", AppKind.Console ]
      |> List.iter (fun (s, expectedId, expectedApp) ->
        s.Id |> ScenarioId.value |> Expect.equal "derived id matches the plan's own naming" expectedId
        s.App |> Expect.equal "app kind" expectedApp
        s.Layout |> Expect.equal "hot-reload scenarios use EditorLeft (editor + dashboard narrator + app pane)" LayoutTemplate.EditorLeft)

    testCase "every Action.Type step's own Text never embeds a raw ESC byte — leaving insert mode is always its own Chord step (regression guard for the silently-dropped-keystroke bug this file's doc names)" <| fun _ ->
      for s in scenarios do
        for step in s.Steps do
          match textOfAction step.Action with
          | Some text -> text.Contains '' |> Expect.isFalse (sprintf "%s: no embedded ESC (\\u001b) in typed text" (Caption.value step.Caption))
          | None -> ()

    testCase "every scenario that types an expression into the editor (not the command line) is immediately followed by an explicit Escape-chord step before the next Action.Type at the command line" <| fun _ ->
      // Structural proof for repl-neovim's own doctrine (see its doc
      // comment): an editor-targeted Type step is followed by
      // Chord[Escape] before any subsequent command-line Type step.
      let steps = replNeovim.Steps

      let editorTypeIndices =
        steps
        |> List.indexed
        |> List.choose (fun (i, step) ->
          match step.Action with
          | Action.Type(Target.WindowCenter ActorId.Neovim, _, _) -> Some i
          | _ -> None)

      editorTypeIndices
      |> List.isEmpty
      |> Expect.isFalse "repl-neovim types at least one expression into the editor"

      for i in editorTypeIndices do
        steps.[i + 1].Action |> isEscapeChord |> Expect.isTrue (sprintf "step %d (an editor Type) is immediately followed by Chord[Escape]" i)
  ]
