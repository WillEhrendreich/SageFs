/// The decisions around a host that is alive and answers nothing (SilentHost.fs): how a wait ends, what holds the host, what the
/// person is told, and how it shows in a session's health. All pure, so each case is a call and a comparison. A few cases are
/// pinned to the exact edge a mutation would move (a pid that only starts like the host's, the self-exclusion, the tracer winning),
/// so a changed comparison fails one of them.
module SageFs.Tests.SilentHostTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.HostAgent.TestDebug
open SageFs.SilentHost
open SageFs.WorkerProtocol

let private config = { FsCheckConfig.defaultConfig with maxTest = 300 }

let private host = 4242

/// How long a host that answered a continue was given: the park and the grace, as the product computes it.
let private waited = Wait.ceilingFor Timeouts.debugContinuePark Timeouts.debugHostAnswerGrace

let private silence (holder: HostHolder) : HostSilence =
  { HostPid = host; Waited = waited; Holder = holder }

let private ready (pid: int) : SessionLifecycleStatus = SessionLifecycleStatus.Ready { Pid = pid; Port = Some 5000 }

let private look (pid: int) (args: string list) : ProcessLook = { Pid = pid; Args = args }

let private genArgs : Gen<string list> =
  Gen.elements [ "dotnet-debug"; "attach"; "4242"; "vim"; "dotnet"; "/x/dotnet-dump.dll"; "analyze"; "--process-id=4242"; "42420"; "-p" ]
  |> Gen.listOf

let private genLooks : Gen<ProcessLook list> =
  gen {
    let! pids = Gen.choose (1, 9000) |> Gen.listOf
    let! args = Gen.listOfLength pids.Length genArgs
    return List.map2 look pids args
  }

