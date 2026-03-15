/// Property-based tests for the SessionWorkflow domain model.
///
/// These tests document WHY the model is correct — design invariants
/// that the type system alone cannot fully guarantee. Each property
/// answers: "What must ALWAYS be true about the relationship between
/// workflows, feedback strategies, REPL capabilities, and FSI flags?"
///
/// Muratori's filter applied: we do NOT test things the compiler already
/// guarantees (e.g., exhaustive match). We test design invariants that
/// could be accidentally violated in the update/rendering functions.
module SageFs.Tests.WorkflowPropertyTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.WorkflowTypes
open SageFs.Tests.SharedGenerators

// ── Generators ──────────────────────────────────────────────

let private genBrowserRefreshConfig =
  Gen.elements [
    BrowserRefreshConfig.defaults
    { WatchPatterns = [ "*.fs" ] }
    { WatchPatterns = [ "*.fsx"; "*.fs"; "*.html" ] }
    { WatchPatterns = [] }
  ]

let private genSessionWorkflow =
  Gen.oneof [
    Gen.constant SessionWorkflow.Interactive
    genBrowserRefreshConfig |> Gen.map SessionWorkflow.WebLive
  ]

type WorkflowGenerators =
  static member SessionWorkflow () =
    Arb.fromGen genSessionWorkflow
  static member BrowserRefreshConfig () =
    Arb.fromGen genBrowserRefreshConfig

let private workflowConfig = {
  propConfig with
    arbitrary = [
      typeof<WorkflowGenerators>
    ]
}

// ── Property tests ──────────────────────────────────────────

