module SageFs.Tests.RestartBoundariesTests

/// WHY — `RestartScope.UnitScope` is computed, carried through the run state
/// and logged, but had nothing to ACT on: SageFs launches a user's app as a
/// PROCESS, so there is no in-process per-unit object to restart. This is the
/// opt-in that gives it one without asking anyone to change their source.
///
/// The three properties below are the whole design, and each is a way a naive
/// version strands a live value laid out by the old type:
///   - boundaries are PER SESSION, so one app cannot narrow another's restart
///   - zero or several declaring a type is a REFUSAL, never a guess
///   - a repeated identical declaration is fine; a conflicting one is refused

open Expecto
open Expecto.Flip
open SageFs

/// A real session id — `SessionId` has a private constructor, so a boundary
/// registry is keyed by one SageFs actually issued.
let private fresh () = SageFs.Registry.For (SageFs.WorkerProtocol.SessionId.newId ())

[<Tests>]
let restartBoundariesTests =
  testList "restart boundaries" [

    testCase "WHY — a declared type scopes to its boundary" <| fun _ ->
      let r = fresh ()
      r.Declare "order-store" "Order" |> ignore
      r.RestartScopeFor "Order"
      |> Expect.equal "scoped to the declared boundary" (Ok "order-store")

    testCase "WHY — an UNDECLARED type is refused, so the restart stays app-wide" <| fun _ ->
      fresh ()
      |> fun r -> r.RestartScopeFor "Todo"
      |> Expect.equal "nothing claims it" (Error SageFs.Unscoped.Undeclared)

    testCase "WHY — SEVERAL boundaries claiming a type is refused, never resolved by picking one" <| fun _ ->
      let r = fresh ()
      r.Declare "store-a" "Order" |> ignore
      r.Declare "store-b" "Order" |> ignore
      r.RestartScopeFor "Order"
      |> Expect.equal "ambiguous, and named" (Error(SageFs.Unscoped.Ambiguous [ "store-a"; "store-b" ]))

    testCase "WHY — boundaries are PER SESSION: one app cannot narrow another's restart" <| fun _ ->
      let a = SageFs.Registry.For (SageFs.WorkerProtocol.SessionId.newId ())
      let b = SageFs.Registry.For (SageFs.WorkerProtocol.SessionId.newId ())
      a.Declare "order-store" "Order" |> ignore
      b.Declare "cart" "Cart" |> ignore
      a.TryRestartScopeFor "Cart"
      |> Expect.equal "app-b's boundary does not leak into app-a" None
      a.TryRestartScopeFor "Order"
      |> Expect.equal "app-a's own boundary resolves" (Some "order-store")

    testCase "WHY — repeating an IDENTICAL declaration is accepted, because startup can run twice" <| fun _ ->
      let r = fresh ()
      r.Declare "order-store" "Order" |> ignore
      match r.Declare "order-store" "Order" with
      | SageFs.Declared.Accepted b -> b.Id |> Expect.equal "the same boundary" "order-store"
      | SageFs.Declared.Conflicted c -> failtestf "a repeat must not conflict: %A" c
    testCase "WHY — ONE id claiming TWO types is refused, because the scope would be a coin flip" <| fun _ ->
      let r = fresh ()
      r.Declare "store" "Order" |> ignore
      match r.Declare "store" "Cart" with
      | SageFs.Declared.Conflicted c ->
        c.AlreadyHolds |> Expect.equal "the first claim stands" "Order"
        c.TriedToHold |> Expect.equal "the second is reported" "Cart"
      | SageFs.Declared.Accepted _ -> failtest "two types must not share one boundary"

    testCase "WHY — a declaration is INSPECTABLE, so an inert opt-in is not invisible" <| fun _ ->
      let r = fresh ()
      r.Declare "order-store" "Order" |> ignore
      r.Declared
      |> List.length
      |> Expect.equal "the user can see what they declared" 1
      r.Declared
      |> List.head
      |> r.Describe
      |> fun d ->
        (d.Contains "order-store") |> Expect.isTrue "it names the boundary"
        (d.Contains "Order") |> Expect.isTrue "it names the type"

    testCase "WHY — the empty registry refuses everything, and says Undeclared rather than guessing" <| fun _ ->
      fresh ()
      |> fun r -> r.RestartScopeFor "Anything"
      |> Expect.equal "no declarations, no scope" (Error SageFs.Unscoped.Undeclared)

    testCase "WHY — the option form and the detailed form never disagree" <| fun _ ->
      let r = fresh ()
      r.Declare "store" "Order" |> ignore
      r.Declare "store2" "Cart" |> ignore
      r.Declare "store3" "Cart" |> ignore
      (r.TryRestartScopeFor "Cart", r.RestartScopeFor "Cart")
      |> Expect.equal "ambiguous is None AND Ambiguous" (None, Error(SageFs.Unscoped.Ambiguous [ "store2"; "store3" ]))
    ]

