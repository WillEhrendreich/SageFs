/// BDD tests for wiring WorkflowDetection.suggest into session creation.
///
/// These tests validate the INTEGRATION SEAM — the pure helper that formats
/// detection results into user-facing hints during createSession responses.
/// Existing detection logic tests live in WorkflowScenarioTests; these focus
/// on the composition: detection → hint formatting → response enrichment.
module SageFs.Tests.WorkflowDetectionWiringTests

open Expecto
open Expecto.Flip
open SageFs.WorkflowTypes

// ── Helper under test ──────────────────────────────────────────
// McpTools.formatDetectionHint lives in Mcp.fs; we test it here
// because it's a pure function with no IO dependencies.

// ── Scenario: Datastar projects get actionable hints ───────────

let private datastarHintScenarios =
  testList "Datastar project hints" [

    testCase
      "Datastar project gets Live suggestion because SSE-driven DOM morphing needs hot reload"
      <| fun _ ->
      // GIVEN a project whose packages include Falco.Datastar
      let packageRefs = [ "Falco"; "Falco.Datastar"; "Falco.Markup" ]
      let workflow = SessionWorkflow.Interactive

      // WHEN the detection helper runs during session creation
      let hint =
        SageFs.McpTools.formatDetectionHint packageRefs workflow

      // THEN a hint is returned mentioning Datastar
      hint
      |> Expect.isSome
        "Datastar project should produce a hint"
      hint.Value
      |> Expect.stringContains
        "hint should mention detected packages" "Falco.Datastar"

    testCase
      "Datastar hint includes switch_workflow guidance"
      <| fun _ ->
      // GIVEN a Datastar project in Interactive mode
      let packageRefs = [ "Falco.Datastar" ]
      let workflow = SessionWorkflow.Interactive

      // WHEN the hint is generated
      let hint =
        SageFs.McpTools.formatDetectionHint packageRefs workflow

      // THEN it tells the user how to switch
      hint.Value
      |> Expect.stringContains
        "hint should mention switch_workflow" "switch_workflow"
  ]

// ── Scenario: Plain projects get no hint ───────────────────────

let private noHintScenarios =
  testList "plain projects produce no hint" [

    testCase
      "Plain library project gets no suggestion because REPL is the natural default"
      <| fun _ ->
      // GIVEN a non-web project
      let packageRefs =
        [ "FSharp.Core"; "Newtonsoft.Json"; "Expecto" ]
      let workflow = SessionWorkflow.Interactive

      // WHEN the detection helper runs
      let hint =
        SageFs.McpTools.formatDetectionHint packageRefs workflow

      // THEN no hint — Interactive is already the right choice
      hint
      |> Expect.isNone
        "non-web project should not produce a hint"

    testCase
      "Empty package list returns None — no packages means no detection signal"
      <| fun _ ->
      // GIVEN no package references at all
      let packageRefs: string list = []
      let workflow = SessionWorkflow.Interactive

      // WHEN the detection helper runs
      let hint =
        SageFs.McpTools.formatDetectionHint packageRefs workflow

      // THEN no hint
      hint
      |> Expect.isNone
        "empty package list should not produce a hint"
  ]

// ── Scenario: Web framework detection ──────────────────────────

let private webFrameworkHintScenarios =
  testList "web framework hints" [

    testCase
      "Web framework project gets Live suggestion even without Datastar"
      <| fun _ ->
      // GIVEN a Giraffe project (no Datastar)
      let packageRefs = [ "Giraffe"; "FSharp.Core" ]
      let workflow = SessionWorkflow.Interactive

      // WHEN the detection helper runs
      let hint =
        SageFs.McpTools.formatDetectionHint packageRefs workflow

      // THEN a hint is produced for the web framework
      hint
      |> Expect.isSome
        "web framework should produce a hint"
      hint.Value
      |> Expect.stringContains
        "hint should mention the detected package" "Giraffe"

    testCase
      "Saturn web project gets Live suggestion"
      <| fun _ ->
      // GIVEN a Saturn project
      let packageRefs = [ "Saturn"; "FSharp.Core" ]
      let workflow = SessionWorkflow.Interactive

      // WHEN the detection helper runs
      let hint =
        SageFs.McpTools.formatDetectionHint packageRefs workflow

      // THEN a hint is produced
      hint
      |> Expect.isSome
        "Saturn project should produce a hint"
  ]

// ── Scenario: Already in WebLive mode ──────────────────────────

let private alreadyWebLiveScenarios =
  testList "no hint when already in WebLive" [

    testCase
      "WebLive workflow produces no hint because user already chose Live"
      <| fun _ ->
      // GIVEN a Datastar project already in WebLive mode
      let packageRefs = [ "Falco.Datastar" ]
      let workflow =
        SessionWorkflow.WebLive BrowserRefreshConfig.defaults

      // WHEN the detection helper runs
      let hint =
        SageFs.McpTools.formatDetectionHint packageRefs workflow

      // THEN no hint — they're already in the right mode
      hint
      |> Expect.isNone
        "WebLive workflow should not produce a redundant hint"
  ]

