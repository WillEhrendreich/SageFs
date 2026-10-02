// WHY — measured with the VS Code tour harness against a shared daemon that already had other
// agents' sessions on it:
//   * "SageFs: Create Session" made a session but the window kept naming another one, because the
//     extension bound itself to the head of the daemon's whole session list, and Enable Live Testing
//     then ran against that other session ("no tests found") until the user picked this one by id;
//   * a window with no session of its own took a stranger's session as its own.
// These pin the decisions as pure functions: which session a window binds to, and which session a
// Create Session call just made.
//
// Runs under plain `dotnet fsi` (no Fable), like the other contract tests here.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/BufferBridge.fs"
#load "../src/SessionScopePure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.SessionScopePure

let private ref id dir : SessionRef = { Id = id; WorkingDirectory = dir }

let private roots = [ "/home/me/app" ]

let tests =
  testList "VS Code session scope - which session a window is bound to" [

    // ── relationOf ──

    testCase "WHY - a session started in the workspace folder is this workspace's" <| fun _ ->
      relationOf roots "/home/me/app"
      |> Expect.equal "same folder" WorkspaceRelation.InThisWorkspace

    testCase "WHY - a session in a folder under the workspace is this workspace's" <| fun _ ->
      relationOf roots "/home/me/app/src/Core"
      |> Expect.equal "nested folder" WorkspaceRelation.InThisWorkspace

    testCase "WHY - a sibling that merely shares the name prefix is not this workspace's" <| fun _ ->
      relationOf roots "/home/me/app2"
      |> Expect.equal "prefix trap" WorkspaceRelation.ElsewhereOnThisMachine

    testCase "WHY - another agent's project is elsewhere" <| fun _ ->
      relationOf roots "/home/will/Work/molina"
      |> Expect.equal "other project" WorkspaceRelation.ElsewhereOnThisMachine

    testCase "WHY - trailing separators and the Windows spelling do not change the answer" <| fun _ ->
      relationOf [ "C:\\Code\\app\\" ] "c:/code/app/src"
      |> Expect.equal "same place, spelled differently" WorkspaceRelation.InThisWorkspace

    testCase "WHY - no workspace folder, or no directory, is never this workspace" <| fun _ ->
      relationOf [] "/home/me/app" |> Expect.equal "no roots" WorkspaceRelation.ElsewhereOnThisMachine
      relationOf roots "" |> Expect.equal "no directory" WorkspaceRelation.ElsewhereOnThisMachine

    // ── bind ──

    testCase "WHY - the session the user picked stays bound while it exists, wherever it lives" <| fun _ ->
      bind roots (Selection.Selected "b") [ ref "a" "/home/me/app"; ref "b" "/elsewhere" ]
      |> Expect.equal "picked wins" (Binding.Bound (ref "b" "/elsewhere"))

    testCase "WHY - a picked session that is gone falls back to the workspace's own" <| fun _ ->
      bind roots (Selection.Selected "gone") [ ref "x" "/elsewhere"; ref "a" "/home/me/app" ]
      |> Expect.equal "workspace session" (Binding.Bound (ref "a" "/home/me/app"))

    testCase "WHY - with nothing picked, the workspace's session is chosen, not the head of the daemon's list" <| fun _ ->
      bind roots Selection.NotSelected [ ref "x" "/home/will/Work/molina"; ref "a" "/home/me/app" ]
      |> Expect.equal "workspace session" (Binding.Bound (ref "a" "/home/me/app"))

    testCase "WHY - a window with no session of its own binds to none, never to a stranger's" <| fun _ ->
      bind roots Selection.NotSelected [ ref "x" "/home/will/Work/molina"; ref "y" "/tmp/other" ]
      |> Expect.equal "no session for this workspace" Binding.NoSessionForThisWorkspace

    testCase "WHY - an empty daemon binds to none" <| fun _ ->
      bind roots Selection.NotSelected []
      |> Expect.equal "none" Binding.NoSessionForThisWorkspace

    testCase "WHY - several sessions for the workspace bind to the first listed, deterministically" <| fun _ ->
      bind roots Selection.NotSelected [ ref "a" "/home/me/app"; ref "b" "/home/me/app/sub" ]
      |> Expect.equal "first" (Binding.Bound (ref "a" "/home/me/app"))

    // ── phaseOfStatus ──
    // The status bar used to read the DAEMON-wide status, which is the status of whichever session
    // the daemon had active, so a stranger's fault or warmup showed up in an unrelated window. It
    // reads the bound session's own status through this.

    testCase "WHY - a session that can evaluate is Usable" <| fun _ ->
      for status in [ "Ready"; "Evaluating"; "Building" ] do
        phaseOfStatus status |> Expect.equal status SessionPhase.Usable

    testCase "WHY - a session still coming up is Warming" <| fun _ ->
      for status in [ "Starting"; "Restarting"; "Warming Up" ] do
        phaseOfStatus status |> Expect.equal status SessionPhase.Warming

    testCase "WHY - a session that fell over or was stopped is Down" <| fun _ ->
      for status in [ "Faulted"; "Stopped"; "error" ] do
        phaseOfStatus status |> Expect.equal status SessionPhase.Down

    testCase "WHY - a status this client has never heard of is named, not judged" <| fun _ ->
      phaseOfStatus "Hibernating"
      |> Expect.equal "unrecognised" (SessionPhase.Unrecognised "Hibernating")

    // ── identifyCreated ──

    testCase "WHY - the daemon's reply names the new session, so that session is the one" <| fun _ ->
      identifyCreated [ "a" ] "b" "/home/me/app" [ ref "a" "/home/me/app"; ref "b" "/home/me/app" ]
      |> Expect.equal "named by the reply" (CreatedSession.CreatedAs (ref "b" "/home/me/app"))

    testCase "WHY - when the reply is prose, the one new session in the working directory is the one" <| fun _ ->
      identifyCreated [ "a" ] "Session created" "/home/me/app" [ ref "a" "/x"; ref "n" "/home/me/app" ]
      |> Expect.equal "diffed" (CreatedSession.CreatedAs (ref "n" "/home/me/app"))

    testCase "WHY - another agent's new session at the same moment is not mistaken for ours" <| fun _ ->
      identifyCreated [ "a" ] "ok" "/home/me/app" [ ref "a" "/x"; ref "theirs" "/home/will/Work/molina"; ref "n" "/home/me/app" ]
      |> Expect.equal "ours by directory" (CreatedSession.CreatedAs (ref "n" "/home/me/app"))

    testCase "WHY - a reply that names a session that already existed does not pick it" <| fun _ ->
      identifyCreated [ "a" ] "a" "/home/me/app" [ ref "a" "/x"; ref "n" "/home/me/app" ]
      |> Expect.equal "new one" (CreatedSession.CreatedAs (ref "n" "/home/me/app"))

    testCase "WHY - nothing new means nothing to activate" <| fun _ ->
      identifyCreated [ "a" ] "ok" "/home/me/app" [ ref "a" "/home/me/app" ]
      |> Expect.equal "not identified" CreatedSession.NotIdentified

    testCase "WHY - two new sessions in the same directory cannot be told apart, so none is guessed" <| fun _ ->
      identifyCreated [] "ok" "/home/me/app" [ ref "n1" "/home/me/app"; ref "n2" "/home/me/app" ]
      |> Expect.equal "ambiguous" CreatedSession.NotIdentified
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
