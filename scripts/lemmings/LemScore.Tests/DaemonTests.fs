module LemScore.Tests.DaemonTests

open Expecto
open Expecto.Flip
open LemScore
open LemScore.Types
open LemScore.SharedDaemon
open LemScore.CmdcStream
open LemScore.Tests.Samples

/// The /health body of the live dev daemon (captured 2026-10-01).
let private healthBody =
  """{"anomalies":[],"apiVersion":3,"componentFailures":[],"diagnosticSummary":"No sessions registered with the daemon.","error":null,"features":["live-testing"],"healthy":true,"memoryMB":205,"memoryPressure":"normal","memoryPressureNote":"the machine has room","overall":"Healthy","sessionCount":0,"sessionStates":[],"status":"no session","updateCheck":{"status":"unknown"},"version":"0.6.875.0"}"""

/// get_daemon_status text out of a real stream: the one the smoke probe captured.
let private statusText () : string =
  sampleLines "parse-seed-real-run.ndjson"
  |> ofLines
  |> fun s -> s.Calls |> List.find (fun c -> c.Name = "mcp__sagefs__get_daemon_status") |> _.Text

let private status pressure bytes : DaemonStatus =
  { DaemonVersion = "0.6.875.0"; CoreVersion = "0.6.875+d7e11077"; MemoryPressure = pressure; AvailableBytes = bytes; ActiveLeases = 0 }

let private gb (n: int64) = n * 1024L * 1024L * 1024L

/// The shape McpServer.fs's GET /api/sessions writes.
let private sessionsBody =
  """{"sessions":[
      {"id":"a1","status":"Ready","workingDirectory":"/tmp/lem/space-bunny-parse-seed-01/w","projects":[],"loadedProjects":["/tmp/lem/space-bunny-parse-seed-01/w/DemoEnv/DemoEnv.fsproj"],"workflowLabel":"REPL"},
      {"id":"b2","status":"Ready","workingDirectory":"/home/will/Work/SageFs","projects":[],"loadedProjects":[],"workflowLabel":"REPL"},
      {"id":"c3","status":"Starting","workingDirectory":"/tmp/lem/space-bunny-parse-seed-011/w","projects":[],"loadedProjects":[],"workflowLabel":"REPL"},
      {"id":"d4","status":"Ready","workingDirectory":"/home/will/Work/SageFs","projects":["/tmp/lem/space-bunny-parse-seed-01/w/DemoEnv.Tests/DemoEnv.Tests.fsproj"],"loadedProjects":[],"workflowLabel":"REPL"}
    ]}"""

[<Tests>]
let healthTests =
  testList "SharedDaemon health and status" [
    testCase "the dev daemon's /health parses" <| fun _ ->
      match parseHealth healthBody with
      | Ok h ->
        h.Healthy |> Expect.isTrue "healthy"
        h.Version |> Expect.equal "version" "0.6.875.0"
        h.MemoryPressure |> Expect.equal "pressure" "normal"
      | Error e -> failtest e

    testCase "an unhealthy /health is read as unhealthy" <| fun _ ->
      match parseHealth """{"healthy":false,"overall":"Degraded","version":"1"}""" with
      | Ok h -> h.Healthy |> Expect.isFalse "unhealthy"
      | Error e -> failtest e

    testCase "a /health that is not JSON is an error with the reason" <| fun _ ->
      parseHealth "<html>" |> Result.isError |> Expect.isTrue "refused"

    testCase "the real get_daemon_status text gives version, commit, memory and leases" <| fun _ ->
      match parseDaemonStatus (statusText ()) with
      | Ok s ->
        s.CoreVersion |> Expect.stringStarts "commit is carried" "0.6.875+"
        s.MemoryPressure |> Expect.equal "pressure" "normal"
        (s.AvailableBytes > 0L) |> Expect.isTrue "available memory read"
        s.ActiveLeases |> Expect.equal "no leases" 0
      | Error e -> failtest e

    testCase "an SSE reply and a plain JSON reply carry the same JSON-RPC payload" <| fun _ ->
      let json = """{"result":{"x":1},"id":2}"""
      jsonRpcPayload ("event: message\ndata: " + json + "\n\n") |> Expect.equal "sse" json
      jsonRpcPayload json |> Expect.equal "plain" json
  ]

[<Tests>]
let capacityTests =
  testList "SharedDaemon capacity gate" [
    testCase "normal pressure and plenty of memory dispatches" <| fun _ ->
      checkCapacity (status "normal" (gb 54L)) |> Expect.equal "ok" (Ok ())

    testCase "anything but normal pressure waits, and says which pressure" <| fun _ ->
      match checkCapacity (status "elevated" (gb 54L)) with
      | Error why -> why |> Expect.stringContains "names it" "elevated"
      | Ok () -> failtest "dispatched under pressure"

    testCase "under 8 GB available waits, and says how much" <| fun _ ->
      match checkCapacity (status "normal" (gb 6L)) with
      | Error why -> why |> Expect.stringContains "names the floor" "8 GB"
      | Ok () -> failtest "dispatched with too little memory"

    testCase "exactly 8 GB is enough" <| fun _ ->
      checkCapacity (status "normal" (gb 8L)) |> Expect.equal "boundary" (Ok ())
  ]

[<Tests>]
let sessionTests =
  testList "SharedDaemon sessions and cleanup scope" [
    let sessions () = match parseSessions sessionsBody with Ok s -> s | Error e -> failtest e
    let mine = "/tmp/lem/space-bunny-parse-seed-01"

    yield testCase "the sessions list parses" <| fun _ ->
      sessions () |> List.map _.Id |> Expect.equal "ids" [ "a1"; "b2"; "c3"; "d4" ]

    yield testCase "a session belongs to a run by working directory or by a project it loaded" <| fun _ ->
      sessions () |> List.filter (belongsTo mine) |> List.map _.Id |> Expect.equal "mine" [ "a1"; "d4" ]

    yield testCase "a sibling run whose id merely starts the same is not matched" <| fun _ ->
      belongsTo mine (sessions () |> List.find (fun s -> s.Id = "c3")) |> Expect.isFalse "prefix is not containment"

    yield testCase "Will's own session is never matched" <| fun _ ->
      belongsTo mine (sessions () |> List.find (fun s -> s.Id = "b2")) |> Expect.isFalse "b2 is not a lemming's"

    yield testCase "an empty, relative or shallow run directory is refused so it can match nothing" <| fun _ ->
      for bad in [ ""; "/"; "/tmp"; "/tmp/lem"; "relative/path/here" ] do
        validRunDir bad |> Result.isError |> Expect.isTrue (sprintf "'%s' must be refused" bad)

    yield testCase "a run directory three segments deep is accepted, trailing slash trimmed" <| fun _ ->
      validRunDir "/tmp/lem/x/" |> Expect.equal "ok" (Ok "/tmp/lem/x")
  ]
