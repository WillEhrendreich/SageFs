module SageFs.Tests.CohortBoundedViewTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features

// The dashboard cohort panel and `get_cohort_status` listed EVERY member and
// claim in the frame (43 orphaned claims, hours old, in the live daemon), so
// the panel grew without bound. `CohortBoundedView` is the pure decision of
// WHAT to show under a row cap and how many rows were hidden — never a silent
// truncation. Shown rows are chosen most-actionable-first (a Held claim is
// never hidden while an Orphaned one is shown).

let private check (what: string) (holds: bool) =
  if not holds then failwithf "violated: %s" what

[<Tests>]
let boundedViewTests =
  testList "CohortBoundedView — bounded rows with an honest overflow indicator" [

    test "the live scene: 2 held + 43 orphaned under the cap shows every Held claim and reports 35 hidden orphans" {
      let classes =
        Array.append (Array.create 2 CohortBoundedView.ClaimClass.HeldClaim) (Array.create 43 CohortBoundedView.ClaimClass.OrphanedClaim)
      let view = CohortBoundedView.bound 10 (fun i -> classes.[i]) classes.Length
      view.Shown.Length |> Expect.equal "exactly the cap is shown" 10
      view.Shown |> Array.take 2 |> Expect.equal "both Held claims are shown first" [| 0; 1 |]
      view.HiddenTotal |> Expect.equal "35 rows are hidden, and the view says so" 35
      view.HiddenByClass |> Map.find CohortBoundedView.ClaimClass.OrphanedClaim
      |> Expect.equal "all of the hidden rows are orphaned" 35
      CohortBoundedView.claimOverflowLabel view
      |> Expect.equal "the indicator names the count and the class" "+35 more claims (35 orphaned)"
    }

    test "under the cap nothing is hidden and no indicator is drawn" {
      let view = CohortBoundedView.bound 10 (fun _ -> CohortBoundedView.ClaimClass.HeldClaim) 3
      view.Shown |> Expect.equal "every row shown" [| 0; 1; 2 |]
      view.HiddenTotal |> Expect.equal "nothing hidden" 0
      CohortBoundedView.claimOverflowLabel view |> Expect.equal "no label when nothing is hidden" ""
    }

    test "TWIN WITH TEETH: silent first-N truncation hides a Held claim behind orphans and reports nothing" {
      let classes =
        Array.append (Array.create 43 CohortBoundedView.ClaimClass.OrphanedClaim) (Array.create 2 CohortBoundedView.ClaimClass.HeldClaim)
      let twinShown = CohortBoundedView.truncateTwin 10 classes.Length
      twinShown |> Array.exists (fun i -> classes.[i] = CohortBoundedView.ClaimClass.HeldClaim)
      |> Expect.isFalse "the old/naive behavior hides the two live claims behind 43 stale ones"
      let real = CohortBoundedView.bound 10 (fun i -> classes.[i]) classes.Length
      real.Shown |> Array.filter (fun i -> classes.[i] = CohortBoundedView.ClaimClass.HeldClaim) |> Array.length
      |> Expect.equal "the real view always surfaces the live claims" 2
    }

    testProperty "bound: exactly min(cap, n) distinct in-range rows; hidden accounts for the rest" <|
      fun (cap: PositiveInt) (raw: byte list) ->
        let classes = raw |> List.map (fun b -> if b % 3uy = 0uy then CohortBoundedView.ClaimClass.HeldClaim elif b % 3uy = 1uy then CohortBoundedView.ClaimClass.OrphanedClaim else CohortBoundedView.ClaimClass.ReleasedClaim) |> Array.ofList
        let n = classes.Length
        let view = CohortBoundedView.bound cap.Get (fun i -> classes.[i]) n
        let hiddenSum = view.HiddenByClass |> Map.toList |> List.sumBy snd
        check "shown count is min(cap, n)" (view.Shown.Length = min cap.Get n)
        check "shown rows are distinct" ((Array.distinct view.Shown).Length = view.Shown.Length)
        check "shown rows are in range" (view.Shown |> Array.forall (fun i -> i >= 0 && i < n))
        check "hidden total accounts for the rest" (view.HiddenTotal = n - view.Shown.Length)
        check "per-class tally sums to the hidden total" (hiddenSum = view.HiddenTotal)
        true

    testProperty "bound: no hidden row outranks a shown row (Held before Orphaned before Released)" <|
      fun (cap: PositiveInt) (raw: byte list) ->
        let classes = raw |> List.map (fun b -> if b % 3uy = 0uy then CohortBoundedView.ClaimClass.HeldClaim elif b % 3uy = 1uy then CohortBoundedView.ClaimClass.OrphanedClaim else CohortBoundedView.ClaimClass.ReleasedClaim) |> Array.ofList
        let view = CohortBoundedView.bound cap.Get (fun i -> classes.[i]) classes.Length
        let shown = Set.ofArray view.Shown
        let hidden = [ 0 .. classes.Length - 1 ] |> List.filter (fun i -> not (Set.contains i shown))
        hidden |> List.forall (fun h -> view.Shown |> Array.forall (fun s -> classes.[s] <= classes.[h]))

    testProperty "bound is deterministic, and rows of one class keep index order" <|
      fun (cap: PositiveInt) (raw: byte list) ->
        let classes = raw |> List.map (fun b -> if b % 2uy = 0uy then CohortBoundedView.MemberClass.PresentMember else CohortBoundedView.MemberClass.DepartedMember) |> Array.ofList
        let a = CohortBoundedView.bound cap.Get (fun i -> classes.[i]) classes.Length
        let b = CohortBoundedView.bound cap.Get (fun i -> classes.[i]) classes.Length
        let ordered =
          a.Shown |> Array.groupBy (fun i -> classes.[i]) |> Array.forall (fun (_, idxs) -> idxs = Array.sort idxs)
        check "bound is deterministic" (a = b)
        check "rows of one class keep index order" ordered
        true
  ]
