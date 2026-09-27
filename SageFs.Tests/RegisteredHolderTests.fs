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

/// WHY — the app and the worker run in the SAME process, and each used to build
/// its own `HolderRegistry`. They are therefore two unrelated objects, so the
/// worker's `LiveCountOf` read a registry the app never wrote to and answered 0
/// for every type — producing a cheap `RespawnOnly` on the strength of nothing.
///
/// Every test above passed while that was true, because every one of them
/// registers into and reads back from the SAME instance. These are the tests
/// that would have caught it: they go through the ambient handle exactly as app
/// code does, and they are the only ones that can see the two-registry failure
/// at all.
///
/// THEY SHARE A MUTABLE STATIC, so they run in ONE list deliberately. Split
/// across two, Expecto's parallelism interleaves them and they destroy each
/// other's state — which is exactly what happened: one failed in the full run,
/// then after a fix that reset the handle unconditionally, BOTH failed, because
/// each one's reset landed between the other's setup and its assertion. The
/// lesson is not "reset harder", it is that a test which mutates process-wide
/// state must not share a process with one that reads it.
[<Tests>]
let currentRegistryTests =
  testList "the registry the restart actually reads" [

    // Expecto runs cases inside a list in PARALLEL, and these share a
    // process-wide mutable static. Symptom, in order: one failed in the full
    // run; after a fix that reset the handle unconditionally, BOTH failed,
    // because each reset landed between the other's setup and its assertion.
    // The lesson is not "reset harder" — it is that a test mutating
    // process-wide state must be SERIALISED against one that reads it, and a
    // lock is the only thing that actually serialises.
    let currentRegistryLock = obj ()
    let withCurrent (f: unit -> unit) =
      lock currentRegistryLock (fun () ->
        HolderRegistry.Current <- None
        try f () finally HolderRegistry.Current <- None)

    testCase "WHY — a value held through the ambient handle is visible to whoever reads Current" <| fun _ ->
      // The shape of the real bug, written down as an assertion: register
      // through the handle a RESTART reads, and read back through the SAME
      // handle a restart would use.
      withCurrent (fun () ->
        HolderRegistry.Current <- Some(HolderRegistry.New ())
        let held =
          match RegisteredHolder.holdInCurrent "Order" 1 with
          | RegisteredHolder.HeldOrUnseen.Visible h -> h
          | RegisteredHolder.HeldOrUnseen.NoRegistryPublished why ->
            failtestf "the worker published a registry, so this must succeed: %s" why
        (Holder.read (RegisteredHolder.cellOf held))
        |> Expect.equal "the value is the one that was held" 1
        // AND the registry the restart reads now knows about it. `Current` is
        // already the `option` `liveCountOf` takes, and it answers with a
        // `LiveCount` — so the assertion is on the CASE, which is the claim.
        // Asserting a count here would be asserting the wrong fact: `HeldBy` is
        // the claim, and it is the one the restart acts on.
        (RegisteredHolder.liveCountOf HolderRegistry.Current "Order")
        |> function
        | SageFs.LiveCount.HeldBy _ -> ()
        | other -> failtestf "a value held through the handle must be evidence, got %A" other)

    testCase "WHY — with NO published registry, holding REFUSES rather than creating an invisible one" <| fun _ ->
      // The honest answer when the app is not under SageFs. Silently falling
      // back to a private registry is the defect: the value would be held and
      // no restart could ever see it.
      //
      // `Current` is a mutable STATIC, and this is the THIRD time it has cost
      // a test: it passed in isolation and failed in the full run, because some
      // other test in the same process had published a handle. So the reset is
      // now unconditional and happens BEFORE the match, and the surrounding
      // tests restore it in a `finally` — ambient state plus a shared test
      // process is a bad pair, and the cost of learning that is one failure per
      // occurrence unless the reset is not conditional on anything.
      HolderRegistry.Current <- None
      withCurrent (fun () ->
        match RegisteredHolder.holdInCurrent "Order" 1 with
        | RegisteredHolder.HeldOrUnseen.Visible _ ->
          failtest "an unpublished registry must not accept a value nothing can see"
        | RegisteredHolder.HeldOrUnseen.NoRegistryPublished why ->
          (why.Length > 0) |> Expect.isTrue "and it must say why")

    testCase "WHY — publishing a registry does NOT make two apps share cells: the explicit-registry form is still isolated" <| fun _ ->
      // The property that must survive the fix. `Current` is one ambient handle,
      // not a global switch that dissolves the per-registry isolation the whole
      // design rests on.
      let mine = HolderRegistry.New ()
      let theirs = HolderRegistry.New ()
      RegisteredHolder.holdRegistered theirs "Order" 1 |> ignore
      (mine.LiveHolding "Order")
      |> Expect.equal "an explicitly-passed registry still sees nothing of another's" 0
      (theirs.LiveHolding "Order")
      |> Expect.equal "and its own" 1
  ]
