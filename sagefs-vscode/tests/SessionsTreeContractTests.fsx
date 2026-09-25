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

/// Default health for the label/description cases below is whatever the
/// daemon would actually say about a live worker: Healthy. The health-specific
/// cases override it explicitly.
let private session id status projects loaded evals : SessionRowInput =
  { Id = id
    Status = status
    DeclaredProjects = projects
    LoadedProjects = loaded
    EvalCount = evals
    WorkingDirectory = "/w"
    IsActive = false
    Health =
      match status with
      | "Ready" | "Evaluating" -> SessionHealth.Healthy
      | "Starting" | "Restarting" -> SessionHealth.Starting
      | "Faulted" -> SessionHealth.Failed "boom"
      | "Stopped" -> SessionHealth.Failed "Session is stopped."
      | _ -> SessionHealth.Unknown }

let tests =
  testList "VS Code Sessions tree - pure row shaping" [

    testCase "only a usable selected-session lifecycle is ready" <| fun _ ->
      isReadyStatus "Ready" |> Expect.isTrue "ready"
      isReadyStatus "Evaluating" |> Expect.isTrue "evaluating worker remains usable"
      isReadyStatus "Building" |> Expect.isTrue "building worker remains routable"
      isReadyStatus "Starting" |> Expect.isFalse "starting is not ready"
      isReadyStatus "Faulted" |> Expect.isFalse "faulted is not ready"
      isReadyStatus "Stopped" |> Expect.isFalse "stopped is not ready"

    testCase "session targets classify projects and solutions without discovery" <| fun _ ->
      SessionTarget.ofPath @"C:\repo\App.fsproj" |> Expect.equal "project" (ProjectSession @"C:\repo\App.fsproj")
      SessionTarget.ofPath @"C:\repo\App.slnx" |> Expect.equal "slnx" (SolutionSession @"C:\repo\App.slnx")
      SessionTarget.ofPath @"C:\repo\App.sln" |> Expect.equal "sln" (SolutionSession @"C:\repo\App.sln")

    testCase "WHY - a codicon token never reaches the label, because VS Code prints it literally there" <| fun _ ->
      let row = renderRow (session "abc" "Ready" [| "/w/Foo.fsproj" |] [||] 0)
      Expect.isFalse "label carries no $( token" (row.Label.Contains "$(")
      Expect.isFalse "description carries no $( token" (row.Description.Contains "$(")
      Expect.isFalse "icon id is bare, not a $(..) token" (row.Icon.Contains "$(")

    testCase "WHY - status rides the icon slot, which is the one place VS Code renders it" <| fun _ ->
      // A loaded session: the healthy case. (Ready-but-Degraded is a warning — see below.)
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

    // ── The daemon's verdict, and the two directions the old guess was wrong ──
    //
    // WHY these five: `icon` used to be derived from `Array.isEmpty
    // (effectiveProjects input)`, which is inverted relative to
    // SageFs.Core/SessionHealth.fs in BOTH directions. These pin the corrected
    // mapping against the classifier's own stated rules
    // (SessionHealth.fs:95-96 and :118-121).

    testCase "WHY - a bare REPL session is Healthy, and must NOT wear a warning triangle" <| fun _ ->
      // SessionHealth.fs:95-96 — `projectRoles = []` that loaded nothing is
      // explicitly NOT degraded: nothing was expected, so nothing is wrong.
      // The deleted heuristic put "warning" here, on the most ordinary session
      // in the product.
      let row = renderRow { session "a" "Ready" [||] [||] 0 with Health = SessionHealth.Healthy }
      row.Icon |> Expect.equal "green bolt for a healthy bare session" "zap"
      row.Description |> Expect.equal "no health noise on the common case" "Ready"

    testCase "WHY - a Ready session the daemon calls Degraded wears the warning, whatever it loaded" <| fun _ ->
      // SessionHealth.fs:118-121 — Degraded requires projectRoles NON-empty,
      // which under the deleted heuristic meant `effectiveProjects` non-empty,
      // which rendered "zap". Exactly the case the icon exists to catch, shown
      // green.
      let row =
        renderRow
          { session "a" "Ready" [||] [| "/w/A.fsproj" |] 0 with
              Health = SessionHealth.Degraded "0 assemblies loaded; run dotnet build" }
      row.Icon |> Expect.equal "warned, not zapped" "warning"
      row.Description |> Expect.stringContains "the verdict is visible without hovering" "Degraded"
      row.Description |> Expect.stringContains "the worker status is still shown beside it" "Ready"

    testCase "WHY - the Degraded reason reaches the user, because it is why the case carries one" <| fun _ ->
      (renderRow
        { session "a" "Ready" [||] [| "/w/A.fsproj" |] 0 with
            Health = SessionHealth.Degraded "run `dotnet build` on it, then hard_reset_fsi_session" }).Tooltip
      |> Expect.stringContains "remedy in the tooltip" "then hard_reset_fsi_session"

    testCase "WHY - a Failed session's reason is shown too, not swallowed into a bare error glyph" <| fun _ ->
      let row = renderRow { session "a" "Faulted" [||] [||] 0 with Health = SessionHealth.Failed "worker exited 139" }
      row.Icon |> Expect.equal "error" "error"
      row.Tooltip |> Expect.stringContains "reason" "worker exited 139"

    testCase "WHY - no verdict on the wire is rendered as no verdict, not as a guess" <| fun _ ->
      // An older daemon sends no `health` field. Claiming either "healthy" or
      // "degraded" for it would be inventing the fact this whole change exists
      // to stop inventing.
      let row = renderRow { session "a" "Ready" [||] [||] 0 with Health = SessionHealth.Unknown }
      row.Icon |> Expect.equal "falls back to the lifecycle status" "zap"
      row.Description |> Expect.equal "silent about health it does not know" "Ready"

    testCase "WHY - ofWire is total, and an unrecognised status is Unknown rather than a verdict" <| fun _ ->
      SessionHealth.ofWire "Healthy" "" |> Expect.equal "healthy" SessionHealth.Healthy
      SessionHealth.ofWire "Starting" "" |> Expect.equal "starting" SessionHealth.Starting
      SessionHealth.ofWire "Degraded" "why" |> Expect.equal "degraded carries the reason" (SessionHealth.Degraded "why")
      SessionHealth.ofWire "Failed" "why" |> Expect.equal "failed carries the reason" (SessionHealth.Failed "why")
      SessionHealth.ofWire "SomethingNew" "x" |> Expect.equal "unknown, not guessed" SessionHealth.Unknown

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
