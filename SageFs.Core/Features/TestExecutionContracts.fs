namespace SageFs.Features.LiveTesting

open System

[<RequireQualifiedAccess>]
type TestExecutionTermination =
  | StreamCompleted
  | StreamStalled of after: TimeSpan
  | StreamCancelled of reason: string
  | TransportFailed of message: string

module TestExecutionTermination =
  let noResultReason = function
    | TestExecutionTermination.StreamCompleted -> NoResultReason.StreamEnded
    | TestExecutionTermination.StreamStalled after -> NoResultReason.StreamStalled after
    | TestExecutionTermination.StreamCancelled _ -> NoResultReason.RunCancelled
    | TestExecutionTermination.TransportFailed message -> NoResultReason.TransportFailed message

[<RequireQualifiedAccess>]
type TestExecutionIssue =
  | MissingResults of testIds: TestId list
  | DuplicateResults of testIds: TestId list
  | UnrequestedResults of testIds: TestId list

[<RequireQualifiedAccess>]
type TestExecutionIntegrity =
  | Complete
  | Invalid of issues: TestExecutionIssue list

type TestExecutionReport = {
  RequestedTestIds: TestId list
  Termination: TestExecutionTermination
  Results: TestRunResult array
  Integrity: TestExecutionIntegrity
}

module TestExecutionReport =
  let create
    (requestedTestIds: TestId list)
    (termination: TestExecutionTermination)
    (results: TestRunResult array)
    : TestExecutionReport =
    let requested = Set.ofList requestedTestIds
    let grouped =
      results
      |> Array.groupBy (fun result -> result.TestId)
      |> Map.ofArray
    let reported = grouped |> Map.toList |> List.map fst |> Set.ofList
    let missing = Set.difference requested reported |> Set.toList
    let duplicates =
      grouped
      |> Map.toList
      |> List.choose (fun (testId, entries) ->
        match Array.length entries with
        | 1 -> None
        | _ -> Some testId)
      |> List.sort
    let unrequested = Set.difference reported requested |> Set.toList
    let issues =
      [ if missing |> List.isEmpty then None else Some (TestExecutionIssue.MissingResults missing)
        if duplicates |> List.isEmpty then None else Some (TestExecutionIssue.DuplicateResults duplicates)
        if unrequested |> List.isEmpty then None else Some (TestExecutionIssue.UnrequestedResults unrequested) ]
      |> List.choose id
    { RequestedTestIds = requestedTestIds
      Termination = termination
      Results = results
      Integrity =
        match issues with
        | [] -> TestExecutionIntegrity.Complete
        | issues -> TestExecutionIntegrity.Invalid issues }

  let isComplete report =
    match report.Integrity with
    | TestExecutionIntegrity.Complete -> true
    | TestExecutionIntegrity.Invalid _ -> false

  let issues report =
    match report.Integrity with
    | TestExecutionIntegrity.Complete -> []
    | TestExecutionIntegrity.Invalid issues -> issues

/// Whether a batch of test results represents a complete run.
[<RequireQualifiedAccess>]
type BatchCompletion =
  | Complete of requested: int * returned: int
  | Partial of requested: int * returned: int
  | Superseded

type TestResultsBatchPayload = {
  Generation: RunGeneration
  Freshness: ResultFreshness
  Completion: BatchCompletion
  Entries: TestStatusEntry array
  Summary: TestSummary
  LastDecision: LiveTestingDecision option
}

module TestResultsBatchPayload =
  let create
    (generation: RunGeneration)
    (freshness: ResultFreshness)
    (completion: BatchCompletion)
    (activation: LiveTestingActivation)
    (entries: TestStatusEntry array)
    (lastDecision: LiveTestingDecision option)
    : TestResultsBatchPayload =
    let summary =
      entries
      |> Array.map (fun e -> e.Status)
      |> TestSummary.fromStatuses activation
    { Generation = generation
      Freshness = freshness
      Completion = completion
      Entries = entries
      Summary = summary
      LastDecision = lastDecision }

  /// Derive completion from requested vs returned counts and freshness.
  let deriveCompletion (freshness: ResultFreshness) (requested: int) (returned: int) : BatchCompletion =
    match freshness with
    | StaleCodeEdited | StaleWrongGeneration -> BatchCompletion.Superseded
    | Fresh ->
      match returned >= requested with
      | true -> BatchCompletion.Complete(requested, returned)
      | false -> BatchCompletion.Partial(requested, returned)

  let isEmpty (p: TestResultsBatchPayload) =
    Array.isEmpty p.Entries
