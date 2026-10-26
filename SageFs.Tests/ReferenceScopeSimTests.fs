module SageFs.Tests.ReferenceScopeSimTests

open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Simulation
open SageFs.Simulation.ReferenceScopeSim

/// DST for the claim `list_references` makes about how far the compiler looked: "project-wide" only when every project
/// was checked, a partial scope that names what it skipped, and one budget for the whole session. See
/// `SageFs.Simulation/ReferenceScopeSim.fs` for the fold, the independent truth it is checked against, and the twins.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match ReferenceScopeInvariants.violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Budget=%g\n  Projects=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Budget t.Scenario.Projects vs

let private violatedIds (t: Trace) : string list = ReferenceScopeInvariants.violations t |> List.map fst

let private isProjectWide (t: Trace) : bool =
  match t.Reply with
  | ReferenceReply.Counted(CountedScope.ProjectWide _, _, _) -> true
  | ReferenceReply.Counted(CountedScope.SomeProjects _, _, _)
  | ReferenceReply.NotCounted _ -> false

[<Tests>]
let tests =
  testList "DST reference scope" [

    testList "the real gather over the real scope rule holds the invariants" [
      testPropertyWithConfig simConfig
        "seeded sessions — a reply is project-wide only when every project was reached, its sites are those of the reached projects, and the run fits the budget"
        <| fun (seed: int) -> assertHolds (run Policy.Real (ReferenceScopeGenerators.fromSeed seed))

      testCase "bothFit: two projects inside one budget are project-wide" <| fun _ ->
        let t = run Policy.Real ReferenceScopeGenerators.bothFit
        isProjectWide t |> Expect.isTrue "every project was reached"
        assertHolds t

      testCase "secondDoesNotFit: the first fills the budget, so the reply is partial" <| fun _ ->
        let t = run Policy.Real ReferenceScopeGenerators.secondDoesNotFit
        isProjectWide t |> Expect.isFalse "Host was never reached"
        t.ScopeLine |> Expect.stringContains "names the project it skipped" "Host"
        assertHolds t

      testCase "pairExceedsBudget: each project fits alone and the three do not, so the reply is partial" <| fun _ ->
        let t = run Policy.Real ReferenceScopeGenerators.pairExceedsBudget
        isProjectWide t |> Expect.isFalse "the budget is shared"
        assertHolds t

      testCase "nothingFits: no project reached means no count" <| fun _ ->
        let t = run Policy.Real ReferenceScopeGenerators.nothingFits
        match t.Reply with
        | ReferenceReply.NotCounted _ -> ()
        | other -> failtestf "expected no count, got %A" other
        assertHolds t

      testCase "firstRaises: a failing compiler on one project is named and the other is still counted, as partial" <| fun _ ->
        let t = run Policy.Real ReferenceScopeGenerators.firstRaises
        isProjectWide t |> Expect.isFalse "Core was not checked"
        t.ScopeLine |> Expect.stringContains "says the compiler failed" "the compiler failed"
        assertHolds t

      testCase "bare: a session with no project is unavailable" <| fun _ ->
        let t = run Policy.Real ReferenceScopeGenerators.bare
        t.ScopeLine |> Expect.stringContains "says there is no project" "no project"
        assertHolds t
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED — the labels-partial-as-project-wide twin reports a file-scoped count as project-wide" <| fun _ ->
        let t = run Policy.LabelsPartialAsProjectWide ReferenceScopeGenerators.secondDoesNotFit
        isProjectWide t |> Expect.isTrue "the twin's mistake: it claims the whole session"
        violatedIds t |> Expect.contains "the false scope must be caught" "scope-names-what-was-reached"
        violatedIds t |> Expect.contains "and by the line a reader sees" "the-scope-line-leads-and-agrees"

      testCase "REPRODUCED — the budget-per-project twin runs past the one budget" <| fun _ ->
        let t = run Policy.BudgetPerProject ReferenceScopeGenerators.pairExceedsBudget
        violatedIds t |> Expect.contains "an unbounded question must be caught" "the-whole-question-fits-the-budget"

      testCase "REPRODUCED — the forgets-what-it-skipped twin hides the project it did not reach" <| fun _ ->
        let t = run Policy.ForgetsWhatItSkipped ReferenceScopeGenerators.secondDoesNotFit
        violatedIds t |> Expect.contains "a partial scope that names nothing must be caught" "scope-names-what-was-reached"

      testCase "the invariants have teeth: some seeded session violates them under each twin" <| fun _ ->
        let seeds = [ 1 .. 200 ]
        let anyViolates policy =
          seeds |> List.exists (fun seed -> ReferenceScopeInvariants.violations (run policy (ReferenceScopeGenerators.fromSeed seed)) |> List.isEmpty |> not)
        anyViolates Policy.LabelsPartialAsProjectWide |> Expect.isTrue "calling a partial look project-wide is caught by a generated session"
        anyViolates Policy.BudgetPerProject |> Expect.isTrue "a budget per project is caught by a generated session"
        anyViolates Policy.ForgetsWhatItSkipped |> Expect.isTrue "forgetting the skipped projects is caught by a generated session"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical reply and scope line (real gather)" <| fun (seed: int) ->
        let a = run Policy.Real (ReferenceScopeGenerators.fromSeed seed)
        let b = run Policy.Real (ReferenceScopeGenerators.fromSeed seed)
        a.Reply = b.Reply && a.ScopeLine = b.ScopeLine && a.Elapsed = b.Elapsed
    ]
  ]
