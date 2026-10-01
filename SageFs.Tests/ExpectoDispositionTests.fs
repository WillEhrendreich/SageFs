/// The pure rule behind skipping pending and unfocused Expecto tests, and the fail-closed edge
/// around reading it. ExpectoPendingTests proves the rule against Expecto's own runner on
/// generated trees; this file pins every cell of the rule and the strings it reports.
module SageFs.Tests.ExpectoDispositionTests

open System.Threading
open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.Features.LiveTesting.BuiltInExecutors

let private allStates =
  [ ExpectoFocusState.Normal; ExpectoFocusState.Focused; ExpectoFocusState.Pending ]

let private allScopes =
  [ ExpectoFocusScope.NoFocusedTests; ExpectoFocusScope.SomeFocused ]

let private flat (state: ExpectoFocusState) (focus: ExpectoFocusScope) : ExpectoFlatFocus =
  { State = state; TreeFocus = focus }

[<Tests>]
let expectoDispositionTests =
  testList "Expecto disposition" [
    testCase "WHY — every state and scope has one disposition, and it is Expecto's rule" <| fun _ ->
      let expected =
        [ (ExpectoFocusState.Normal, ExpectoFocusScope.NoFocusedTests), ExpectoDisposition.RunIt
          (ExpectoFocusState.Normal, ExpectoFocusScope.SomeFocused), ExpectoDisposition.SkipIt ExpectoSkipCause.NotFocused
          (ExpectoFocusState.Focused, ExpectoFocusScope.NoFocusedTests), ExpectoDisposition.RunIt
          (ExpectoFocusState.Focused, ExpectoFocusScope.SomeFocused), ExpectoDisposition.RunIt
          (ExpectoFocusState.Pending, ExpectoFocusScope.NoFocusedTests), ExpectoDisposition.SkipIt ExpectoSkipCause.Pending
          (ExpectoFocusState.Pending, ExpectoFocusScope.SomeFocused), ExpectoDisposition.SkipIt ExpectoSkipCause.Pending ]
      let actual =
        [ for state in allStates do
            for scope in allScopes -> (state, scope), ExpectoDisposition.decide state scope ]
      actual |> List.sortBy fst |> Expect.equal "the whole table" (expected |> List.sortBy fst)

    testCase "WHY — a pending test never runs and a focused test always runs, whatever else is focused" <| fun _ ->
      for scope in allScopes do
        ExpectoDisposition.decide ExpectoFocusState.Pending scope
        |> Expect.equal "pending never runs" (ExpectoDisposition.SkipIt ExpectoSkipCause.Pending)
        ExpectoDisposition.decide ExpectoFocusState.Focused scope
        |> Expect.equal "focused always runs" ExpectoDisposition.RunIt

    testCase "WHY — the two skip reasons are the words the receipt shows, and they differ" <| fun _ ->
      ExpectoSkipCause.reason ExpectoSkipCause.Pending |> Expect.equal "pending reason" "pending (ptest)"
      ExpectoSkipCause.reason ExpectoSkipCause.NotFocused |> Expect.equal "not focused reason" "not focused"

    testCase "WHY — Expecto's three FocusState case names read back, and any other name is an Error that names it" <| fun _ ->
      ExpectoFocusState.tryParse "Normal" |> Expect.equal "Normal" (Result.Ok ExpectoFocusState.Normal)
      ExpectoFocusState.tryParse "Focused" |> Expect.equal "Focused" (Result.Ok ExpectoFocusState.Focused)
      ExpectoFocusState.tryParse "Pending" |> Expect.equal "Pending" (Result.Ok ExpectoFocusState.Pending)
      match ExpectoFocusState.tryParse "Skipped" with
      | Result.Error why ->
        why |> Expect.equal "the unknown name travels with the error" (ExpectoFocusUnreadable.UnknownStateCase "Skipped")
        ExpectoFocusUnreadable.describe why |> Expect.stringContains "and the description names it" "Skipped"
      | Result.Ok state -> failtestf "an unknown case name must not parse, got %A" state

    testCase "WHY — focus is on for the assembly when it is on for any test, and off for none" <| fun _ ->
      ExpectoFocusScope.ofFlats []
      |> Expect.equal "no tests, no focus" ExpectoFocusScope.NoFocusedTests
      ExpectoFocusScope.ofFlats [ flat ExpectoFocusState.Normal ExpectoFocusScope.NoFocusedTests; flat ExpectoFocusState.Pending ExpectoFocusScope.NoFocusedTests ]
      |> Expect.equal "nothing focused" ExpectoFocusScope.NoFocusedTests
      ExpectoFocusScope.ofFlats [ flat ExpectoFocusState.Normal ExpectoFocusScope.NoFocusedTests; flat ExpectoFocusState.Normal ExpectoFocusScope.SomeFocused ]
      |> Expect.equal "one focused tree turns focus on for all" ExpectoFocusScope.SomeFocused

    testAsync "WHY — a test whose Expecto state could not be read is not run and is not reported as passed" {
      let bodyRan = ref false
      let tree = testList "unreadable" [ test "t" { bodyRan.Value <- true } ]
      match ExpectoExecutor.tryBuildCacheFromExpecto typeof<Expecto.Test>.Assembly with
      | None -> failtest "the Expecto reflection cache could not be built"
      | Some cache ->
        let lookup = ExpectoExecutor.lookupFromBindings cache [ "Binding", (fun () -> box tree) ]
        let reflected = lookup |> Map.toList |> List.exactlyOne |> snd
        let unreadable = { reflected with Focus = Result.Error ExpectoFocusUnreadable.NoStateProperty }
        let! result = ExpectoExecutor.executeReflected cache unreadable CancellationToken.None
        result |> Expect.equal "it is reported as not run" TestResult.NotRun
        bodyRan.Value |> Expect.isFalse "the body must not run when its state is unknown"
    }
  ]
