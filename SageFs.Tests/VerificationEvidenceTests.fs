module SageFs.Tests.VerificationEvidenceTests

open Expecto
open Expecto.Flip
open SageFs.Features.Verification

let private exact text =
  match ExactTestRef.create text with
  | Ok value -> value
  | Error err -> failtestf "expected exact ref, got error: %s" err

[<Tests>]
let tests =
  testList "Verification evidence" [
    testCase "a passing snippet is trustworthy only when SageFs can prove the loaded code is current" <| fun _ ->
      let evidence =
        VerificationEvidence.synthesize
          (SessionTrust.Trusted "session-1")
          (LoadedDefinitionState.ConfirmedCurrent "UserPreferences.fs@v2")
          (Some (SnippetObservation.SnippetPassed "missing directory now returns Error"))
          None
      evidence
      |> Expect.equal
        "trusted current snippet evidence should be accepted"
        (VerificationEvidence.ConfirmedBySnippet "missing directory now returns Error")

    testCase "a passing snippet with stale loaded code is blocked rather than green" <| fun _ ->
      let evidence =
        VerificationEvidence.synthesize
          (SessionTrust.Trusted "session-1")
          (LoadedDefinitionState.ConfirmedStale ("disk-v2", "loaded-v1"))
          (Some (SnippetObservation.SnippetPassed "looked green"))
          None
      match evidence with
      | VerificationEvidence.Blocked (VerificationBlocker.LoadedCodeNotCurrent _) -> ()
      | other -> failtestf "expected blocked stale evidence, got %A" other

    testCase "snippet plus exact guard produces stronger combined evidence" <| fun _ ->
      let guard = exact "Tests.UserPreferences.guard"
      let evidence =
        VerificationEvidence.synthesize
          (SessionTrust.Trusted "session-1")
          (LoadedDefinitionState.ConfirmedCurrent "UserPreferences.fs@v2")
          (Some (SnippetObservation.SnippetPassed "behavior holds"))
          (Some (ExactTestObservation.ExactTestPassed guard))
      evidence
      |> Expect.equal
        "both local proof and exact guard should compose"
        (VerificationEvidence.ConfirmedBySnippetAndExactTest ("behavior holds", guard))

    testCase "failing evidence refutes the claim" <| fun _ ->
      let evidence =
        VerificationEvidence.synthesize
          (SessionTrust.Trusted "session-1")
          (LoadedDefinitionState.ConfirmedCurrent "UserPreferences.fs@v2")
          (Some (SnippetObservation.SnippetFailed "still throws DirectoryNotFoundException"))
          None
      evidence
      |> Expect.equal
        "failed snippet should refute the claim"
        (VerificationEvidence.Refuted "still throws DirectoryNotFoundException")
  ]
