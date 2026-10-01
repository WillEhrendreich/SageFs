module SageFs.Tests.BuildConfirmationSimTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.Simulation
open SageFs.Simulation.BuildConfirmationSim
open SageFs.Simulation.BuildConfirmationInvariants

/// DST for the claim "a real build agrees with what the live eval said". The fold under test is
/// the real `BuildConfirmation.step`; see `SageFs.Simulation/BuildConfirmationSim.fs` for the world
/// around it, the op order that serves as the scheduler, and the twins.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Ops vs

let private effectsOf (t: Trace) : ConfirmationEffect list = t.Steps |> List.collect (fun s -> s.Effects)

let private marksFor (content: int) (t: Trace) : Map<TestId, ResultProvenance> list =
  effectsOf t
  |> List.choose (function
    | ConfirmationEffect.Mark (c, marks) when c = contentId content -> Some marks
    | _ -> None)

/// What the rows of this content say at the end: each test's last mark.
let private finalRows (content: int) (t: Trace) : Map<TestId, ResultProvenance> =
  marksFor content t |> List.fold (fun acc marks -> Map.fold (fun m k v -> Map.add k v m) acc marks) Map.empty

let private builds (t: Trace) : (int64 * Confirmation) list =
  effectsOf t |> List.choose (function ConfirmationEffect.StartBuild (g, c) -> Some (g, c) | _ -> None)

let private isOutcome (p: ResultProvenance) =
  match p with
  | ResultProvenance.VerifiedByBuild | ResultProvenance.BuildDisagrees _ -> true
  | ResultProvenance.Compiled | ResultProvenance.Evaluated -> false

let private outcomesFor (content: int) (t: Trace) =
  marksFor content t |> List.collect (fun marks -> marks |> Map.toList |> List.filter (snd >> isOutcome))

