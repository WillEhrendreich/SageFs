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
