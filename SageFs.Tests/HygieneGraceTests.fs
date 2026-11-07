/// Step 1 of docs/plans/hygiene-that-runs-itself.md: a merged worktree is not reclaimable until it has been quiet for its
/// grace. A finished agent may be resumed (the gate often finds a flake in what it wrote), so "merged" alone is not the
/// moment to remove it. Grace only ever delays a removal: it can turn a reclaimable standing into a kept one and never the
/// other way round.
module SageFs.Tests.HygieneGraceTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.WorkspaceHygiene
open SageFs.WorkspaceHygieneRender
open SageFs.Tests.HygieneFixtures

let private grace = HygieneAges.oneDay

let private quietFor (age: TimeSpan) (s: Subject) : Subject =
  { s with LastTouched = now - age; Retention = Retention.KeepFor grace }

type private ArbHolder =
  static member Subject() = arbSubject

let private cfg = { FsCheckConfig.defaultConfig with maxTest = 300; arbitrary = [ typeof<ArbHolder> ] }

let private idleFor (age: TimeSpan) (s: Subject) : Subject = { s with LastTouched = now - age }

let private summaryOf (subjects: Subject list) =
  let leftovers = subjects |> List.map (classify now)
  SageFs.WorkspaceHygieneRender.summarize leftovers (Planner.plan leftovers)

[<Tests>]
let holdsWorkTests =
  testList "Workspace hygiene: work that exists nowhere else" [

    testCase "a worktree with commits the base lacks, or uncommitted real files, holds work; a merged one does not" <| fun _ ->
      let summary =
        summaryOf
          [ worktree "unmerged" |> unmergedWith [] |> idleFor (HygieneAges.days 38.0)
            worktree "dirty" |> mergedWith MergeHow.Ancestor [ realFile "src/a.fs" ] |> idleFor (HygieneAges.days 5.0)
            worktree "merged" |> idleFor (HygieneAges.days 9.0) ]
      match summary.Held with
      | HeldWork.WorkHeld(count, oldest, _) ->
        count |> Expect.equal "two hold work" 2
        oldest |> Expect.equal "the oldest has been idle 38 days" (HygieneAges.days 38.0)
      | HeldWork.NothingHeld -> failtest "expected work held"

    testCase "a worktree somebody is using is not counted: its work is in front of them" <| fun _ ->
      let summary = summaryOf [ worktree "busy" |> unmergedWith [] |> withUse (InUseReason.LiveSession "s1") ]
      summary.Held |> Expect.equal "nothing held" HeldWork.NothingHeld

    testCase "the status line leads with work that exists nowhere else, even when there are too few worktrees to trip the pile threshold" <| fun _ ->
      let summary = summaryOf [ worktree "unmerged" |> unmergedWith [] |> idleFor (HygieneAges.days 38.0) ]
      match SageFs.WorkspaceHygieneRender.nudge summary with
      | Some line ->
        line |> Expect.stringContains "says what is at risk" "1 worktree holds work that is nowhere else"
        line |> Expect.stringContains "says how old the oldest is" "oldest idle 38d"
        (line.IndexOf "holds work" < line.IndexOf "safe to reclaim") |> Expect.isTrue "it comes before the pile counts"
      | None -> failtest "expected a line"

    testCase "several worktrees are counted in the plural" <| fun _ ->
      let summary = summaryOf [ for i in 1..3 -> worktree (sprintf "u%d" i) |> unmergedWith [] ]
      match SageFs.WorkspaceHygieneRender.nudge summary with
      | Some line -> line |> Expect.stringContains "plural" "3 worktrees hold work that is nowhere else"
      | None -> failtest "expected a line"

    testCase "with nothing held and a small pile the line stays silent, as before" <| fun _ ->
      SageFs.WorkspaceHygieneRender.nudge (summaryOf [ worktree "merged" ]) |> Expect.isNone "silent"

    testPropertyWithConfig cfg "PROPERTY: the count of held worktrees never exceeds the worktrees there are" <| fun (subjects: Subject list) ->
      let leftovers = subjects |> List.map (classify now)
      let summary = SageFs.WorkspaceHygieneRender.summarize leftovers (Planner.plan leftovers)
      let worktrees = leftovers |> List.filter (fun l -> Leftover.kind l = LeftoverKind.AgentWorktree) |> List.length
      match summary.Held with
      | HeldWork.NothingHeld -> true
      | HeldWork.WorkHeld(count, _, _) -> count >= 1 && count <= worktrees
  ]

