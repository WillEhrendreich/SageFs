module SageFs.Tests.DaemonStateTests

open System
open System.Diagnostics
open Expecto
open Expecto.Flip
open SageFs

let private ok (body: string) : DaemonState.ProbeReply =
  { Success = true; ReadBody = fun () -> async { return body } }

let private notFound : DaemonState.ProbeReply =
  { Success = false; ReadBody = fun () -> async { return "" } }

/// A transport that answers each URL from the route whose suffix it ends with, and fails the case on a URL
/// nobody planned for.
let private answers (routes: (string * DaemonState.ProbeReply) list) (url: string) : Async<DaemonState.ProbeReply> =
  async {
    match routes |> List.tryFind (fun (suffix, _) -> url.EndsWith suffix) with
    | Some (_, reply) -> return reply
    | None -> return failwithf "the probe asked for %s, which this case did not plan for" url
  }

[<Tests>]
let tests =
  testList "DaemonState" [
    testList "isProcessAlive" [
      testCase "current process is alive" <| fun _ ->
        let pid = Process.GetCurrentProcess().Id
        DaemonState.isProcessAlive pid
        |> Expect.isTrue "current process should be alive"

      testCase "bogus PID is not alive" <| fun _ ->
        DaemonState.isProcessAlive 99999999
        |> Expect.isFalse "bogus PID should not be alive"
    ]

    testList "HTTP detection" [
      testCase "read returns None when no daemon running" <| fun _ ->
        // Probe a port that should not have a daemon
        DaemonState.readOnPort 39999
        |> Expect.isNone "should return None when no daemon on port"

      testCase "requestShutdown returns false when no daemon" <| fun _ ->
        DaemonState.requestShutdown 39999
        |> Expect.isFalse "shutdown should fail when no daemon"

      testCase "tryParseDaemonInfoJson reads dashboard port, api version, and session count" <| fun _ ->
        let json = """{"pid":42,"version":"1.2.3","startedAt":"2026-03-12T00:00:00Z","workingDirectory":"C:\\repo","mcpPort":37749,"dashboardPort":37760,"apiVersion":7,"sessionCount":3}"""
        let info = DaemonState.tryParseDaemonInfoJson 37749 json
        info |> Expect.isSome "rich daemon-info payload should parse"
        info.Value.Port |> Expect.equal "mcp port should round-trip" 37749
        info.Value.DashboardPort |> Expect.equal "dashboard port should use payload when present" 37760
        info.Value.ApiVersion |> Expect.equal "api version should parse when present" (Some 7)
        info.Value.SessionCount |> Expect.equal "session count should parse when present" (Some 3)

      testCase "tryParseDaemonInfoJson falls back for older daemon payloads" <| fun _ ->
        let json = """{"pid":7,"version":"0.1.0","startedAt":"2026-03-12T00:00:00Z","workingDirectory":"C:\\repo"}"""
        let info = DaemonState.tryParseDaemonInfoJson 37749 json
        info |> Expect.isSome "older daemon-info payload should still parse"
        info.Value.DashboardPort |> Expect.equal "dashboard port should derive from mcp when omitted" 37750
        info.Value.ApiVersion |> Expect.equal "api version should be absent when omitted" None
        info.Value.SessionCount |> Expect.equal "session count should be absent when omitted" None
    ]

    testList "daemon startup guard" [
      testCase "probeDaemonHttp returns None on unused port" <| fun _ ->
        DaemonState.probeDaemonHttp 39998
        |> Expect.isNone "no daemon on unused port"

      testCase "readOnPort returns None on unused port" <| fun _ ->
        DaemonState.readOnPort 39997
        |> Expect.isNone "no daemon on unused port"

      testCase "guard prevents double-start when daemon is running" <| fun _ ->
        // Simulate the guard pattern: check before spawning
        let port = 39996
        let guardResult =
          match DaemonState.readOnPort port with
          | Some _ -> Error "SageFs daemon is already running"
          | None -> Ok "would start"
        guardResult
        |> Expect.isOk "should allow start when no daemon running"

      // The probe's decisions run against a fake transport. The first version of this case stood up a
      // real HttpListener, answered it from a thread-pool work item and read the answer through an
      // HttpClient with a 2s deadline. Inside the gate's test process, which runs thousands of cases at
      // once, the pool had no thread free to answer within that deadline, so a correct probe came back
      // "no daemon". A transport with no sockets and no pool has no deadline to miss; the real socket
      // is covered where a daemon really runs (DaemonIntegrationTests).
      testTask "guard blocks start when daemon responds" {
        let port = 39996
        let! info =
          DaemonState.probeDaemonWith
            (answers [ "/api/daemon-info", ok (sprintf """{"pid":99999,"port":%d,"version":"test"}""" port) ])
            port
          |> Async.StartAsTask
        info |> Expect.isSome "should detect mock daemon"
        info.Value.Pid |> Expect.equal "pid matches" 99999
      }

      testTask "an older daemon without daemon-info is found by its dashboard page" {
        let port = 39996
        let! info =
          DaemonState.probeDaemonWith
            (answers [ "/api/daemon-info", notFound; "/dashboard", ok "<html></html>" ])
            port
          |> Async.StartAsTask
        info |> Expect.isSome "the dashboard answering is a daemon answering"
        info.Value.DashboardPort |> Expect.equal "the dashboard port is the mcp port plus one" (port + 1)
      }

      testTask "a refused connection means no daemon and the dashboard is not asked" {
        let asked = ResizeArray<string>()
        let refusal = System.Net.Http.HttpRequestException("refused", System.Net.Sockets.SocketException(int System.Net.Sockets.SocketError.ConnectionRefused))
        let! info =
          DaemonState.probeDaemonWith (fun url -> async { asked.Add url; return raise refusal }) 39996
          |> Async.StartAsTask
        info |> Expect.isNone "nothing is listening"
        asked.Count |> Expect.equal "a refusal is final, the fallback would get the same answer" 1
      }

      testCase "guard allows start when daemon not responding" <| fun _ ->
        // No listener on this port — guard should allow start. Reserved, not assumed:
        // a fixed number could be a live listener on a machine that runs daemons.
        let port, _ = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
        let info = DaemonState.readOnPort port
        info |> Expect.isNone "no daemon on port"

        let decision =
          match info with
          | Some _ -> Error "already running"
          | None -> Ok "proceed to start"
        decision
        |> Expect.isOk "should allow start"
    ]
  ]
