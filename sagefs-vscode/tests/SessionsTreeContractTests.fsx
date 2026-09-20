// WHY — the Sessions tree is the first thing a user reads in the SageFs
// sidebar, and it was rendering `$(zap) no projectReady`: a raw codicon token
// (VS Code only expands `$(...)` in a MarkdownString or iconPath, never in a
// TreeItem label), the word "no project" for a session that HAS a project
// loaded, and label+description concatenated with no separator in the
// accessible name. These pin the pure row-shaping so a regression fails here
// under `dotnet fsi` instead of silently in the sidebar.
//
// Runs under plain `dotnet fsi` (no Fable), mirroring AppRunContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/SessionsTreePure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.SessionsTreePure

let private session id status projects loaded evals : SessionRowInput =
  { Id = id
    Status = status
    DeclaredProjects = projects
    LoadedProjects = loaded
    EvalCount = evals
    WorkingDirectory = "/w"
    IsActive = false }

let tests =
  testList "VS Code Sessions tree - pure row shaping" [

    testCase "WHY - a codicon token never reaches the label, because VS Code prints it literally there" <| fun _ ->
      let row = renderRow (session "abc" "Ready" [| "/w/Foo.fsproj" |] [||] 0)
      Expect.isFalse "label carries no $( token" (row.Label.Contains "$(")
      Expect.isFalse "description carries no $( token" (row.Description.Contains "$(")
      Expect.isFalse "icon id is bare, not a $(..) token" (row.Icon.Contains "$(")

    testCase "WHY - status rides the icon slot, which is the one place VS Code renders it" <| fun _ ->
      // A loaded session: the healthy case. (Ready-with-nothing is a warning — see below.)
      (renderRow (session "a" "Ready" [| "/w/A.fsproj" |] [||] 0)).Icon |> Expect.equal "ready" "zap"
      (renderRow (session "a" "Starting" [||] [||] 0)).Icon |> Expect.equal "starting" "loading~spin"
      (renderRow (session "a" "Faulted" [||] [||] 0)).Icon |> Expect.equal "faulted" "error"
      (renderRow (session "a" "Stopped" [||] [||] 0)).Icon |> Expect.equal "stopped" "circle-slash"
      (renderRow (session "a" "Wat" [||] [||] 0)).Icon |> Expect.equal "unknown" "question"

    testCase "WHY - the label is the project name, because that is what the user is looking for" <| fun _ ->
      (renderRow (session "a" "Ready" [| "/w/src/Foo.fsproj" |] [||] 0)).Label
      |> Expect.equal "bare project name" "Foo"

    testCase "WHY - several projects are listed, so a solution session does not masquerade as one project" <| fun _ ->
      (renderRow (session "a" "Ready" [| "/w/A.fsproj"; "/w/B.fsproj" |] [||] 0)).Label
      |> Expect.equal "both" "A, B"

    testCase "WHY - what the session ACTUALLY loaded wins over what was declared, because projects=[] can still load a project" <| fun _ ->
      // Measured on 0.6.668: create with projects=[] in a dir holding one
      // .fsproj reports projects=[] yet loads it. The tree must show the truth.
      (renderRow (session "a" "Ready" [||] [| "/w/Real.fsproj" |] 0)).Label
      |> Expect.equal "loaded wins" "Real"

    testCase "WHY - 'no project' is only claimed when the session is Ready and truly loaded nothing" <| fun _ ->
      (renderRow (session "a" "Ready" [||] [||] 0)).Label
      |> Expect.equal "honest empty" "(no project loaded)"

    testCase "WHY - a session still starting says so instead of claiming it has no project" <| fun _ ->
      (renderRow (session "a" "Starting" [||] [||] 0)).Label
      |> Expect.equal "not yet known" "(loading…)"

    testCase "WHY - the accessible name separates label from description, because they used to read as 'no projectReady'" <| fun _ ->
      let row = renderRow (session "a" "Ready" [| "/w/Foo.fsproj" |] [||] 3)
      row.AccessibleName |> Expect.stringContains "has a separator" " — "
      row.AccessibleName |> Expect.equal "reads as two fields" "Foo — Ready · 3 evals"
      row.Description |> Expect.stringContains "carries the status" "Ready"
      row.Description |> Expect.stringContains "carries the eval count" "3 evals"

    testCase "WHY - one eval is singular, because '1 evals' is the kind of thing users screenshot" <| fun _ ->
      (renderRow (session "a" "Ready" [||] [| "/w/A.fsproj" |] 1)).Description
      |> Expect.stringContains "singular" "1 eval"

    testCase "WHY - a session with no evals does not advertise a count at all" <| fun _ ->
      (renderRow (session "a" "Ready" [||] [| "/w/A.fsproj" |] 0)).Description
      |> Expect.equal "just the status" "Ready"

    testCase "WHY - the active session is marked, because every other view acts on it implicitly" <| fun _ ->
      let row = renderRow { session "a" "Ready" [| "/w/A.fsproj" |] [||] 0 with IsActive = true }
      row.Description |> Expect.stringContains "says active" "active"

    testCase "WHY - a Ready session that loaded nothing is NOT reported as healthy" <| fun _ ->
      // Ready means "a worker process is alive", not "your code is loaded".
      let row = renderRow (session "a" "Ready" [||] [||] 0)
      row.Icon |> Expect.equal "warned, not zapped" "warning"

    testCase "WHY - the tooltip carries the working directory, the one field that explains a wrong-project session" <| fun _ ->
      let row = renderRow { session "a" "Ready" [| "/w/A.fsproj" |] [||] 2 with WorkingDirectory = "/home/me/repo" }
      row.Tooltip |> Expect.stringContains "dir" "/home/me/repo"
      row.Tooltip |> Expect.stringContains "id" "a"

    testCase "WHY - the tooltip lists every loaded project path, because the label only has room for names" <| fun _ ->
      (renderRow (session "a" "Ready" [||] [| "/w/x/A.fsproj"; "/w/y/B.fsproj" |] 0)).Tooltip
      |> Expect.stringContains "full path" "/w/y/B.fsproj"

    testCase "WHY - context value still distinguishes active/inactive/stopped for the inline menus" <| fun _ ->
      (renderRow { session "a" "Ready" [||] [| "/w/A.fsproj" |] 0 with IsActive = true }).ContextValue
      |> Expect.equal "active ready" "session-active-ready"
      (renderRow (session "a" "Stopped" [||] [||] 0)).ContextValue
      |> Expect.equal "stopped" "session-stopped"
      (renderRow (session "a" "Ready" [||] [| "/w/A.fsproj" |] 0)).ContextValue
      |> Expect.equal "inactive" "session-inactive"
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