[<Tests>]
let graceTests =
  testList "Workspace hygiene: a finished worktree's grace" [

    testCase "a merged worktree used inside its grace is kept until the grace ends, and is not reclaimable" <| fun _ ->
      let s = worktree "a" |> quietFor HygieneAges.anHour
      match standingOf now s with
      | Standing.WithinRetention until ->
        until |> Expect.equal "kept until last use plus the grace" (now - HygieneAges.anHour + grace)
        Standing.reclaimability (standingOf now s) |> Expect.equal "kept means untouchable" Reclaimability.Untouchable
      | other -> failtestf "expected WithinRetention, got %A" other

    testCase "the same worktree quiet for longer than the grace is Merged and reclaimable" <| fun _ ->
      let s = worktree "a" |> quietFor HygieneAges.twoDays
      standingOf now s |> Expect.equal "merged once past the grace" (Standing.Merged MergeHow.Ancestor)

    testCase "every way of being merged waits the same grace" <| fun _ ->
      for how in [ MergeHow.Ancestor; MergeHow.PatchEquivalent; MergeHow.TreeEqual ] do
        match worktree "a" |> mergedWith how [] |> quietFor HygieneAges.anHour |> standingOf now with
        | Standing.WithinRetention _ -> ()
        | other -> failtestf "%A: expected WithinRetention, got %A" how other

    testCase "a merged worktree with only build output waits the grace too" <| fun _ ->
      let s = worktree "a" |> mergedWith MergeHow.Ancestor [ generatedFile "packages.lock.json" ] |> quietFor HygieneAges.anHour
      match standingOf now s with
      | Standing.WithinRetention _ -> ()
      | other -> failtestf "expected WithinRetention, got %A" other

    testCase "work that exists nowhere else is judged by git however recently it was used: grace never hides it" <| fun _ ->
      match worktree "a" |> unmergedWith [] |> quietFor HygieneAges.anHour |> standingOf now with
      | Standing.CleanButUnmerged _ -> ()
      | other -> failtestf "expected CleanButUnmerged, got %A" other
      match worktree "a" |> mergedWith MergeHow.Ancestor [ realFile "src/b.fs" ] |> quietFor HygieneAges.anHour |> standingOf now with
      | Standing.DirtyReal _ -> ()
      | other -> failtestf "expected DirtyReal, got %A" other

    testCase "a worktree with no grace set is judged as before" <| fun _ ->
      let s = { worktree "a" with LastTouched = now - HygieneAges.anHour }
      standingOf now s |> Expect.equal "no retention, no wait" (Standing.Merged MergeHow.Ancestor)

    testPropertyWithConfig cfg "PROPERTY: a grace never makes a worktree more reclaimable than it was without one" <| fun (s: Subject) ->
      match s.Kind, s.Git with
      | LeftoverKind.AgentWorktree, GitEvidence.Worktree _ ->
        let without = { s with Retention = Retention.NeverExpires }
        let withGrace = { s with Retention = Retention.KeepFor grace }
        match Standing.reclaimability (standingOf now withGrace), Standing.reclaimability (standingOf now without) with
        | Reclaimability.Reclaimable, Reclaimability.Reclaimable -> true
        | Reclaimability.Reclaimable, _ -> false
        | _ -> true
      | _ -> true
  ]

