module SageFs.Tests.MachineTierTests

/// The machine tier: a closed set derived by a pure function from what a probe saw, a profile that
/// remembers the probe and how long starts have taken, and the resolution that decides which of
/// the override, the profile and a fresh probe is in force.
///
/// The probes below are the machines that were measured on 2026-10-01 with
/// `scripts/machine-bench.fsx` (see docs/TROUBLESHOOTING.md), so a boundary is checked against a
/// machine that exists and not against a number made up to fit.
open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs

let private propConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }

/// The Ryzen 7 5800XT the reference was taken on: 16 threads, 64 GB, NVMe, steady 24 ms.
let private fastDesktop : MachineProbe =
  { LogicalCores = 16
    TotalMemoryMb = 64215L
    AvailableMemoryMb = 39076L
    Storage = StorageKind.SolidState
    Calibration = Calibration.Measured (24.0, 30.0) }

/// The AMD Phenom II X4 B50 (4 cores, 7.9 GB, a 5400 rpm disk), steady 122 ms.
let private phenom : MachineProbe =
  { LogicalCores = 4
    TotalMemoryMb = 7913L
    AvailableMemoryMb = 4812L
    Storage = StorageKind.Rotational
    Calibration = Calibration.Measured (122.0, 111.0) }

let private tierGen : Gen<MachineTier> = Gen.elements MachineTier.all

let private probeGen : Gen<MachineProbe> =
  gen {
    let! cores = Gen.choose (1, 32)
    let! total = Gen.choose (512, 65536)
    let! availablePercent = Gen.choose (1, 100)
    let! storage = Gen.elements [ StorageKind.Rotational; StorageKind.SolidState; StorageKind.Unknown ]
    let! steady = Gen.choose (10, 400)
    let! measured = Gen.elements [ true; true; true; false ]
    return
      { LogicalCores = cores
        TotalMemoryMb = int64 total
        AvailableMemoryMb = int64 total * int64 availablePercent / 100L
        Storage = storage
        Calibration =
          (match measured with
           | true -> Calibration.Measured (float steady, float steady)
           | false -> Calibration.NotMeasured "test") }
  }

let private probeArb = Arb.fromGen probeGen