/// WHY — a boundary is the one place that ALREADY holds the value, so it is
/// the only place a migration can happen without the liveness registry taking a
/// strong reference to live state. A registry that holds a value reports
/// `HeldBy` for an object nothing else uses, which is the same lie as a
/// reflection probe: live because we are holding it, not because anything wants
/// it.
///
/// So `Migrate` lives on the boundary. These pin the three claims that make it
/// safe, and the negative controls that would fail against a stub.
[<Tests>]
let boundaryMigrationHookTests =
  testList "a boundary's migration hook" [

    testCase "WHY — a boundary WITH a hook is consulted, and its verdict is the one the decision sees" <| fun _ ->
      let r = fresh ()
      r.DeclareWithMigration
        "order-store"
        "Order"
        (Some(fun _value _newType -> SageFs.MigrationWorth.WorthCarrying 3))
      |> ignore
      match r.MigrationVerdictFor "Order" (box 1) typeof<int> with
      | Some v -> v |> Expect.equal "the hook's own answer travels" (SageFs.MigrationWorth.WorthCarrying 3)
      | None -> failtest "a declared hook must be consulted"

    testCase "WHY — a boundary with NO hook answers 'I said nothing', which is NOT 'nothing is live'" <| fun _ ->
      // The distinction the whole hook rests on. `None` here means a boundary's
      // silence; a caller that read it as `HeldByNothing` would skip a build
      // with no evidence — the exact defect `LiveCount` was introduced to end.
      let r = fresh ()
      r.Declare "order-store" "Order" |> ignore
      (r.MigrationVerdictFor "Order" (box 1) typeof<int>)
      |> Expect.equal "a silent boundary is not a migration verdict" None
      // And the liveness answer is unaffected by the hook's absence.
      r.RestartScopeFor "Order"
      |> Expect.equal "the boundary still scopes the restart" (Ok "order-store")

    testCase "WHY — a REPEAT registration must not silently drop a hook, or an accepted repeat is a downgrade" <| fun _ ->
      let r = fresh ()
      r.DeclareWithMigration
        "order-store"
        "Order"
        (Some(fun _value _newType -> SageFs.MigrationWorth.WorthCarrying 3))
      |> ignore
      // The same claim again, with NO hook — the ordinary `Declare` a
      // second startup path would call.
      r.Declare "order-store" "Order" |> ignore
      match r.MigrationVerdictFor "Order" (box 1) typeof<int> with
      | Some v -> v |> Expect.equal "the hook survives the repeat" (SageFs.MigrationWorth.WorthCarrying 3)
      | None -> failtest "a repeat must not clear a hook it did not supply"

    testCase "WHY — a hook on an UNDECLARED type is not reachable, because there is no boundary to ask" <| fun _ ->
      let r = fresh ()
      r.DeclareWithMigration
        "order-store"
        "Order"
        (Some(fun _ _ -> SageFs.MigrationWorth.WorthCarrying 1))
      |> ignore
      (r.MigrationVerdictFor "Todo" (box 1) typeof<int>)
      |> Expect.equal "a type no boundary holds has no hook to consult" None

    testCase "WHY — a REFUSING hook is believed, because a boundary that says 'cannot' is evidence the way liveness is" <| fun _ ->
      let r = fresh ()
      r.DeclareWithMigration
        "order-store"
        "Order"
        (Some(fun _ _ -> SageFs.MigrationWorth.NotWorthCarrying "'Payload': no carry rule"))
      |> ignore
      // The refusal must reach the decision, not be swallowed into a None.
      match r.MigrationVerdictFor "Order" (box 1) typeof<int> with
      | Some v -> v |> Expect.equal "the refusal travels intact" (SageFs.MigrationWorth.NotWorthCarrying "'Payload': no carry rule")
      | None -> failtest "a refusal must NOT be reported as silence"
  ]