// ── Scenario: Purity and transparency ──────────────────────────

let private purityScenarios =
  testList "detection purity and transparency" [

    testCase
      "Detection is idempotent — calling suggest twice with same refs yields same result"
      <| fun _ ->
      // GIVEN any set of package refs
      let packageRefs = [ "Falco.Datastar"; "Falco" ]

      // WHEN suggest is called twice
      let first = WorkflowDetection.suggest packageRefs
      let second = WorkflowDetection.suggest packageRefs

      // THEN results are identical (pure function)
      second
      |> Expect.equal
        "suggest should be idempotent" first

    testCase
      "Suggestion carries detected packages for transparent user messaging"
      <| fun _ ->
      // GIVEN a project with Datastar
      let packageRefs =
        [ "Falco"; "Falco.Datastar"; "FSharp.Core" ]

      // WHEN suggest returns Some
      let suggestion = WorkflowDetection.suggest packageRefs

      // THEN DetectedPackages is non-empty and contains the trigger
      suggestion
      |> Expect.isSome
        "Datastar project should produce a suggestion"
      suggestion.Value.DetectedPackages
      |> Expect.isNonEmpty
        "DetectedPackages must list what triggered the suggestion"
      suggestion.Value.DetectedPackages
      |> Expect.contains
        "should contain Falco.Datastar" "Falco.Datastar"
  ]

// ── Property: idempotency for arbitrary inputs ─────────────────

open FsCheck

let private propertyTests =
  testList "detection properties" [

    testProperty
      "suggest is a pure function — same input always gives same output"
      (fun (packageRefs: string list) ->
        let first = WorkflowDetection.suggest packageRefs
        let second = WorkflowDetection.suggest packageRefs
        first = second)

    testProperty
      "empty list never suggests anything — stability"
      (fun () ->
        WorkflowDetection.suggest [] = None)

    testProperty
      "adding unknown packages does not change suggestion"
      (fun (unknowns: string list) ->
        let known = [ "Falco.Datastar" ]
        let unknownsFiltered =
          unknowns
          |> List.filter (fun s ->
            not (s.Contains("Falco"))
            && not (s.Contains("Giraffe"))
            && not (s.Contains("Saturn"))
            && not (s.Contains("Datastar"))
            && not (s.Contains("Microsoft.AspNetCore")))
        WorkflowDetection.suggest known
          = WorkflowDetection.suggest (known @ unknownsFiltered))

    testProperty
      "suggest is idempotent with respect to deduplication"
      (fun (refs: string list) ->
        WorkflowDetection.suggest refs
          = WorkflowDetection.suggest (List.distinct refs))

    testProperty
      "Datastar dominance — Falco.Datastar always surfaces in detected packages"
      (fun (others: string list) ->
        let refs = "Falco.Datastar" :: others
        match WorkflowDetection.suggest refs with
        | Some s ->
          s.DetectedPackages
          |> List.exists (fun p -> p.Contains("Datastar"))
        | None -> false)
  ]

// ── extractPackageNames properties ─────────────────────────

let private extractionTests =
  testList "extractPackageNames" [

    testCase
      "empty project list yields empty package names"
      <| fun _ ->
      WorkflowDetection.extractPackageNames []
      |> Expect.isEmpty
        "no projects means no packages"

    testCase
      "test project packages are excluded"
      <| fun _ ->
      let input =
        [ [ "Falco"; "Falco.Datastar" ]
          [ "Expecto"; "FSharp.Core" ] ]
      let result =
        WorkflowDetection.extractPackageNames input
      result
      |> Expect.contains
        "should contain Falco" "Falco"
      result
      |> List.contains "Expecto"
      |> Expect.isFalse
        "test project packages should be excluded"

    testCase
      "non-test project packages are returned"
      <| fun _ ->
      let input = [ [ "Falco"; "FSharp.Core" ] ]
      let result =
        WorkflowDetection.extractPackageNames input
      result
      |> Expect.isNonEmpty
        "non-test project packages should be returned"

    testProperty
      "extractPackageNames returns distinct results"
      (fun (packages: string list) ->
        let input = [ packages; packages ]
        let result =
          WorkflowDetection.extractPackageNames input
        result = List.distinct result)

    testProperty
      "test projects never leak into extracted packages"
      (fun (nonTestPkgs: string list) ->
        let testProject = [ "Expecto"; "FSharp.Core" ]
        let input = [ nonTestPkgs; testProject ]
        let result =
          WorkflowDetection.extractPackageNames input
        result
        |> List.contains "Expecto"
        |> not)
  ]

// ── Root export ────────────────────────────────────────────────

[<Tests>]
let workflowDetectionWiringTests =
  testList "WorkflowDetection wiring" [
    datastarHintScenarios
    noHintScenarios
    webFrameworkHintScenarios
    alreadyWebLiveScenarios
    purityScenarios
    propertyTests
    extractionTests
  ]
