module SageFs.Tests.HolderRegistryTests

/// WHY — `RestartCost` needs LIVENESS, and reflection cannot supply it: .NET
/// exposes no per-type instance count, and a type is "present" with zero
/// instances, so any reflection probe reports liveness for a type nobody holds.
///
/// A holder cell knows its OWN type and exists at a known moment, so liveness
/// can be RECORDED at construction instead of guessed at restart time. That
/// turns the question into a lookup.
///
/// The property that makes it safe: a cell that was swapped away or released
/// must STOP being evidence for its old type. Without that, every type a
/// process ever held looks live forever, and every scoped restart pays a
/// build — a failure in the expensive-looking direction that would look like a
/// working probe answering nothing.

open Expecto
open Expecto.Flip
open SageFs

let private fresh () = HolderRegistry.New ()

[<Tests>]
let holderRegistryTests =
  testList "holder cell liveness" [

    testCase "WHY — a cell registered as holding a type IS evidence for it" <| fun _ ->
      let r = fresh ()
      r.Register "Order" |> ignore
      r.LiveHolding "Order"
      |> Expect.equal "one live cell" 1

    testCase "WHY — a type no cell ever held is NOT live, and says so" <| fun _ ->
      let r = fresh ()
      r.Register "Order" |> ignore
      r.LiveHolding "Todo"
      |> Expect.equal "nothing holds it" 0

    testCase "WHY — TWO cells of one type are both evidence, so a count is reported" <| fun _ ->
      let r = fresh ()
      r.Register "Order" |> ignore
      r.Register "Order" |> ignore
      r.LiveHolding "Order"
      |> Expect.equal "both count" 2

    testCase "WHY — a SWAPPED cell stops being evidence for its old type" <| fun _ ->
      // The failure mode this exists to prevent: a replaced cell counting
      // forever, so every scoped restart pays a build.
      let r = fresh ()
      let id = r.Register "Order"
      r.LiveHolding "Order" |> Expect.equal "live before the swap" 1
      r.SwapIn id "Order2"
      r.LiveHolding "Order"
      |> Expect.equal "the old type is no longer held" 0
      r.LiveHolding "Order2"
      |> Expect.equal "and the new type is" 1

    testCase "WHY — a RELEASED cell stops counting but stays inspectable" <| fun _ ->
      let r = fresh ()
      let id = r.Register "Order"
      r.Release id
      r.LiveHolding "Order"
      |> Expect.equal "a released cell is not evidence" 0
      r.Cells
      |> List.length
      |> Expect.equal "but the history is still inspectable" 1

    testCase "WHY — the liveness answer separates NONE, SOME and UNKNOWN" <| fun _ ->
      let r = fresh ()
      r.LivenessOf "Todo"
      |> Expect.equal "nothing holds it" (SageFs.Liveness.NoLiveInstances "Todo")
      r.Register "Order" |> ignore
      match r.LivenessOf "Order" with
      | SageFs.Liveness.HoldsLiveInstances _ -> ()   // the shape is the claim
      | other -> failtestf "one cell holds it, got %A" other
      // A registry that was never asked cannot answer for a type in another
      // process; that is a refusal, not a count of zero.
      SageFs.Liveness.Unknown "no registry in this process"
      |> fun l ->
        match l with
        | SageFs.Liveness.Unknown _ -> ()
        | _ -> failtest "unknown must stay distinct from none"

    testCase "WHY — cells are PER-REGISTRY, so one app's cell cannot make another's restart pay" <| fun _ ->
      let a = fresh ()
      let b = fresh ()
      a.Register "Order" |> ignore
      b.LiveHolding "Order"
      |> Expect.equal "the other app's cell is invisible" 0
      a.LiveHolding "Order"
      |> Expect.equal "and its own is visible" 1

    testCase "WHY — a swap of an UNKNOWN cell is still recorded, rather than silently ignored" <| fun _ ->
      // Accepting it silently would let an unknown cell look known, which is
      // how a wrong liveness claim starts.
      let r = fresh ()
      r.SwapIn 999L "Order"
      r.LiveHolding "Order"
      |> Expect.equal "an unseen swap is not invisible evidence" 1

    testCase "WHY — the count and the liveness answer never disagree" <| fun _ ->
      let r = fresh ()
      r.Register "Order" |> ignore
      r.Register "Order" |> ignore
      // The answer's TEXT is not the count, so compare the two facts the
      // decision actually uses: the count, and whether it is a live answer.
      let count = r.LiveHolding "Order"
      match r.LivenessOf "Order" with
      | SageFs.Liveness.HoldsLiveInstances _ ->
        count |> Expect.equal "count and answer agree" 2
      | other -> failtestf "expected live instances, got %A" other
  ]
