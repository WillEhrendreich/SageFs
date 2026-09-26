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
