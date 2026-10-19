module SageFs.Tests.HatchWatchTests

/// Issue #160, the daemon's half: every use of the escape hatch the sagefs-repl-guard hook reports is counted, by kind,
/// so "twenty agents all fell back today" is a number. `HatchUse` is the closed type both sides compile
/// (SageFs.Core/HatchUse.fs); `HatchLedger` is the pure count; `HatchWatch` is the one live ledger the route writes.

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features

let private t0 = DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero)
let private later = t0.AddMinutes 5.0

let private declaredFaulted = { Declaration = GateDeclaration.DeclaredGate; Repl = ReplAtTheTime.Faulted }
let private undeclaredFaulted = { Declaration = GateDeclaration.UndeclaredGate; Repl = ReplAtTheTime.Faulted }
let private undeclaredNotReady = { Declaration = GateDeclaration.UndeclaredGate; Repl = ReplAtTheTime.NotReady }

let private anyUse = Gen.elements HatchUse.all

[<Tests>]
let hatchUseTests =
  testList "HatchUse: the closed type both the hook and the daemon compile" [

    testCase "WHY — every kind of use is one declaration and one REPL state, six in all" <| fun _ ->
      HatchUse.all |> List.length |> Expect.equal "two declarations times three REPL states" 6
      HatchUse.all |> List.distinct |> List.length |> Expect.equal "no kind listed twice" 6

    testCase "WHY — each kind's tokens read back as the same kind, so the hook and the daemon cannot drift apart" <| fun _ ->
      HatchUse.all
      |> List.map (fun hatch ->
        let declaration, repl = HatchUse.toTokens hatch
        HatchUse.tryParse declaration repl)
      |> Expect.equal "round trip" (HatchUse.all |> List.map Ok)

    testCase "WHY — the tokens are the spelling the daemon documents" <| fun _ ->
      HatchUse.toTokens undeclaredNotReady |> Expect.equal "undeclared, not_ready" ("undeclared", "not_ready")
      HatchUse.toTokens declaredFaulted |> Expect.equal "declared, faulted" ("declared", "faulted")
      HatchUse.toTokens { Declaration = GateDeclaration.DeclaredGate; Repl = ReplAtTheTime.Ready }
      |> Expect.equal "declared, ready" ("declared", "ready")

    testCase "WHY — an unknown declaration is refused by name, with the tokens that would have been accepted" <| fun _ ->
      match HatchUse.tryParse "final-gate" "faulted" with
      | Error(HatchRefusal.UnknownDeclaration token) ->
        token |> Expect.equal "the refusal carries what it was given" "final-gate"
        HatchRefusal.describe (HatchRefusal.UnknownDeclaration token)
        |> Expect.stringContains "and what was accepted" "declared, undeclared"
      | Error(HatchRefusal.UnknownRepl _)
      | Ok _ -> failtest "a made-up declaration must be refused as an unknown declaration"

    testCase "WHY — an unknown REPL state is refused by name, with the tokens that would have been accepted" <| fun _ ->
      match HatchUse.tryParse "declared" "broken" with
      | Error(HatchRefusal.UnknownRepl token) ->
        HatchRefusal.describe (HatchRefusal.UnknownRepl token)
        |> Expect.stringContains "and what was accepted" "ready, faulted, not_ready"
      | Error(HatchRefusal.UnknownDeclaration _)
      | Ok _ -> failtest "a made-up REPL state must be refused as an unknown REPL state"

    testCase "WHY — a report with nothing in it is refused, not read as a default kind" <| fun _ ->
      HatchUse.tryParse "" "" |> Expect.equal "no declaration" (Error(HatchRefusal.UnknownDeclaration ""))
      HatchUse.tryParse null null |> Expect.equal "no tokens at all" (Error(HatchRefusal.UnknownDeclaration null))
  ]

