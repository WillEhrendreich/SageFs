/// Deterministic Simulation Testing for live tweaking: an interleaved,
/// seeded stream of tweaks, concurrent file edits, saves, rollbacks,
/// reformats and crashes, folded through the REAL `TweakTransaction`,
/// `TweakAddress`, `LiteralEdit` and `TweakLog`, never a reimplementation
/// of them. Three twins reintroduce a skipped hash check, a naive
/// line-based undo, and a compaction that drops an unsaved tweak, proving
/// the invariants below actually catch something.
module SageFs.Tests.TweakSimDstTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.TweakSim
open SageFs.Features.Tweak

let private seeds = [ 1 .. 500 ]

let private describe (scenario: Scenario) =
  sprintf "seed=%d initialX=%d initialOther=%d events=%A" scenario.Seed scenario.InitialX scenario.InitialOther scenario.Events

let private expectNone (label: string) (bad: (Scenario * TweakSimInvariants.Violation list) list) =
  match bad with
  | [] -> ()
  | _ ->
    failtestf "%s: %d of %d seeds.\n%s" label bad.Length seeds.Length
      (bad
       |> List.truncate 5
       |> List.map (fun (s, vs) -> sprintf "%s\n  %s" (describe s) (vs |> List.truncate 3 |> List.map (fun v -> sprintf "[%d] %s" v.Index v.Why) |> String.concat "\n  "))
       |> String.concat "\n")

let private realTrace (scenario: Scenario) = trace SaveBehavior.Real RollbackBehavior.Real CompactionBehavior.Real scenario

