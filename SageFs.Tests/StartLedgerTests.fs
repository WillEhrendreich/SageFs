module SageFs.Tests.StartLedgerTests

/// What a machine teaches SageFs about its own start times, kept between runs: the daemon records how long
/// each stage took, the next daemon reads it, and the first attempt of the next start is as patient as
/// the machine has shown it needs to be.
open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Tests.StartEscalationTimeouts

let private probe : MachineProbe =
  { LogicalCores = 4
    TotalMemoryMb = 7913L
    AvailableMemoryMb = 4812L
    Storage = StorageKind.Rotational
    Calibration = Calibration.Measured (122.0, 111.0) }

let private withDataDir (run: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-start-ledger-" + Guid.NewGuid().ToString("N"))
  try
    StartLedger.close ()
    run dir
  finally
    StartLedger.close ()
    try Directory.Delete(dir, true) with _ -> ()

[<Tests>]
let tests =
  testList "Start ledger" [

    testCase "WHY — a ledger that was never opened knows nothing and records nothing, so a process that is not the daemon cannot write a profile" <| fun _ ->
      StartLedger.close ()
      StartLedger.history StartStage.WorkerPort |> Expect.equal "nothing known" StageHistory.NeverSeen
      StartLedger.record StartStage.WorkerPort slowHealthyStart
      StartLedger.history StartStage.WorkerPort |> Expect.equal "still nothing" StageHistory.NeverSeen

    testCase "WHY — an observation is remembered, and written to the profile so the next daemon starts already knowing the machine" <| fun _ ->
      withDataDir <| fun dir ->
        StartLedger.initialise dir (MachineProfile.ofProbe probe)
        StartLedger.record StartStage.WorkerPort slowHealthyStart
        (match StartLedger.history StartStage.WorkerPort with
         | StageHistory.Seen estimate ->
           estimate.Samples |> Expect.equal "one sample" 1
           estimate.SmoothedMs |> Expect.equal "the observation" slowHealthyStart.TotalMilliseconds
         | StageHistory.NeverSeen -> failtest "the observation was recorded")
        // A new process reads what the first one wrote.
        StartLedger.close ()
        (match MachineProbeReader.readProfile dir with
         | ProfileRead.Found (_, profile) ->
           (MachineProfile.stage StartStage.WorkerPort profile |> function StageHistory.Seen e -> e.Samples | StageHistory.NeverSeen -> 0)
           |> Expect.equal "the profile on disk holds the observation" 1
         | other -> failtestf "expected a profile on disk, got %A" other)

    testCase "WHY — opening the ledger on an existing profile keeps what it already learned, and adds to it" <| fun _ ->
      withDataDir <| fun dir ->
        let learned = MachineProfile.ofProbe probe |> MachineProfile.observe StartStage.WorkerPort learnedStart.TotalMilliseconds
        StartLedger.initialise dir learned
        StartLedger.record StartStage.WorkerPort quickStart
        match StartLedger.history StartStage.WorkerPort with
        | StageHistory.Seen estimate -> estimate.Samples |> Expect.equal "the earlier sample and the new one" 2
        | StageHistory.NeverSeen -> failtest "history was kept"

    testCase "WHY — a stage the ledger has not seen says NeverSeen even when another stage has been seen" <| fun _ ->
      withDataDir <| fun dir ->
        StartLedger.initialise dir (MachineProfile.ofProbe probe)
        StartLedger.record StartStage.WorkerPort slowHealthyStart
        StartLedger.history StartStage.WorkerReady |> Expect.equal "other stage" StageHistory.NeverSeen

    testCase "WHY — the base profile for a daemon is the one on disk if there is one, else the probe it just took" <| fun _ ->
      withDataDir <| fun dir ->
        let fresh = { Tier = MachineTier.Constrained; Source = TierSource.Probed probe; Override = OverrideUse.NotSet }
        StartLedger.baseProfile dir fresh |> Expect.equal "from the probe" (MachineProfile.ofProbe probe)
        let learned = MachineProfile.ofProbe probe |> MachineProfile.observe StartStage.WorkerPort learnedStart.TotalMilliseconds
        MachineProbeReader.writeProfile dir learned |> Expect.equal "written" (Ok ())
        StartLedger.baseProfile dir fresh |> Expect.equal "from the file" learned
  ]
