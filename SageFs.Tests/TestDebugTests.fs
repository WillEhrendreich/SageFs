module SageFs.Tests.TestDebugTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.LiveTesting
open SageFs.HostAgent
open SageFs.HostAgent.TestDebug
open SageFs.FsiHost.FsiProtocol

/// The host's side of debugging one test: the pure hold (`step`), what Linux's Yama module says about attaching, the shell
/// that performs the effects (`DebugHold`), the agent that classifies a test's symbols, and the wire the answers cross.

let private makeTest (name: string) : TestCase =
  { Id = TestId.create name TestFramework.Expecto
    FullName = name
    DisplayName = name
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

let private aTest = makeTest "suite/adds"
let private bound = FixtureDurations.slowResult
let private passed : TestResult = TestResult.Passed FixtureDurations.usualResult

/// How long the shell is told to wait for a debugger that is mid-attach. The fake probe never reads a clock, so this is only
/// what it is asked for.
let private grace = TestTimeouts.patienceBrief

let private ticketNamed (name: string) = DebugTicket name

/// One application of `step` from a given slot.
let private stepFrom (hold: Hold) (event: HoldEvent) = step bound hold event

let private genTicket : Gen<DebugTicket> = Gen.elements [ "t1"; "t2"; "t3"; "other" ] |> Gen.map DebugTicket

let private genPresence : Gen<DebuggerPresence> =
  Gen.elements [ DebuggerPresence.DebuggerAttached; DebuggerPresence.NoDebugger ]

let private genResult : Gen<TestResult> =
  Gen.elements [ passed; TestResult.Skipped "reason"; TestResult.NotRun ]

/// Every slot a hold can be in, for a few tickets.
let private genHold : Gen<Hold> =
  gen {
    let! ticket = genTicket
    return!
      Gen.oneof
        [ Gen.constant Hold.Idle
          Gen.constant (Hold.Holding(ticket, aTest))
          Gen.constant (Hold.Awaiting(ticket, aTest))
          Gen.constant (Hold.Running ticket)
          Gen.constant (Hold.Ended(ticket, DebugEnd.ReleasedWithoutDebugger))
          Gen.constant (Hold.Ended(ticket, DebugEnd.NoDebuggerWithin bound)) ]
  }

let private genEvent : Gen<HoldEvent> =
  gen {
    let! ticket = genTicket
    let! presence = genPresence
    let! result = genResult
    return!
      Gen.oneof
        [ Gen.constant (HoldEvent.Begin(ticket, aTest))
          Gen.constant (HoldEvent.Release(ticket, presence))
          Gen.constant (HoldEvent.DebuggerArrived ticket)
          Gen.constant (HoldEvent.AttachWindowSpent ticket)
          Gen.constant (HoldEvent.Expire ticket)
          Gen.constant (HoldEvent.Finished(ticket, result))
          Gen.constant (HoldEvent.HostEnding "the host ended") ]
  }

let private config = { FsCheckConfig.defaultConfig with maxTest = 400 }

/// A probe that records what the shell asks of it and says when the door was closed again. Whether a debugger is attached when
/// the editor releases the test is `presence`; one that attaches while the host waits is `Arrive`, and the wait running out
/// is `Spend`. Both are events the case raises, so no case waits on a clock.
type private FakeProbe(presence: DebuggerPresence, access: AttachAccess) =
  let opened = ref 0
  let closed = ref 0
  let closedSignal = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
  let waits = ResizeArray<TimeSpan>()
  let waiting = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
  let attach = TaskCompletionSource<DebuggerPresence>(TaskCreationOptions.RunContinuationsAsynchronously)
  member _.Probe : DebuggerProbe =
    { Presence = fun () -> presence
      AwaitAttach =
        fun span ->
          async {
            lock waits (fun () -> waits.Add span)
            waiting.TrySetResult() |> ignore
            return! attach.Task |> Async.AwaitTask
          }
      OpenForAttach =
        fun () ->
          Interlocked.Increment(&opened.contents) |> ignore
          access
      CloseForAttach =
        fun () ->
          Interlocked.Increment(&closed.contents) |> ignore
          closedSignal.TrySetResult() |> ignore }
  member _.Opened = Volatile.Read(&opened.contents)
  member _.Closed = Volatile.Read(&closed.contents)
  member _.DoorClosed : Task<unit> = closedSignal.Task
  /// Every span the shell asked the probe to wait for a debugger.
  member _.Waits : TimeSpan list = lock waits (fun () -> List.ofSeq waits)
  /// Completes once the shell has started watching for a debugger.
  member _.Watching : Task<unit> = waiting.Task
  /// A debugger attaches while the host waits.
  member _.Arrive() : unit = attach.TrySetResult DebuggerPresence.DebuggerAttached |> ignore
  /// The wait for a debugger runs out.
  member _.Spend() : unit = attach.TrySetResult DebuggerPresence.NoDebugger |> ignore

/// A hold over a fake probe and a test runner the case controls: it counts runs and finishes when told to.
let private mkHold (presence: DebuggerPresence) (holdFor: TimeSpan) =
  let probe = FakeProbe(presence, AttachAccess.Open)
  let runs = ref 0
  let finish = TaskCompletionSource<TestResult>(TaskCreationOptions.RunContinuationsAsynchronously)
  let runTest (_: TestCase) : Async<TestResult> =
    async {
      Interlocked.Increment(&runs.contents) |> ignore
      return! finish.Task |> Async.AwaitTask
    }
  let hold = DebugHold(4242, probe.Probe, holdFor, grace, runTest, (fun _ -> SymbolSupport.CompiledWithSymbols))
  hold, probe, runs, finish

let private held (answer: DebugBegin) : DebugTarget =
  match answer with
  | DebugBegin.Held target -> target
  | other -> failtestf "expected Held, got %A" other

let private continueFor (hold: DebugHold) (ticket: DebugTicket) (park: TimeSpan) : Task<DebugProgress> =
  hold.Continue(ticket, park) |> Async.StartAsTask

[<Tests>]
let tests =
  testList "TestDebug" [

    testList "step (the pure hold)" [

      testPropertyWithConfig config "WHY: an event for a ticket the slot does not hold changes nothing"
      <| Prop.forAll (Arb.fromGen (Gen.zip genHold genEvent)) (fun (hold, event) ->
        let slotTicket =
          match hold with
          | Hold.Holding(t, _) | Hold.Awaiting(t, _) | Hold.Running t | Hold.Ended(t, _) -> Some t
          | Hold.Idle -> None
        let eventTicket =
          match event with
          | HoldEvent.Release(t, _) | HoldEvent.DebuggerArrived t | HoldEvent.AttachWindowSpent t | HoldEvent.Expire t | HoldEvent.Finished(t, _) -> Some t
          | HoldEvent.Begin _ | HoldEvent.HostEnding _ -> None
        match event, slotTicket <> eventTicket with
        | (HoldEvent.Release _ | HoldEvent.DebuggerArrived _ | HoldEvent.AttachWindowSpent _ | HoldEvent.Expire _ | HoldEvent.Finished _), true ->
          stepFrom hold event = (hold, HoldEffect.NoEffect)
        | _ -> true)

      testPropertyWithConfig config "WHY: a Begin is refused exactly when the slot is Holding, Awaiting or Running, and then nothing moves"
      <| Prop.forAll (Arb.fromGen genHold) (fun hold ->
        let after, effect = stepFrom hold (HoldEvent.Begin(ticketNamed "new", aTest))
        match hold with
        | Hold.Holding(holder, _) | Hold.Awaiting(holder, _) | Hold.Running holder -> after = hold && effect = HoldEffect.RefuseOpen holder
        | Hold.Idle | Hold.Ended _ -> after = Hold.Holding(ticketNamed "new", aTest) && effect = HoldEffect.ArmExpiry)

      testPropertyWithConfig config "WHY: a test is started only by a Release that saw a debugger or by a debugger arriving while the host waited, and only for the held test"
      <| Prop.forAll (Arb.fromGen (Gen.zip genHold genEvent)) (fun (hold, event) ->
        match stepFrom hold event with
        | _, HoldEffect.StartTest(ticket, test) ->
          match hold, event with
          | Hold.Holding(held, heldTest), HoldEvent.Release(released, DebuggerPresence.DebuggerAttached)
          | Hold.Awaiting(held, heldTest), HoldEvent.Release(released, DebuggerPresence.DebuggerAttached) ->
            ticket = held && released = held && test = heldTest
          | Hold.Awaiting(held, heldTest), HoldEvent.DebuggerArrived arrived -> ticket = held && arrived = held && test = heldTest
          | _ -> false
        | _ -> true)

      testPropertyWithConfig config "WHY: an event never leaves a hold half-ended: Ended stays Ended until a Begin replaces it"
      <| Prop.forAll (Arb.fromGen (Gen.zip genHold genEvent)) (fun (hold, event) ->
        match hold, stepFrom hold event with
        | Hold.Ended _, (after, _) ->
          match event with
          | HoldEvent.Begin _ -> true
          | _ -> after = hold
        | _ -> true)

      testCase "WHY: the full path is held, released with a debugger, finished" <| fun _ ->
        let ticket = ticketNamed "t"
        let held, armed = stepFrom Hold.Idle (HoldEvent.Begin(ticket, aTest))
        armed |> Expect.equal "begin arms the expiry" HoldEffect.ArmExpiry
        let running, started = stepFrom held (HoldEvent.Release(ticket, DebuggerPresence.DebuggerAttached))
        started |> Expect.equal "release starts the test" (HoldEffect.StartTest(ticket, aTest))
        let ended, _ = stepFrom running (HoldEvent.Finished(ticket, passed))
        ended |> Expect.equal "finish ends it" (Hold.Ended(ticket, DebugEnd.Attached passed))

      testCase "WHY: a release with no debugger yet waits for one that is still attaching, and does not run the test" <| fun _ ->
        let ticket = ticketNamed "t"
        let held, _ = stepFrom Hold.Idle (HoldEvent.Begin(ticket, aTest))
        let waiting, effect = stepFrom held (HoldEvent.Release(ticket, DebuggerPresence.NoDebugger))
        waiting |> Expect.equal "waiting for a debugger" (Hold.Awaiting(ticket, aTest))
        effect |> Expect.equal "the shell is told to watch for one" (HoldEffect.AwaitAttach ticket)

      testCase "WHY: a debugger that arrives while the host waits runs the test, once" <| fun _ ->
        let ticket = ticketNamed "t"
        let waiting = Hold.Awaiting(ticket, aTest)
        let running, effect = stepFrom waiting (HoldEvent.DebuggerArrived ticket)
        running |> Expect.equal "running" (Hold.Running ticket)
        effect |> Expect.equal "the test starts" (HoldEffect.StartTest(ticket, aTest))
        stepFrom running (HoldEvent.DebuggerArrived ticket)
        |> Expect.equal "a second arrival changes nothing" (running, HoldEffect.NoEffect)

      testCase "WHY: a wait that runs out without a debugger ends the hold without running the test" <| fun _ ->
        let ticket = ticketNamed "t"
        let ended, effect = stepFrom (Hold.Awaiting(ticket, aTest)) (HoldEvent.AttachWindowSpent ticket)
        ended |> Expect.equal "ended unattached" (Hold.Ended(ticket, DebugEnd.ReleasedWithoutDebugger))
        effect |> Expect.equal "nothing started" HoldEffect.NoEffect
        stepFrom ended (HoldEvent.DebuggerArrived ticket)
        |> Expect.equal "a debugger that arrives after the wait is too late" (ended, HoldEffect.NoEffect)

      testCase "WHY: a second release while the host waits starts the test if the debugger is there now, and otherwise changes nothing" <| fun _ ->
        let ticket = ticketNamed "t"
        let waiting = Hold.Awaiting(ticket, aTest)
        stepFrom waiting (HoldEvent.Release(ticket, DebuggerPresence.DebuggerAttached))
        |> Expect.equal "runs" (Hold.Running ticket, HoldEffect.StartTest(ticket, aTest))
        stepFrom waiting (HoldEvent.Release(ticket, DebuggerPresence.NoDebugger))
        |> Expect.equal "still waiting, and not asked to watch twice" (waiting, HoldEffect.NoEffect)

      testCase "WHY: the expiry of the hold does not end a wait for a debugger, because the wait has its own end" <| fun _ ->
        let ticket = ticketNamed "t"
        let waiting = Hold.Awaiting(ticket, aTest)
        stepFrom waiting (HoldEvent.Expire ticket) |> Expect.equal "unchanged" (waiting, HoldEffect.NoEffect)

      testCase "WHY: the expiry carries the bound it enforced, and a later release cannot revive the hold" <| fun _ ->
        let ticket = ticketNamed "t"
        let held, _ = stepFrom Hold.Idle (HoldEvent.Begin(ticket, aTest))
        let expired, _ = stepFrom held (HoldEvent.Expire ticket)
        expired |> Expect.equal "expired with the bound" (Hold.Ended(ticket, DebugEnd.NoDebuggerWithin bound))
        stepFrom expired (HoldEvent.Release(ticket, DebuggerPresence.DebuggerAttached))
        |> Expect.equal "the late release changes nothing" (expired, HoldEffect.NoEffect)

      testCase "WHY: the host ending turns a held, waiting or running test into HostLost, and an idle slot stays idle" <| fun _ ->
        let ticket = ticketNamed "t"
        let lost = Hold.Ended(ticket, DebugEnd.HostLost "gone")
        stepFrom (Hold.Holding(ticket, aTest)) (HoldEvent.HostEnding "gone") |> fst |> Expect.equal "held" lost
        stepFrom (Hold.Awaiting(ticket, aTest)) (HoldEvent.HostEnding "gone") |> fst |> Expect.equal "waiting for a debugger" lost
        stepFrom (Hold.Running ticket) (HoldEvent.HostEnding "gone") |> fst |> Expect.equal "running" lost
        stepFrom Hold.Idle (HoldEvent.HostEnding "gone") |> fst |> Expect.equal "idle" Hold.Idle

      testCase "WHY: observe answers a ticket the slot does not hold as NoSuchHold, never as someone else's result" <| fun _ ->
        observe (ticketNamed "mine") (Hold.Ended(ticketNamed "theirs", DebugEnd.Attached passed))
        |> Expect.equal "someone else's hold" (DebugProgress.Ended DebugEnd.NoSuchHold)
        observe (ticketNamed "mine") Hold.Idle
        |> Expect.equal "nothing held" (DebugProgress.Ended DebugEnd.NoSuchHold)

      testCase "WHY: a hold waiting for a debugger counts as still going, so the editor hears how it ended when the wait does" <| fun _ ->
        observe (ticketNamed "mine") (Hold.Awaiting(ticketNamed "mine", aTest)) |> Expect.equal "still going" DebugProgress.StillRunning
    ]

    testList "Yama (can a debugger attach to this process)" [

      testCase "WHY: the four scopes the kernel documents read as themselves, with any whitespace around them" <| fun _ ->
        YamaScope.parse "0\n" |> Expect.equal "0" YamaScope.SameUser
        YamaScope.parse " 1 " |> Expect.equal "1" YamaScope.DescendantsOrNamed
        YamaScope.parse "2" |> Expect.equal "2" YamaScope.AdminOnly
        YamaScope.parse "3\n" |> Expect.equal "3" YamaScope.Disabled

      testPropertyWithConfig config "WHY: anything else is Unrecognised and says what it read, never a guess"
      <| fun (text: string) ->
        let trimmed = (match text with null -> "" | t -> t).Trim()
        match trimmed with
        | "0" | "1" | "2" | "3" -> true
        | _ -> YamaScope.parse trimmed = YamaScope.Unrecognised trimmed

      testCase "WHY: scope 0 and no Yama need nothing from the host" <| fun _ ->
        let naming () : PtracerNaming = failtest "the host must not touch ptrace permissions when nothing restricts attaching"
        attachAccess YamaScope.NotRestricted naming |> Expect.equal "not restricted" AttachAccess.Open
        attachAccess YamaScope.SameUser naming |> Expect.equal "same user" AttachAccess.Open

      testCase "WHY: scope 1 is the one the host can fix, by naming a tracer, and it says so when that is refused" <| fun _ ->
        attachAccess YamaScope.DescendantsOrNamed (fun () -> PtracerNaming.Named)
        |> Expect.equal "named" AttachAccess.Open
        match attachAccess YamaScope.DescendantsOrNamed (fun () -> PtracerNaming.Refused 1) with
        | AttachAccess.Blocked reason ->
          reason |> Expect.stringContains "says what to change" "ptrace_scope=0"
          reason |> Expect.stringContains "says what happened" "errno 1"
        | AttachAccess.Open -> failtest "a refused naming must not read as open"
        match attachAccess YamaScope.DescendantsOrNamed (fun () -> PtracerNaming.CouldNotCall "no libc") with
        | AttachAccess.Blocked reason ->
          reason |> Expect.stringContains "says what to change" "ptrace_scope=0"
          reason |> Expect.stringContains "says why" "no libc"
        | AttachAccess.Open -> failtest "a call that could not be made must not read as open"

      testCase "WHY: scopes 2 and 3 are blocked with what to do about it, and the host does not try to fix them" <| fun _ ->
        let naming () : PtracerNaming = failtest "the host cannot fix this scope, so it must not try"
        match attachAccess YamaScope.AdminOnly naming, attachAccess YamaScope.Disabled naming with
        | AttachAccess.Blocked admin, AttachAccess.Blocked disabled ->
          admin |> Expect.stringContains "root or sysctl" "root"
          disabled |> Expect.stringContains "reboot" "reboot"
        | other -> failtestf "expected both blocked, got %A" other

      testCase "WHY: a scope SageFs does not recognise is blocked with the text it read, not assumed open" <| fun _ ->
        match attachAccess (YamaScope.Unrecognised "7") (fun () -> PtracerNaming.Named) with
        | AttachAccess.Blocked reason -> reason |> Expect.stringContains "names the text" "'7'"
        | AttachAccess.Open -> failtest "an unknown scope must not read as open"
    ]

    testList "DebugHold (the shell)" [

      testTask "WHY: begin names the process and the test, opens the door once, and the answer carries the bound" {
        let hold, probe, runs, _ = mkHold DebuggerPresence.DebuggerAttached TestTimeouts.patienceInProcess
        let target = hold.Begin aTest |> held
        target.Pid |> Expect.equal "the pid it was given" 4242
        target.TestId |> Expect.equal "the test" aTest.Id
        target.TestName |> Expect.equal "the test's name" aTest.DisplayName
        target.Access |> Expect.equal "what the probe said" AttachAccess.Open
        target.HoldFor |> Expect.equal "the bound" TestTimeouts.patienceInProcess
        probe.Opened |> Expect.equal "the door was opened once" 1
        runs.Value |> Expect.equal "begin does not run the test" 0
      }

      testTask "WHY: a release with a debugger runs the test once and the editor hears the result when it finishes" {
        let hold, probe, runs, finish = mkHold DebuggerPresence.DebuggerAttached TestTimeouts.patienceInProcess
        let target = hold.Begin aTest |> held
        let! early = continueFor hold target.Ticket bound
        early |> Expect.equal "still running while the test is open" DebugProgress.StillRunning
        finish.SetResult passed
        let! ended = continueFor hold target.Ticket TestTimeouts.patienceInProcess
        ended |> Expect.equal "the run's result" (DebugProgress.Ended(DebugEnd.Attached passed))
        runs.Value |> Expect.equal "the test ran once, however many times the editor continued" 1
        probe.Closed |> Expect.equal "the door was closed again" 1
      }

      testTask "WHY: a release with no debugger waits for one, and when the wait runs out the test never runs and the door is closed" {
        let hold, probe, runs, _ = mkHold DebuggerPresence.NoDebugger TestTimeouts.patienceInProcess
        let target = hold.Begin aTest |> held
        let waiting = continueFor hold target.Ticket TestTimeouts.patienceInProcess
        do! probe.Watching.WaitAsync TestTimeouts.patienceBrief
        probe.Waits |> Expect.equal "the shell asked the probe to wait for the grace it was given" [ grace ]
        probe.Closed |> Expect.equal "the door stays open while the host waits for a debugger" 0
        probe.Spend()
        let! ended = waiting
        ended |> Expect.equal "told why" (DebugProgress.Ended DebugEnd.ReleasedWithoutDebugger)
        runs.Value |> Expect.equal "the test did not run" 0
        probe.Closed |> Expect.equal "the door was closed" 1
      }

      testTask "WHY: a debugger that finishes attaching after the release runs the test, and the editor hears how it went" {
        let hold, probe, runs, finish = mkHold DebuggerPresence.NoDebugger TestTimeouts.patienceInProcess
        let target = hold.Begin aTest |> held
        let waiting = continueFor hold target.Ticket TestTimeouts.patienceInProcess
        do! probe.Watching.WaitAsync TestTimeouts.patienceBrief
        runs.Value |> Expect.equal "nothing runs before the debugger is there" 0
        probe.Arrive()
        finish.SetResult passed
        let! ended = waiting
        ended |> Expect.equal "the run's result" (DebugProgress.Ended(DebugEnd.Attached passed))
        runs.Value |> Expect.equal "the test ran once" 1
        probe.Closed |> Expect.equal "the door was closed once the hold was over" 1
      }

      testTask "WHY: a host that ends while it waits for a debugger tells the waiting editor it was lost, and a debugger that arrives after does not run the test" {
        let hold, probe, runs, _ = mkHold DebuggerPresence.NoDebugger TestTimeouts.patienceInProcess
        let target = hold.Begin aTest |> held
        let waiting = continueFor hold target.Ticket TestTimeouts.patienceInProcess
        do! probe.Watching.WaitAsync TestTimeouts.patienceBrief
        hold.HostEnding "the FSI host is shutting down"
        let! ended = waiting
        ended |> Expect.equal "told the host was lost" (DebugProgress.Ended(DebugEnd.HostLost "the FSI host is shutting down"))
        probe.Arrive()
        let! again = continueFor hold target.Ticket bound
        again |> Expect.equal "still lost, and nothing ran" (DebugProgress.Ended(DebugEnd.HostLost "the FSI host is shutting down"))
        runs.Value |> Expect.equal "the test did not run" 0
        probe.Closed |> Expect.equal "the door was closed" 1
      }

      testTask "WHY: a probe that fails while the host waits ends the wait as no debugger, never a hold that waits for ever" {
        let probe =
          { (FakeProbe(DebuggerPresence.NoDebugger, AttachAccess.Open)).Probe with AwaitAttach = fun _ -> async { return failwith "the probe broke" } }
        let hold = DebugHold(1, probe, TestTimeouts.patienceInProcess, grace, (fun _ -> async { return passed }), (fun _ -> SymbolSupport.CompiledWithSymbols))
        let target = hold.Begin aTest |> held
        let! ended = continueFor hold target.Ticket TestTimeouts.patienceInProcess
        ended |> Expect.equal "no debugger came" (DebugProgress.Ended DebugEnd.ReleasedWithoutDebugger)
      }

      testTask "WHY: the real probe, in a process with no debugger, says none came only once the whole wait is spent" {
        match System.Diagnostics.Debugger.IsAttached with
        | true -> skiptest "a debugger is attached to the test process itself"
        | false ->
          let clock = System.Diagnostics.Stopwatch.StartNew()
          let! presence = systemProbe.AwaitAttach TestTimeouts.settle |> Async.StartAsTask
          presence |> Expect.equal "no debugger came" DebuggerPresence.NoDebugger
          (clock.Elapsed >= TestTimeouts.settle) |> Expect.isTrue "it waited the whole span before saying so"
      }

      testTask "WHY: a hold nobody releases is dropped when the bound passes, the door is closed, and the test never runs" {
        let hold, probe, runs, _ = mkHold DebuggerPresence.DebuggerAttached bound
        let target = hold.Begin aTest |> held
        do! probe.DoorClosed.WaitAsync TestTimeouts.patienceBrief
        let! ended = continueFor hold target.Ticket TestTimeouts.patienceInProcess
        ended |> Expect.equal "told it expired" (DebugProgress.Ended(DebugEnd.NoDebuggerWithin bound))
        runs.Value |> Expect.equal "the late continue did not run the test" 0
      }

      testTask "WHY: a second begin while a test is held is turned away, and the first hold still works" {
        let hold, _, runs, finish = mkHold DebuggerPresence.DebuggerAttached TestTimeouts.patienceInProcess
        let first = hold.Begin aTest |> held
        match hold.Begin(makeTest "suite/other") with
        | DebugBegin.Unsupported(UnsupportedReason.HoldAlreadyOpen holder) -> holder |> Expect.equal "names the holder" first.Ticket
        | other -> failtestf "expected the second begin to be refused, got %A" other
        finish.SetResult passed
        let! ended = continueFor hold first.Ticket TestTimeouts.patienceInProcess
        ended |> Expect.equal "the first hold ran" (DebugProgress.Ended(DebugEnd.Attached passed))
        runs.Value |> Expect.equal "once" 1
      }

      testTask "WHY: after a hold ends the next begin gets the slot, and the old ticket is no longer anyone's" {
        let hold, _, _, finish = mkHold DebuggerPresence.DebuggerAttached TestTimeouts.patienceInProcess
        let first = hold.Begin aTest |> held
        finish.SetResult passed
        let! _ = continueFor hold first.Ticket TestTimeouts.patienceInProcess
        let second = hold.Begin(makeTest "suite/next") |> held
        second.Ticket |> Expect.notEqual "a fresh ticket" first.Ticket
        let! old = continueFor hold first.Ticket bound
        old |> Expect.equal "the old ticket holds nothing" (DebugProgress.Ended DebugEnd.NoSuchHold)
      }

      testTask "WHY: a ticket nobody issued holds nothing" {
        let hold, _, _, _ = mkHold DebuggerPresence.DebuggerAttached TestTimeouts.patienceInProcess
        let! answer = continueFor hold (ticketNamed "never-issued") bound
        answer |> Expect.equal "no such hold" (DebugProgress.Ended DebugEnd.NoSuchHold)
      }

      testTask "WHY: the host ending while the test runs wakes the waiting editor with HostLost" {
        let hold, _, _, _ = mkHold DebuggerPresence.DebuggerAttached TestTimeouts.patienceInProcess
        let target = hold.Begin aTest |> held
        let waiting = continueFor hold target.Ticket TestTimeouts.patienceInProcess
        let! early = continueFor hold target.Ticket bound
        early |> Expect.equal "still running before the host ends" DebugProgress.StillRunning
        hold.HostEnding "the FSI host is shutting down"
        let! ended = waiting
        ended |> Expect.equal "told the host was lost" (DebugProgress.Ended(DebugEnd.HostLost "the FSI host is shutting down"))
      }

      testTask "WHY: a test that throws ends the run as a failure the editor can read, never a hung hold" {
        let probe = FakeProbe(DebuggerPresence.DebuggerAttached, AttachAccess.Open)
        let hold =
          DebugHold(1, probe.Probe, TestTimeouts.patienceInProcess, grace, (fun _ -> async { return failwith "boom" }), (fun _ -> SymbolSupport.CompiledWithSymbols))
        let target = hold.Begin aTest |> held
        let! ended = continueFor hold target.Ticket TestTimeouts.patienceInProcess
        match ended with
        | DebugProgress.Ended(DebugEnd.Attached(TestResult.Failed(TestFailure.ExceptionThrown(message, _), _))) ->
          message |> Expect.stringContains "carries the exception" "boom"
        | other -> failtestf "expected a failed run, got %A" other
      }

      testTask "WHY: a blocked attach is passed to the editor with the reason, so it can say what to change" {
        let probe = FakeProbe(DebuggerPresence.DebuggerAttached, AttachAccess.Blocked "ptrace_scope is 2")
        let hold = DebugHold(1, probe.Probe, TestTimeouts.patienceInProcess, grace, (fun _ -> async { return passed }), (fun _ -> SymbolSupport.CompiledWithSymbols))
        (hold.Begin aTest |> held).Access |> Expect.equal "blocked with the reason" (AttachAccess.Blocked "ptrace_scope is 2")
      }
    ]

    testList "Agent (which test has symbols)" [

      // A small framework assembly stands in for what an eval emitted: the agent only needs an assembly the custom
      // executor accepts, so the executor's marker is one of the assembly's own references.
      let evalAssembly = typeof<System.Collections.Generic.LinkedList<int>>.Assembly
      let marker =
        match evalAssembly.GetReferencedAssemblies() |> Array.tryHead with
        | Some reference -> reference.Name |> Option.ofObj |> Option.defaultValue ""
        | None -> ""
      let evalTest = makeTest "eval/defined"
      let executor : TestExecutor =
        TestExecutor.Custom
          { Description = { Name = TestFramework.Expecto; AssemblyMarker = marker }
            Discover = fun _ -> ({ Tests = [ evalTest ]; RunTest = (fun _ -> async { return passed }) } : DiscoveryResult) }
      let emptyInit : AgentInit =
        { Projects = []; ResolveFrom = []; ValueReads = SageFs.Middleware.ValueReadTracking.ValueReadWatch.IgnoreValueReads }
      let sources : AssemblySources = { Dynamic = (fun () -> [| evalAssembly |]); Loaded = (fun () -> [||]) }
      let newAgent () = Agent(emptyInit, sources, [ executor ])
      /// An agent whose wait for a debugger is short, so a case that has no debugger does not wait out the real one.
      let newAgentWaiting (wait: TimeSpan) = Agent(emptyInit, sources, [ executor ], wait)
      let afterEval =
        { EvaluatedCode = "let x = 1"; Detours = DetourPolicy.RegisterOnly; Discovery = DiscoveryPolicy.Forced; IsFileSave = false; Closures = [] }

      testCase "WHY: a test an eval defined is held with DefinedByEval, so the editor can say breakpoints in it will not bind" <| fun _ ->
        let agent = newAgent ()
        agent.AfterEval afterEval |> ignore
        match agent.DebugBegin evalTest with
        | DebugBegin.Held target ->
          target.Symbols |> Expect.equal "no PDB for evaluated code" SymbolSupport.DefinedByEval
          target.Pid |> Expect.equal "this process" Environment.ProcessId
        | other -> failtestf "expected Held, got %A" other

      testCase "WHY: a test the eval did not define is held as compiled, because the project's assembly has its PDB" <| fun _ ->
        let agent = newAgent ()
        agent.AfterEval afterEval |> ignore
        match agent.DebugBegin(makeTest "project/compiled") with
        | DebugBegin.Held target -> target.Symbols |> Expect.equal "compiled" SymbolSupport.CompiledWithSymbols
        | other -> failtestf "expected Held, got %A" other

      testCase "WHY: before any eval nothing is eval-defined" <| fun _ ->
        match (newAgent ()).DebugBegin evalTest with
        | DebugBegin.Held target -> target.Symbols |> Expect.equal "compiled" SymbolSupport.CompiledWithSymbols
        | other -> failtestf "expected Held, got %A" other

      testTask "WHY: with no debugger attached to this process, and none arriving, a release never runs the test" {
        let agent = newAgentWaiting TestTimeouts.settle
        agent.AfterEval afterEval |> ignore
        let target = agent.DebugBegin evalTest |> held
        let! ended = agent.DebugContinue(target.Ticket, TestTimeouts.patienceInProcess) |> Async.StartAsTask
        ended |> Expect.equal "the test process has no debugger" (DebugProgress.Ended DebugEnd.ReleasedWithoutDebugger)
      }
    ]

    testList "FsiProtocol (the wire to the host)" [

      testCase "WHY: the debug requests and answers survive the codec, including a reason with a newline in it" <| fun _ ->
        let ticket = ticketNamed "debug-1-1"
        let target : DebugTarget =
          { Pid = 31337
            Ticket = ticket
            TestId = aTest.Id
            TestName = aTest.DisplayName
            Symbols = SymbolSupport.DefinedByEval
            Access = AttachAccess.Blocked "line one\nline two"
            HoldFor = TestTimeouts.patienceInProcess }
        let requests = [ AgentDebugBegin(7L, aTest); AgentDebugContinue(8L, ticket, TestTimeouts.patienceBrief) ]
        let responses =
          [ AgentDebugBeginResult(7L, DebugBegin.Held target)
            AgentDebugBeginResult(7L, DebugBegin.Unsupported(UnsupportedReason.HoldAlreadyOpen ticket))
            AgentDebugBeginResult(7L, DebugBegin.Unsupported(UnsupportedReason.HostUnavailable "gone"))
            AgentDebugContinueResult(8L, DebugProgress.StillRunning)
            AgentDebugContinueResult(8L, DebugProgress.Ended(DebugEnd.Attached passed))
            AgentDebugContinueResult(8L, DebugProgress.Ended(DebugEnd.NoDebuggerWithin bound))
            AgentDebugContinueResult(8L, DebugProgress.Ended DebugEnd.ReleasedWithoutDebugger)
            AgentDebugContinueResult(8L, DebugProgress.Ended DebugEnd.NoSuchHold)
            AgentDebugContinueResult(8L, DebugProgress.Ended(DebugEnd.HostLost "gone")) ]
        for request in requests do
          decodeRequest (encodeRequest request) |> Expect.equal "request round trip" (Result.Ok request)
        for response in responses do
          decodeResponse (encodeResponse response) |> Expect.equal "response round trip" (Result.Ok response)

      testCase "WHY: the protocol still proves every type it mentions is representable" <| fun _ ->
        checkSupported () |> Expect.isOk "supported"
    ]
  ]
