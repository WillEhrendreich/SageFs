module SageFs.Tests.TierAdmissionTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Build.TierPlan

/// The decision a scheduler makes before it starts a tier process: pure over a snapshot of the machine. The cases
/// pin each rule alone, against `Admission.standard`, so a threshold that moves breaks the case that names it.

let private gib (n: int) = int64 n * Admission.bytesPerGiB

let private limits = Admission.standard 4

let private head = { Label = "--integration-host[1/5]"; PeakBytes = gib 2 }

let private calm = { Pressure = Measured 5.0; Memory = AvailableBytes (gib 40) }

let private running (label: string) (startedAt: float) =
  { Unit = { Label = label; PeakBytes = gib 2 }; StartedAt = startedAt }

let private afterRamp = Admission.rampSeconds * 2.0

let private scheduleWith (runningUnits: RunningUnit list) (lastAdmit: LastAdmit) =
  { Waiting = [ head ]; HeadSince = 0.0; Running = runningUnits; LastAdmit = lastAdmit }

let private decideNow (reading: Reading) (now: float) (schedule: Schedule) =
  decide (ByPressure limits) reading now schedule head

[<Tests>]
let tests =
  testList "TierPlan admission" [

    testCase "WHY — an idle machine admits the first unit at once, because nothing is running to wait for" <| fun _ ->
      decideNow calm 0.0 (scheduleWith [] NeverAdmitted)
      |> Expect.equal "admitted on fit" (Admit Fits)

    testCase "WHY — CPU pressure at the bound holds a unit back once the floor of units already runs" <| fun _ ->
      let busy = { calm with Pressure = Measured Admission.cpuPressureBound }
      let floor = [ for i in 1 .. Admission.pressureFloorUnits -> running (sprintf "u%d" i) 0.0 ]
      match decideNow busy afterRamp (scheduleWith floor (AdmittedAt 0.0)) with
      | Wait (CpuBusy (percent, bound)) ->
        percent |> Expect.equal "names the reading" Admission.cpuPressureBound
        bound |> Expect.equal "names the bound" Admission.cpuPressureBound
      | other -> failtestf "expected CpuBusy, got %A" other

    testCase "WHY — a busy neighbour cannot stall the gate below the floor: pressure is ignored while fewer units run" <| fun _ ->
      let busy = { calm with Pressure = Measured 95.0 }
      let below = [ for i in 1 .. Admission.pressureFloorUnits - 1 -> running (sprintf "u%d" i) 0.0 ]
      decideNow busy afterRamp (scheduleWith below (AdmittedAt 0.0))
      |> Expect.equal "still admitted" (Admit Fits)

    testCase "WHY — memory must cover the reserve plus the unit's own peak" <| fun _ ->
      let needed = Admission.memoryReserveBytes + head.PeakBytes
      let short = { calm with Memory = AvailableBytes (needed - 1L) }
      match decideNow short 0.0 (scheduleWith [ running "u1" 0.0 ] (AdmittedAt 0.0)) with
      | Wait (MemoryShort (n, a)) ->
        n |> Expect.equal "needs reserve + peak (the running unit has finished ramping)" needed
        a |> Expect.equal "against what is available" (needed - 1L)
      | other -> failtestf "expected MemoryShort, got %A" other
      decideNow { calm with Memory = AvailableBytes needed } afterRamp (scheduleWith [ running "u1" 0.0 ] (AdmittedAt 0.0))
      |> Expect.equal "exactly enough admits" (Admit Fits)

    testCase "WHY — memory a running unit has not reached yet is still reserved for it" <| fun _ ->
      let needed = Admission.memoryReserveBytes + head.PeakBytes
      let justEnough = { calm with Memory = AvailableBytes needed }
      match decideNow justEnough 0.0 (scheduleWith [ running "u1" 0.0 ] (AdmittedAt (-10.0))) with
      | Wait (MemoryShort (n, _)) -> n |> Expect.isGreaterThan "the fresh unit's whole peak is added" needed
      | other -> failtestf "a unit that just started still holds its peak in reserve, got %A" other

    testCase "WHY — two starts are never closer than the settle time, because avg10 and memory lag a start" <| fun _ ->
      let justAfter = Admission.settleSeconds / 2.0
      match decideNow calm justAfter (scheduleWith [ running "u1" 0.0 ] (AdmittedAt 0.0)) with
      | Wait (Settling remaining) -> remaining |> Expect.floatClose Accuracy.medium "the rest of the settle time" (Admission.settleSeconds - justAfter)
      | other -> failtestf "expected Settling, got %A" other

    testCase "WHY — the hard cap holds whatever the machine says" <| fun _ ->
      let full = [ for i in 1 .. Admission.maxTierProcesses -> running (sprintf "u%d" i) 0.0 ]
      decideNow calm afterRamp (scheduleWith full (AdmittedAt 0.0))
      |> Expect.equal "at the cap" (Wait (AtProcessCap Admission.maxTierProcesses))

    testCase "WHY — an unreadable machine falls back to the static concurrency, never to unbounded" <| fun _ ->
      let blind = { Pressure = NotMeasured "no /proc/pressure"; Memory = AvailableBytes (gib 40) }
      let atFallback = [ for i in 1 .. 4 -> running (sprintf "u%d" i) 0.0 ]
      decideNow blind afterRamp (scheduleWith atFallback (AdmittedAt 0.0))
      |> Expect.equal "the fallback is the cap" (Wait (AtProcessCap 4))
      decideNow blind afterRamp (scheduleWith (List.take 3 atFallback) (AdmittedAt 0.0))
      |> Expect.equal "below it, admitted" (Admit Fits)

    testCase "WHY — an explicit request is not second-guessed: a fixed count admits up to it and no further" <| fun _ ->
      let tight = { Pressure = Measured 99.0; Memory = AvailableBytes 0L }
      decide (Fixed 2) tight 0.0 (scheduleWith [ running "u1" 0.0 ] (AdmittedAt 0.0)) head
      |> Expect.equal "pressure and memory are ignored" (Admit Fits)
      decide (Fixed 2) tight 0.0 (scheduleWith [ running "u1" 0.0; running "u2" 0.0 ] (AdmittedAt 0.0)) head
      |> Expect.equal "the count is" (Wait (AtProcessCap 2))

    testCase "WHY — with nothing of ours running, a unit that waited out the patience starts anyway, so a neighbour cannot starve the gate" <| fun _ ->
      let starved = { calm with Memory = AvailableBytes 0L }
      let schedule = scheduleWith [] NeverAdmitted
      match decideNow starved (Admission.patienceSeconds - 1.0) schedule with
      | Wait (MemoryShort _) -> ()
      | other -> failtestf "before the patience it waits, got %A" other
      decideNow starved Admission.patienceSeconds schedule
      |> Expect.equal "after the patience it starts, and says why" (Admit StarvationGuard)

    testCase "WHY — the starvation guard never fires while one of our own units runs: it frees memory by finishing" <| fun _ ->
      let starved = { calm with Memory = AvailableBytes 0L }
      match decideNow starved (Admission.patienceSeconds * 10.0) (scheduleWith [ running "u1" 0.0 ] (AdmittedAt 0.0)) with
      | Wait (MemoryShort _) -> ()
      | other -> failtestf "expected MemoryShort, got %A" other

    testCase "WHY — advance looks at the head of the line only, so the order the caller chose is the order units start in" <| fun _ ->
      let big = { Label = "big"; PeakBytes = gib 30 }
      let small = { Label = "small"; PeakBytes = gib 1 }
      let tight = { calm with Memory = AvailableBytes (gib 20) }
      let schedule = { startSchedule 0.0 [ big; small ] with Running = [ running "u1" 0.0 ]; LastAdmit = AdmittedAt 0.0 }
      match advance (ByPressure limits) tight afterRamp schedule with
      | Held (MemoryShort _) -> ()
      | other -> failtestf "the small unit must not jump the big one, got %A" other

    testCase "WHY — a started unit leaves the line, joins the running set and becomes the last start" <| fun _ ->
      let schedule = startSchedule 0.0 [ head ]
      match advance (ByPressure limits) calm 7.0 schedule with
      | Started (unit, basis, next) ->
        unit |> Expect.equal "the head" head
        basis |> Expect.equal "on fit" Fits
        next.Waiting |> Expect.isEmpty "left the line"
        next.Running |> Expect.equal "is running since now" [ { Unit = head; StartedAt = 7.0 } ]
        next.LastAdmit |> Expect.equal "is the last start" (AdmittedAt 7.0)
        next.HeadSince |> Expect.equal "the next head starts waiting now" 7.0
      | other -> failtestf "expected Started, got %A" other
      advance (ByPressure limits) calm 7.0 (startSchedule 0.0 []) |> Expect.equal "an empty line is drained" Drained

    testCase "WHY — a finished unit frees its place and only its own" <| fun _ ->
      let schedule = { startSchedule 0.0 [] with Running = [ running "a" 0.0; running "b" 0.0 ] }
      (finishUnit "a" schedule).Running
      |> List.map (fun r -> r.Unit.Label)
      |> Expect.equal "b still runs" [ "b" ]

    // ── reading the machine, against the real files' shapes ──
    testCase "WHY — the pressure reading is the `some` line's avg10, not the `full` line's" <| fun _ ->
      "some avg10=0.50 avg60=13.60 avg300=12.01 total=403518219\nfull avg10=9.00 avg60=0.00 avg300=0.00 total=0\n"
      |> parseCpuPressure
      |> Expect.equal "0.5" (Measured 0.5)
      "some avg10=36.11 avg60=29.90 avg300=14.98 total=433730206" |> parseCpuPressure |> Expect.equal "no trailing newline" (Measured 36.11)

    testCase "WHY — a pressure file that is not what it should be is NotMeasured with a reason, never zero" <| fun _ ->
      for text in [ ""; "full avg10=1.00"; "some avg60=3.0"; "some avg10=abc avg60=1" ] do
        match parseCpuPressure text with
        | NotMeasured reason -> reason |> Expect.isNotEmpty (sprintf "says why for %A" text)
        | Measured _ -> failtestf "%A read as a measurement" text

    testCase "WHY — MemAvailable is read in kilobytes and returned in bytes" <| fun _ ->
      "MemTotal:       65000000 kB\nMemFree:        1000 kB\nMemAvailable:   45123456 kB\nBuffers: 5 kB\n"
      |> parseMemAvailable
      |> Expect.equal "kB times 1024" (AvailableBytes (45123456L * 1024L))

    testCase "WHY — a meminfo without MemAvailable is Unreadable with a reason" <| fun _ ->
      match parseMemAvailable "MemTotal: 5 kB\n" with
      | Unreadable reason -> reason |> Expect.isNotEmpty "says why"
      | AvailableBytes _ -> failtest "no MemAvailable line, yet a measurement"

    testProperty "WHY — a reading renders to text and back without losing the value (the pressure file's own format)" <|
      fun (PositiveInt hundredths) ->
        let percent = float (hundredths % 10000) / 100.0
        parseCpuPressure (sprintf "some avg10=%.2f avg60=0.00 avg300=0.00 total=0" percent)
        |> function Measured p -> abs (p - percent) < 0.0001 | NotMeasured _ -> false
  ]