[<Tests>]
let buildConfirmationSimTests =
  testList "DST build confirmation" [

    testList "the real decision holds the invariants" [
      testPropertyWithConfig simConfig
        "seeded scenarios — no stale confirmation, one build at a time, bursts coalesce, the eval is never delayed, failure is loud, everything resolves"
        <| fun (seed: int) -> assertHolds (run (BuildConfirmationGenerators.fromSeed seed))

      testCase "agrees: every row of the content ends VerifiedByBuild" <| fun _ ->
        let t = run BuildConfirmationGenerators.agrees
        finalRows 1 t
        |> Map.toList
        |> List.map snd
        |> List.distinct
        |> Expect.equal "all verified" [ ResultProvenance.VerifiedByBuild ]
        assertHolds t

      testCase "differs: the test the build disagreed on says so, with both verdicts, and the rest are verified" <| fun _ ->
        let t = run BuildConfirmationGenerators.differs
        let rows = finalRows 1 t
        match Map.find (List.head testIds) rows with
        | ResultProvenance.BuildDisagrees (BuildDisagreement.ResultDiffers (evaluated, built)) ->
          evaluated |> Expect.notEqual "the two verdicts differ" built
        | other -> failtestf "expected the first test to disagree, got %A" other
        rows
        |> Map.remove (List.head testIds)
        |> Map.toList
        |> List.map snd
        |> List.distinct
        |> Expect.equal "the others agreed" [ ResultProvenance.VerifiedByBuild ]
        assertHolds t

      testCase "buildFails: every row says the build failed, and why" <| fun _ ->
        let t = run BuildConfirmationGenerators.buildFails
        finalRows 1 t
        |> Map.forall (fun _ p -> match p with ResultProvenance.BuildDisagrees (BuildDisagreement.BuildFailed message) -> message <> "" | _ -> false)
        |> Expect.isTrue "BuildFailed with the compiler's message on every row"
        assertHolds t

      testCase "burst: three evaluated runs with no pause between them cost one build, for the last" <| fun _ ->
        let t = run BuildConfirmationGenerators.burst
        builds t
        |> List.map (fun (_, c) -> c.Content)
        |> Expect.equal "one build, of the newest content" [ contentId 2 ]
        assertHolds t

      testCase "editedWhileBuilding: the build is abandoned and its late answer is applied to nothing" <| fun _ ->
        let t = run BuildConfirmationGenerators.editedWhileBuilding
        effectsOf t
        |> List.exists (function ConfirmationEffect.AbandonBuild _ -> true | _ -> false)
        |> Expect.isTrue "the build of older text is abandoned"
        outcomesFor 0 t |> Expect.isEmpty "no outcome is ever marked for the text that was edited away"
        assertHolds t

      testCase "supersededWhileBuilding: an older build's answers never reach the newer rows" <| fun _ ->
        let t = run BuildConfirmationGenerators.supersededWhileBuilding
        outcomesFor 0 t |> Expect.isEmpty "nothing for the older content"
        outcomesFor 1 t
        |> List.forall (fun (_, p) -> match p with ResultProvenance.BuildDisagrees (BuildDisagreement.ResultDiffers _) -> false | _ -> true)
        |> Expect.isTrue "the older build's 'differs' answer was not applied to the newer content"
        assertHolds t

      testCase "hungBuild: a build that never answers is said so, not left looking evaluated forever" <| fun _ ->
        let t = run BuildConfirmationGenerators.hungBuild
        finalRows 0 t
        |> Map.forall (fun _ p -> match p with ResultProvenance.BuildDisagrees (BuildDisagreement.BuildUnanswered _) -> true | _ -> false)
        |> Expect.isTrue "BuildUnanswered on every row"
        assertHolds t

      testCase "hungRun: a run against the build that never answers is said so too" <| fun _ ->
        let t = run BuildConfirmationGenerators.hungRun
        finalRows 0 t
        |> Map.forall (fun _ p -> match p with ResultProvenance.BuildDisagrees (BuildDisagreement.BuildUnanswered _) -> true | _ -> false)
        |> Expect.isTrue "BuildUnanswered on every row"
        assertHolds t

      testCase "evaluatedNothing: an eval that ran no tests confirms nothing and builds nothing" <| fun _ ->
        let t = run BuildConfirmationGenerators.evaluatedNothing
        builds t |> Expect.isEmpty "no build for a run of nothing"
        effectsOf t |> Expect.isEmpty "no effect at all"
        assertHolds t
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED — the applies-stale-answers twin puts an older build's verdict on newer rows" <| fun _ ->
        let t = runAppliesStaleAnswers BuildConfirmationGenerators.supersededWhileBuilding
        violations t |> List.map fst |> Expect.contains "no-stale-confirmation must fire" "no-stale-confirmation"

      testCase "REPRODUCED — the builds-without-waiting twin spends a build on every pause in a burst" <| fun _ ->
        let t = runBuildsWithoutWaiting BuildConfirmationGenerators.burst
        violations t |> List.map fst |> Expect.contains "bursts-coalesce must fire" "bursts-coalesce"

      testCase "REPRODUCED — the swallows-failures twin says nothing when the build fails" <| fun _ ->
        let t = runSwallowsFailures BuildConfirmationGenerators.buildFails
        violations t |> List.map fst |> Expect.contains "failure-is-loud must fire" "failure-is-loud"

      testCase "the invariants have teeth: some seeded scenario violates them under each twin" <| fun _ ->
        let seeds = [ 1 .. 200 ]
        let anyViolates runTwin =
          seeds |> List.exists (fun seed -> violations (runTwin (BuildConfirmationGenerators.fromSeed seed)) |> List.isEmpty |> not)
        anyViolates runAppliesStaleAnswers |> Expect.isTrue "applies-stale-answers is caught by a generated scenario"
        anyViolates runBuildsWithoutWaiting |> Expect.isTrue "builds-without-waiting is caught by a generated scenario"
        anyViolates runSwallowsFailures |> Expect.isTrue "swallows-failures is caught by a generated scenario"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical steps (real decision)" <| fun (seed: int) ->
        let a = run (BuildConfirmationGenerators.fromSeed seed)
        let b = run (BuildConfirmationGenerators.fromSeed seed)
        a.Steps = b.Steps
    ]
  ]
