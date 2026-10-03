#!/usr/bin/env dotnet fsi
/// Re-establish a live MCP session against the running SageFs daemon, and PROVE it with a real
/// tool call.
///
/// WHY THIS EXISTS. The MCP session is held in the DAEMON'S MEMORY: the SDK issues an
/// `Mcp-Session-Id` at `initialize` and keeps that session in the process. Every daemon restart —
/// and `install-local --force` restarts it on purpose, so a new build is served — destroys the
/// session, so the id a connected client is still holding becomes a 404. Nothing re-runs
/// `initialize`, so the client never learns a new one, and the connection looks broken until a
/// human reloads. The connection was never broken: it was LIVE, pointing at a session that no
/// longer existed. Re-`initialize` is the entire repair, and it is one round trip.
///
/// IT EXITS NON-ZERO UNLESS IT PROVED ITSELF, which is the part that matters — a reconnect script
/// that prints "OK" without proof is a script that will lie to you when the daemon is unhealthy:
///   1. the daemon is reachable on this port (a wrong port is a different problem);
///   2. `initialize` succeeds and mints an `Mcp-Session-Id` in the RESPONSE HEADERS;
///   3. a DELIBERATELY STALE id is REJECTED — the control, without which a successful call below
///      would only prove the endpoint answers, not that the session is what makes it answer;
///   4. a REAL `tools/call` on the freshly minted id returns a result and no error.
///
/// USAGE:
///   dotnet fsi scripts/reconnect-mcp.fsx            # the daemon on 37749
///   dotnet fsi scripts/reconnect-mcp.fsx 37801     # another port

open System
open System.Net.Http
open System.Text

let port =
  match Environment.GetCommandLineArgs() |> Array.skip 1 |> Array.tryFind (fun a -> a <> "fsi" && a.EndsWith ".fsx" = false) with
  | Some p -> p
  | None -> "37749"

let baseUrl = sprintf "http://127.0.0.1:%s/" port
let http = new HttpClient()
http.Timeout <- TimeSpan.FromSeconds 20.0

let failIfDown msg =
  printfn "FAIL: %s" msg
  exit 1

let sendWith (sessionId: string option) (body: string) =
  let req = new HttpRequestMessage(HttpMethod.Post, baseUrl)
  req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream") |> ignore
  match sessionId with
  | Some id -> req.Headers.TryAddWithoutValidation("Mcp-Session-Id", id) |> ignore
  | None -> ()
  req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
  // `.Result` rather than a pipe: `dotnet fsi` runs this as a Task, and `Async.AwaitTask` does
  // not apply. A Task is awaited by the runtime here, so this cannot deadlock on a live socket.
  let r = http.SendAsync(req).Result
  let text = r.Content.ReadAsStringAsync().Result
  r, text

// 1. Reachable at all? A wrong port is a different failure from a dead session.
let health = http.GetAsync(baseUrl + "health").Result
if not health.IsSuccessStatusCode then
  failIfDown (sprintf "the daemon on %s answered %d — is the port right? (default 37749)" baseUrl (int health.StatusCode))
printfn "daemon is up on %s" baseUrl

// 2. initialize, and take the session id from the response HEADERS.
let initBody =
  """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"reconnect-mcp","version":"1"}}}"""

let initResp, _ = sendWith None initBody
if not initResp.IsSuccessStatusCode then
  failIfDown (sprintf "initialize answered %d" (int initResp.StatusCode))

let sessionId =
  match initResp.Headers.TryGetValues("Mcp-Session-Id") with
  | true, values -> values |> Seq.head
  | _ -> failIfDown "initialize succeeded but minted no Mcp-Session-Id header"
printfn "minted session %s" sessionId

// 3. THE CONTROL. A stale id MUST be rejected, or step 4 proves nothing.
let staleResp, _ =
  sendWith (Some "this-session-was-never-minted")
    """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"get_daemon_status","arguments":{}}}"""
let staleRejected = not staleResp.IsSuccessStatusCode
printfn "control: a never-minted session id -> %d, %s" (int staleResp.StatusCode) (if staleRejected then "rejected (as it must be)" else "ACCEPTED — the session is not what is being verified")
if not staleRejected then failIfDown "the daemon accepted a session id that was never minted"

// 4. The real proof.
let callResp, callText =
  sendWith (Some sessionId)
    """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_daemon_status","arguments":{}}}"""

let hasResult = callText.Contains("\"result\"")
let hasError = callText.Contains("\"error\"")
printfn "probe: a real tools/call -> %d, %d bytes, result=%b error=%b" (int callResp.StatusCode) callText.Length hasResult hasError

if callResp.IsSuccessStatusCode && hasResult && not hasError then
  printfn "OK - the session is live, and proven by a control plus a real tool call."
  exit 0
else
  failIfDown "the id was minted but a real tool call against it did not succeed"