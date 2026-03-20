module SageFs.Features.Verification

open System
open SageFs
open SageFs.WorkerProtocol
open SageFs.Features.LiveTesting

[<RequireQualifiedAccess>]
type LoadedDefinitionState =
  | ConfirmedCurrent of artifact: string
  | ConfirmedStale of diskArtifact: string * loadedArtifact: string
  | UnknownLoadState of reason: string

module LoadedDefinitionState =
  let isCurrent = function
    | LoadedDefinitionState.ConfirmedCurrent _ -> true
    | _ -> false

[<RequireQualifiedAccess>]
type RegressionRisk =
  | LocalizedBehavior
  | SharedContract
  | SafetyCritical

type ExactTestRef = private ExactTestRef of string

module ExactTestRef =
  let create (text: string) =
    match String.IsNullOrWhiteSpace text with
    | true -> Error "Exact test name cannot be empty."
    | false -> Ok (ExactTestRef (text.Trim()))

  let value (ExactTestRef text) = text

type PatternText = private PatternText of string

module PatternText =
  let create (text: string) =
    match String.IsNullOrWhiteSpace text with
    | true -> Error "Pattern cannot be empty."
    | false -> Ok (PatternText (text.Trim()))

  let value (PatternText text) = text

[<RequireQualifiedAccess>]
type VerificationIntent =
  | ExploreBehavior of subject: string
  | VerifyChangedBehavior of subject: string * regressionRisk: RegressionRisk
  | RunExactTest of ExactTestRef
  | EscalateConfidence of RegressionRisk

[<RequireQualifiedAccess>]
type SessionTrust =
  | Trusted of sessionId: string
  | Ambiguous of candidateSessionIds: string list
  | WarmingUp of sessionId: string
  | Unavailable of sessionId: string * status: string
  | StaleDefinitions of filePath: string
  | TypeIdentityCompromised of diagnostic: string
  | Missing

module SessionTrust =
  type SessionObservation = {
    MatchingSessionIds: string list
    SessionStatus: SessionStatus option
    LoadedState: LoadedDefinitionState option
    TypeIdentityDiagnostic: string option
  }

  let classify (observation: SessionObservation) =
    match observation.TypeIdentityDiagnostic with
    | Some diagnostic when not (String.IsNullOrWhiteSpace diagnostic) ->
      SessionTrust.TypeIdentityCompromised diagnostic
    | _ ->
      match observation.LoadedState with
      | Some (LoadedDefinitionState.ConfirmedStale (diskArtifact, _)) ->
        SessionTrust.StaleDefinitions diskArtifact
      | _ ->
        match observation.MatchingSessionIds with
        | [] -> SessionTrust.Missing
        | [ singleSession ] ->
          match observation.SessionStatus with
          | Some SessionStatus.Ready ->
            SessionTrust.Trusted singleSession
          | Some SessionStatus.Starting
          | Some SessionStatus.Restarting
          | Some SessionStatus.Evaluating
          | Some (SessionStatus.Building _) ->
            SessionTrust.WarmingUp singleSession
          | Some status ->
            SessionTrust.Unavailable (singleSession, SessionStatus.label status)
          | None ->
            SessionTrust.Unavailable (singleSession, "Unknown")
        | many -> SessionTrust.Ambiguous many

  let isTrusted = function
    | SessionTrust.Trusted _ -> true
    | _ -> false

[<RequireQualifiedAccess>]
type VerificationMode =
  | SnippetFirst
  | ExactTestOnly of ExactTestRef
  | SnippetThenExactTest of ExactTestRef
  | BroaderConfidenceRun

[<RequireQualifiedAccess>]
type VerificationBlocker =
  | SessionNotTrustworthy of SessionTrust
  | LoadedCodeNotCurrent of LoadedDefinitionState
  | ConflictingEvidence of string
  | MissingEvidence of string

[<RequireQualifiedAccess>]
type VerificationPlan =
  | Perform of VerificationMode
  | Blocked of VerificationBlocker

[<RequireQualifiedAccess>]
type SnippetObservation =
  | SnippetPassed of summary: string
  | SnippetFailed of summary: string

[<RequireQualifiedAccess>]
type ExactTestObservation =
  | ExactTestPassed of ExactTestRef
  | ExactTestFailed of ExactTestRef * summary: string

[<RequireQualifiedAccess>]
type VerificationEvidence =
  | ConfirmedBySnippet of summary: string
  | ConfirmedByExactTest of ExactTestRef
  | ConfirmedBySnippetAndExactTest of summary: string * ExactTestRef
  | Refuted of summary: string
  | Blocked of VerificationBlocker

[<RequireQualifiedAccess>]
type TestSelection =
  | ExactFullName of ExactTestRef
  | FuzzyPattern of PatternText

[<RequireQualifiedAccess>]
type TestSelectionResolution =
  | AllDiscovered of TestCase array
  | ExactMatch of TestCase
  | FuzzyMatches of TestCase array
  | NoExactMatch of ExactTestRef
  | AmbiguousExactMatches of ExactTestRef * TestCase array

