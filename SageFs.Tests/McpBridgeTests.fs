module SageFs.Tests.McpBridgeTests

open Expecto
open Expecto.Flip
open SageFs.McpBridge

let private policy = { MaxProbeAttempts = 3 }

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

      testCase "isTerminal is true only once Fatal or Closed" <| fun _ ->
        initial |> isTerminal |> Expect.isFalse "AwaitingDaemon is not terminal"
        { Transport = TransportState.Ready None; Pending = [] } |> isTerminal |> Expect.isFalse "Ready is not terminal"
        { Transport = TransportState.Closed; Pending = [] } |> isTerminal |> Expect.isTrue "Closed is terminal"
        { Transport = TransportState.Fatal "x"; Pending = [] } |> isTerminal |> Expect.isTrue "Fatal is terminal"

      testCase "giving up rejects every message still queued from the race — it never just rots in Pending" <| fun _ ->
        // Regression: the first version of giveUp only set Transport=Fatal
        // and left `Pending` untouched — a message that queued up during
        // the race and was still there when the bridge gave up was never
        // forwarded (never Ready) and never rejected (no future StdinLine
        // event touches it), so it silently vanished. `decide` must reject
        // it in the SAME transition that gives up.
        let waiting =
          { Transport = TransportState.AwaitingDaemon(DaemonReadiness.Probing policy.MaxProbeAttempts)
            Pending = [ req 1 "initialize"; notif "notifications/x" ] }
        let final, actions = decide policy waiting Event.Probe
        final.Transport |> (function TransportState.Fatal _ -> () | other -> failtestf "expected Fatal, got %A" other)
        final.Pending |> Expect.isEmpty "nothing is left dangling in Pending"
        actions
        |> Expect.equal
          "ReportFatal first, then a RejectMessage for every queued message, in order"
          [ Action.ReportFatal(match final.Transport with TransportState.Fatal r -> r | _ -> "")
            Action.RejectMessage(req 1 "initialize", (match final.Transport with TransportState.Fatal r -> r | _ -> ""))
            Action.RejectMessage(notif "notifications/x", (match final.Transport with TransportState.Fatal r -> r | _ -> "")) ]
    ]

    testList "messages never lost or duplicated during the race" [
      testCase "a message that arrives while waiting is queued, not forwarded yet" <| fun _ ->
        let s1, _ = decide policy initial Event.Probe
        let s2, actions = decide policy s1 (Event.StdinLine(req 1 "initialize"))
        s2.Pending |> Expect.equal "queued exactly once" [ req 1 "initialize" ]
        actions |> Expect.equal "not forwarded while still waiting" []

      testCase "every queued message is drained exactly once, in arrival order, the moment the daemon is Ready" <| fun _ ->
        let s1, _ = decide policy initial Event.Probe
        let s2, _ = decide policy s1 (Event.StdinLine(req 1 "initialize"))
        let s3, _ = decide policy s2 (Event.StdinLine(notif "notifications/x"))
        let final, actions = decide policy s3 (Event.ProbeResult true)
        final.Pending |> Expect.isEmpty "the queue is empty once drained"
        actions
        |> Expect.equal
          "drained in arrival order, each forwarded exactly once"
          [ Action.ForwardToDaemon(req 1 "initialize"); Action.ForwardToDaemon(notif "notifications/x") ]

      testCase "once Ready, a message forwards immediately with no queueing" <| fun _ ->
        let readyState = { Transport = TransportState.Ready(Some "sid"); Pending = [] }
        let state, actions = decide policy readyState (Event.StdinLine(req 1 "tools/list"))
        state.Pending |> Expect.isEmpty "nothing queued once Ready"
        actions |> Expect.equal "forwarded immediately" [ Action.ForwardToDaemon(req 1 "tools/list") ]

      testCase "a message arriving after Closed is rejected, never silently dropped" <| fun _ ->
        let closed = { Transport = TransportState.Closed; Pending = [] }
        let _, actions = decide policy closed (Event.StdinLine(req 1 "tools/list"))
        match actions with
        | [ Action.RejectMessage(m, reason) ] ->
          m |> Expect.equal "the exact message is reported" (req 1 "tools/list")
          reason |> Expect.isNotEmpty "carries a reason"
        | other -> failtestf "expected a single RejectMessage, got %A" other

      testCase "a message arriving after Fatal is rejected with the fatal reason" <| fun _ ->
        let fatal = { Transport = TransportState.Fatal "daemon gone"; Pending = [] }
        let _, actions = decide policy fatal (Event.StdinLine(req 1 "tools/list"))
        match actions with
        | [ Action.RejectMessage(_, reason) ] -> reason |> Expect.stringContains "carries the fatal reason" "daemon gone"
        | other -> failtestf "expected a single RejectMessage, got %A" other
    ]

    testList "session id capture" [
      testCase "the first capture is recorded" <| fun _ ->
        let readyNoSid = { Transport = TransportState.Ready None; Pending = [] }
        let state, actions = decide policy readyNoSid (Event.SessionIdCaptured "abc")
        state.Transport |> Expect.equal "session id is now set" (TransportState.Ready(Some "abc"))
        actions |> Expect.equal "capture has no side effect of its own" []

      testCase "a second capture is ignored — captured exactly once" <| fun _ ->
        let readyWithSid = { Transport = TransportState.Ready(Some "abc"); Pending = [] }
        let state, actions = decide policy readyWithSid (Event.SessionIdCaptured "zzz")
        state.Transport |> Expect.equal "the original id is kept" (TransportState.Ready(Some "abc"))
        actions |> Expect.equal "no action from a repeat capture" []
    ]

    testList "the daemon dies mid-session" [
      testCase "an HttpFailed while Ready becomes a clear Fatal" <| fun _ ->
        let readyState = { Transport = TransportState.Ready(Some "sid"); Pending = [] }
        let state, actions = decide policy readyState (Event.HttpFailed "connection reset")
        match state.Transport with
        | TransportState.Fatal reason -> reason |> Expect.stringContains "carries the underlying reason" "connection reset"
        | other -> failtestf "expected Fatal, got %A" other
        match actions with
        | [ Action.ReportFatal _ ] -> ()
        | other -> failtestf "expected a single ReportFatal, got %A" other

      testCase "a DaemonStartFailed while Starting becomes a clear Fatal naming the failure" <| fun _ ->
        let starting = { Transport = TransportState.AwaitingDaemon(DaemonReadiness.Starting 1); Pending = [] }
        let state, _ = decide policy starting (Event.DaemonStartFailed "port in use")
        match state.Transport with
        | TransportState.Fatal reason -> reason |> Expect.stringContains "carries the underlying reason" "port in use"
        | other -> failtestf "expected Fatal, got %A" other
    ]

    testList "shutdown" [
      testCase "stdin closing with a captured session id terminates it, then shuts down" <| fun _ ->
        let readyState = { Transport = TransportState.Ready(Some "sid"); Pending = [] }
        let state, actions = decide policy readyState Event.StdinClosed
        state.Transport |> Expect.equal "Closed" TransportState.Closed
        actions |> Expect.equal "terminate then shut down, in order" [ Action.TerminateSession "sid"; Action.Shutdown ]

      testCase "stdin closing with no session id just shuts down" <| fun _ ->
        let readyState = { Transport = TransportState.Ready None; Pending = [] }
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
        final.Pending |> Expect.isEmpty "nothing left dangling"
        match actions with
        | [ Action.Shutdown; Action.RejectMessage(m, _) ] -> m |> Expect.equal "the exact queued message is rejected" (req 1 "initialize")
        | other -> failtestf "expected Shutdown then a RejectMessage, got %A" other

      testCase "isConnected reflects Ready and nothing else" <| fun _ ->
        initial |> isConnected |> Expect.isFalse "AwaitingDaemon is not connected"
        { Transport = TransportState.Ready None; Pending = [] } |> isConnected |> Expect.isTrue "Ready is connected"
        { Transport = TransportState.Closed; Pending = [] } |> isConnected |> Expect.isFalse "Closed is not connected"
    ]
  ]