[<Tests>]
let workflowPropertyTests =
  testList "Workflow domain model properties" [

    // --- Orthogonality invariants ---

    testList "feedback strategy is determined solely by workflow shape" [

      testPropertyWithConfig workflowConfig
        "Interactive always produces ReplDriven feedback" <|
        fun () ->
          SessionWorkflow.Interactive
          |> SessionWorkflow.feedbackStrategy
          |> (=) FeedbackStrategy.ReplDriven

      testPropertyWithConfig workflowConfig
        "WebLive always produces SaveDriven feedback" <|
        fun (cfg: BrowserRefreshConfig) ->
          SessionWorkflow.WebLive cfg
          |> SessionWorkflow.feedbackStrategy
          |> (=) (FeedbackStrategy.SaveDriven cfg)
    ]

    // --- FSI flag consistency ---

    testList "FSI flags are consistent with workflow" [

      testPropertyWithConfig workflowConfig
        "Interactive never emits --multiemit-" <|
        fun () ->
          SessionWorkflow.Interactive
          |> SessionWorkflow.fsiArgs
          |> List.contains "--multiemit-"
          |> not

      testPropertyWithConfig workflowConfig
        "WebLive always emits --multiemit-" <|
        fun (cfg: BrowserRefreshConfig) ->
          SessionWorkflow.WebLive cfg
          |> SessionWorkflow.fsiArgs
          |> List.contains "--multiemit-"

      testPropertyWithConfig workflowConfig
        "no workflow emits contradictory multiemit flags" <|
        fun (workflow: SessionWorkflow) ->
          let flags = SessionWorkflow.fsiArgs workflow
          not (
            List.contains "--multiemit-" flags
            && List.contains "--multiemit" flags
          )
    ]

    // --- REPL capability consistency ---

    testList "REPL capability is consistent with feedback strategy" [

      testPropertyWithConfig workflowConfig
        "ReplDriven feedback always yields Full REPL capability" <|
        fun (workflow: SessionWorkflow) ->
          match SessionWorkflow.feedbackStrategy workflow with
          | FeedbackStrategy.ReplDriven ->
            SessionWorkflow.replCapability workflow = ReplCapability.Full
          | FeedbackStrategy.SaveDriven _ -> true // not this case

      testPropertyWithConfig workflowConfig
        "SaveDriven feedback always yields ExpressionOnly REPL capability" <|
        fun (workflow: SessionWorkflow) ->
          match SessionWorkflow.feedbackStrategy workflow with
          | FeedbackStrategy.SaveDriven _ ->
            SessionWorkflow.replCapability workflow = ReplCapability.ExpressionOnly
          | FeedbackStrategy.ReplDriven -> true // not this case

      testPropertyWithConfig workflowConfig
        "Full REPL capability implies no --multiemit- in FSI args" <|
        fun (workflow: SessionWorkflow) ->
          match SessionWorkflow.replCapability workflow with
          | ReplCapability.Full ->
            SessionWorkflow.fsiArgs workflow
            |> List.contains "--multiemit-"
            |> not
          | ReplCapability.ExpressionOnly -> true // not this case
    ]

    // --- Label uniqueness and completeness ---

    testList "workflow labels are well-behaved" [

      testPropertyWithConfig workflowConfig
        "every workflow produces a non-empty label" <|
        fun (workflow: SessionWorkflow) ->
          SessionWorkflow.label workflow
          |> System.String.IsNullOrWhiteSpace
          |> not

      testPropertyWithConfig workflowConfig
        "different workflow shapes produce different labels" <|
        fun (cfg: BrowserRefreshConfig) ->
          let interactiveLabel =
            SessionWorkflow.label SessionWorkflow.Interactive
          let webLiveLabel =
            SessionWorkflow.label (SessionWorkflow.WebLive cfg)
          interactiveLabel <> webLiveLabel
    ]

    // --- Hot reload consistency ---

    testList "hot reload active is consistent with workflow" [

      testPropertyWithConfig workflowConfig
        "isHotReloadActive matches presence of --multiemit- flag" <|
        fun (workflow: SessionWorkflow) ->
          let hasMultiemitMinus =
            SessionWorkflow.fsiArgs workflow
            |> List.contains "--multiemit-"
          SessionWorkflow.isHotReloadActive workflow = hasMultiemitMinus
    ]

    // --- WorkflowDetection properties ---

    testList "workflow detection" [

      testCase "non-web packages produce no suggestion" <| fun _ ->
        [ "Newtonsoft.Json"; "FSharp.Core"; "Expecto" ]
        |> WorkflowDetection.suggest
        |> Expect.isNone "no suggestion for non-web project"

      testCase "empty package list produces no suggestion" <| fun _ ->
        []
        |> WorkflowDetection.suggest
        |> Expect.isNone "no suggestion for empty packages"

      testCase "Falco.Datastar triggers suggestion" <| fun _ ->
        [ "Falco"; "Falco.Datastar"; "FSharp.Core" ]
        |> WorkflowDetection.suggest
        |> Expect.isSome "should suggest for Datastar"

      testCase "Falco without Datastar triggers suggestion" <| fun _ ->
        [ "Falco"; "FSharp.Core" ]
        |> WorkflowDetection.suggest
        |> Expect.isSome "should suggest for web framework"

      testCase "Datastar suggestion takes priority over generic web" <| fun _ ->
        let result =
          [ "Falco"; "Falco.Datastar"; "Giraffe" ]
          |> WorkflowDetection.suggest
        result
        |> Expect.isSome "should produce suggestion"
        result.Value.Reason
        |> Expect.stringContains "should mention Datastar" "Datastar"

      testCase "suggested workflow is always WebLive" <| fun _ ->
        let result =
          [ "Saturn"; "FSharp.Core" ]
          |> WorkflowDetection.suggest
        match result with
        | Some s ->
          match s.SuggestedWorkflow with
          | SessionWorkflow.WebLive _ -> ()
          | SessionWorkflow.Interactive ->
            failtest "should suggest WebLive, not Interactive"
        | None -> failtest "should produce suggestion for Saturn"

      testCase "detected packages list is accurate" <| fun _ ->
        let result =
          [ "Falco"; "Newtonsoft.Json"; "Giraffe" ]
          |> WorkflowDetection.suggest
        result
        |> Expect.isSome "should suggest"
        result.Value.DetectedPackages
        |> Expect.hasLength "should find both web packages" 2
    ]
  ]
