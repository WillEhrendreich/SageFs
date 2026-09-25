module SageFs.Tests.GranularRestartTests

/// WHY — `type-migration-direction.md` step 1, and the most expensive thing in
/// the product: a type change restarts the WHOLE app even when only one unit
/// held the old value.
///
/// `RestartPolicy.Decision` is `Restart of delay | GiveUp of SageFsError`. It
/// has no subject, so a consumer cannot tell "restart the worker" from
/// "restart the unit holding the old instance" — and `RestartPolicy.State` is a
/// single shared counter, so two subjects cannot be tracked independently.
///
/// The contract: the decision names its subject, the subject is a closed
/// parameterized DU, and a unit-scoped restart is a distinct representable
/// fact rather than a comment on a whole-worker restart.
open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.GranularRestart

let private worker = RestartSubject.Worker
let private unitScope name = RestartSubject.UnitScope name

[<Tests>]
let granularRestartTests = testList "granular restart" [

  testCase "WHY — a restart decision names the subject it decided about" <| fun _ ->
    let verdict, _ =
      GranularRestart.decide (unitScope "order-store") defaultPolicy emptyBudget (DateTime.UtcNow.AddSeconds -2.0)

    match verdict with
    | Verdict.Restart(RestartSubject.UnitScope name, _, _) ->
      name |> Expect.equal "the decision must carry the unit it restarted" "order-store"
    | other -> failtestf "expected a unit-scoped restart, got %A" other

  testCase "WHY — a worker restart and a unit restart are different facts, not the same value" <| fun _ ->
    let workerVerdict, _ = GranularRestart.decide worker defaultPolicy emptyBudget (DateTime.UtcNow.AddSeconds -2.0)
    let unitVerdict, _ = GranularRestart.decide (unitScope "order-store") defaultPolicy emptyBudget (DateTime.UtcNow.AddSeconds -2.0)

    (workerVerdict = unitVerdict)
    |> Expect.isFalse "a worker restart must not be indistinguishable from a unit restart"

  testCase "WHY — give-up also names its subject, so a consumer never has to guess" <| fun _ ->
    // Drive the real policy to give up: more restarts than MaxRestarts inside
    // the window, with no crash in the startup window.
    let now = DateTime.UtcNow
    let spent : Budget =
      { RestartCount = defaultPolicy.MaxRestarts
        LastRestartAt = Some(now.AddSeconds -1.0)
        WindowStart = Some(now.AddSeconds -30.0) }

    let verdict, _ = GranularRestart.decide (unitScope "order-store") defaultPolicy spent now

    match verdict with
    | Verdict.GiveUp(RestartSubject.UnitScope name, _) ->
      name |> Expect.equal "a give-up must still say which unit gave up" "order-store"
    | other -> failtestf "expected a give-up verdict, got %A" other

  testCase "WHY — the backoff and attempt count come from the real policy, not a copy" <| fun _ ->
    let verdict, _ = GranularRestart.decide worker defaultPolicy emptyBudget (DateTime.UtcNow.AddSeconds -2.0)

    match verdict with
    | Verdict.Restart(_, delay, attempt) ->
      delay
      |> Expect.equal "the delay must be the real policy's first backoff" defaultPolicy.BackoffBase

      attempt |> Expect.equal "the first restart is attempt 1" 1
    | other -> failtestf "expected a restart verdict, got %A" other

  testCase "WHY — the subject vocabulary is closed: a new restart kind must be added here" <| fun _ ->
    // `allSubjects` is the enumeration the DST harness folds through. If a
    // subject kind is added without a label, this fails rather than silently
    // producing an unnamed subject in a user-facing restart reason.
    allSubjects
    |> List.map describe
    |> Expect.equal "every subject must have a stable label" [ "worker"; "unit:order-store" ]

  testCase "WHY — one subject's exhausted budget must not spend another's" <| fun _ ->
    let now = DateTime.UtcNow

    // Burn the unit's budget to exhaustion.
    let spent : Budget =
      { RestartCount = defaultPolicy.MaxRestarts
        LastRestartAt = Some(now.AddSeconds -1.0)
        WindowStart = Some(now.AddSeconds -30.0) }

    let exhausted =
      [ "unit:order-store", spent ] |> Map.ofList

    // The worker is untouched, so it still restarts.
    let workerVerdict, afterWorker = GranularRestart.decideFor exhausted worker defaultPolicy now

    // The unit is still exhausted, and still gives up.
    let unitVerdict, afterUnit = GranularRestart.decideFor afterWorker (unitScope "order-store") defaultPolicy now

    match workerVerdict with
    | Verdict.Restart(RestartSubject.Worker, _, _) -> ()
    | other -> failtestf "the worker must still be restartable, got %A" other

    match unitVerdict with
    | Verdict.GiveUp(RestartSubject.UnitScope _, _) -> ()
    | other -> failtestf "the exhausted unit must still give up, got %A" other

    // And a give-up must not hand the unit a fresh budget.
    (budgetOf afterUnit (unitScope "order-store")).RestartCount
    |> Expect.equal "a give-up must not clear what the subject already spent" defaultPolicy.MaxRestarts
]