/// WHY — an app declares a boundary so a type change can be scoped to it, and
/// until now it could NOT: `boundaryRegistry` was a local `let` inside the
/// worker's `run`, so any declaration an app made was invisible and every type
/// change fell back to the module-inferred scope.
///
/// The refusal matters as much as the acceptance. Silently accepting a
/// declaration no restart can see would hand a user a scoped-restart guarantee
/// that does not exist — worse than refusing, because they would then trust a
/// restart that stays whole-app.
[<Tests>]
let appDeclarationTests =
  testList "an app declaring a boundary the restart can see" [

    // `Registry.Current` is a mutable STATIC and Expecto runs cases in a list
    // in PARALLEL — the same lesson `RegisteredHolderTests` had to learn for
    // `HolderRegistry.Current`. Publish, assert, and restore under a lock, or
    // one test's reset lands between another's setup and its assertion.
    let lockObj = obj ()
    let withRegistry (f: SageFs.Registry -> unit) =
      let r = SageFs.Registry.For (SageFs.WorkerProtocol.SessionId.newId ())
      lock lockObj (fun () ->
        SageFs.Registry.Current <- Some r
        try f r finally SageFs.Registry.Current <- None)

    testCase "WHY — with a published registry, an app's declaration IS the one the restart reads" <| fun _ ->
      // The bug being fixed, written as an assertion: declare through the app's
      // entry point, read back through the worker's accessor. Before, these were
      // two objects and this read nothing.
      withRegistry (fun published ->
        match SageFs.Declare.inCurrent "order-store" "Order" None with
        | SageFs.Declaration.DeclaredBoundary _ -> ()
        | other -> failtestf "a fresh declaration must be accepted, got %A" other
        published.RestartScopeFor "Order"
        |> Expect.equal "and the restart scopes to it" (Ok "order-store"))

    testCase "WHY — with NOTHING published, declaring REFUSES rather than creating an invisible boundary" <| fun _ ->
      lock lockObj (fun () ->
        SageFs.Registry.Current <- None
        match SageFs.Declare.inCurrent "order-store" "Order" None with
        | SageFs.Declaration.DeclaredBoundary _ -> failtest "a declaration nothing can read must not be accepted"
        | SageFs.Declaration.Unpublished why ->
          (why.Contains "invisible") |> Expect.isTrue "and it must say the boundary would be invisible"
        | SageFs.Declaration.ConflictedWith _ -> failtest "a conflict is a different fact")

    testCase "WHY — a CONFLICT is a different refusal from UNPUBLISHED, because the user's action differs" <| fun _ ->
      // Same app, one worker, declared twice for different types: the first
      // claim stands. That is a CONFLICT, and folding it into "unpublished"
      // would tell a user running under SageFs that they are not.
      withRegistry (fun _ ->
        SageFs.Declare.inCurrent "store" "Order" None |> ignore
        match SageFs.Declare.inCurrent "store" "Cart" None with
        | SageFs.Declaration.ConflictedWith c ->
          c.AlreadyHolds |> Expect.equal "the first claim stands" "Order"
          c.TriedToHold |> Expect.equal "the second is reported" "Cart"
        | other -> failtestf "expected a conflict, got %A" other)

    testCase "WHY — a declaration carrying a MIGRATE hook keeps it, so an app can opt into a carry" <| fun _ ->
      // The hook is what would make a real migration reachable; a declaration
      // that silently dropped it would leave the feature inert while looking
      // configured.
      withRegistry (fun _ ->
        let hook = Some(fun _ _ -> SageFs.MigrationWorth.WorthCarrying 2)
        match SageFs.Declare.inCurrent "order-store" "Order" hook with
        | SageFs.Declaration.DeclaredBoundary b ->
          (b.Migrate.IsSome) |> Expect.isTrue "the hook survives the declaration"
        | other -> failtestf "expected a declaration, got %A" other)
  ]
