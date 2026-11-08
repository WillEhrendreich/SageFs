module SageFs.Tests.McpBridgeTests

open Expecto
open Expecto.Flip
open SageFs.McpBridge

let private policy = { MaxProbeAttempts = 3; MaxReconnectProbes = 4; QueueCap = 5 }

let private req id method =
  RpcMessage.Request(RpcId.N(int64 id), method, sprintf """{"jsonrpc":"2.0","id":%d,"method":"%s"}""" id method)

let private notif method =
  RpcMessage.Notification(method, sprintf """{"jsonrpc":"2.0","method":"%s"}""" method)

[<Tests>]
let tests =
  testList "McpBridge" [

    testList "parseRpcMessage" [
      testCase "a request has an id and a method" <| fun _ ->
        match parseRpcMessage """{"jsonrpc":"2.0","id":1,"method":"initialize"}""" with
        | RpcMessage.Request(RpcId.N 1L, "initialize", _) -> ()
        | other -> failtestf "expected a Request, got %A" other

      testCase "a string id parses as RpcId.S" <| fun _ ->
        match parseRpcMessage """{"jsonrpc":"2.0","id":"abc","method":"initialize"}""" with
        | RpcMessage.Request(RpcId.S "abc", "initialize", _) -> ()
        | other -> failtestf "expected a string-id Request, got %A" other

      testCase "a notification has a method and no id" <| fun _ ->
        match parseRpcMessage """{"jsonrpc":"2.0","method":"notifications/initialized"}""" with
        | RpcMessage.Notification("notifications/initialized", _) -> ()
        | other -> failtestf "expected a Notification, got %A" other

      testCase "a response has an id and no method" <| fun _ ->
        match parseRpcMessage """{"jsonrpc":"2.0","id":1,"result":{}}""" with
        | RpcMessage.Response(RpcId.N 1L, _) -> ()
        | other -> failtestf "expected a Response, got %A" other

      testCase "invalid JSON is Unparseable, not an exception" <| fun _ ->
        match parseRpcMessage "not json at all {{{" with
        | RpcMessage.Unparseable(_, reason) -> reason |> Expect.isNotEmpty "should carry a reason"
        | other -> failtestf "expected Unparseable, got %A" other

      testCase "neither id nor method is Unparseable" <| fun _ ->
        match parseRpcMessage """{"jsonrpc":"2.0"}""" with
        | RpcMessage.Unparseable _ -> ()
        | other -> failtestf "expected Unparseable, got %A" other
    ]

    // Issue #164: a tool call whose string carries a literal newline arrives as several stdin lines.
    testList "stdin lines" [
      /// Feed lines in order; returns where the reader is afterwards and what it delivered so far.
      let feed (lines: string list) : LineState * RpcMessage list =
        lines
        |> List.fold (fun (state, delivered) line ->
          let next, messages = acceptLine state line
          next, delivered @ messages) (LineState.Idle, [])

      /// Feed lines in order and then close stdin, so a held fragment is delivered too.
      let feedAll (lines: string list) : RpcMessage list =
        let state, delivered = feed lines
        delivered @ closeInput state

      let kinds (messages: RpcMessage list) =
        messages
        |> List.map (function
          | RpcMessage.Request(id, _, _) -> sprintf "Request %A" id
          | RpcMessage.Notification _ -> "Notification"
          | RpcMessage.Response _ -> "Response"
          | RpcMessage.Unparseable _ -> "Unparseable")

      // The same tool call written twice: the id before the string with the newline, and the id after it.
      let idBefore = [ """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"arguments":{"code":"let x = 1"""; """"}}}""" ]
      let idAfter = [ """{"jsonrpc":"2.0","method":"tools/call","params":{"arguments":{"code":"let x = 1"""; """"}},"id":7}""" ]

      testCase "WHY — a request whose id comes after the newline is still answerable, so the caller gets an error and not a hang (#164)" <| fun _ ->
        feedAll idAfter
        |> List.choose (function RpcMessage.Request(id, methodName, _) -> Some(id, methodName) | _ -> None)
        |> Expect.equal "the id and the method are recovered from the joined lines" [ RpcId.N 7L, "tools/call" ]

      testCase "WHY — a request whose id comes before the newline is answered at once, as it was before the lines were joined" <| fun _ ->
        let _, delivered = feed [ idBefore.[0] ]
        delivered |> kinds |> Expect.equal "the first line alone is already the request" [ "Request N 7L" ]

      testCase "WHY — the tail of a message already answered is logged and dropped, never read as a second request" <| fun _ ->
        feedAll idBefore |> kinds |> Expect.equal "one request, then the leftover" [ "Request N 7L"; "Unparseable" ]

      testCase "WHY — a blank line inside an unfinished string is kept with the message, and a blank line on its own is skipped" <| fun _ ->
        feedAll [ """{"jsonrpc":"2.0","method":"tools/call","params":{"c":"a"""; ""; """b"},"id":4}""" ]
        |> kinds |> Expect.equal "the blank line joined the fragment" [ "Request N 4L" ]
        feedAll [ ""; "" ] |> Expect.isEmpty "nothing to deliver"

      testCase "WHY — stdin closing inside a fragment delivers it once as Unparseable, so it is logged and not lost" <| fun _ ->
        feedAll [ """{"jsonrpc":"2.0","params":{"c":"x""" ]
        |> kinds |> Expect.equal "the fragment is flushed at close" [ "Unparseable" ]

      testCase "WHY — lines that never finish are given up on at the size bound, so a runaway stream cannot grow without limit" <| fun _ ->
        let chunk = System.String('x', 1024 * 1024)
        let lines = """{"jsonrpc":"2.0","params":{"c":"x""" :: List.replicate 5 chunk
        let _, deliveredBeforeClose = feed lines
        deliveredBeforeClose |> List.isEmpty |> Expect.isFalse "the held text was flushed before stdin closed"
        deliveredBeforeClose
        |> List.forall (function RpcMessage.Unparseable(raw, _) -> raw.Length <= maxHeldChars + chunk.Length | _ -> false)
        |> Expect.isTrue "each flush is bounded"

      testProperty "WHY — a complete message after an unfinished fragment is delivered as itself, never absorbed into the fragment" <| fun (n: uint16) ->
        let line = rawOf (req (int n) "tools/list")
        let fragments =
          [ """{"jsonrpc":"2.0","params":{"c":"x"""
            """{"jsonrpc":"2.0","id":"""
            "not json at all {{{" ]
        fragments
        |> List.forall (fun fragment ->
          feedAll [ fragment; line ] |> List.last = req (int n) "tools/list")
    ]

    testList "the startup race" [
      testCase "Probe from the initial state bumps the probe counter and asks to probe" <| fun _ ->
        let state, actions = decide policy initial Event.Probe
        state.Transport |> Expect.equal "should be Probing 1" (TransportState.AwaitingDaemon(DaemonReadiness.Probing 1))
        actions |> Expect.equal "should ask to probe" [ Action.Probe ]

      testCase "the first unhealthy result starts the daemon, exactly once" <| fun _ ->
        let s1, _ = decide policy initial Event.Probe
        let s2, actions = decide policy s1 (Event.ProbeResult false)
        s2.Transport |> Expect.equal "should move to Starting" (TransportState.AwaitingDaemon(DaemonReadiness.Starting 0))
        actions |> Expect.equal "should start the daemon" [ Action.StartDaemon ]

      testCase "a second unhealthy result never starts the daemon again" <| fun _ ->
        let s1, _ = decide policy initial Event.Probe
        let s2, _ = decide policy s1 (Event.ProbeResult false)
        let s3, _ = decide policy s2 Event.Probe
        let _, actions = decide policy s3 (Event.ProbeResult false)
        actions |> Expect.equal "no StartDaemon on the second unhealthy result" []

      testCase "a healthy result reaches Ready with no pending session id" <| fun _ ->
        let s1, _ = decide policy initial Event.Probe
        let final, actions = decide policy s1 (Event.ProbeResult true)
        final.Transport |> Expect.equal "should be Ready with no session id yet" (TransportState.Ready None)
        actions |> Expect.equal "nothing queued, nothing to forward" []

      testCase "exhausting every probe attempt gives up with a Fatal, actionable reason" <| fun _ ->
        let rec spin st n =
          match n with
          | 0 -> st
          | _ ->
            let st1, _ = decide policy st Event.Probe
            let st2, _ = decide policy st1 (Event.ProbeResult false)
            spin st2 (n - 1)
        let final = spin initial 10
        match final.Transport with
        | TransportState.Fatal reason ->
          reason |> Expect.stringContains "should point at 'sagefs check'" "sagefs check"
        | other -> failtestf "expected Fatal, got %A" other

      testCase "giving up emits Shutdown — a Fatal bridge terminates on its own, it never just sits there" <| fun _ ->
        // Regression: `giveUp` used to reach Fatal WITHOUT ever emitting
        // `Action.Shutdown`, and neither did the Ready+HttpFailed Fatal
        // transition — and decide has no `Fatal, StdinClosed -> Shutdown`
        // transition either, so nothing downstream ever completed
        // `run`'s `exitCode.Task`. Reproduced live: `sagefs mcp` answered
        // `initialize`, hit the issue #138 400, and then just sat there —
        // `timeout` had to kill it. `run`'s own doc comment already
        // promised "1 if the bridge ended Fatal"; this makes that true.
        let s1, _ = decide policy initial Event.Probe
        let s2, _ = decide policy s1 (Event.ProbeResult false) // AwaitingDaemon(Starting 0)
        let s3, actions = decide { policy with MaxProbeAttempts = 0 } s2 Event.Probe
        match s3.Transport with
        | TransportState.Fatal _ -> actions |> List.contains Action.Shutdown |> Expect.isTrue "give-up must emit Shutdown"
        | other -> failtestf "expected Fatal, got %A" other

      testCase "isTerminal is true only once Fatal or Closed" <| fun _ ->
        initial |> isTerminal |> Expect.isFalse "AwaitingDaemon is not terminal"
        { Transport = TransportState.Ready None; Pending = Backlog.empty } |> isTerminal |> Expect.isFalse "Ready is not terminal"
        { Transport = TransportState.Closed; Pending = Backlog.empty } |> isTerminal |> Expect.isTrue "Closed is terminal"
        { Transport = TransportState.Fatal "x"; Pending = Backlog.empty } |> isTerminal |> Expect.isTrue "Fatal is terminal"

      testCase "giving up rejects every message still queued from the race — it never just rots in Pending" <| fun _ ->
        // Regression: the first version of giveUp only set Transport=Fatal
        // and left `Pending` untouched — a message that queued up during
        // the race and was still there when the bridge gave up was never
        // forwarded (never Ready) and never rejected (no future StdinLine
        // event touches it), so it silently vanished. `decide` must reject
        // it in the SAME transition that gives up.
        let waiting =
          { Transport = TransportState.AwaitingDaemon(DaemonReadiness.Probing policy.MaxProbeAttempts)
            Pending = Backlog.ofList [ req 1 "initialize"; notif "notifications/x" ] }
        let final, actions = decide policy waiting Event.Probe
        final.Transport |> (function TransportState.Fatal _ -> () | other -> failtestf "expected Fatal, got %A" other)
        Backlog.toList final.Pending |> Expect.isEmpty "nothing is left dangling in Pending"
        actions
        |> Expect.equal
          "ReportFatal first, then a RejectMessage for every queued message, then Shutdown last — Fatal is terminal, not something the caller has to wait for stdin to close to escape"
          [ Action.ReportFatal(match final.Transport with TransportState.Fatal r -> r | _ -> "")
            Action.RejectMessage(req 1 "initialize", (match final.Transport with TransportState.Fatal r -> r | _ -> ""))
            Action.RejectMessage(notif "notifications/x", (match final.Transport with TransportState.Fatal r -> r | _ -> ""))
            Action.Shutdown ]
    ]

    testList "messages never lost or duplicated during the race" [
      testCase "a message that arrives while waiting is queued, not forwarded yet" <| fun _ ->
        let s1, _ = decide policy initial Event.Probe
        let s2, actions = decide policy s1 (Event.StdinLine(req 1 "initialize"))
        Backlog.toList s2.Pending |> Expect.equal "queued exactly once" [ req 1 "initialize" ]
        actions |> Expect.equal "not forwarded while still waiting" []

      testCase "every queued message is drained exactly once, in arrival order, the moment the daemon is Ready" <| fun _ ->
        let s1, _ = decide policy initial Event.Probe
        let s2, _ = decide policy s1 (Event.StdinLine(req 1 "initialize"))
        let s3, _ = decide policy s2 (Event.StdinLine(notif "notifications/x"))
        let final, actions = decide policy s3 (Event.ProbeResult true)
        Backlog.toList final.Pending |> Expect.isEmpty "the queue is empty once drained"
        actions
        |> Expect.equal
          "drained in arrival order, each forwarded exactly once"
          [ Action.ForwardToDaemon(req 1 "initialize"); Action.ForwardToDaemon(notif "notifications/x") ]

      testCase "once Ready, a message forwards immediately with no queueing" <| fun _ ->
        let readyState = { Transport = TransportState.Ready(Some "sid"); Pending = Backlog.empty }
        let state, actions = decide policy readyState (Event.StdinLine(req 1 "tools/list"))
        Backlog.toList state.Pending |> Expect.isEmpty "nothing queued once Ready"
        actions |> Expect.equal "forwarded immediately" [ Action.ForwardToDaemon(req 1 "tools/list") ]

      testCase "a message arriving after Closed is rejected, never silently dropped" <| fun _ ->
        let closed = { Transport = TransportState.Closed; Pending = Backlog.empty }
        let _, actions = decide policy closed (Event.StdinLine(req 1 "tools/list"))
        match actions with
        | [ Action.RejectMessage(m, reason) ] ->
          m |> Expect.equal "the exact message is reported" (req 1 "tools/list")
          reason |> Expect.isNotEmpty "carries a reason"
        | other -> failtestf "expected a single RejectMessage, got %A" other

      testCase "a message arriving after Fatal is rejected with the fatal reason" <| fun _ ->
        let fatal = { Transport = TransportState.Fatal "daemon gone"; Pending = Backlog.empty }
        let _, actions = decide policy fatal (Event.StdinLine(req 1 "tools/list"))
        match actions with
        | [ Action.RejectMessage(_, reason) ] -> reason |> Expect.stringContains "carries the fatal reason" "daemon gone"
        | other -> failtestf "expected a single RejectMessage, got %A" other
    ]

    testList "session id capture" [
      testCase "the first capture is recorded" <| fun _ ->
        let readyNoSid = { Transport = TransportState.Ready None; Pending = Backlog.empty }
        let state, actions = decide policy readyNoSid (Event.SessionIdCaptured "abc")
        state.Transport |> Expect.equal "session id is now set" (TransportState.Ready(Some "abc"))
        actions |> Expect.equal "the push stream opens on the first session, replacing nothing" [ Action.OpenPushStream("abc", StreamSuccession.First) ]

      testCase "the same id captured again changes nothing" <| fun _ ->
        let readyWithSid = { Transport = TransportState.Ready(Some "abc"); Pending = Backlog.empty }
        let state, actions = decide policy readyWithSid (Event.SessionIdCaptured "abc")
        state |> Expect.equal "the state is untouched" readyWithSid
        actions |> Expect.equal "no action from a repeat capture" []

      testCase "a different id replaces the held one, because it is the daemon's answer to a re-handshake" <| fun _ ->
        let readyWithSid = { Transport = TransportState.Ready(Some "abc"); Pending = Backlog.empty }
        let state, actions = decide policy readyWithSid (Event.SessionIdCaptured "zzz")
        state.Transport |> Expect.equal "every later request carries the new id" (TransportState.Ready(Some "zzz"))
        actions
        |> Expect.equal "the new session's stream opens and the old one's is cancelled" [ Action.OpenPushStream("zzz", StreamSuccession.Replaces "abc") ]
    ]

    testList "a protocol-level rejection stays alive (issue #138, defect 2)" [
      testCase "Ready + RequestRejected answers just that message and stays Ready" <| fun _ ->
        let readyState = { Transport = TransportState.Ready(Some "sid"); Pending = Backlog.empty }
        let msg = req 2 "tools/list"
        let state, actions = decide policy readyState (Event.RequestRejected(msg, "daemon answered 400: bad request"))
        state.Transport |> Expect.equal "still Ready, still holding its session id — a 400 is not the daemon going away" (TransportState.Ready(Some "sid"))
        actions |> Expect.equal "reuses RejectMessage — the same shape any other unforwardable message gets" [ Action.RejectMessage(msg, "daemon answered 400: bad request") ]

      testCase "RequestRejected with no session id yet still stays Ready" <| fun _ ->
        let readyState = { Transport = TransportState.Ready None; Pending = Backlog.empty }
        let msg = req 1 "initialize"
        let state, _ = decide policy readyState (Event.RequestRejected(msg, "daemon answered 400: bad request"))
        state.Transport |> Expect.equal "Ready None, not Fatal" (TransportState.Ready None)

      testCase "a stray RequestRejected outside Ready is a no-op, same posture as a stray HttpFailed" <| fun _ ->
        let msg = req 1 "tools/list"
        let fatal = { Transport = TransportState.Fatal "already gone"; Pending = Backlog.empty }
        let state, actions = decide policy fatal (Event.RequestRejected(msg, "irrelevant"))
        state |> Expect.equal "unchanged" fatal
        actions |> Expect.equal "nothing to do" []
    ]

    testList "the daemon dies mid-session" [
      testCase "an HttpFailed while Ready waits for the daemon and keeps the session id it held" <| fun _ ->
        let readyState = { Transport = TransportState.Ready(Some "sid"); Pending = Backlog.empty }
        let state, actions = decide policy readyState (Event.HttpFailed "connection reset")
        state.Transport
        |> Expect.equal "the bridge is reconnecting, not Fatal" (TransportState.AwaitingDaemon(DaemonReadiness.Reconnecting(0, HeldSession.Id "sid")))
        actions |> Expect.equal "it probes at once" [ Action.Probe ]

      testCase "the end of the push stream of the held session is the daemon going away" <| fun _ ->
        let readyState = { Transport = TransportState.Ready(Some "sid"); Pending = Backlog.empty }
        let state, _ = decide policy readyState (Event.ServerStreamClosed("sid", "server stream closed"))
        state.Transport
        |> Expect.equal "reconnecting, holding the id" (TransportState.AwaitingDaemon(DaemonReadiness.Reconnecting(0, HeldSession.Id "sid")))

      testCase "the end of an OLDER session's push stream says nothing about the daemon" <| fun _ ->
        let readyState = { Transport = TransportState.Ready(Some "new"); Pending = Backlog.empty }
        let state, actions = decide policy readyState (Event.ServerStreamClosed("old", "server stream closed"))
        state |> Expect.equal "unchanged" readyState
        actions |> Expect.equal "nothing to do" []

      testCase "a request that was in flight when the daemon went away is still answered, even when the stream's end got there first" <| fun _ ->
        let lost = { Transport = TransportState.AwaitingDaemon(DaemonReadiness.Reconnecting(0, HeldSession.Id "sid")); Pending = Backlog.empty }
        let msg = req 5 "tools/call"
        let state, actions = decide policy lost (Event.RequestRejected(msg, "the daemon became unreachable"))
        state |> Expect.equal "still reconnecting" lost
        actions |> Expect.equal "the client gets its error" [ Action.RejectMessage(msg, "the daemon became unreachable") ]

      testCase "a daemon that answers again starts the re-handshake, and the queue waits for the new session" <| fun _ ->
        let waiting =
          { Transport = TransportState.AwaitingDaemon(DaemonReadiness.Reconnecting(2, HeldSession.Id "stale"))
            Pending = Backlog.ofList [ req 7 "tools/list" ] }
        let state, actions = decide policy waiting (Event.ProbeResult true)
        state.Transport |> Expect.equal "minting a session, not Ready under the id the new daemon never issued" (TransportState.Rehandshaking "stale")
        Backlog.toList state.Pending |> Expect.equal "the queued request is still queued" [ req 7 "tools/list" ]
        actions |> Expect.equal "only the re-handshake goes out" [ Action.Rehandshake "stale" ]

      testCase "a lost daemon is waited for, and one that never returns is started after MaxReconnectProbes probes" <| fun _ ->
        let lost = { Transport = TransportState.AwaitingDaemon(DaemonReadiness.Reconnecting(0, HeldSession.Id "sid")); Pending = Backlog.empty }
        let probeOnce (s: State, started: int) =
          let s1, a1 = decide policy s Event.Probe
          let s2, a2 = decide policy s1 (Event.ProbeResult false)
          s2, started + (a1 @ a2 |> List.filter ((=) Action.StartDaemon) |> List.length)
        let beforeWindowEnds, startedEarly = List.fold (fun acc _ -> probeOnce acc) (lost, 0) [ 1 .. policy.MaxReconnectProbes - 1 ]
        startedEarly |> Expect.equal "no daemon is started while probes still fit the window" 0
        beforeWindowEnds |> isTerminal |> Expect.isFalse "the bridge is still alive"
        let finalState, started = probeOnce (beforeWindowEnds, startedEarly)
        started |> Expect.equal "the daemon is started once the window of probes has found none" 1
        finalState.Transport
        |> Expect.equal "now in the cold start's Starting state" (TransportState.AwaitingDaemon(DaemonReadiness.Starting 0))

      testCase "a DaemonStartFailed while Starting becomes a clear Fatal naming the failure" <| fun _ ->
        let starting = { Transport = TransportState.AwaitingDaemon(DaemonReadiness.Starting 1); Pending = Backlog.empty }
        let state, _ = decide policy starting (Event.DaemonStartFailed "port in use")
        match state.Transport with
        | TransportState.Fatal reason -> reason |> Expect.stringContains "carries the underlying reason" "port in use"
        | other -> failtestf "expected Fatal, got %A" other
    ]

    testList "shutdown" [
      testCase "stdin closing with a captured session id terminates it, then shuts down" <| fun _ ->
        let readyState = { Transport = TransportState.Ready(Some "sid"); Pending = Backlog.empty }
        let state, actions = decide policy readyState Event.StdinClosed
        state.Transport |> Expect.equal "Closed" TransportState.Closed
        actions |> Expect.equal "terminate then shut down, in order" [ Action.TerminateSession "sid"; Action.Shutdown ]

      testCase "stdin closing with no session id just shuts down" <| fun _ ->
        let readyState = { Transport = TransportState.Ready None; Pending = Backlog.empty }
        let state, actions = decide policy readyState Event.StdinClosed
        state.Transport |> Expect.equal "Closed" TransportState.Closed
        actions |> Expect.equal "shut down only" [ Action.Shutdown ]

      testCase "stdin closing mid-race also just shuts down" <| fun _ ->
        let state, actions = decide policy initial Event.StdinClosed
        state.Transport |> Expect.equal "Closed" TransportState.Closed
        actions |> Expect.equal "shut down only" [ Action.Shutdown ]

      testCase "stdin closing mid-race rejects every message still queued — it never just rots in Pending" <| fun _ ->
        // Regression, found by the DST: a client that sends a message and
        // then disconnects before the daemon ever answers used to leave
        // that message sitting in Pending forever — Closed never drains or
        // rejects it, and no future event ever looks at it again.
        let s1, _ = decide policy initial Event.Probe
        let waiting, _ = decide policy s1 (Event.StdinLine(req 1 "initialize"))
        let final, actions = decide policy waiting Event.StdinClosed
        final.Transport |> Expect.equal "Closed" TransportState.Closed
        Backlog.toList final.Pending |> Expect.isEmpty "nothing left dangling"
        match actions with
        | [ Action.Shutdown; Action.RejectMessage(m, _) ] -> m |> Expect.equal "the exact queued message is rejected" (req 1 "initialize")
        | other -> failtestf "expected Shutdown then a RejectMessage, got %A" other

      testCase "isConnected reflects Ready and nothing else" <| fun _ ->
        initial |> isConnected |> Expect.isFalse "AwaitingDaemon is not connected"
        { Transport = TransportState.Ready None; Pending = Backlog.empty } |> isConnected |> Expect.isTrue "Ready is connected"
        { Transport = TransportState.Closed; Pending = Backlog.empty } |> isConnected |> Expect.isFalse "Closed is not connected"
    ]

    testList "the re-handshake is not atomic (P17)" [
      let rehandshaking = { Transport = TransportState.Rehandshaking "stale"; Pending = Backlog.empty }

      testCase "WHY — a line that arrives while the new session is being minted is queued, never sent under the id the new daemon does not know" <| fun _ ->
        let state, actions = decide policy rehandshaking (Event.StdinLine(req 1 "tools/call"))
        actions |> Expect.equal "nothing goes to the daemon yet" []
        Backlog.toList state.Pending |> Expect.equal "the line is held" [ req 1 "tools/call" ]
        state.Transport |> Expect.equal "still minting" (TransportState.Rehandshaking "stale")

      testCase "the capture ends the wait: one push stream on the new id, then every held line in arrival order" <| fun _ ->
        let held = Backlog.ofList [ req 1 "tools/list"; notif "notifications/x"; req 2 "tools/call" ]
        let state, actions = decide policy { rehandshaking with Pending = held } (Event.SessionIdCaptured "fresh")
        state.Transport |> Expect.equal "Ready under the new id" (TransportState.Ready(Some "fresh"))
        Backlog.toList state.Pending |> Expect.isEmpty "the queue is empty"
        actions
        |> Expect.equal
          "the stream replaces the stale session's, then the queue goes out"
          [ Action.OpenPushStream("fresh", StreamSuccession.Replaces "stale")
            Action.ForwardToDaemon(req 1 "tools/list")
            Action.ForwardToDaemon(notif "notifications/x")
            Action.ForwardToDaemon(req 2 "tools/call") ]

      testCase "a failed re-handshake answers what is queued, and goes back to waiting for a daemon that can give it a session" <| fun _ ->
        let held = Backlog.ofList [ req 1 "tools/list"; notif "notifications/x" ]
        let state, actions = decide policy { rehandshaking with Pending = held } (Event.RehandshakeFailed "the daemon answered without a session id")
        state.Transport
        |> Expect.equal "waiting again, still holding the stale id" (TransportState.AwaitingDaemon(DaemonReadiness.Reconnecting(0, HeldSession.Id "stale")))
        Backlog.toList state.Pending |> Expect.isEmpty "nothing is left held"
        match actions with
        | [ Action.RejectMessage(first, reason); Action.RejectMessage(second, _); Action.Probe ] ->
          first |> Expect.equal "the request is answered" (req 1 "tools/list")
          second |> Expect.equal "so is the notification, in order" (notif "notifications/x")
          reason |> Expect.stringContains "the reason names what failed" "the daemon answered without a session id"
        | other -> failtestf "expected two rejections and a probe, got %A" other

      testCase "a request that was in flight when the daemon died is still answered while the new session is minted" <| fun _ ->
        let msg = req 5 "tools/call"
        let state, actions = decide policy rehandshaking (Event.RequestRejected(msg, "the daemon became unreachable"))
        state |> Expect.equal "unchanged" rehandshaking
        actions |> Expect.equal "the client gets its error" [ Action.RejectMessage(msg, "the daemon became unreachable") ]

      testCase "a request in flight when the reconnect window ended and the bridge started its own daemon is still answered" <| fun _ ->
        let starting = { Transport = TransportState.AwaitingDaemon(DaemonReadiness.Starting 1); Pending = Backlog.empty }
        let msg = req 5 "tools/call"
        let state, actions = decide policy starting (Event.RequestRejected(msg, "the daemon became unreachable"))
        state |> Expect.equal "unchanged" starting
        actions |> Expect.equal "the client gets its error" [ Action.RejectMessage(msg, "the daemon became unreachable") ]

      testCase "a late failure report does not restart the re-handshake" <| fun _ ->
        let state, actions = decide policy rehandshaking (Event.HttpFailed "connection reset")
        state |> Expect.equal "unchanged" rehandshaking
        actions |> Expect.equal "nothing to do" []

      testCase "stdin closing while the session is minted rejects what is queued and shuts down" <| fun _ ->
        let held = Backlog.ofList [ req 1 "tools/list" ]
        let state, actions = decide policy { rehandshaking with Pending = held } Event.StdinClosed
        state.Transport |> Expect.equal "Closed" TransportState.Closed
        match actions with
        | [ Action.Shutdown; Action.RejectMessage(m, _) ] -> m |> Expect.equal "the held request is answered" (req 1 "tools/list")
        | other -> failtestf "expected Shutdown then a RejectMessage, got %A" other

      testProperty "WHY — however many lines arrive during the re-handshake, each is forwarded exactly once, in order, after the capture" <| fun (count: byte) ->
        let n = int count % (policy.QueueCap + 1)
        let lines = [ for i in 1..n -> req i "tools/call" ]
        let queued =
          lines |> List.fold (fun (s: State) line -> fst (decide policy s (Event.StdinLine line))) rehandshaking
        let forwardedBefore =
          lines |> List.fold (fun acc line -> acc @ snd (decide policy rehandshaking (Event.StdinLine line))) []
        let _, released = decide policy queued (Event.SessionIdCaptured "fresh")
        forwardedBefore = []
        && (released |> List.choose (function Action.ForwardToDaemon m -> Some m | _ -> None)) = lines

      testCase "WHY — a client that outpaces a daemon that never returns hits a cap, and the message past it is answered with an error that says why" <| fun _ ->
        let full =
          { Transport = TransportState.AwaitingDaemon(DaemonReadiness.Reconnecting(0, HeldSession.Id "sid"))
            Pending = Backlog.ofList [ for i in 1 .. policy.QueueCap -> req i "tools/call" ] }
        let overflowing = req 99 "tools/call"
        let state, actions = decide policy full (Event.StdinLine overflowing)
        state |> Expect.equal "the queue did not grow" full
        match actions with
        | [ Action.RejectMessage(m, reason) ] ->
          m |> Expect.equal "the overflowing request is the one answered" overflowing
          reason |> Expect.stringContains "says the queue was full" "full"
          reason |> Expect.stringContains "names the cap" (string policy.QueueCap)
        | other -> failtestf "expected one RejectMessage, got %A" other

      testCase "the cap holds while the new session is minted too" <| fun _ ->
        let full = { rehandshaking with Pending = Backlog.ofList [ for i in 1 .. policy.QueueCap -> req i "tools/call" ] }
        let state, actions = decide policy full (Event.StdinLine(req 99 "tools/call"))
        state |> Expect.equal "the queue did not grow" full
        match actions with
        | [ Action.RejectMessage _ ] -> ()
        | other -> failtestf "expected one RejectMessage, got %A" other

      testProperty "the backlog hands messages back oldest first and counts them without walking them" <| fun (count: byte) ->
        let messages = [ for i in 1 .. int count -> req i "tools/list" ]
        let backlog = messages |> List.fold (fun b m -> Backlog.enqueue m b) Backlog.empty
        Backlog.toList backlog = messages && Backlog.count backlog = messages.Length && backlog = Backlog.ofList messages
    ]

    testList "a re-handshake keeps what the client declared" [
      testCase "WHY — an initialize hands back its params verbatim, because the daemon records the CLIENT's capabilities from them, and those decide whether it may ever be asked a question"
      <| fun _ ->
        let raw =
          """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{"elicitation":{}},"clientInfo":{"name":"a-client","version":"1"}}}"""
        match parseRpcMessage raw |> initializeParamsOf with
        | None -> failtest "an initialize must yield its params"
        | Some p ->
          p |> Expect.stringContains "keeps the client's capabilities" "\"elicitation\""
          p |> Expect.stringContains "keeps the client's protocol version" "2025-06-18"

      testCase "WHY — only an initialize yields params, so a heal can never pick up a payload that was never a handshake"
      <| fun _ ->
        parseRpcMessage """{"jsonrpc":"2.0","id":7,"method":"tools/list","params":{"cursor":"x"}}"""
        |> initializeParamsOf
        |> Expect.isNone "a tools/list is not a handshake"
        parseRpcMessage """{"jsonrpc":"2.0","method":"notifications/initialized"}"""
        |> initializeParamsOf
        |> Expect.isNone "a notification is not a handshake"
        parseRpcMessage """{"jsonrpc":"2.0","id":1,"method":"initialize"}"""
        |> initializeParamsOf
        |> Expect.isNone "an initialize with no params has nothing to preserve"

      testCase "WHY — the re-handshake body carries the held params under the bridge's own id, so the daemon sees the real client again"
      <| fun _ ->
        let held =
          Some """{"protocolVersion":"2025-06-18","capabilities":{"elicitation":{}},"clientInfo":{"name":"a-client","version":"1"}}"""
        let body = reHandshakeBodyFor held
        body |> Expect.stringContains "keeps the held capabilities" "\"elicitation\""
        body |> Expect.stringContains "keeps the client's protocol version" "2025-06-18"
        body |> Expect.stringContains "keeps the bridge's own id" "sagefs-mcp-bridge-rehandshake"
        body |> Expect.stringContains "and is still an initialize" "\"method\":\"initialize\""

      testCase "WHY — with nothing held the bridge still mints a session, with the empty capabilities it has always sent"
      <| fun _ ->
        let body = reHandshakeBodyFor None
        body |> Expect.stringContains "still an initialize" "\"method\":\"initialize\""
        body.Contains "\"elicitation\"" |> Expect.isFalse "nothing was declared, so nothing is claimed"

      testCase "WHY — an initialize is remembered and nothing after it forgets, or the first ordinary message would strip the capabilities the heal needs"
      <| fun _ ->
        let init = parseRpcMessage """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""
        let afterInit = heldAfter None init
        afterInit |> Expect.isSome "an initialize is what gets remembered"
        let toolsList = parseRpcMessage """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""
        let notification = parseRpcMessage """{"jsonrpc":"2.0","method":"notifications/initialized"}"""
        heldAfter afterInit toolsList
        |> Expect.equal "an ordinary message never forgets the declaration" afterInit
        heldAfter afterInit notification
        |> Expect.equal "nor does a notification" afterInit
        // And a second handshake replaces the first rather than merging with it.
        let second =
          parseRpcMessage """{"jsonrpc":"2.0","id":9,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{}}}"""
        match heldAfter afterInit second with
        | None -> failtest "the second handshake must still be remembered"
        | Some p -> p |> Expect.stringContains "a later handshake replaces the earlier one" "2024-11-05"
    ]
  ]
