/// Step 3 of docs/plans/hygiene-that-runs-itself.md: the ledger is pure. What a sweep saw becomes lines (`transitions`), lines
/// become one record per worktree (`fold`), and "resumable" is a fact read from the record. The lines are JSON, one per line,
/// and a line that cannot be read costs that line and nothing else.
module SageFs.Tests.HygieneLedgerTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.WorkspaceHygiene
open SageFs.HygieneLedger
open SageFs.Tests.HygieneFixtures

let private path = worktreeRoot + "/agent-a1b2c3"

let private observed (activity: Activity) : Observed =
  { Path = path
    Branch = BranchLabel.OnBranch "worktree-agent-a1b2c3"
    Tip = "abc1234"
    Maker = Maker.HarnessAgent "a1b2c3"
    Activity = activity }

let private at (minutes: float) : DateTime = now + HygieneAges.minutes minutes

let private removal (kind: LeftoverKind) (branch: BranchLabel) (tip: Tip) (merged: MergeState) (why: string) : Removal =
  { Kind = kind; Target = path; Branch = branch; Tip = tip; Merged = merged; Why = why }

/// Run sweeps in order against a growing file, the way the daemon does: each sweep reads the file, observes, appends.
let private sweeps (observations: (float * Activity) list) : LedgerLine list =
  observations
  |> List.fold (fun (file: LedgerLine list) (minutes, activity) ->
    let known = Ledger.fold file
    file @ Ledger.transitions (at minutes) known [ observed activity ]) []

let private genMaker : Gen<Maker> =
  Gen.oneof [
    Gen.elements [ "a1"; "b22" ] |> Gen.map Maker.HarnessAgent
    Gen.constant (Maker.SessionOwner("mcp", "conn-1"))
    Gen.constant Maker.MakerUnrecorded
  ]

let private genEvent : Gen<LedgerEvent> =
  gen {
    let! maker = genMaker
    let! how = Gen.elements [ MergeState.NotMerged; MergeState.MergedBy MergeHow.Ancestor; MergeState.MergedBy MergeHow.PatchEquivalent; MergeState.MergedBy MergeHow.TreeEqual ]
    let! kind = Gen.elements Kind.all
    return!
      Gen.elements [
        LedgerEvent.FirstSeen(path, BranchLabel.OnBranch "b", "abc1234", maker)
        LedgerEvent.WentQuiet path
        LedgerEvent.Resumed path
        LedgerEvent.Salvaged(path, "salvage/agent-a1b2c3", "def5678", 3)
        LedgerEvent.Removed(removal kind (BranchLabel.OnBranch "b") (Tip.AtTip "abc1234") how "merged and quiet")
        LedgerEvent.Rescued(path, "rescue/agent-a1b2c3", "0123abc")
      ]
  }

type private ArbLine =
  static member Line() : Arbitrary<LedgerLine> =
    gen {
      let! event = genEvent
      let! minutes = Gen.choose (0, 5000)
      return { At = at (float minutes); Event = event }
    }
    |> Arb.fromGen

let private cfg = { FsCheckConfig.defaultConfig with maxTest = 300; arbitrary = [ typeof<ArbLine> ] }

