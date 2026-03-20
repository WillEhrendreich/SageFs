module SageFs.Tests.VerificationEvidencePropertyTests

open Expecto
open SageFs.Features.Verification
open SageFs.Tests.SharedGenerators

[<Tests>]
let tests =
  testList "Verification evidence properties" [
    testPropertyWithConfig propConfig "blocked preconditions can never yield confirmed green" <| fun () ->
      let cases = [
        VerificationEvidence.synthesize SessionTrust.Missing (LoadedDefinitionState.ConfirmedCurrent "artifact") None None
        VerificationEvidence.synthesize (SessionTrust.Trusted "s") (LoadedDefinitionState.UnknownLoadState "unknown") None None
        VerificationEvidence.synthesize (SessionTrust.TypeIdentityCompromised "TypeLoadException") (LoadedDefinitionState.ConfirmedCurrent "artifact") None None
      ]
      cases
      |> List.forall (function
        | VerificationEvidence.ConfirmedBySnippet _
        | VerificationEvidence.ConfirmedByExactTest _
        | VerificationEvidence.ConfirmedBySnippetAndExactTest _ -> false
        | _ -> true)

    testPropertyWithConfig propConfig "no observations means missing evidence, not success" <| fun () ->
      match VerificationEvidence.synthesize (SessionTrust.Trusted "s") (LoadedDefinitionState.ConfirmedCurrent "artifact") None None with
      | VerificationEvidence.Blocked (VerificationBlocker.MissingEvidence _) -> true
      | _ -> false
  ]
