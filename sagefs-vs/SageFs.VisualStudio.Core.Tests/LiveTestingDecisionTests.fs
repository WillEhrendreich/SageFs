module SageFs.VisualStudio.Core.Tests.LiveTestingDecisionTests

open Xunit
open FsUnit.Xunit
open SageFs.VisualStudio.Core

[<Fact>]
let ``formatDecisionHint explains conservative fallback clearly`` () =
  let decision =
    { Cause = RerunCause.FileSaved
      FilePath = "src/Compiled.fs"
      Precision = SelectionPrecision.ConservativeFallback
      Trust = FreshnessTrust.FreshApproximate
      ChangedSymbols = [||]
      SelectedTests = [| "Compiled.Tests.should_build_a"; "Compiled.Tests.should_build_b" |]
      DeferredTests = [||]
      Reason = "fallback rebuild" }

  TestSummary.formatDecisionHint decision
  |> should equal "Why: conservative fallback rebuild (2 selected)"

[<Fact>]
let ``formatDecisionHint explains policy suppression clearly`` () =
  let decision =
    { Cause = RerunCause.KeystrokeBuffered
      FilePath = "src/Architecture.fs"
      Precision = SelectionPrecision.SuppressedByPolicy
      Trust = FreshnessTrust.Suppressed
      ChangedSymbols = [| "Architecture.Rule" |]
      SelectedTests = [||]
      DeferredTests = [| "Architecture.Tests.should_hold" |]
      Reason = "suppressed" }

  TestSummary.formatDecisionHint decision
  |> should equal "Why: run policy deferred 1 test(s)"
