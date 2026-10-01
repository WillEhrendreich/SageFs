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
          Gen.constant (HoldEvent.Expire ticket)
          Gen.constant (HoldEvent.Finished(ticket, result))
          Gen.constant (HoldEvent.HostEnding "the host ended") ]
  }

let private config = { FsCheckConfig.defaultConfig with maxTest = 400 }

/// A probe that records what the shell asks of it and says when the door was closed again.
type private FakeProbe(presence: DebuggerPresence, access: AttachAccess) =
  let opened = ref 0
  let closed = ref 0
  let closedSignal = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
  member _.Probe : DebuggerProbe =
    { Presence = fun () -> presence
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
  let hold = DebugHold(4242, probe.Probe, holdFor, runTest, (fun _ -> SymbolSupport.CompiledWithSymbols))
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
          | Hold.Holding(t, _) | Hold.Running t | Hold.Ended(t, _) -> Some t
          | Hold.Idle -> None
        let eventTicket =
          match event with
          | HoldEvent.Release(t, _) | HoldEvent.Expire t | HoldEvent.Finished(t, _) -> Some t
          | HoldEvent.Begin _ | HoldEvent.HostEnding _ -> None
        match event, slotTicket <> eventTicket with
        | (HoldEvent.Release _ | HoldEvent.Expire _ | HoldEvent.Finished _), true ->
          stepFrom hold event = (hold, HoldEffect.NoEffect)
        | _ -> true)

      testPropertyWithConfig config "WHY: a Begin is refused exactly when the slot is Holding or Running, and then nothing moves"
      <| Prop.forAll (Arb.fromGen genHold) (fun hold ->
        let after, effect = stepFrom hold (HoldEvent.Begin(ticketNamed "new", aTest))
        match hold with
        | Hold.Holding(holder, _) | Hold.Running holder -> after = hold && effect = HoldEffect.RefuseOpen holder
        | Hold.Idle | Hold.Ended _ -> after = Hold.Holding(ticketNamed "new", aTest) && effect = HoldEffect.ArmExpiry)

      testPropertyWithConfig config "WHY: a test is started only by a Release with a debugger attached, and only for the held test"
      <| Prop.forAll (Arb.fromGen (Gen.zip genHold genEvent)) (fun (hold, event) ->
        match stepFrom hold event with
        | _, HoldEffect.StartTest(ticket, test) ->
          match hold, event with
          | Hold.Holding(held, heldTest), HoldEvent.Release(released, DebuggerPresence.DebuggerAttached) ->
            ticket = held && released = held && test = heldTest
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

      testCase "WHY: a release with no debugger ends the hold without running the test" <| fun _ ->
        let ticket = ticketNamed "t"
        let held, _ = stepFrom Hold.Idle (HoldEvent.Begin(ticket, aTest))
        let ended, effect = stepFrom held (HoldEvent.Release(ticket, DebuggerPresence.NoDebugger))
        ended |> Expect.equal "ended unattached" (Hold.Ended(ticket, DebugEnd.ReleasedWithoutDebugger))
        effect |> Expect.equal "nothing started" HoldEffect.NoEffect

      testCase "WHY: the expiry carries the bound it enforced, and a later release cannot revive the hold" <| fun _ ->
        let ticket = ticketNamed "t"
        let held, _ = stepFrom Hold.Idle (HoldEvent.Begin(ticket, aTest))
        let expired, _ = stepFrom held (HoldEvent.Expire ticket)
        expired |> Expect.equal "expired with the bound" (Hold.Ended(ticket, DebugEnd.NoDebuggerWithin bound))
        stepFrom expired (HoldEvent.Release(ticket, DebuggerPresence.DebuggerAttached))
        |> Expect.equal "the late release changes nothing" (expired, HoldEffect.NoEffect)

      testCase "WHY: the host ending turns a held or running test into HostLost, and an idle slot stays idle" <| fun _ ->
        let ticket = ticketNamed "t"
        let lost = Hold.Ended(ticket, DebugEnd.HostLost "gone")
        stepFrom (Hold.Holding(ticket, aTest)) (HoldEvent.HostEnding "gone") |> fst |> Expect.equal "held" lost
        stepFrom (Hold.Running ticket) (HoldEvent.HostEnding "gone") |> fst |> Expect.equal "running" lost
        stepFrom Hold.Idle (HoldEvent.HostEnding "gone") |> fst |> Expect.equal "idle" Hold.Idle

      testCase "WHY: observe answers a ticket the slot does not hold as NoSuchHold, never as someone else's result" <| fun _ ->
        observe (ticketNamed "mine") (Hold.Ended(ticketNamed "theirs", DebugEnd.Attached passed))
        |> Expect.equal "someone else's hold" (DebugProgress.Ended DebugEnd.NoSuchHold)
        observe (ticketNamed "mine") Hold.Idle
        |> Expect.equal "nothing held" (DebugProgress.Ended DebugEnd.NoSuchHold)
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

      testTask "WHY: a release with no debugger never runs the test" {
        let hold, probe, runs, _ = mkHold DebuggerPresence.NoDebugger TestTimeouts.patienceInProcess
        let target = hold.Begin aTest |> held
        let! ended = continueFor hold target.Ticket TestTimeouts.patienceInProcess
        ended |> Expect.equal "told why" (DebugProgress.Ended DebugEnd.ReleasedWithoutDebugger)
        runs.Value |> Expect.equal "the test did not run" 0
        probe.Closed |> Expect.equal "the door was closed" 1
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
          DebugHold(1, probe.Probe, TestTimeouts.patienceInProcess, (fun _ -> async { return failwith "boom" }), (fun _ -> SymbolSupport.CompiledWithSymbols))
        let target = hold.Begin aTest |> held
        let! ended = continueFor hold target.Ticket TestTimeouts.patienceInProcess
        match ended with
        | DebugProgress.Ended(DebugEnd.Attached(TestResult.Failed(TestFailure.ExceptionThrown(message, _), _))) ->
          message |> Expect.stringContains "carries the exception" "boom"
        | other -> failtestf "expected a failed run, got %A" other
      }

      testTask "WHY: a blocked attach is passed to the editor with the reason, so it can say what to change" {
        let probe = FakeProbe(DebuggerPresence.DebuggerAttached, AttachAccess.Blocked "ptrace_scope is 2")
        let hold = DebugHold(1, probe.Probe, TestTimeouts.patienceInProcess, (fun _ -> async { return passed }), (fun _ -> SymbolSupport.CompiledWithSymbols))
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
      let afterEval =
        { EvaluatedCode = "let x = 1"; Detours = DetourPolicy.RegisterOnly; Discovery = DiscoveryPolicy.Forced; IsFileSave = false }

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

      testTask "WHY: with no debugger attached to this process a release never runs the test" {
        let agent = newAgent ()
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
