module SageFs.Tests.ProfileRecorderSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.ProfileRecorderSim

/// DST for the profile tool's recorder. See `SageFs.Simulation/ProfileRecorderSim.fs` for the simulated collector and disk, the
/// fault-to-outcome table the invariants read, and the three twins.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs -> failtestf "INVARIANT VIOLATION (%s) - replay Seed=%d Fault=%A EvalThrows=%b\n  %A" t.Reducer t.Scenario.Seed t.Scenario.Fault t.Scenario.EvalThrows vs

let private violatedIds (t: Trace) : string list = violations t |> List.map fst

let private scenario (fault: Fault) (evalThrows: bool) : Scenario = { Seed = 0; Fault = fault; EvalThrows = evalThrows }

let private everyFault =
  [ Fault.NoFault; Fault.ToolNotInstalled; Fault.AttachFails; Fault.HostEndsMidRecording; Fault.StopTimesOut; Fault.DiskFull; Fault.CorruptTrace ]

[<Tests>]
let tests =
  testList "DST profile recorder" [

    testList "the real recorder holds the invariants" [
      testPropertyWithConfig simConfig
        "seeded scenarios - no file outlives the call, the code runs once under a started recording and never under a refused one, and every fault gives its named outcome"
        <| fun (seed: int) -> assertHolds (run Policy.Real (fromSeed seed))

      testCase "every fault, with the eval returning and with it throwing, holds the invariants" <| fun _ ->
        for fault in everyFault do
          for throws in [ false; true ] do
            assertHolds (run Policy.Real (scenario fault throws))

      testCase "a clean run reports the hot method first with the whole managed time" <| fun _ ->
        match (run Policy.Real (scenario Fault.NoFault false)).Outcome with
        | Outcome.Reported report ->
          (report.SelfRows |> List.head |> fun r -> r.Label) |> Expect.equal "hot method first" "FSI-ASSEMBLY!FSI_0008.hotFib"
          report.ManagedMs |> Expect.equal "30 ms of managed CPU" 30.0
          report.NativeMs |> Expect.equal "500 ms in native code, counted apart" 500.0
        | other -> failtestf "expected a report, got %A" other

      testCase "a host that ends mid-recording gives HostGone and the eval's own result is kept" <| fun _ ->
        let t = run Policy.Real (scenario Fault.HostEndsMidRecording false)
        caseNameOf (match t.Outcome with | Outcome.TraceFailed f -> f | other -> failtestf "got %A" other) |> Expect.equal "named" "HostGone"
        t.EvalSeen |> Expect.equal "the eval's result survives" (Some "eval-result")
        t.FilesLeft |> Expect.isEmpty "and the file is gone"

      testCase "a refused recording never runs the code" <| fun _ ->
        for fault in [ Fault.ToolNotInstalled; Fault.AttachFails ] do
          let t = run Policy.Real (scenario fault false)
          t.EvalRuns |> Expect.equal "the user's code did not run" 0
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED - the leaves-file twin keeps the trace on the disk when the host ends" <| fun _ ->
        let t = run Policy.LeavesFileWhenHostEnds (scenario Fault.HostEndsMidRecording false)
        violatedIds t |> Expect.contains "a leftover trace must be caught" "no-file-outlives-the-call"

      testCase "REPRODUCED - the runs-eval-when-refused twin runs the code under a refused recording" <| fun _ ->
        let t = run Policy.RunsEvalWhenRefused (scenario Fault.ToolNotInstalled false)
        violatedIds t |> Expect.contains "code run under a refusal must be caught" "eval-runs-once-under-a-recording-and-never-under-a-refusal"

      testCase "REPRODUCED - the skips-finish twin leaves a recording running when the eval throws" <| fun _ ->
        let t = run Policy.SkipsFinishWhenEvalThrows (scenario Fault.NoFault true)
        violatedIds t |> Expect.contains "an unfinished recording must be caught" "a-started-recording-is-finished-once"

      testCase "the invariants have teeth: some seeded scenario violates them under each twin" <| fun _ ->
        let seeds = [ 1 .. 300 ]
        let anyViolates policy = seeds |> List.exists (fun seed -> run policy (fromSeed seed) |> violations |> List.isEmpty |> not)
        anyViolates Policy.LeavesFileWhenHostEnds |> Expect.isTrue "a leftover file is caught by a generated scenario"
        anyViolates Policy.RunsEvalWhenRefused |> Expect.isTrue "code under a refusal is caught by a generated scenario"
        anyViolates Policy.SkipsFinishWhenEvalThrows |> Expect.isTrue "an unfinished recording is caught by a generated scenario"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical outcome and counters (real recorder)" <| fun (seed: int) ->
        let a = run Policy.Real (fromSeed seed)
        let b = run Policy.Real (fromSeed seed)
        a.Outcome = b.Outcome && a.EvalRuns = b.EvalRuns && a.FinishCalls = b.FinishCalls && a.FilesLeft = b.FilesLeft
    ]
  ]
