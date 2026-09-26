module SageFs.Tests.RegisteredHolderTests

/// WHY — `Holder.hold` is pure: it makes a cell and tells nobody. Right for a
/// value type, WRONG for a restart boundary, because a cell in a running app
/// is then invisible to the restart that would need to know about it. A
/// registry nobody calls is a registry that is always empty, and an always-
/// empty registry is an `Unconsulted` that pays a build forever.
///
/// `holdRegistered` is the second way to hold a value, and the difference is
/// the whole point. It is NOT a default parameter on `hold`: a pure `hold` that
/// silently mutated global state could not be opted out of, nor tested alone.
///
/// THESE ASSERT THE ANSWER'S INTENT, NEVER A COUNT. A count says how much; a
/// case says what was found AND what it means, and "asked and found nothing" is
/// a different claim from "never asked" — which a count cannot express. An
/// earlier version of this file asserted `Some 0` / `Some 1` and so could not
/// have told those two apart.

open Expecto
open Expecto.Flip
open SageFs

/// The answer CLAIMS something holds the old shape.
let private holdsSomething answer =
  match answer with
  | LiveCount.HeldBy _ -> true
  | LiveCount.HeldByNothing
  | LiveCount.Unconsulted _
  | LiveCount.SourceFailed _ -> false

/// The answer is a CLAIM that nothing holds it — evidence, not absence.
let private claimsNothing answer =
  match answer with
  | LiveCount.HeldByNothing -> true
  | _ -> false

/// The answer refuses to say anything at all.
let private refuses answer =
  match answer with
  | LiveCount.Unconsulted _
  | LiveCount.SourceFailed _ -> true
  | _ -> false

/// Whether the answer lets the restart skip the build.
let private restartsForFree answer =
  RestartCost.rebuilds (RestartCost.decideFromLiveCount answer) |> not

[<Tests>]
let registeredHolderTests =
  testList "a holder a running app can create and SageFs can find" [

    testCase "WHY — a registered holder is immediately visible, and VISIBILITY MEANS REBUILD" <| fun _ ->
      let r = HolderRegistry.New ()
      let held = RegisteredHolder.holdRegistered r "Order" 42
      let answer = RegisteredHolder.liveCountOf (Some r) "Order"
      // Being VISIBLE is the win — the restart now knows a live old-layout
      // value exists, which it could never know before. And because one does
      // exist, a rebuild is required. Verified in a live SageFs session, not
      // assumed: `LiveCountOf "Order"` is `HeldBy` and `rebuilds` is true.
      //
      // An earlier version of this line asserted the OPPOSITE ("so the build is
      // not needed") and the gate caught it. Visibility and cheapness are
      // different claims: this one is evidence, and evidence of a live holder
      // is precisely what makes the build necessary.
      holdsSomething answer |> Expect.isTrue "the restart can see the holder"
      restartsForFree answer |> Expect.isFalse "so a live holder still forces the build"
      Holder.read (RegisteredHolder.cellOf held) |> Expect.equal "and the value is intact" 42

    testCase "WHY — a PURE hold is invisible, which is the whole reason this exists" <| fun _ ->
      let r = HolderRegistry.New ()
      let cell = Holder.hold 42
      Holder.read cell |> Expect.equal "the pure cell works" 42
      // A registry that WAS asked still finds nothing: the pure cell told
      // nobody, so nothing is evidence for the old shape.
      claimsNothing (RegisteredHolder.liveCountOf (Some r) "Order")
      |> Expect.isTrue "so it is free to skip the build"

    testCase "WHY — NO registry REFUSES, which is a DIFFERENT claim from 'nothing holds it'" <| fun _ ->
      // The distinction the whole cost decision rests on. "No registry here" has
      // told us nothing about whether anything holds the old shape — and a
      // count could not have said so, because both would have been zero.
      let neverAsked = RegisteredHolder.liveCountOf None "Order"
      refuses neverAsked |> Expect.isTrue "it refuses rather than answering"
      claimsNothing neverAsked
      |> Expect.isFalse "and it is NOT the same claim as an empty registry"
      restartsForFree neverAsked |> Expect.isFalse "so it pays the build"

    testCase "WHY — a swapped holder stops being evidence for its old type, so a scoped restart becomes cheap" <| fun _ ->
      let r = HolderRegistry.New ()
      let held = RegisteredHolder.holdRegistered r "Order" 42
      holdsSomething (RegisteredHolder.liveCountOf (Some r) "Order")
      |> Expect.isTrue "live before the swap"
      let _swapped = RegisteredHolder.swapIn r held "Order2"
      let after = RegisteredHolder.liveCountOf (Some r) "Order"
      claimsNothing after |> Expect.isTrue "the old type is free"
      restartsForFree after |> Expect.isTrue "so the build is not needed"

    testCase "WHY — a RELEASED holder stops being evidence but its value is still readable" <| fun _ ->
      let r = HolderRegistry.New ()
      let held = RegisteredHolder.holdRegistered r "Order" 42
      RegisteredHolder.release r held
      claimsNothing (RegisteredHolder.liveCountOf (Some r) "Order")
      |> Expect.isTrue "released is not evidence"
      Holder.read (RegisteredHolder.cellOf held)
      |> Expect.equal "and the value is intact" 42

    testCase "WHY — the cell and its id travel together, so a later swap can find it" <| fun _ ->
      let r = HolderRegistry.New ()
      let held = RegisteredHolder.holdRegistered r "Order" 42
      // `Held` has exactly ONE case, so there is no other shape to handle.
      let cell = RegisteredHolder.cellOf held
      let id =
        match held with
        | Held.Held(_, id) -> id
      (Holder.read cell, id) |> Expect.equal "the caller gets both" (42, 1L)

    testCase "WHY — two holders of one type are both evidence, and each is separately addressable" <| fun _ ->
      let r = HolderRegistry.New ()
      let first = RegisteredHolder.holdRegistered r "Order" 1
      let second = RegisteredHolder.holdRegistered r "Order" 2
      holdsSomething (RegisteredHolder.liveCountOf (Some r) "Order")
      |> Expect.isTrue "both are evidence"
      let idOf h =
        match h with
        | Held.Held(_, id) -> id
      (idOf first <> idOf second)
      |> Expect.isTrue "each holder has its own id"

    testCase "WHY — holders are PER-REGISTRY, so one app's cell cannot make another's restart pay" <| fun _ ->
      let a = HolderRegistry.New ()
      let b = HolderRegistry.New ()
      RegisteredHolder.holdRegistered a "Order" 1 |> ignore
      claimsNothing (RegisteredHolder.liveCountOf (Some b) "Order")
      |> Expect.isTrue "the other app sees no evidence"
      holdsSomething (RegisteredHolder.liveCountOf (Some a) "Order")
      |> Expect.isTrue "and its own is visible"
  ]
