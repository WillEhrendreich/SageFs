namespace SageFs.Simulation

open SageFs.Features.LiveTesting
open SageFs.Simulation.SymbolTestsRoutingSim

/// Named invariants over a `SymbolTestsRoutingSim.Trace`. Each check compares what the route
/// answered with the expectation the sim recorded from its own bookkeeping, so none of them calls
/// the code under test.
module SymbolTestsRoutingInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private firstViolation (t: Trace) (describe: AskDecision -> string option) : Outcome =
    t.Final.Decisions
    |> List.tryPick describe
    |> function
       | Some msg -> Outcome.Violated (sprintf "%s (seed=%d, ops=%A)" msg t.Scenario.Seed t.Scenario.Ops)
       | None -> Outcome.Holds

  let private idsOf (map: SymbolTestMap) : Set<TestId> =
    map.SymbolToTests |> Map.values |> Seq.collect id |> Set.ofSeq

  /// Whatever the answer lists belongs to the asked session: its symbols, its tests, nothing else.
  let theAnswerIsTheAskedSessionsOwn : Invariant =
    { Id = "the-answer-lists-only-the-asked-sessions-own-symbols-and-tests"
      Description =
        "Every symbol and every test in the answer for session S belongs to S. Which session is primary does not matter."
      Check = fun t ->
        firstViolation t (fun d ->
          let ownSymbols = symbolsOf d.Asked |> Set.ofArray
          let ownTests = expectedTests d.Asked |> Map.values |> Seq.collect id |> Set.ofSeq
          let foreignSymbols = d.Got.SymbolToTests |> Map.keys |> Set.ofSeq |> fun got -> Set.difference got ownSymbols
          let foreignTests = Set.difference (idsOf d.Got) ownTests
          match Set.isEmpty foreignSymbols && Set.isEmpty foreignTests with
          | true -> None
          | false ->
            Some (sprintf "asked #%d (primary #%d): the answer carries symbols %A and tests that are not session #%d's" d.Asked d.Primary (Set.toList foreignSymbols) d.Asked)) }

  /// A session whose file was analysed and whose tests ran has a map: both of its symbols, each
  /// tied to exactly the test that reaches it.
  let anAnalysedSessionIsNeverEmpty : Invariant =
    { Id = "a-session-that-analysed-and-ran-its-tests-is-never-answered-empty"
      Description =
        "When session S has type-checked its file and run its tests, the answer for S maps each of S's symbols to the tests that reach it, whoever is primary."
      Check = fun t ->
        firstViolation t (fun d ->
          match d.Progress with
          | Progress.Untouched
          | Progress.AnalysedOnly
          | Progress.RanOnly -> None
          | Progress.AnalysedAndRan ->
            let got = d.Got.SymbolToTests |> Map.map (fun _ ids -> Set.ofArray ids)
            match got = expectedTests d.Asked, d.Got.State with
            | true, SymbolTestsState.Mapped -> None
            | _ ->
              Some (sprintf "asked #%d (primary #%d) analysed and ran, expected %A, answered %A with state %A" d.Asked d.Primary (expectedTests d.Asked) got d.Got.State)) }

  /// An empty answer says why it is empty. Not analysed yet fills after the next type-check; analysed
  /// without coverage fills after a run.
  let anEmptyAnswerNamesItsReason : Invariant =
    { Id = "an-empty-answer-names-why-it-is-empty"
      Description =
        "Nothing analysed answers NothingAnalysed; analysed with no recorded coverage answers NoCoverageRecorded."
      Check = fun t ->
        firstViolation t (fun d ->
          let expected =
            match d.Progress with
            | Progress.Untouched
            | Progress.RanOnly -> Some SymbolTestsState.NothingAnalysed
            | Progress.AnalysedOnly -> Some SymbolTestsState.NoCoverageRecorded
            | Progress.AnalysedAndRan -> None
          match expected with
          | None -> None
          | Some state ->
            match d.Got.State = state && Map.isEmpty d.Got.SymbolToTests with
            | true -> None
            | false ->
              Some (sprintf "asked #%d (primary #%d) progress %A: expected an empty answer in state %A, got state %A with %d symbols" d.Asked d.Primary d.Progress state d.Got.State d.Got.SymbolToTests.Count)) }

  let all : Invariant list =
    [ theAnswerIsTheAskedSessionsOwn
      anAnalysedSessionIsNeverEmpty
      anEmptyAnswerNamesItsReason ]

  /// The invariants that failed for a trace, as (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some (inv.Id, msg))