[<Tests>]
let tweakSimDstTests =
  testList "Live tweaking DST" [

    testCase "the scenarios are deterministic: the same seed gives the same trace" <| fun _ ->
      let scenario = scenarioOf 42
      let a = realTrace scenario |> List.last
      let b = realTrace scenario |> List.last
      (a.X, a.Source, a.Log.Events |> List.map (fun e -> e.Id, e.Event))
      |> Expect.equal "replaying the same seed yields the identical trace"
        (b.X, b.Source, b.Log.Events |> List.map (fun e -> e.Id, e.Event))

    testList "the real implementation holds every invariant" [

      testCase "NEVER-APPLIED-WITHOUT-TYPE-CHECK across seeded schedules" <| fun _ ->
        seeds
        |> List.map (fun s -> let sc = scenarioOf s in sc, TweakSimInvariants.neverAppliedWithoutTypeCheck sc (realTrace sc))
        |> List.filter (fun (_, vs) -> not (List.isEmpty vs))
        |> expectNone "a tweak was applied without a type check"

      testCase "FAILURE-KEEPS-LAST-GOOD across seeded schedules" <| fun _ ->
        seeds
        |> List.map (fun s -> let sc = scenarioOf s in sc, TweakSimInvariants.failureKeepsLastGoodValue (realTrace sc))
        |> List.filter (fun (_, vs) -> not (List.isEmpty vs))
        |> expectNone "a failure lost the last good value"

      testCase "DIRTY-SET-MATCHES-GROUND-TRUTH across seeded schedules (crash included)" <| fun _ ->
        seeds
        |> List.map (fun s -> let sc = scenarioOf s in sc, TweakSimInvariants.dirtySetMatchesGroundTruth (realTrace sc))
        |> List.filter (fun (_, vs) -> not (List.isEmpty vs))
        |> expectNone "the journal disagreed with which tweaks are really unsaved"

      testCase "SAVE-RECORDS-THE-REAL-TEXT-BEFORE across seeded schedules" <| fun _ ->
        seeds
        |> List.map (fun s -> let sc = scenarioOf s in sc, TweakSimInvariants.saveRecordsTheRealTextBefore (realTrace sc))
        |> List.filter (fun (_, vs) -> not (List.isEmpty vs))
        |> expectNone "a save's own logged textBefore didn't match reality"

      testCase "SAVE-NEVER-OVERWRITES-A-CHANGED-HASH across seeded schedules" <| fun _ ->
        seeds
        |> List.map (fun s -> let sc = scenarioOf s in sc, TweakSimInvariants.saveNeverOverwritesAChangedHash (realTrace sc))
        |> List.filter (fun (_, vs) -> not (List.isEmpty vs))
        |> expectNone "a save overwrote an expression that changed underneath it"

      testCase "OTHER-BINDING-UNTOUCHED across seeded schedules" <| fun _ ->
        seeds
        |> List.map (fun s -> let sc = scenarioOf s in sc, TweakSimInvariants.otherBindingOnlyChangesWhenEdited sc (realTrace sc))
        |> List.filter (fun (_, vs) -> not (List.isEmpty vs))
        |> expectNone "an operation on x leaked into the unrelated binding"

      testCase "REFORMAT-NEVER-BREAKS-THE-ADDRESS across seeded schedules" <| fun _ ->
        seeds
        |> List.map (fun s -> let sc = scenarioOf s in sc, TweakSimInvariants.reformatNeverBreaksTheAddress sc (realTrace sc))
        |> List.filter (fun (_, vs) -> not (List.isEmpty vs))
        |> expectNone "a reformat broke the address"

      testCase "LOG-STAYS-WITHIN-BUDGET across seeded schedules" <| fun _ ->
        seeds
        |> List.map (fun s -> let sc = scenarioOf s in sc, TweakSimInvariants.logStaysWithinBudget (realTrace sc))
        |> List.filter (fun (_, vs) -> not (List.isEmpty vs))
        |> expectNone "the log grew past its budget"

      testCase "SNAPSHOT-PLUS-TAIL-MATCHES-FULL-HISTORY across seeded schedules" <| fun _ ->
        seeds
        |> List.map (fun s -> let sc = scenarioOf s in sc, TweakSimInvariants.snapshotPlusTailMatchesFullHistory (realTrace sc))
        |> List.filter (fun (_, vs) -> not (List.isEmpty vs))
        |> expectNone "a compacted view disagreed with the full history"
    ]

    testList "TWIN: skipping the hash check on save clobbers a concurrent edit" [

      testCase "REPRODUCED, a hand-picked scenario: tweak, then a user edit lands before save" <| fun _ ->
        let scenario =
          { Seed = -1
            InitialX = 1L
            InitialOther = 0L
            Events = [ SimEvent.Tweak 2L; SimEvent.UserEditsFile 999L; SimEvent.Save ] }
        let real = trace SaveBehavior.Real RollbackBehavior.Real CompactionBehavior.Real scenario |> List.last
        let twin = trace SaveBehavior.SkipHashCheckTwin RollbackBehavior.Real CompactionBehavior.Real scenario |> List.last
        let readX (s: State) = TweakAddress.resolve s.Source address |> Result.map (fun r -> r.Text)
        readX real |> Expect.equal "the real save refuses; the user's edit survives on disk" (Ok "999")
        readX twin |> Expect.equal "the twin clobbers the user's concurrent edit with the stale tweak value" (Ok "2")

      testCase "the invariant has teeth: some seeded scenario violates SAVE-NEVER-OVERWRITES-A-CHANGED-HASH under the twin" <| fun _ ->
        let violating =
          seeds
          |> List.map (fun s -> trace SaveBehavior.SkipHashCheckTwin RollbackBehavior.Real CompactionBehavior.Real (scenarioOf s))
          |> List.map TweakSimInvariants.saveNeverOverwritesAChangedHash
          |> List.filter (List.isEmpty >> not)
        violating |> Expect.isNonEmpty "the twin must clobber a changed hash on at least one seeded schedule"
    ]

    testList "TWIN: naive line-based undo clobbers a later edit" [

      testCase "REPRODUCED, a hand-picked scenario: tweak+save, x moves on again, and the OTHER binding lands on the vacated text" <| fun _ ->
        // The naive twin finds the op's own "after" text (a plain string
        // search) and puts "before" back wherever it finds it first, with
        // no notion of address or hash. Once `x` itself no longer holds
        // "2" (a direct edit moved it to "99"), the only place "2" still
        // appears in the file is the UNRELATED `other` binding, so the
        // naive search clobbers THAT, while the real rollback correctly
        // refuses (the hash it would roll back no longer matches anything).
        let scenario =
          { Seed = -2
            InitialX = 1L
            InitialOther = 0L
            Events =
              [ SimEvent.Tweak 2L // x: 1 -> 2 (applied)
                SimEvent.Save // logged: an op whose "after" text is "2"
                SimEvent.UserEditsFile 99L // x moves on directly; disk no longer holds "2" for x
                SimEvent.EditOtherBinding 2L // `other` now happens to hold the exact text "2"
                SimEvent.RollbackLast ] }
        let real = trace SaveBehavior.Real RollbackBehavior.Real CompactionBehavior.Real scenario |> List.last
        let twin = trace SaveBehavior.Real RollbackBehavior.NaiveLineBasedTwin CompactionBehavior.Real scenario |> List.last
        let otherOf (s: State) =
          TweakAddress.resolve s.Source ({ ModulePath = [ "M" ]; BindingName = "other"; Path = [] }: TweakAddress.TweakAddress)
          |> Result.map (fun r -> r.Text)
        otherOf real |> Expect.equal "the real, hash-checked rollback refuses (x no longer holds '2') and never touches the unrelated binding" (Ok "2")
        otherOf twin |> Expect.notEqual "the naive twin's blind text search finds '2' in the wrong place and clobbers it" (otherOf real)

      testCase "the invariant has teeth: some seeded scenario has the naive twin change the OTHER binding on a RollbackLast" <| fun _ ->
        let violating =
          seeds
          |> List.map (fun s ->
            let sc = scenarioOf s
            sc, trace SaveBehavior.Real RollbackBehavior.NaiveLineBasedTwin CompactionBehavior.Real sc)
          |> List.map (fun (sc, tr) -> TweakSimInvariants.otherBindingOnlyChangesWhenEdited sc tr)
          |> List.filter (List.isEmpty >> not)
        violating |> Expect.isNonEmpty "OTHER-BINDING-UNTOUCHED must catch the naive rollback on at least one seeded schedule"
    ]

    testList "TWIN: a compaction that drops an unsaved tweak" [

      testCase "REPRODUCED, an unsaved tweak sitting behind a burst of unrelated activity survives under Real, is lost under the twin" <| fun _ ->
        let scenario =
          { Seed = -3
            InitialX = 1L
            InitialOther = 0L
            // `EditOtherBinding` doesn't log anything, so the filler here
            // has to be something that DOES append events, `Reformat` is
            // address-independent bookkeeping, harmless to interleave.
            Events =
              [ SimEvent.Tweak 2L // applied, never saved, must never be compacted away
                SimEvent.Reformat
                SimEvent.Reformat
                SimEvent.Reformat
                SimEvent.Reformat
                SimEvent.Reformat
                SimEvent.Reformat
                SimEvent.Reformat ] }
        let real = trace SaveBehavior.Real RollbackBehavior.Real CompactionBehavior.Real scenario |> List.last
        let twin = trace SaveBehavior.Real RollbackBehavior.Real CompactionBehavior.DropsUnsavedTwin scenario |> List.last
        TweakLog.dirtySet real.Log |> Set.contains address |> Expect.isTrue "the real compactor keeps the unsaved tweak's dirty status visible"
        TweakLog.dirtySet twin.Log |> Set.contains address |> Expect.isFalse "the twin's count-only compaction drops the event that recorded it, silently losing the dirty flag"

      testCase "the invariant has teeth: some seeded scenario has DIRTY-SET-MATCHES-GROUND-TRUTH fail under the twin" <| fun _ ->
        let violating =
          seeds
          |> List.map (fun s -> trace SaveBehavior.Real RollbackBehavior.Real CompactionBehavior.DropsUnsavedTwin (scenarioOf s))
          |> List.map TweakSimInvariants.dirtySetMatchesGroundTruth
          |> List.filter (List.isEmpty >> not)
        violating |> Expect.isNonEmpty "DIRTY-SET-MATCHES-GROUND-TRUTH must catch the count-only compaction on at least one seeded schedule"
    ]

    testList "the matrix: every invariant holds under every CompactionMode x ReplayScope combination" [

      let matrix =
        [ for mode in [ TweakLog.CompactionMode.OnSessionClose; TweakLog.CompactionMode.LiveOnBudget ] do
            for scope in [ TweakLog.ReplayScope.SageFsWritesOnly; TweakLog.ReplayScope.EverythingAsDiffs ] ->
              { TweakSim.defaultSettings with CompactionMode = mode; ReplayScope = scope } ]

      for settings in matrix do
        testCase (sprintf "%A / %A" settings.CompactionMode settings.ReplayScope) <| fun _ ->
          let bad =
            seeds
            |> List.truncate 150
            |> List.map (fun s ->
              let sc = scenarioOf s
              let states = traceWith settings SaveBehavior.Real RollbackBehavior.Real CompactionBehavior.Real sc
              sc, TweakSimInvariants.all sc states, states)
            |> List.filter (fun (_, vs, _) -> not (List.isEmpty vs))
          bad
          |> List.map (fun (sc, vs, _) -> sc, vs)
          |> expectNone (sprintf "under %A / %A" settings.CompactionMode settings.ReplayScope)

      testCase "OPEN-CONFLICT-EXCLUSIVE: no save or rollback ever lands on an address with an open conflict" <| fun _ ->
        seeds
        |> List.map (fun s -> let sc = scenarioOf s in sc, TweakSimInvariants.openConflictBlocksSaveAndRollback (realTrace sc))
        |> List.filter (fun (_, vs) -> not (List.isEmpty vs))
        |> expectNone "a save or rollback landed while a conflict was open on that address"

      testCase "EVERYTHING-AS-DIFFS-REPLAYS-WHOLE-FILE: under that scope, replayWholeFile reproduces the file exactly" <| fun _ ->
        let settings = { TweakSim.defaultSettings with ReplayScope = TweakLog.ReplayScope.EverythingAsDiffs }
        seeds
        |> List.map (fun s ->
          let sc = scenarioOf s
          let states = traceWith settings SaveBehavior.Real RollbackBehavior.Real CompactionBehavior.Real sc
          sc, TweakSimInvariants.everythingAsDiffsReplaysWholeFileExactly (TweakSim.fileOf sc.InitialX sc.InitialOther) states)
        |> List.filter (fun (_, vs) -> not (List.isEmpty vs))
        |> expectNone "replayWholeFile disagreed with the actual final file"
    ]
  ]
