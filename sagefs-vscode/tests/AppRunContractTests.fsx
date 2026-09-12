// WHY — pins the pure request-shaping and status-bar-text mapping for
// run-app/stop-app (POST /api/sessions/{sid}/run-app and .../stop-app) so a
// regression (wrong JSON shape, a forgotten AppRunState branch) fails fast
// under `dotnet fsi` instead of only surfacing as a silent VS Code UI bug.
//
// Runs under plain `dotnet fsi` (no Fable). Tests the AppRunPure module
// which has no Fable dependency, mirroring CoverageViewContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/AppRunPure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.AppRunPure

let private view state urls message : AppStateView =
  { State = state; Message = message; Urls = urls; EntryPoint = ""; RunId = "" }

let tests =
  testList "VS Code run-app/stop-app contract - pure logic" [

    testCase "WHY - request - no project produces the default-target empty body" <| fun _ ->
      requestBodyForRunApp None
      |> Expect.equal "default target" "{}"

    testCase "WHY - request - an empty or whitespace project also means default target" <| fun _ ->
      requestBodyForRunApp (Some "   ")
      |> Expect.equal "blank counts as no project" "{}"

    testCase "WHY - request - a named project is carried as { project: name }" <| fun _ ->
      requestBodyForRunApp (Some "Web")
      |> Expect.equal "named project" "{\"project\":\"Web\"}"

    testCase "WHY - request - a project name with a quote is escaped so the JSON body stays valid" <| fun _ ->
      requestBodyForRunApp (Some "We\"b")
      |> Expect.equal "escaped quote" "{\"project\":\"We\\\"b\"}"

    testCase "WHY - status bar - Running with a URL shows the play glyph and the URL" <| fun _ ->
      statusBarText (view "Running" [ "http://localhost:5000" ] "Web is running at http://localhost:5000")
      |> Expect.equal "running with url" (Some "▶ Running http://localhost:5000")

    testCase "WHY - status bar - Running with no URL still shows the play glyph" <| fun _ ->
      statusBarText (view "Running" [] "Web is running (no web server)")
      |> Expect.equal "running no url" (Some "▶ Running")

    testCase "WHY - status bar - Starting shows the hourglass" <| fun _ ->
      statusBarText (view "Starting" [] "Starting Web…")
      |> Expect.equal "starting" (Some "⏳ Starting")

    testCase "WHY - status bar - CouldNotStart shows the warning glyph with the daemon's own reason" <| fun _ ->
      statusBarText (view "CouldNotStart" [] "Web could not start: worker unreachable")
      |> Expect.equal "could not start" (Some "⚠ Web could not start: worker unreachable")

    testCase "WHY - status bar - BuildFailed shows the warning glyph with the daemon's own reason" <| fun _ ->
      statusBarText (view "BuildFailed" [] "Web could not be rebuilt: FS0001")
      |> Expect.equal "build failed" (Some "⚠ Web could not be rebuilt: FS0001")

    testCase "WHY - status bar - RestartRequired shows the restart glyph" <| fun _ ->
      statusBarText (view "RestartRequired" [] "Web must restart: reload changed")
      |> Expect.equal "restart required" (Some "↻ Restart required")

    testCase "WHY - status bar - NotRunning hides the status bar item entirely" <| fun _ ->
      statusBarText (view "NotRunning" [] "Not running")
      |> Expect.equal "hidden when not running" None

    testCase "WHY - status bar - an unlisted/future state still shows something actionable, never silently hides" <| fun _ ->
      statusBarText (view "Crashed" [] "Web crashed: exit code 1")
      |> Expect.equal "unlisted state falls back to the reason" (Some "⚠ Web crashed: exit code 1")
  ]

let _ = Expecto.Tests.runTestsWithCLIArgs [] [||] tests