[<Tests>]
let ledgerTests =
  testList "Workspace hygiene: the ledger" [

    testCase "the harness agent is named by the directory and by the lock git carries while it runs" <| fun _ ->
      Maker.ofName "agent-a1b2c3d4" |> Expect.equal "from the directory name" (Maker.HarnessAgent "a1b2c3d4")
      Maker.ofName "claude agent agent-a236525b3b09f599b (pid 6613 start 13675)"
      |> Expect.equal "from the lock reason" (Maker.HarnessAgent "a236525b3b09f599b")
      Maker.ofName "plan-browser-refresh" |> Expect.equal "a plan worktree has no harness agent" Maker.MakerUnrecorded
      Maker.ofName "agent-" |> Expect.equal "a prefix with no id is nothing" Maker.MakerUnrecorded

    testCase "a worktree seen for the first time is written once, with who made it and where its tip was" <| fun _ ->
      match sweeps [ 0.0, Activity.InUse ] with
      | [ { Event = LedgerEvent.FirstSeen(p, branch, tip, maker) } ] ->
        p |> Expect.equal "path" path
        branch |> Expect.equal "branch" (BranchLabel.OnBranch "worktree-agent-a1b2c3")
        tip |> Expect.equal "tip" "abc1234"
        maker |> Expect.equal "maker" (Maker.HarnessAgent "a1b2c3")
      | other -> failtestf "expected one FirstSeen, got %A" other

    testCase "looking again at an unchanged worktree writes nothing, however often the daemon looks" <| fun _ ->
      sweeps [ for i in 0..20 -> float i * 15.0, Activity.InUse ]
      |> List.length |> Expect.equal "one line for twenty-one sweeps" 1

    testCase "a worktree that stops being used is written as quiet, and one used again as resumed" <| fun _ ->
      let lines = sweeps [ 0.0, Activity.InUse; 15.0, Activity.Quiet; 30.0, Activity.Quiet; 45.0, Activity.InUse ]
      lines |> List.map (fun l -> l.Event)
      |> Expect.equal "first seen, quiet, resumed" [ LedgerEvent.FirstSeen(path, BranchLabel.OnBranch "worktree-agent-a1b2c3", "abc1234", Maker.HarnessAgent "a1b2c3"); LedgerEvent.WentQuiet path; LedgerEvent.Resumed path ]

    testCase "a worktree first seen already quiet is written as first seen and quiet at once" <| fun _ ->
      sweeps [ 0.0, Activity.Quiet ] |> List.map (fun l -> l.Event |> function | LedgerEvent.FirstSeen _ -> "seen" | LedgerEvent.WentQuiet _ -> "quiet" | _ -> "other")
      |> Expect.equal "two lines" [ "seen"; "quiet" ]

    testCase "the record says when it went quiet, and resumable is read from that with a grace" <| fun _ ->
      let record = sweeps [ 0.0, Activity.InUse; 60.0, Activity.Quiet ] |> Ledger.fold |> Map.find path
      record.Activity |> Expect.equal "quiet since the sweep that saw it" (RecordedActivity.RecordedQuietSince(at 60.0))
      let grace = HygieneAges.oneDay
      Ledger.isResumable grace (at 120.0) record |> Expect.isTrue "an hour after it went quiet it may still be resumed"
      Ledger.isResumable grace (at (60.0 + 25.0 * 60.0)) record |> Expect.isFalse "a day and an hour after, it may not"

    testCase "a worktree in use is always resumable, and a removed one never is" <| fun _ ->
      let inUse = sweeps [ 0.0, Activity.InUse ] |> Ledger.fold |> Map.find path
      Ledger.isResumable HygieneAges.oneDay (at 100000.0) inUse |> Expect.isTrue "in use is resumable by definition"
      let removed =
        sweeps [ 0.0, Activity.Quiet ]
        @ [ { At = at 10.0; Event = LedgerEvent.Removed(removal LeftoverKind.AgentWorktree BranchLabel.NoBranch (Tip.AtTip "abc1234") (MergeState.MergedBy MergeHow.Ancestor) "merged") } ]
        |> Ledger.fold |> Map.find path
      Ledger.isResumable HygieneAges.oneDay (at 11.0) removed |> Expect.isFalse "a removed worktree has nothing to resume"

    testCase "a worktree that comes back after it was removed starts a new record" <| fun _ ->
      let lines =
        sweeps [ 0.0, Activity.Quiet ]
        @ [ { At = at 10.0; Event = LedgerEvent.Removed(removal LeftoverKind.AgentWorktree BranchLabel.NoBranch Tip.TipUnknown MergeState.NotMerged "merged") } ]
      let again = Ledger.transitions (at 20.0) (Ledger.fold lines) [ observed Activity.InUse ]
      again |> List.map (fun l -> l.Event |> function | LedgerEvent.FirstSeen _ -> "seen" | _ -> "other")
      |> Expect.equal "seen as new" [ "seen" ]

    testCase "a line that cannot be read is skipped and the lines around it survive" <| fun _ ->
      let good = Ledger.encode { At = at 1.0; Event = LedgerEvent.WentQuiet path }
      let lines = [ good; "{ this is not json"; ""; good.Substring(0, good.Length / 2); good ]
      Ledger.decodeAll lines |> List.length |> Expect.equal "the two whole lines are read, the torn ones are not" 2

    testCase "one event is one line of text: no newline ever appears inside it" <| fun _ ->
      let line = Ledger.encode { At = at 1.0; Event = LedgerEvent.Removed(removal LeftoverKind.AgentWorktree (BranchLabel.OnBranch "a\nb") Tip.TipUnknown MergeState.NotMerged "two\nlines") }
      line.Contains "\n" |> Expect.isFalse "JSON escapes the newlines"

    testPropertyWithConfig cfg "PROPERTY: every line reads back as the line that was written" <| fun (line: LedgerLine) ->
      Ledger.decode (Ledger.encode line) = Some line

    testPropertyWithConfig cfg "PROPERTY: observing the same world twice writes nothing the second time" <| fun (activities: bool list) ->
      let steps = activities |> List.mapi (fun i quiet -> float i, (if quiet then Activity.Quiet else Activity.InUse))
      let file = sweeps steps
      match List.tryLast steps with
      | None -> true
      | Some(_, last) -> Ledger.transitions (at 99999.0) (Ledger.fold file) [ observed last ] |> List.isEmpty
  ]