[<Tests>]
let tests =
  testList "Machine tier" [

    testList "the closed set" [

      testCase "WHY — every tier's name parses back to it, in any case, so the override variable and the status text agree" <| fun _ ->
        for tier in MachineTier.all do
          MachineTier.tryParse (MachineTier.toString tier) |> Expect.equal "round trip" (Ok tier)
          MachineTier.tryParse (MachineTier.toString tier).ToUpperInvariant() |> Expect.equal "upper case" (Ok tier)
          MachineTier.tryParse ("  " + (MachineTier.toString tier).ToLowerInvariant() + " ") |> Expect.equal "lower case, padded" (Ok tier)

      testCase "WHY — a typo in the override is an error that lists what is accepted, not a silent fallback" <| fun _ ->
        match MachineTier.tryParse "Quick" with
        | Ok tier -> failtestf "expected an error, got %A" tier
        | Error message ->
          for tier in MachineTier.all do
            message |> Expect.stringContains "names every accepted tier" (MachineTier.toString tier)

      testCase "WHY — an unset or unreadable environment value is Fast, the durations as written, so nothing is slowed by accident" <| fun _ ->
        MachineTier.ofEnvironmentValue null |> Expect.equal "unset" MachineTier.Fast
        MachineTier.ofEnvironmentValue "" |> Expect.equal "empty" MachineTier.Fast
        MachineTier.ofEnvironmentValue "nonsense" |> Expect.equal "unreadable" MachineTier.Fast
        MachineTier.ofEnvironmentValue "Constrained" |> Expect.equal "named" MachineTier.Constrained

      testCase "WHY — the factors start at 1 on Fast (nothing gets slower there) and only grow with the tier" <| fun _ ->
        MachineTier.factor MachineTier.Fast |> Expect.equal "Fast is the baseline" 1.0
        let factors = MachineTier.all |> List.map MachineTier.factor
        factors |> List.pairwise |> List.forall (fun (a, b) -> b > a) |> Expect.isTrue "each tier waits longer than the one before"

      testPropertyWithConfig propConfig "slowest is commutative, idempotent, and never faster than either argument" <|
        Prop.forAll (Arb.fromGen tierGen) (fun a ->
          Prop.forAll (Arb.fromGen tierGen) (fun b ->
            let s = MachineTier.slowest a b
            MachineTier.slowest b a = s
            && MachineTier.slowest a a = a
            && MachineTier.rank s >= MachineTier.rank a
            && MachineTier.rank s >= MachineTier.rank b))

      testPropertyWithConfig propConfig "scaleWait never shortens a wait, never exceeds the ceiling unless the wait already did, and is the identity on Fast" <|
        fun (PositiveInt seconds) ->
          let baseline = TimeSpan.FromSeconds(float seconds)
          [ for tier in MachineTier.all ->
              let scaled = MachineTier.scaleWait tier baseline
              scaled >= baseline
              && scaled <= max baseline MachineTier.scaledWaitCeiling
              && (tier <> MachineTier.Fast || scaled = baseline) ]
          |> List.forall id

      testPropertyWithConfig propConfig "scaleWait is monotonic in the tier" <|
        fun (PositiveInt seconds) ->
          let baseline = TimeSpan.FromSeconds(float seconds)
          MachineTier.all
          |> List.map (fun tier -> MachineTier.scaleWait tier baseline)
          |> List.pairwise
          |> List.forall (fun (a, b) -> b >= a)
    ]

    testList "the tier a probe gives" [

      testCase "WHY — the machines that were measured land where the measurements put them" <| fun _ ->
        MachineProbe.tierOf fastDesktop |> Expect.equal "the 5800XT desktop" MachineTier.Fast
        MachineProbe.tierOf phenom |> Expect.equal "the Phenom II X4 on a spinning disk" MachineTier.Constrained

      testCase "WHY — the Phenom is Constrained for more than one reason, so a faster disk alone would not move it to Fast" <| fun _ ->
        let withSsd = { phenom with Storage = StorageKind.SolidState }
        MachineProbe.tierOf withSsd |> Expect.equal "its CPU is five times slower than the reference" MachineTier.Constrained

      testCase "WHY — the speed boundaries sit where documented: 1.75 times the reference is the edge of Fast, 3.5 of Standard, 8 of Constrained" <| fun _ ->
        let at (slowness: float) =
          MachineProbe.speedTier (Calibration.Measured (MachineProbe.ReferenceCalibration * slowness, 0.0))
        at 1.74 |> Expect.equal "just under 1.75" MachineTier.Fast
        at 1.75 |> Expect.equal "at 1.75" MachineTier.Standard
        at 3.49 |> Expect.equal "just under 3.5" MachineTier.Standard
        at 3.5 |> Expect.equal "at 3.5" MachineTier.Constrained
        at 7.99 |> Expect.equal "just under 8" MachineTier.Constrained
        at 8.0 |> Expect.equal "at 8" MachineTier.Minimal

      testCase "WHY — a calibration that was not taken says nothing about the CPU, so the other limits decide" <| fun _ ->
        let probe = { phenom with Calibration = Calibration.NotMeasured "the timer failed" }
        MachineProbe.speedTier probe.Calibration |> Expect.equal "no opinion on speed" MachineTier.Fast
        MachineProbe.tierOf probe |> Expect.equal "the disk still makes it Constrained" MachineTier.Constrained

      testCase "WHY — a spinning disk makes a machine at least Constrained, whatever else it has" <| fun _ ->
        MachineProbe.tierOf { fastDesktop with Storage = StorageKind.Rotational }
        |> Expect.equal "fast CPU, many cores, plenty of memory, spinning disk" MachineTier.Constrained

      testPropertyWithConfig propConfig "the tier is the slowest of what the CPU, cores, memory and disk each give" <|
        Prop.forAll probeArb (fun probe ->
          MachineProbe.tierOf probe
          = ([ MachineProbe.speedTier probe.Calibration
               MachineProbe.coreTier probe.LogicalCores
               MachineProbe.memoryTier probe.AvailableMemoryMb
               MachineProbe.storageTier probe.Storage ]
             |> List.reduce MachineTier.slowest))

      testPropertyWithConfig propConfig "fewer cores never give a faster tier" <|
        Prop.forAll probeArb (fun probe ->
          Prop.forAll (Arb.fromGen (Gen.choose (1, 32))) (fun cores ->
            let fewer = min cores probe.LogicalCores
            MachineTier.rank (MachineProbe.tierOf { probe with LogicalCores = fewer })
            >= MachineTier.rank (MachineProbe.tierOf probe)
            || fewer = probe.LogicalCores))

      testPropertyWithConfig propConfig "less memory never gives a faster tier" <|
        Prop.forAll probeArb (fun probe ->
          Prop.forAll (Arb.fromGen (Gen.choose (0, 65536))) (fun mb ->
            let less = min (int64 mb) probe.AvailableMemoryMb
            MachineTier.rank (MachineProbe.tierOf { probe with AvailableMemoryMb = less })
            >= MachineTier.rank (MachineProbe.tierOf probe)))

      testPropertyWithConfig propConfig "a slower CPU never gives a faster tier" <|
        Prop.forAll probeArb (fun probe ->
          Prop.forAll (Arb.fromGen (Gen.choose (1, 1000))) (fun extra ->
            match probe.Calibration with
            | Calibration.NotMeasured _ -> true
            | Calibration.Measured (steady, first) ->
              let slower = { probe with Calibration = Calibration.Measured (steady + float extra, first) }
              MachineTier.rank (MachineProbe.tierOf slower) >= MachineTier.rank (MachineProbe.tierOf probe)))

      testPropertyWithConfig propConfig "a disk that spins never gives a faster tier than one that does not" <|
        Prop.forAll probeArb (fun probe ->
          MachineTier.rank (MachineProbe.tierOf { probe with Storage = StorageKind.Rotational })
          >= MachineTier.rank (MachineProbe.tierOf { probe with Storage = StorageKind.SolidState }))
    ]

    testList "what the machine teaches" [

      testCase "WHY — the first observation of a stage sets the mean to it and the deviation to half of it, as RFC 6298 says" <| fun _ ->
        let estimate = StageEstimate.first StartStage.WorkerPort TestTimeouts.StartEscalationTimeouts.firstObservation.TotalMilliseconds
        estimate.SmoothedMs |> Expect.equal "mean" TestTimeouts.StartEscalationTimeouts.firstObservation.TotalMilliseconds
        estimate.DeviationMs |> Expect.equal "deviation" (TestTimeouts.StartEscalationTimeouts.firstObservation.TotalMilliseconds / 2.0)
        estimate.Samples |> Expect.equal "samples" 1
        StageEstimate.timeout estimate
        |> Expect.equal "mean plus four deviations is three times the observation"
             (TimeSpan.FromMilliseconds(TestTimeouts.StartEscalationTimeouts.firstObservation.TotalMilliseconds * 3.0))

      testCase "WHY — a later observation moves the mean an eighth of the way and the deviation a quarter of the way, as RFC 6298 says" <| fun _ ->
        let first = TestTimeouts.StartEscalationTimeouts.firstObservation.TotalMilliseconds
        let later = TestTimeouts.StartEscalationTimeouts.laterObservation.TotalMilliseconds
        let estimate = StageEstimate.observe (StageEstimate.first StartStage.WorkerPort first) later
        estimate.SmoothedMs |> Expect.floatClose Accuracy.high "mean" (first * 7.0 / 8.0 + later / 8.0)
        estimate.DeviationMs |> Expect.floatClose Accuracy.high "deviation" ((first / 2.0) * 3.0 / 4.0 + (later - first) / 4.0)
        estimate.Samples |> Expect.equal "samples" 2

      testPropertyWithConfig propConfig "steady observations converge: the timeout settles on the observation, from above" <|
        fun (PositiveInt seconds) ->
          let d = float seconds * 1000.0
          let converged =
            List.init 80 id |> List.fold (fun e _ -> StageEstimate.observe e d) (StageEstimate.first StartStage.WorkerPort (d * 3.0))
          let timeout = StageEstimate.timeout converged
          timeout.TotalMilliseconds >= d && timeout.TotalMilliseconds < d * 1.01

      testPropertyWithConfig propConfig "the timeout is never below the mean" <|
        fun (observations: PositiveInt list) ->
          match observations with
          | [] -> true
          | head :: tail ->
            let e = tail |> List.fold (fun e (PositiveInt ms) -> StageEstimate.observe e (float ms)) (StageEstimate.first StartStage.WorkerReady (float (let (PositiveInt v) = head in v)))
            (StageEstimate.timeout e).TotalMilliseconds >= e.SmoothedMs

      testCase "WHY — a profile keeps one estimate per stage, and a second observation updates it instead of adding another" <| fun _ ->
        let profile =
          MachineProfile.ofProbe phenom
          |> MachineProfile.observe StartStage.WorkerPort TestTimeouts.StartEscalationTimeouts.firstObservation.TotalMilliseconds
          |> MachineProfile.observe StartStage.WorkerPort TestTimeouts.StartEscalationTimeouts.laterObservation.TotalMilliseconds
          |> MachineProfile.observe StartStage.WorkerReady TestTimeouts.StartEscalationTimeouts.laterObservation.TotalMilliseconds
        profile.Stages |> List.length |> Expect.equal "one per stage" 2
        match MachineProfile.stage StartStage.WorkerPort profile with
        | StageHistory.Seen e -> e.Samples |> Expect.equal "two observations of the port stage" 2
        | StageHistory.NeverSeen -> failtest "the stage was observed"
        MachineProfile.ofProbe phenom
        |> MachineProfile.stage StartStage.WorkerPort
        |> Expect.equal "a stage never seen says so" StageHistory.NeverSeen

      testCase "WHY — a profile survives being written and read back through SageFs.Json, on this runtime" <| fun _ ->
        let profile =
          MachineProfile.ofProbe phenom
          |> MachineProfile.observe StartStage.WorkerPort TestTimeouts.StartEscalationTimeouts.learnedStart.TotalMilliseconds
        let text = Json.serialize (Json.indented Json.standard) profile
        Json.deserialize<MachineProfile> Json.standard text
        |> Expect.equal "the same profile comes back" (Ok profile)
    ]

    testList "which tier is in force" [

      let neverProbed () : Result<MachineProbe, string> = failwith "the probe must not be taken"
      let noProfile () = ProfileRead.NoProfile

      testCase "WHY — an override that names a tier wins over everything, and nothing else is read or measured" <| fun _ ->
        let resolved = TierResolution.resolve "minimal" (fun () -> failwith "the profile must not be read") neverProbed
        resolved.Tier |> Expect.equal "the named tier" MachineTier.Minimal
        resolved.Source |> Expect.equal "forced" TierSource.Forced
        resolved.Override |> Expect.equal "honoured" OverrideUse.Honoured

      testCase "WHY — a profile from an earlier run is used without measuring again" <| fun _ ->
        let profile = MachineProfile.ofProbe phenom
        let resolved = TierResolution.resolve null (fun () -> ProfileRead.Found ("/data/machine-profile.json", profile)) neverProbed
        resolved.Tier |> Expect.equal "the profile's tier" MachineTier.Constrained
        resolved.Source |> Expect.equal "from the profile" (TierSource.FromProfile ("/data/machine-profile.json", phenom))

      testCase "WHY — with no profile the machine is probed, and the tier is the probe's" <| fun _ ->
        let resolved = TierResolution.resolve null noProfile (fun () -> Ok fastDesktop)
        resolved.Tier |> Expect.equal "Fast" MachineTier.Fast
        resolved.Source |> Expect.equal "probed" (TierSource.Probed fastDesktop)

      testCase "WHY — a profile that cannot be read is probed past, not trusted and not fatal" <| fun _ ->
        let resolved = TierResolution.resolve "" (fun () -> ProfileRead.Unreadable "truncated") (fun () -> Ok phenom)
        resolved.Tier |> Expect.equal "the probe decides" MachineTier.Constrained

      testCase "WHY — an override that is not a tier is reported and ignored, and the machine decides" <| fun _ ->
        let resolved = TierResolution.resolve "Quick" noProfile (fun () -> Ok phenom)
        resolved.Tier |> Expect.equal "the probe decides" MachineTier.Constrained
        resolved.Override |> Expect.equal "the rejected value is carried" (OverrideUse.Rejected "Quick")

      testCase "WHY — when even the probe fails the tier is Standard and the reason is kept, so a failure never makes a machine look fast" <| fun _ ->
        let resolved = TierResolution.resolve null noProfile (fun () -> Error "no counters")
        resolved.Tier |> Expect.equal "neutral" MachineTier.Standard
        resolved.Source |> Expect.equal "the reason" (TierSource.ProbeFailed "no counters")
    ]

    testList "on this machine" [

      testCase "WHY — the calibration workload runs and reports a positive time, first and steady" <| fun _ ->
        let m = MachineCalibration.measure ()
        m.FirstMs > 0.0 |> Expect.isTrue "first run measured"
        m.SteadyMs > 0.0 |> Expect.isTrue "steady run measured"

      testCase "WHY — a probe of this machine is plausible, so the reader is wired to real counters" <| fun _ ->
        match MachineProbeReader.read (Path.GetTempPath()) with
        | Error reason -> failtestf "the probe failed: %s" reason
        | Ok probe ->
          probe.LogicalCores >= 1 |> Expect.isTrue "at least one core"
          probe.TotalMemoryMb > 0L |> Expect.isTrue "some memory"
          probe.AvailableMemoryMb <= probe.TotalMemoryMb |> Expect.isTrue "available is within total"
          (match probe.Calibration with
           | Calibration.Measured (steady, _) -> steady > 0.0 |> Expect.isTrue "calibrated"
           | Calibration.NotMeasured reason -> failtestf "the calibration was not taken: %s" reason)

      testCase "WHY — a profile written to a data directory is read back, and a missing or corrupt one is said to be" <| fun _ ->
        let dir = Path.Combine(Path.GetTempPath(), "sagefs-machine-profile-" + Guid.NewGuid().ToString("N"))
        try
          MachineProbeReader.readProfile dir |> Expect.equal "nothing there yet" ProfileRead.NoProfile
          let profile = MachineProfile.ofProbe phenom
          MachineProbeReader.writeProfile dir profile |> Expect.equal "written" (Ok ())
          (match MachineProbeReader.readProfile dir with
           | ProfileRead.Found (_, read) -> read |> Expect.equal "the same profile" profile
           | other -> failtestf "expected the profile, got %A" other)
          File.WriteAllText(MachineProbeReader.profilePath dir, "{ not json")
          (match MachineProbeReader.readProfile dir with
           | ProfileRead.Unreadable _ -> ()
           | other -> failtestf "expected Unreadable, got %A" other)
        finally
          try Directory.Delete(dir, true) with _ -> ()
    ]
  ]
