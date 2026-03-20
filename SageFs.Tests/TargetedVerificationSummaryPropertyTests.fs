module SageFs.Tests.TargetedVerificationSummaryPropertyTests

open Expecto
open SageFs.Features.Verification

[<Tests>]
let tests =
  testList "Targeted verification summary properties" [
    testProperty "blocked plans always summarize as blocked language" <| fun () ->
      let reports = [
        { Trust = SessionTrust.Missing
          Plan = VerificationPlan.Blocked (VerificationBlocker.SessionNotTrustworthy SessionTrust.Missing)
          Evidence = None }
        { Trust = SessionTrust.Trusted "s"
          Plan = VerificationPlan.Blocked (VerificationBlocker.LoadedCodeNotCurrent (LoadedDefinitionState.UnknownLoadState "unknown"))
          Evidence = None }
      ]
      reports
      |> List.forall (fun report ->
        TargetedVerification.summarize report
        |> fun text -> text.Contains("Blocked"))
  ]