module TestSelection =
  [<Literal>]
  let ExactPrefix = "exact:"

  let parse (patternFilter: string option) =
    match patternFilter with
    | None -> Ok None
    | Some raw when String.IsNullOrWhiteSpace raw -> Ok None
    | Some raw ->
      let text = raw.Trim()
      match text.StartsWith(ExactPrefix, StringComparison.OrdinalIgnoreCase) with
      | true ->
        let exactName = text.Substring(ExactPrefix.Length)
        ExactTestRef.create exactName
        |> Result.map (fun exact -> Some (TestSelection.ExactFullName exact))
      | false ->
        PatternText.create text
        |> Result.map (fun pattern -> Some (TestSelection.FuzzyPattern pattern))

  let private filterByCategory (categoryFilter: TestCategory option) (discoveredTests: TestCase array) =
    match categoryFilter with
    | None -> discoveredTests
    | Some category -> discoveredTests |> Array.filter (fun tc -> tc.Category = category)

  let private containsIgnoreCase (needle: string) (haystack: string) =
    haystack.Contains(needle, StringComparison.OrdinalIgnoreCase)

  let resolve
    (discoveredTests: TestCase array)
    (selection: TestSelection option)
    (categoryFilter: TestCategory option)
    : TestSelectionResolution =
    let categoryScoped = filterByCategory categoryFilter discoveredTests
    match selection with
    | None -> TestSelectionResolution.AllDiscovered categoryScoped
    | Some (TestSelection.FuzzyPattern pattern) ->
      let text = PatternText.value pattern
      categoryScoped
      |> Array.filter (fun tc ->
        containsIgnoreCase text tc.FullName
        || containsIgnoreCase text tc.DisplayName)
      |> TestSelectionResolution.FuzzyMatches
    | Some (TestSelection.ExactFullName exact) ->
      let exactName = ExactTestRef.value exact
      let matches =
        categoryScoped
        |> Array.filter (fun tc -> String.Equals(tc.FullName, exactName, StringComparison.OrdinalIgnoreCase))
      match matches with
      | [| single |] -> TestSelectionResolution.ExactMatch single
      | [||] -> TestSelectionResolution.NoExactMatch exact
      | many -> TestSelectionResolution.AmbiguousExactMatches (exact, many)

module VerificationPlanner =
  let private requireTrustedSession sessionTrust =
    match sessionTrust with
    | SessionTrust.Trusted _ -> None
    | other -> Some (VerificationBlocker.SessionNotTrustworthy other)

  let private requireCurrentDefinitions loadedState =
    match loadedState with
    | LoadedDefinitionState.ConfirmedCurrent _ -> None
    | other -> Some (VerificationBlocker.LoadedCodeNotCurrent other)

  let plan
    (sessionTrust: SessionTrust)
    (loadedState: LoadedDefinitionState)
    (namedGuard: ExactTestRef option)
    (intent: VerificationIntent)
    : VerificationPlan =
    match requireTrustedSession sessionTrust with
    | Some blocker -> VerificationPlan.Blocked blocker
    | None ->
      match requireCurrentDefinitions loadedState with
      | Some blocker -> VerificationPlan.Blocked blocker
      | None ->
        match intent, namedGuard with
        | VerificationIntent.ExploreBehavior _, _ ->
          VerificationPlan.Perform VerificationMode.SnippetFirst
        | VerificationIntent.VerifyChangedBehavior (_, RegressionRisk.LocalizedBehavior), None ->
          VerificationPlan.Perform VerificationMode.SnippetFirst
        | VerificationIntent.VerifyChangedBehavior _, Some guard ->
          VerificationPlan.Perform (VerificationMode.SnippetThenExactTest guard)
        | VerificationIntent.VerifyChangedBehavior (_, RegressionRisk.SafetyCritical), None ->
          VerificationPlan.Perform VerificationMode.BroaderConfidenceRun
        | VerificationIntent.VerifyChangedBehavior (_, RegressionRisk.SharedContract), None ->
          VerificationPlan.Perform VerificationMode.SnippetFirst
        | VerificationIntent.RunExactTest exact, _ ->
          VerificationPlan.Perform (VerificationMode.ExactTestOnly exact)
        | VerificationIntent.EscalateConfidence _, _ ->
          VerificationPlan.Perform VerificationMode.BroaderConfidenceRun