[<Tests>]
let tests =
  testList "a host that is alive and answers nothing" [

    testList "what holds it" [

      testCase "WHY: a process the kernel names as tracing the host is the holder, whatever else is running" <| fun _ ->
        findHolder host (Some(77, "netcoredbg")) [ look 9 [ "dotnet-debug"; "attach"; "4242" ] ]
        |> Expect.equal "the tracer" (HostHolder.TracedBy(77, "netcoredbg"))

      testPropertyWithConfig config "WHY: the tracer wins over every process list"
      <| Prop.forAll (Arb.fromGen genLooks) (fun looks ->
        findHolder host (Some(77, "gdb")) looks = HostHolder.TracedBy(77, "gdb"))

      testCase "WHY: an inspector that names the host's pid in its arguments is the holder" <| fun _ ->
        findHolder host None [ look 9 [ "/home/will/.dotnet/tools/dotnet-debug"; "attach"; "4242" ] ]
        |> Expect.equal "dotnet-debug" (HostHolder.NamedAsTarget(9, "dotnet-debug"))

      testCase "WHY: an inspector run through dotnet, with the pid after an equals sign, is found too" <| fun _ ->
        findHolder host None [ look 9 [ "dotnet"; "/x/dotnet-dump.dll"; "analyze"; "--process-id=4242" ] ]
        |> Expect.equal "dotnet-dump" (HostHolder.NamedAsTarget(9, "dotnet-dump"))

      testCase "WHY: a pid that only starts like the host's is another process" <| fun _ ->
        findHolder host None [ look 9 [ "dotnet-debug"; "attach"; "42420" ] ]
        |> Expect.equal "nothing found" HostHolder.NotFound

      testCase "WHY: a program that is not an inspector is not a holder, even with the pid in its arguments" <| fun _ ->
        findHolder host None [ look 9 [ "vim"; "4242" ] ] |> Expect.equal "nothing found" HostHolder.NotFound

      testCase "WHY: the host itself is never its own holder" <| fun _ ->
        findHolder host None [ look host [ "dotnet-debug"; "4242" ] ] |> Expect.equal "nothing found" HostHolder.NotFound

      testCase "WHY: with two inspectors the lowest pid is named, so the answer does not depend on listing order" <| fun _ ->
        let two = [ look 30 [ "gdb"; "4242" ]; look 12 [ "dotnet-dump"; "4242" ] ]
        findHolder host None two |> Expect.equal "lowest" (HostHolder.NamedAsTarget(12, "dotnet-dump"))
        findHolder host None (List.rev two) |> Expect.equal "same either way" (HostHolder.NamedAsTarget(12, "dotnet-dump"))
    ]

    testList "what the person is told" [

      testCase "WHY: the message says what is happening, what holds the host, and the next action" <| fun _ ->
        let text = describe (silence (HostHolder.NamedAsTarget(9, "dotnet-debug")))
        text |> Expect.stringContains "names the host" "process 4242"
        text |> Expect.stringContains "says how long" (sprintf "%.0f seconds" waited.TotalSeconds)
        text |> Expect.stringContains "names the holder and its pid" "Process 9 (dotnet-debug)"
        text |> Expect.stringContains "says to continue or detach" "Continue it in the debugger, or detach"
        text |> Expect.stringContains "says to ask again" "then ask again"
        text |> Expect.stringContains "says the ticket survives" "the ticket is still valid"
        text |> Expect.stringContains "says what to do when nothing is attached" "hard reset the session"

      testCase "WHY: a host nothing was found for is said to be held by a debugger or inspector, and a killed one is named as a cause" <| fun _ ->
        let text = describe (silence HostHolder.NotFound)
        text |> Expect.stringContains "a debugger or inspector" "a debugger or inspector may hold it"
        text |> Expect.stringContains "a killed debugger" "A debugger that was killed can leave the host stopped"

      testCase "WHY: a tracer is named as the one attached with ptrace" <| fun _ ->
        describe (silence (HostHolder.TracedBy(77, "netcoredbg")))
        |> Expect.stringContains "ptrace" "Process 77 (netcoredbg) is attached to it with ptrace"
    ]

    testList "how a wait ends" [

      testPropertyWithConfig config "WHY: the first event ends the wait and the rest change nothing"
      <| Prop.forAll (Arb.fromGen (Gen.elements [ WaitEvent.Answer 1; WaitEvent.CeilingSpent; WaitEvent.HostEnded "gone" ] |> Gen.nonEmptyListOf)) (fun events ->
        let ended = events |> List.fold Wait.settle Wait.Open
        ended = Wait.Settled(Wait.endOf events.Head))

      testCase "WHY: an answer that comes after the ceiling does not undo having said the host was silent" <| fun _ ->
        [ WaitEvent.CeilingSpent; WaitEvent.Answer 1 ]
        |> List.fold Wait.settle Wait.Open
        |> Expect.equal "still no answer" (Wait.Settled WaitEnd.NoAnswer)

      testCase "WHY: a ceiling that passes after the answer does not take the answer back" <| fun _ ->
        [ WaitEvent.Answer 1; WaitEvent.CeilingSpent ]
        |> List.fold Wait.settle Wait.Open
        |> Expect.equal "still replied" (Wait.Settled(WaitEnd.Replied 1))

      testCase "WHY: a wait with no event is still open" <| fun _ ->
        Wait.Open |> Expect.equal "open" (List.fold Wait.settle (Wait.Open: Wait<int>) [])

      testPropertyWithConfig config "WHY: the ceiling is what the host owes plus the grace, and the daemon's wait on the worker is one grace longer"
      <| fun (owedTicks: uint16) (graceTicks: uint16) ->
        let owed = TimeSpan.FromTicks(int64 owedTicks)
        let grace = TimeSpan.FromTicks(int64 graceTicks)
        Wait.ceilingFor owed grace = owed + grace
        && Wait.relayCeilingFor owed grace = owed + grace + grace
        && (graceTicks = 0us || Wait.relayCeilingFor owed grace > Wait.ceilingFor owed grace)
    ]

    testList "what the daemon knows about the host's voice" [

      testPropertyWithConfig config "WHY: any line from the host makes it responsive, whatever it was before"
      <| Prop.forAll (Arb.fromGen (Gen.elements [ Pulse.Responsive; Pulse.Silent(silence HostHolder.NotFound) ])) (fun before ->
        Pulse.step before PulseEvent.HostSpoke = Pulse.Responsive)

      testPropertyWithConfig config "WHY: a wait that ended with no answer makes the host silent, with that account"
      <| Prop.forAll (Arb.fromGen (Gen.elements [ Pulse.Responsive; Pulse.Silent(silence HostHolder.NotFound) ])) (fun before ->
        let s = silence (HostHolder.TracedBy(1, "x"))
        Pulse.step before (PulseEvent.WentSilent s) = Pulse.Silent s)
    ]

    testList "how it shows in a session's health" [

      let note (pid: int option) = Some { Silence = silence HostHolder.NotFound; WorkerPid = pid }

      testCase "WHY: a Ready session with a silent host is Degraded, with the reason" <| fun _ ->
        match withHostSilence (note (Some 10)) (ready 10) SessionHealth.Healthy with
        | SessionHealth.Degraded reason -> reason |> Expect.stringContains "the silence" "did not answer"
        | other -> failtestf "expected Degraded, got %A" other

      testCase "WHY: a session already Degraded keeps its own reason, after the silence" <| fun _ ->
        match withHostSilence (note (Some 10)) (ready 10) (SessionHealth.Degraded "built with optimizations") with
        | SessionHealth.Degraded reason ->
          reason |> Expect.stringContains "the silence" "did not answer"
          reason |> Expect.stringContains "the earlier reason" "built with optimizations"
        | other -> failtestf "expected Degraded, got %A" other

      testCase "WHY: a session that is starting or failed says what it says, a silent host or not" <| fun _ ->
        withHostSilence (note (Some 10)) (ready 10) SessionHealth.Starting |> Expect.equal "starting" SessionHealth.Starting
        withHostSilence (note (Some 10)) (ready 10) (SessionHealth.Failed "x") |> Expect.equal "failed" (SessionHealth.Failed "x")

      testCase "WHY: a restarted worker is a new host, so a note made under the old worker is not shown" <| fun _ ->
        withHostSilence (note (Some 10)) (ready 11) SessionHealth.Healthy |> Expect.equal "healthy again" SessionHealth.Healthy

      testCase "WHY: a note made without knowing the worker applies to the session's worker" <| fun _ ->
        match withHostSilence (note None) (ready 11) SessionHealth.Healthy with
        | SessionHealth.Degraded _ -> ()
        | other -> failtestf "expected Degraded, got %A" other

      testCase "WHY: no note leaves the health alone" <| fun _ ->
        withHostSilence None (ready 10) SessionHealth.Healthy |> Expect.equal "healthy" SessionHealth.Healthy

      testCase "WHY: the daemon's note is per session, and clearing it takes the Degraded back" <| fun _ ->
        let session = sprintf "hs-%s" (Guid.NewGuid().ToString "N")
        let other = sprintf "hs-%s" (Guid.NewGuid().ToString "N")
        SilentHost.note session (Some 10) (silence HostHolder.NotFound)
        healthOf session (ready 10) SessionHealth.Healthy |> (function SessionHealth.Degraded _ -> true | _ -> false) |> Expect.isTrue "degraded while noted"
        healthOf other (ready 10) SessionHealth.Healthy |> Expect.equal "another session is untouched" SessionHealth.Healthy
        SilentHost.clear session
        healthOf session (ready 10) SessionHealth.Healthy |> Expect.equal "healthy once cleared" SessionHealth.Healthy
    ]
  ]
