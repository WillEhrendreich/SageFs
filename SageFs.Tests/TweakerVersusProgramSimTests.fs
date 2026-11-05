module SageFs.Tests.TweakerVersusProgramSimTests

open Expecto
open Expecto.Flip
open SageFs.Features.Tweak.BindingTweak
open SageFs.Simulation
open SageFs.Simulation.TweakerVersusProgramSim

/// DST for the claim "the pane never mistakes the program's change for a tweak, never writes a value the program moved without a
/// confirmation, and only calls a value evolved when it can say what moved it". See `SageFs.Simulation/TweakerVersusProgramSim.fs`
/// for the fold (the real derivation, save offer and memory), the independent truth it is checked against, and the twins.

let simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let assertHolds (t: Trace) =
  match TweakerVersusProgramInvariants.violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Ops vs

let violatedIds (t: Trace) : string list = TweakerVersusProgramInvariants.violations t |> List.map fst

let lastAfter (t: Trace) : PersistenceState = (List.last t.Snapshots).After

let tokenOf (state: PersistenceState) : string = PersistenceState.token state

[<Tests>]
let tests =
  testList "DST tweaker versus program" [

    testList "the real derivation holds the invariants" [
      testPropertyWithConfig simConfig
        "seeded histories of pushes, program assignments, saves, resets, file edits and adopts"
        <| fun (seed: int) -> assertHolds (run (TweakerVersusProgramGenerators.fromSeed seed))

      testCase "tweakThenSave: a pushed value is tweaked until it is saved, then the file holds it" <| fun _ ->
        let t = run TweakerVersusProgramGenerators.tweakThenSave
        t.Snapshots |> List.map (fun s -> tokenOf s.After) |> Expect.equal "the states" [ "InSource"; "Tweaked"; "InSource" ]
        assertHolds t

      testCase "programOvertakesTweak: a program that moves a pushed value makes the row evolved, naming the push" <| fun _ ->
        let t = run TweakerVersusProgramGenerators.programOvertakesTweak
        match lastAfter t with
        | PersistenceState.Evolved(_, EvolvedBy.OvertookTweak pushed, _) -> pushed |> Expect.equal "the push it overtook" "2"
        | other -> failtestf "expected Evolved, got %A" other
        assertHolds t

      testCase "programChangeAlone: a program's change with nothing pushed is evolved, never tweaked" <| fun _ ->
        let t = run TweakerVersusProgramGenerators.programChangeAlone
        lastAfter t |> tokenOf |> Expect.equal "evolved" "Evolved"
        assertHolds t

      testCase "fileEditedWithNoReload: a file edit is not attributed to the program" <| fun _ ->
        let t = run TweakerVersusProgramGenerators.fileEditedWithNoReload
        lastAfter t |> tokenOf |> Expect.equal "differs, unattributed" "DiffersFromFile"
        assertHolds t

      testCase "saveOnAnEvolvedRow: Save does nothing to the file" <| fun _ ->
        let t = run TweakerVersusProgramGenerators.saveOnAnEvolvedRow
        let save = List.last t.Snapshots
        save.FileAfter |> Expect.equal "the file is as it was" save.FileBefore
        lastAfter t |> tokenOf |> Expect.equal "still evolved" "Evolved"
        assertHolds t

      testCase "adoptDeclined: declining the confirmation leaves the file alone" <| fun _ ->
        let t = run TweakerVersusProgramGenerators.adoptDeclined
        let adopt = List.last t.Snapshots
        adopt.FileAfter |> Expect.equal "the file is as it was" adopt.FileBefore
        assertHolds t

      testCase "adoptConfirmed: confirming writes the live value, and the row is in source" <| fun _ ->
        let t = run TweakerVersusProgramGenerators.adoptConfirmed
        let adopt = List.last t.Snapshots
        adopt.FileAfter |> Expect.equal "the file holds the live value" adopt.LiveBefore
        lastAfter t |> tokenOf |> Expect.equal "in source" "InSource"
        assertHolds t
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED — recording the program's change as a push makes it a tweak" <| fun _ ->
        violatedIds (runProgramChangeCountsAsPush TweakerVersusProgramGenerators.programChangeAlone)
        |> Expect.contains "the program's change must not read as a tweak" "tweaked-exactly-when-pushed"

      testCase "REPRODUCED — a Save that writes whatever is live records the program's change as a decision" <| fun _ ->
        let ids = violatedIds (runSavesWhateverIsLive TweakerVersusProgramGenerators.saveOnAnEvolvedRow)
        ids |> Expect.contains "Save from an evolved row must be caught" "a-save-writes-only-a-tweak"
        ids |> Expect.contains "the unconfirmed write must be caught" "an-evolved-value-is-written-only-when-confirmed"

      testCase "REPRODUCED — adopting without waiting for the answer writes an unconfirmed value" <| fun _ ->
        violatedIds (runAdoptsWithoutConfirmation TweakerVersusProgramGenerators.adoptDeclined)
        |> Expect.contains "the unconfirmed adopt must be caught" "an-evolved-value-is-written-only-when-confirmed"

      testCase "REPRODUCED — reading an edited file as the file the value last matched blames the program for the edit" <| fun _ ->
        violatedIds (runIgnoresFileEdits TweakerVersusProgramGenerators.fileEditedWithNoReload)
        |> Expect.contains "the unattributable difference must be caught" "evolved-only-with-a-reason"

      testCase "the invariants have teeth: some seeded history violates them under each twin" <| fun _ ->
        let seeds = [ 1 .. 300 ]
        let anyViolates runTwin =
          seeds
          |> List.exists (fun seed ->
            TweakerVersusProgramInvariants.violations (runTwin (TweakerVersusProgramGenerators.fromSeed seed)) |> List.isEmpty |> not)
        anyViolates runProgramChangeCountsAsPush |> Expect.isTrue "the program's change counted as a push is caught by a generated history"
        anyViolates runSavesWhateverIsLive |> Expect.isTrue "a Save that writes whatever is live is caught by a generated history"
        anyViolates runAdoptsWithoutConfirmation |> Expect.isTrue "an unconfirmed adopt is caught by a generated history"
        anyViolates runIgnoresFileEdits |> Expect.isTrue "ignoring file edits is caught by a generated history"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical snapshots (real derivation)" <| fun (seed: int) ->
        let a = run (TweakerVersusProgramGenerators.fromSeed seed)
        let b = run (TweakerVersusProgramGenerators.fromSeed seed)
        a.Snapshots = b.Snapshots
    ]
  ]
