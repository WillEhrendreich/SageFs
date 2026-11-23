/// If one decision in the three type-level redesigns were wrong, would anything
/// notice? Each mutant breaks exactly one case. `detectsOutputMutant` passes
/// only when the real answer satisfies the predicate and the mutated one does
/// not, so a mutant that cannot be reached cannot produce a green result.
module TypeDesignMutationTests

open Expecto
open SageFs
open MutationTestingFramework

// ── SageFsError.HardResetRefused ────────────────────────────────────────────

let private refused = SageFsError.HardResetRefused RestartRefusal.AlreadyInProgress

let private refusalAsServerFailure : Mutant<SageFsError -> int> =
  { Name = "refusal_mapped_to_500"
    Description = "a refused duplicate rebuild is a conflict, not a server failure"
    Apply = fun real error ->
      match error with
      | SageFsError.HardResetRefused _ -> 500
      | other -> real other }

let private refusalAdvisesAnotherReset : Mutant<SageFsError -> string> =
  { Name = "refusal_advises_another_reset"
    Description = "a refusal must say to wait for the rebuild already running"
    Apply = fun real error ->
      match error with
      | SageFsError.HardResetRefused _ -> SageFsError.suggestedAction (SageFsError.HardResetFailed "already in progress")
      | other -> real other }

let refusalMutants =
  testList "mutants of HardResetRefused" [
    detectsOutputMutant refusalAsServerFailure refused SageFsError.toHttpStatus (fun status -> status = 409)
    detectsOutputMutant refusalAdvisesAnotherReset refused SageFsError.suggestedAction
      (fun advice -> advice.Contains "Wait for the rebuild" && not (advice.Contains "dotnet build"))
  ]

// ── WarmupReading at the source-state edge ──────────────────────────────────

let private project = "/w/App.fsproj"
let private failedFetch = SourceStateProbe.WarmupReading.ReadFailed "the dashboard fetch timed out"

let private reasonOf (state: SourceState) =
  match state with
  | SourceState.Unknown (UnknownReason.LoadTimeNotReported detail) -> detail
  | _ -> ""

let private failedFetchReadAsNoWorker : Mutant<SourceStateProbe.WarmupReading -> SourceState> =
  { Name = "failed_fetch_read_as_no_worker"
    Description = "a failed observation must not become a fact about the worker"
    Apply = fun real reading ->
      match reading with
      | SourceStateProbe.WarmupReading.ReadFailed _ -> real SourceStateProbe.WarmupReading.NotReported
      | other -> real other }

let readingMutants =
  testList "mutants of WarmupReading" [
    detectsOutputMutant failedFetchReadAsNoWorker failedFetch
      (SourceStateProbe.probe LastRebuild.NeverRebuilt [ project ])
      (fun state ->
        let detail = reasonOf state
        detail.Contains "could not be read" && not (detail.Contains "no worker to ask"))
  ]

// ── BuildPreflight.Ready as a proof ─────────────────────────────────────────

let private dllPath = "/w/obj/App.fsproj.nuget.g.props"
let private assetsPath = "/w/obj/project.assets.json"
let private built path = path = "/w/App.fsproj" || path = assetsPath || path = dllPath
let private noOutputs (_: OutputSearch) : string list = []
let private preflightWith findOutputs =
  BuildPreflight.checkUnder ProjectChooses built (fun _ -> "<Project />") findOutputs
    (SessionProjectTarget.Project "/w/App.fsproj")

let private inventedOutput : Mutant<(OutputSearch -> string list) -> BuildPreflight> =
  { Name = "preflight_invents_an_output"
    Description = "restore artifacts alone must not produce Ready"
    Apply = fun _ _ -> preflightWith (fun _ -> [ "/w/bin/Debug/net11.0/App.dll" ]) }

let preflightMutants =
  testList "mutants of BuildPreflight" [
    detectsOutputMutant inventedOutput noOutputs preflightWith
      (fun result ->
        match result with
        | BuildPreflight.NeedsRebuild _ -> true
        | _ -> false)
  ]

[<Tests>]
let typeDesignMutationTests =
  testList "Type design mutation tests" [ refusalMutants; readingMutants; preflightMutants ]