module VerificationEvidence =
  let synthesize
    (sessionTrust: SessionTrust)
    (loadedState: LoadedDefinitionState)
    (snippet: SnippetObservation option)
    (exactTest: ExactTestObservation option)
    : VerificationEvidence =
    match VerificationPlanner.plan sessionTrust loadedState None (VerificationIntent.ExploreBehavior "behavior") with
    | VerificationPlan.Blocked blocker -> VerificationEvidence.Blocked blocker
    | VerificationPlan.Perform _ ->
      match snippet, exactTest with
      | Some (SnippetObservation.SnippetPassed summary), Some (ExactTestObservation.ExactTestPassed exact) ->
        VerificationEvidence.ConfirmedBySnippetAndExactTest (summary, exact)
      | Some (SnippetObservation.SnippetPassed summary), None ->
        VerificationEvidence.ConfirmedBySnippet summary
      | None, Some (ExactTestObservation.ExactTestPassed exact) ->
        VerificationEvidence.ConfirmedByExactTest exact
      | Some (SnippetObservation.SnippetPassed _), Some (ExactTestObservation.ExactTestFailed (_, summary)) ->
        VerificationEvidence.Blocked (VerificationBlocker.ConflictingEvidence summary)
      | Some (SnippetObservation.SnippetFailed summary), Some (ExactTestObservation.ExactTestPassed _) ->
        VerificationEvidence.Blocked (VerificationBlocker.ConflictingEvidence summary)
      | Some (SnippetObservation.SnippetFailed summary), _ ->
        VerificationEvidence.Refuted summary
      | _, Some (ExactTestObservation.ExactTestFailed (_, summary)) ->
        VerificationEvidence.Refuted summary
      | None, None ->
        VerificationEvidence.Blocked (VerificationBlocker.MissingEvidence "No snippet or exact-test evidence was collected.")

type TargetedVerificationRequest = {
  Intent: VerificationIntent
  NamedGuard: ExactTestRef option
  SessionObservation: SessionTrust.SessionObservation
  LoadedState: LoadedDefinitionState
}

type TargetedVerificationReport = {
  Trust: SessionTrust
  Plan: VerificationPlan
  Evidence: VerificationEvidence option
}

module TargetedVerification =
  let createReport
    (request: TargetedVerificationRequest)
    (snippet: SnippetObservation option)
    (exactTest: ExactTestObservation option)
    : TargetedVerificationReport =
    let trust = SessionTrust.classify request.SessionObservation
    let plan = VerificationPlanner.plan trust request.LoadedState request.NamedGuard request.Intent
    let evidence =
      match plan with
      | VerificationPlan.Blocked blocker -> Some (VerificationEvidence.Blocked blocker)
      | VerificationPlan.Perform _ -> Some (VerificationEvidence.synthesize trust request.LoadedState snippet exactTest)
    { Trust = trust
      Plan = plan
      Evidence = evidence }

  let summarize (report: TargetedVerificationReport) =
    match report.Plan, report.Evidence with
    | VerificationPlan.Blocked (VerificationBlocker.SessionNotTrustworthy (SessionTrust.Ambiguous sessionIds)), _ ->
      sprintf "Blocked: multiple sessions could prove this behavior (%s). Pin one session before trusting results." (String.concat ", " sessionIds)
    | VerificationPlan.Blocked (VerificationBlocker.SessionNotTrustworthy (SessionTrust.WarmingUp sessionId)), _ ->
      sprintf "Blocked: session '%s' is still warming up, so verification would be premature." sessionId
    | VerificationPlan.Blocked (VerificationBlocker.SessionNotTrustworthy (SessionTrust.Unavailable (sessionId, status))), _ ->
      sprintf "Blocked: session '%s' is not trustworthy right now (%s). Recover or choose another session." sessionId status
    | VerificationPlan.Blocked (VerificationBlocker.SessionNotTrustworthy (SessionTrust.TypeIdentityCompromised diagnostic)), _ ->
      sprintf "Blocked: type identity is compromised (%s). Recover the session before claiming green." diagnostic
    | VerificationPlan.Blocked (VerificationBlocker.SessionNotTrustworthy (SessionTrust.StaleDefinitions filePath)), _ ->
      sprintf "Blocked: session still carries stale definitions for '%s'. Reload or recreate the session before verifying." filePath
    | VerificationPlan.Blocked (VerificationBlocker.SessionNotTrustworthy SessionTrust.Missing), _ ->
      "Blocked: there is no trustworthy session to prove this behavior yet."
    | VerificationPlan.Blocked (VerificationBlocker.LoadedCodeNotCurrent (LoadedDefinitionState.ConfirmedStale (diskArtifact, loadedArtifact))), _ ->
      sprintf "Blocked: disk says '%s' but the live session still has '%s'. SageFs should not claim green yet." diskArtifact loadedArtifact
    | VerificationPlan.Blocked (VerificationBlocker.LoadedCodeNotCurrent (LoadedDefinitionState.UnknownLoadState reason)), _ ->
      sprintf "Blocked: SageFs cannot prove what code is loaded (%s)." reason
    | VerificationPlan.Perform VerificationMode.SnippetFirst, _ ->
      "Plan: verify the changed behavior locally with a snippet before waking broader test machinery."
    | VerificationPlan.Perform (VerificationMode.ExactTestOnly exact), _ ->
      sprintf "Plan: run only the exact regression guard '%s'." (ExactTestRef.value exact)
    | VerificationPlan.Perform (VerificationMode.SnippetThenExactTest exact), _ ->
      sprintf "Plan: prove the behavior locally first, then run the exact guard '%s'." (ExactTestRef.value exact)
    | VerificationPlan.Perform VerificationMode.BroaderConfidenceRun, _ ->
      "Plan: broader confidence is justified; run more than a single local proof."
    | _, Some (VerificationEvidence.Blocked blocker) ->
      sprintf "Blocked: %A" blocker
    | _, _ ->
      "Verification report ready."
