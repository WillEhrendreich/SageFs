/// Deterministic Simulation Testing for the settings layer file: two tabs saving, a crash in a write, a corrupt file and
/// a restart, folded through the real catalog and store. The twin is the read-modify-write without the lock; the
/// no-lost-update invariant catches it. Sequenced, because the Live settings write the process-global `Timeouts`.
module SageFs.Tests.SettingsStoreSimDstTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Simulation
open SageFs.Simulation.SettingsStoreSim

let private seeds = [ 1 .. 150 ]

let private violationsFor (behavior: Behavior) (invariant: State list -> SettingsStoreSimInvariants.Violation list) =
  seeds
  |> List.map (fun seed -> let scenario = scenarioOf seed in scenario, invariant (trace behavior scenario))
  |> List.filter (fun (_, vs) -> not (List.isEmpty vs))

let private expectNone (label: string) (bad: (Scenario * SettingsStoreSimInvariants.Violation list) list) =
  match bad with
  | [] -> ()
  | _ ->
    failtestf "%s: %d of %d seeds.\n%s" label bad.Length seeds.Length
      (bad
       |> List.truncate 3
       |> List.map (fun (s, vs) ->
         sprintf "seed=%d ops=%A\n  %s" s.Seed s.Ops
           (vs |> List.truncate 3 |> List.map (fun v -> sprintf "[%d] %s" v.Index v.Why) |> String.concat "\n  "))
       |> String.concat "\n")

[<Tests>]
let settingsStoreSimDstTests =
  testSequenced <| testList "Settings store DST" [

    testCase "the scenarios are deterministic: the same seed gives the same ops" <| fun _ ->
      (scenarioOf 7).Ops |> Expect.equal "replaying a seed gives the identical schedule" (scenarioOf 7).Ops

    testList "the real store holds every invariant" [
      testCase "NO-LOST-UPDATE: the file holds the last accepted edit of each key" <| fun _ ->
        violationsFor Behavior.Real SettingsStoreSimInvariants.noLostUpdate
        |> expectNone "an accepted edit is missing from the file"

      testCase "A-CRASH-IN-A-WRITE-LEAVES-THE-OLD-FILE" <| fun _ ->
        violationsFor Behavior.Real SettingsStoreSimInvariants.crashLeavesTheOldFile
        |> expectNone "a crashed write changed the layer"

      testCase "A-CORRUPT-LAYER-IS-AN-ERROR-NAMING-THE-FILE, NEVER-A-VALUE" <| fun _ ->
        violationsFor Behavior.Real SettingsStoreSimInvariants.corruptLayerIsNeverAValue
        |> expectNone "a corrupt layer resolved to a value, or a save over it lost the person's bytes"

      testCase "AFTER-A-RESTART-EVERY-LIVE-SETTING-IS-IN-USE-AT-ITS-SAVED-VALUE" <| fun _ ->
        violationsFor Behavior.Real SettingsStoreSimInvariants.restartAppliesWhatWasSaved
        |> expectNone "a saved Live setting was not applied at start"
    ]

    testList "the seeds reach the interesting cases, so a green run means something" [
      let traces = seeds |> List.map (scenarioOf >> trace Behavior.Real)
      let ops = traces |> List.collect (List.choose (fun s -> s.Op))

      testCase "some seeds put two tabs' edits next to each other" <| fun _ ->
        seeds
        |> List.exists (fun seed ->
          (scenarioOf seed).Ops
          |> List.pairwise
          |> List.exists (fun (a, b) ->
            match a, b with
            | (Op.Edit(x, _, _) | Op.Clear(x, _)), (Op.Edit(y, _, _) | Op.Clear(y, _)) -> x <> y
            | _ -> false))
        |> Expect.isTrue "at least one seed has neighbouring edits from different tabs"

      testCase "some seeds crash a write, corrupt the file and restart" <| fun _ ->
        ops |> List.exists (fun o -> o = Op.CrashDuringWrite) |> Expect.isTrue "a crash"
        ops |> List.exists (fun o -> o = Op.CorruptFile) |> Expect.isTrue "a corrupt file"
        ops |> List.exists (fun o -> o = Op.Restart) |> Expect.isTrue "a restart"

      testCase "some restarts follow a saved Live value, so the check has something to apply" <| fun _ ->
        traces
        |> List.exists (List.exists (fun s ->
          s.RestartMismatches.IsSome && s.Expected.ContainsKey SettingsCatalog.perTestTimeout.Key))
        |> Expect.isTrue "at least one restart with a saved per-test timeout"
    ]

    testCase "TWIN: the read-modify-write without a lock loses an update, and the no-lost-update invariant catches it" <| fun _ ->
      violationsFor Behavior.UnlockedTwin SettingsStoreSimInvariants.noLostUpdate
      |> List.isEmpty
      |> Expect.isFalse "two tabs reading before either writes drop a key, and the sim sees it"

    testCase "two writers at once, for real: every key both of them saved is in the file" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "sagefs-settings-race-" + Guid.NewGuid().ToString("N").[..7])
      Directory.CreateDirectory dir |> ignore
      try
        let path = SettingsStore.globalPath dir
        let rounds = 200
        let writer (prefix: string) =
          Task.Run(fun () ->
            for round in 1 .. rounds do
              SettingsStore.setKey path (sprintf "%s.%d" prefix round) "1" |> ignore)
        Task.WaitAll [| writer "tabA"; writer "tabB" |]
        let layer = SettingsStore.readLayer path
        layer.Count |> Expect.equal "both tabs' keys are all there" (2 * rounds)
      finally
        try Directory.Delete(dir, true) with _ -> ()

    testCase "TWIN, for real: the unlocked halves with both reads before either write lose a key every time" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "sagefs-settings-race-twin-" + Guid.NewGuid().ToString("N").[..7])
      Directory.CreateDirectory dir |> ignore
      try
        let path = SettingsStore.globalPath dir
        use bothHaveRead = new Barrier(2)
        let unlocked (key: string) =
          Task.Run(fun () ->
            let read = SettingsStore.readLayer path
            bothHaveRead.SignalAndWait()
            SettingsStore.commit path (Map.add key "1" read) |> ignore)
        Task.WaitAll [| unlocked "tabA"; unlocked "tabB" |]
        (SettingsStore.readLayer path).Count |> Expect.equal "one of the two keys was dropped" 1
      finally
        try Directory.Delete(dir, true) with _ -> ()
  ]