[<Tests>]
let hatchLedgerTests =
  testList "HatchLedger: the count" [

    testCase "WHY — a ledger that has seen nothing says so: zero everywhere and NeverSeen" <| fun _ ->
      let ledger = HatchLedger.empty t0
      HatchLedger.total ledger |> Expect.equal "no uses" 0
      ledger.Last |> Expect.equal "never seen" HatchSeen.NeverSeen
      ledger.Since |> Expect.equal "counting since" t0

    testCase "WHY — recording a use counts exactly that kind and no other" <| fun _ ->
      let ledger = HatchLedger.empty t0 |> HatchLedger.record later undeclaredFaulted
      HatchLedger.count undeclaredFaulted ledger |> Expect.equal "the kind recorded" 1
      HatchUse.all
      |> List.filter (fun hatch -> hatch <> undeclaredFaulted)
      |> List.map (fun hatch -> HatchLedger.count hatch ledger)
      |> Expect.allEqual "every other kind" 0

    testCase "WHY — the same kind recorded three times is three, and a different kind does not join it" <| fun _ ->
      let ledger =
        HatchLedger.empty t0
        |> HatchLedger.record t0 undeclaredFaulted
        |> HatchLedger.record t0 undeclaredFaulted
        |> HatchLedger.record t0 declaredFaulted
        |> HatchLedger.record later undeclaredFaulted
      HatchLedger.count undeclaredFaulted ledger |> Expect.equal "three undeclared faulted" 3
      HatchLedger.count declaredFaulted ledger |> Expect.equal "one declared faulted" 1
      HatchLedger.total ledger |> Expect.equal "four in all" 4

    testCase "WHY — the last use is when the most recent one was recorded" <| fun _ ->
      HatchLedger.empty t0
      |> HatchLedger.record t0 undeclaredNotReady
      |> HatchLedger.record later undeclaredFaulted
      |> fun ledger -> ledger.Last
      |> Expect.equal "the later one" (HatchSeen.LastSeenAt later)

    testCase "WHY — recording keeps the instant counting began" <| fun _ ->
      (HatchLedger.empty t0 |> HatchLedger.record later undeclaredFaulted).Since
      |> Expect.equal "since is the daemon's start, not the first use" t0

    testCase "WHY — the rows list every kind, zeros included, in the order of HatchUse.all, so the wire shape never depends on what was seen" <| fun _ ->
      let rows = HatchLedger.empty t0 |> HatchLedger.record t0 declaredFaulted |> HatchLedger.rows
      rows |> List.map fst |> Expect.equal "all six, in order" HatchUse.all
      rows |> List.map snd |> Expect.equal "only the recorded one counts" (HatchUse.all |> List.map (fun hatch -> if hatch = declaredFaulted then 1 else 0))

    testProperty "WHY — whatever is recorded, each kind's count is how many times it was recorded and the total is how many records there were" <|
      Prop.forAll (Arb.fromGen (Gen.listOf anyUse)) (fun uses ->
        let ledger = uses |> List.fold (fun l hatch -> HatchLedger.record t0 hatch l) (HatchLedger.empty t0)
        HatchLedger.total ledger = List.length uses
        && HatchUse.all |> List.forall (fun hatch -> HatchLedger.count hatch ledger = (uses |> List.filter ((=) hatch) |> List.length)))

    testCase "WHY — the live ledger counts what the route records, and counts from the daemon's own start" <| fun _ ->
      // The ledger is the process's own, so this reads before and after and compares the two. Only these two kinds are
      // recorded here, and no other test in the suite records them.
      let before = HatchWatch.snapshot ()
      HatchWatch.record undeclaredFaulted
      HatchWatch.record undeclaredFaulted
      HatchWatch.record declaredFaulted
      let after = HatchWatch.snapshot ()
      HatchLedger.count undeclaredFaulted after - HatchLedger.count undeclaredFaulted before
      |> Expect.equal "two undeclared faulted" 2
      HatchLedger.count declaredFaulted after - HatchLedger.count declaredFaulted before
      |> Expect.equal "one declared faulted" 1
      after.Since |> Expect.equal "the instant counting began does not move" before.Since
      (match after.Last with
       | HatchSeen.LastSeenAt _ -> true
       | HatchSeen.NeverSeen -> false)
      |> Expect.isTrue "a recorded use has a time"
      (before.Since <= DateTimeOffset.UtcNow) |> Expect.isTrue "counting began no later than now"
  ]