[<Tests>]
let graceZeroTests =
  testList "Workspace hygiene: a grace of zero means no grace" [

    testCase "WHY — the grace setting row accepts 0, where every other age row refuses it" <| fun _ ->
      let row = SageFs.EnvBackedRows.descriptors |> List.find (fun d -> d.Key = "data.finishedWorktreeGraceDays")
      match row.Parse "0" with
      | Ok value -> row.Render value |> Expect.equal "0 reads back as 0, not as the default" "0"
      | Error why -> failtestf "the plan says 0 means no grace, and the row refused it: %A" why
      let other = SageFs.EnvBackedRows.descriptors |> List.find (fun d -> d.Key = "data.workerLogMaxAgeDays")
      other.Parse "0" |> Expect.isError "an ordinary age still refuses 0"

    testPropertyWithConfig cfg "PROPERTY: the grace row accepts exactly the days from zero to the longest age" <| fun (days: float) ->
      let row = SageFs.EnvBackedRows.descriptors |> List.find (fun d -> d.Key = "data.finishedWorktreeGraceDays")
      let text = days.ToString("R", Globalization.CultureInfo.InvariantCulture)
      match row.Parse text with
      | Ok _ -> days >= 0.0 && days <= SageFs.Timeouts.settingAgeLongestDays
      | Error _ -> not (days >= 0.0 && days <= SageFs.Timeouts.settingAgeLongestDays)

    testCase "WHY — the environment variable's 0 is zero, not the one-day default" <| fun _ ->
      SageFs.DataRetention.graceDaysOf "0" 1.0 |> Expect.equal "zero stays zero" TimeSpan.Zero
      SageFs.DataRetention.graceDaysOf "0.5" 1.0 |> Expect.equal "half a day" (HygieneAges.days 0.5)
      for unusable in [ null; ""; "soon"; "-1"; "NaN"; "1e999" ] do
        SageFs.DataRetention.graceDaysOf unusable 1.0 |> Expect.equal (sprintf "%A falls back to the default" unusable) HygieneAges.oneDay

    testCase "a worktree merged a moment ago is reclaimable at once when the grace is zero" <| fun _ ->
      let s = { worktree "a" with LastTouched = now; Retention = Retention.KeepFor TimeSpan.Zero }
      standingOf now s |> Expect.equal "no grace, no wait" (Standing.Merged MergeHow.Ancestor)
  ]

[<Tests>]
let heldLineTests =
  testList "Workspace hygiene: work that exists nowhere else is said the same way everywhere" [

    testCase "the status line, the plan text and the dashboard card all lead with the same sentence" <| fun _ ->
      let leftovers = [ worktree "unmerged" |> unmergedWith [] |> idleFor (HygieneAges.days 38.0) ] |> List.map (classify now)
      let plan = Planner.plan leftovers
      let summary = SageFs.WorkspaceHygieneRender.summarize leftovers plan
      let held = SageFs.WorkspaceHygieneRender.heldLine summary |> Option.defaultWith (fun () -> failtest "expected a held line")
      held |> Expect.equal "the sentence" "1 worktree holds work that is nowhere else (oldest idle 38d, 1000 B)"
      (SageFs.WorkspaceHygieneRender.nudge summary |> Option.defaultValue "") |> Expect.stringContains "the status line carries it" held
      let rendered = SageFs.WorkspaceHygieneRender.renderPlan leftovers plan
      let lines = rendered.Split('\n')
      lines.[1] |> Expect.stringContains "the plan text leads with it, right after its title" held
      lines.[1] |> Expect.stringContains "and names the way to keep the work" "sagefs hygiene --rescue"

    testCase "with nothing held there is no such line" <| fun _ ->
      let leftovers = [ worktree "merged" ] |> List.map (classify now)
      let summary = SageFs.WorkspaceHygieneRender.summarize leftovers (Planner.plan leftovers)
      SageFs.WorkspaceHygieneRender.heldLine summary |> Expect.isNone "nothing to say"
  ]
