// WHY — measured with the tour harness on a daemon shared with other agents:
//   * another agent's session fault (a ShimSubject DLL error naming /home/will/Work/molina) popped
//     up as a warning in an unrelated VS Code window;
//   * the Output channel of that window logged "File reloaded" for files of other sessions;
//   * a window that had not bound a session yet took in every session's test events, and a
//     stranger's "live testing is on" turned the window's own Enable Live Testing off the palette.
// The daemon sends these on ONE stream for every session. These pin which events a window shows:
// the ones from sessions of its own workspace or its own session, unless the user opts in to all.
//
// Runs under plain `dotnet fsi` (no Fable), like the other contract tests here.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/BufferBridge.fs"
#load "../src/SessionScopePure.fs"

open System.IO
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs.Vscode.SessionScopePure

let private ref id dir : SessionRef = { Id = id; WorkingDirectory = dir }

let private roots = [ "/home/me/app" ]
let private known = [ ref "mine" "/home/me/app"; ref "theirs" "/home/will/Work/molina" ]

let private surfaces sources selection subject =
  surfacing sources roots selection known subject

let private packageJson =
  JsonDocument.Parse(File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "..", "package.json"))).RootElement

let tests =
  testList "VS Code event scope - which sessions' events a window shows" [

    // ── surfacing ──

    testCase "WHY - the window's own session's fault is shown" <| fun _ ->
      surfaces EventSources.ThisWorkspaceOnly (Selection.Selected "mine") (EventSubject.SessionEvent "mine")
      |> Expect.equal "own" Surfacing.Show

    testCase "WHY - a fault in a session of this workspace is shown even before the window picked one" <| fun _ ->
      surfaces EventSources.ThisWorkspaceOnly Selection.NotSelected (EventSubject.SessionEvent "mine")
      |> Expect.equal "workspace session" Surfacing.Show

    testCase "WHY - another agent's session fault stays out of an unrelated window" <| fun _ ->
      surfaces EventSources.ThisWorkspaceOnly (Selection.Selected "mine") (EventSubject.SessionEvent "theirs")
      |> Expect.equal "stranger" Surfacing.Hide

    testCase "WHY - a session the daemon does not list cannot be shown to be ours, so it is hidden" <| fun _ ->
      surfaces EventSources.ThisWorkspaceOnly (Selection.Selected "mine") (EventSubject.SessionEvent "ghost")
      |> Expect.equal "unknown" Surfacing.Hide

    testCase "WHY - the session the user picked is shown even when it lives outside the workspace" <| fun _ ->
      surfaces EventSources.ThisWorkspaceOnly (Selection.Selected "theirs") (EventSubject.SessionEvent "theirs")
      |> Expect.equal "picked" Surfacing.Show

    testCase "WHY - with the opt-in on, every session's events are shown" <| fun _ ->
      surfaces EventSources.AllSessions (Selection.Selected "mine") (EventSubject.SessionEvent "theirs")
      |> Expect.equal "all" Surfacing.Show
      surfaces EventSources.AllSessions Selection.NotSelected (EventSubject.SessionEvent "ghost")
      |> Expect.equal "even unknown" Surfacing.Show

    testCase "WHY - a file reload under the workspace is shown whatever session reports it" <| fun _ ->
      surfaces EventSources.ThisWorkspaceOnly Selection.NotSelected (EventSubject.FileEvent ("/home/me/app/src/A.fs", "ghost"))
      |> Expect.equal "file in workspace" Surfacing.Show

    testCase "WHY - a file reload outside the workspace, from a stranger's session, is hidden" <| fun _ ->
      surfaces EventSources.ThisWorkspaceOnly (Selection.Selected "mine") (EventSubject.FileEvent ("/home/will/Work/molina/src/B.fs", "theirs"))
      |> Expect.equal "stranger file" Surfacing.Hide

    testCase "WHY - a file reload outside the workspace from the window's own session is shown" <| fun _ ->
      surfaces EventSources.ThisWorkspaceOnly (Selection.Selected "mine") (EventSubject.FileEvent ("/elsewhere/Shared.fs", "mine"))
      |> Expect.equal "own session" Surfacing.Show

    // ── the opt-in setting ──

    testCase "WHY - the setting maps to the DU at the boundary, off by default" <| fun _ ->
      EventSources.ofSetting false |> Expect.equal "off" EventSources.ThisWorkspaceOnly
      EventSources.ofSetting true |> Expect.equal "on" EventSources.AllSessions

    testCase "WHY - package.json contributes the setting, boolean, default off" <| fun _ ->
      let property =
        packageJson.GetProperty("contributes").GetProperty("configuration").GetProperty("properties")
          .GetProperty("sagefs." + eventSourcesSettingKey)
      property.GetProperty("type").GetString() |> Expect.equal "type" "boolean"
      property.GetProperty("default").GetBoolean() |> Expect.isFalse "off by default"
      property.GetProperty("description").GetString() |> Expect.isNotEmpty "described"

    // ── the listener's session filter ──

    testCase "WHY - once bound, the listener takes only its session's events, untagged ones included in the refusals" <| fun _ ->
      admits (EventFilter.OnlySession "a") "a" |> Expect.isTrue "own"
      admits (EventFilter.OnlySession "a") "b" |> Expect.isFalse "stranger"
      admits (EventFilter.OnlySession "a") "" |> Expect.isFalse "untagged"

    testCase "WHY - before any session is bound the listener still takes the daemon's replay" <| fun _ ->
      admits EventFilter.AnySessionUntilBound "b" |> Expect.isTrue "replay of the active session"

    testCase "WHY - binding keeps what was taken only if all of it came from the bound session" <| fun _ ->
      stateAfterBind [] "a" |> Expect.equal "nothing taken yet" StateAfterBind.KeepState
      stateAfterBind [ "a" ] "a" |> Expect.equal "all ours" StateAfterBind.KeepState
      stateAfterBind [ "b" ] "a" |> Expect.equal "all theirs" StateAfterBind.ClearState
      stateAfterBind [ "a"; "b" ] "a" |> Expect.equal "mixed" StateAfterBind.ClearState
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
