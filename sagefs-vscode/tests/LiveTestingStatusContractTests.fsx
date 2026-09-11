#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/LiveTestingTypes.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.LiveTestingTypes

let summary (activity: string) (short: string) (text: string) : VscTestSummary =
  { Total = 12; Passed = 0; Failed = 0; Running = 0; Stale = 0; Disabled = 0; NotYetRun = 0
    DiscoveryState = "ready_with_tests"; DiscoveryGeneration = 1L; LastDecision = None
    Activity = activity; ActivityShort = short; ActivityText = text }

let view = VscTestSummary.statusBarView

let tests =
  testList "VS Code live-testing status bar contract" [
    testCase "WHY — statusBarView — tests that never ran read as not yet run because 0/12 passed reads as success" <| fun _ ->
      let v = view { summary "settled" "12 not yet run" "12 not yet run" with NotYetRun = 12 }
      v.Text |> Expect.equal "not yet run" "$(circle-outline) 12 not yet run"
      v.Tone |> Expect.equal "plain" VscStatusTone.Plain

    testCase "WHY — statusBarView — a finished discovery with no tests says so because it is not still discovering" <| fun _ ->
      let v = view { summary "no_tests_found" "No tests found" "No tests found (Expecto detected)" with Total = 0 }
      v.Text |> Expect.equal "no tests" "$(beaker) No tests found"
      v.Tooltip.StartsWith "No tests found (Expecto detected)" |> Expect.isTrue "tooltip gives the full wording"

    testCase "WHY — statusBarView — a failed discovery is an error with its reason in the tooltip" <| fun _ ->
      let v = view { summary "discovery_failed" "Test discovery failed" "Could not discover tests: could not load Tests.dll" with Total = 0 }
      v.Text |> Expect.equal "failed" "$(error) Test discovery failed"
      v.Tone |> Expect.equal "error" VscStatusTone.Error
      v.Tooltip.StartsWith "Could not discover tests: could not load Tests.dll" |> Expect.isTrue "tooltip gives the reason"

    testCase "WHY — statusBarView — a compile block is an error naming the file" <| fun _ ->
      let v = view (summary "blocked_by_compile_errors" "Math.fs: 2 errors" "Waiting for Math.fs to compile (2 errors) — showing the last good results: 3 passed")
      v.Text |> Expect.equal "blocked" "$(error) Math.fs: 2 errors"
      v.Tone |> Expect.equal "error" VscStatusTone.Error

    testCase "WHY — statusBarView — a failed rebuild is an error" <| fun _ ->
      let v = view (summary "blocked_by_failed_rebuild" "Tests could not re-run" "Tests could not re-run: error FS0001")
      v.Text |> Expect.equal "rebuild failed" "$(error) Tests could not re-run"
      v.Tone |> Expect.equal "error" VscStatusTone.Error

    testCase "WHY — statusBarView — work in progress spins" <| fun _ ->
      (view (summary "rebuilding" "Rebuilding 2 tests" "")).Text |> Expect.equal "rebuilding" "$(sync~spin) Rebuilding 2 tests"
      (view (summary "running" "Running 2 of 10" "")).Text |> Expect.equal "running" "$(sync~spin) Running 2 of 10"
      (view { summary "discovering" "Looking for tests…" "" with Total = 0 }).Text |> Expect.equal "discovering" "$(sync~spin) Looking for tests…"

    testCase "WHY — statusBarView — settled results pick their icon and tone from the counts" <| fun _ ->
      let failed = view { summary "settled" "1 failed · 12 passed" "1 failed · 12 passed" with Failed = 1; Passed = 12 }
      failed.Text |> Expect.equal "failed" "$(testing-error-icon) 1 failed · 12 passed"
      failed.Tone |> Expect.equal "failed is an error" VscStatusTone.Error
      let stale = view { summary "settled" "10 passed · 2 stale" "" with Passed = 10; Stale = 2 }
      stale.Text |> Expect.equal "stale" "$(warning) 10 passed · 2 stale"
      stale.Tone |> Expect.equal "stale is a warning" VscStatusTone.Warning
      let passed = view { summary "settled" "All 12 passed" "All 12 tests passed" with Passed = 12 }
      passed.Text |> Expect.equal "passed" "$(testing-passed-icon) All 12 passed"
      passed.Tone |> Expect.equal "passed is plain" VscStatusTone.Plain

    testCase "WHY — statusBarView — off reads as off" <| fun _ ->
      (view (summary "off" "Live testing off" "Live testing is off")).Text |> Expect.equal "off" "$(beaker) Live testing off"

    testCase "WHY — statusBarView — an older daemon without an activity keeps the count-based wording" <| fun _ ->
      let v = view { summary "" "" "" with Total = 0; DiscoveryState = "discovering" }
      v.Text |> Expect.equal "legacy discovering" "$(sync~spin) Discovering tests..."
  ]

Expecto.Tests.runTestsWithCLIArgs [] [||] tests
