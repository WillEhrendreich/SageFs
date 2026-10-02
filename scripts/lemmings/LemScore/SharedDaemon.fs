/// Everything the harness says to the ONE shared SageFs daemon (Will's dev daemon on 37749).
/// It only ever reads, or stops sessions by exact id that live under a lemming's own run
/// directory. It never starts, stops, restarts or configures the daemon.
module LemScore.SharedDaemon

open System
open System.Net.Http
open System.Text
open System.Text.Json
open LemScore.Types

/// The shared dev daemon: MCP/HTTP port and the dashboard port beside it.
let defaultMcpPort = 37749
let dashboardPortFor (mcpPort: int) = mcpPort + 1

/// Every request is short and bounded: a daemon that does not answer in this long is
/// treated as unreachable, not waited for.
let requestTimeout = TimeSpan.FromSeconds 10.0

/// A lemming is not dispatched below this much available machine memory.
let minAvailableBytes = 8L * 1024L * 1024L * 1024L

/// The only memoryPressure value that lets a lemming start (see get_daemon_status).
let calmPressure = "normal"

/// How long, and how often, the harness waits for the machine to have room before it
/// gives up and refuses. Bounded: it never waits forever.
let capacityPollInterval = TimeSpan.FromSeconds 30.0
let capacityMaxWait = TimeSpan.FromMinutes 10.0

/// After a stop, the sessions list is re-read this many times, this far apart.
let stopVerifyAttempts = 5
let stopVerifyInterval = TimeSpan.FromSeconds 1.0

let private mcpAccept = "application/json, text/event-stream"
let private mcpSessionHeader = "Mcp-Session-Id"
let private mcpProtocolVersion = "2025-03-26"

let private client = lazy (new HttpClient(Timeout = requestTimeout))

let private baseUrl (port: int) = sprintf "http://localhost:%d" port

let private tryGet (url: string) : Result<string, string> =
  try
    use resp = client.Value.GetAsync(url).GetAwaiter().GetResult()
    let body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    match resp.IsSuccessStatusCode with
    | true -> Ok body
    | false -> Error (sprintf "GET %s answered HTTP %d" url (int resp.StatusCode))
  with ex -> Error (sprintf "GET %s failed: %s" url ex.Message)

let private tryPostJson (url: string) (json: string) : Result<string, string> =
  try
    use content = new StringContent(json, Encoding.UTF8, "application/json")
    use resp = client.Value.PostAsync(url, content).GetAwaiter().GetResult()
    let body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    match resp.IsSuccessStatusCode with
    | true -> Ok body
    | false -> Error (sprintf "POST %s answered HTTP %d: %s" url (int resp.StatusCode) body)
  with ex -> Error (sprintf "POST %s failed: %s" url ex.Message)

let private str (name: string) (el: JsonElement) : string option =
  match el.ValueKind with
  | JsonValueKind.Object ->
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
    | _ -> None
  | _ -> None

let private prop (name: string) (el: JsonElement) : JsonElement option =
  match el.ValueKind with
  | JsonValueKind.Object ->
    match el.TryGetProperty name with
    | true, v -> Some v
    | _ -> None
  | _ -> None

// ---- health --------------------------------------------------------------------------

type Health =
  { Healthy: bool
    Overall: string
    Version: string
    MemoryPressure: string }

let parseHealth (body: string) : Result<Health, string> =
  try
    use doc = JsonDocument.Parse body
    let root = doc.RootElement
    let healthy =
      match prop "healthy" root with
      | Some v -> v.ValueKind = JsonValueKind.True
      | None -> false
    Ok { Healthy = healthy
         Overall = str "overall" root |> Option.defaultValue "unknown"
         Version = str "version" root |> Option.defaultValue "unknown"
         MemoryPressure = str "memoryPressure" root |> Option.defaultValue "unknown" }
  with :? JsonException as ex -> Error (sprintf "/health was not JSON: %s" ex.Message)

let health (mcpPort: int) : Result<Health, string> =
  tryGet (baseUrl mcpPort + "/health") |> Result.bind parseHealth

// ---- get_daemon_status over MCP --------------------------------------------------------

type DaemonStatus =
  { DaemonVersion: string
    CoreVersion: string
    MemoryPressure: string
    AvailableBytes: int64
    ActiveLeases: int }

let parseDaemonStatus (text: string) : Result<DaemonStatus, string> =
  try
    use doc = JsonDocument.Parse text
    let root = doc.RootElement
    let available =
      prop "machineMemory" root
      |> Option.bind (prop "availableBytes")
      |> Option.map (fun v -> v.GetInt64())
    let leases =
      prop "leases" root
      |> Option.bind (prop "activeCount")
      |> Option.map (fun v -> v.GetInt32())
      |> Option.defaultValue 0
    match available with
    | None -> Error "get_daemon_status carried no machineMemory.availableBytes"
    | Some bytes ->
      Ok { DaemonVersion = str "daemonVersion" root |> Option.defaultValue "unknown"
           CoreVersion = str "coreVersion" root |> Option.defaultValue "unknown"
           MemoryPressure = str "memoryPressure" root |> Option.defaultValue "unknown"
           AvailableBytes = bytes
           ActiveLeases = leases }
  with :? JsonException as ex -> Error (sprintf "get_daemon_status was not JSON: %s" ex.Message)

/// An MCP streamable-HTTP reply is either plain JSON or SSE; either way the JSON-RPC
/// message is the last `data:` payload (or the whole body).
let jsonRpcPayload (body: string) : string =
  let dataLines =
    body.Split('\n')
    |> Array.map _.TrimEnd('\r')
    |> Array.filter (fun l -> l.StartsWith "data:")
    |> Array.map (fun l -> l.Substring(5).Trim())
  match Array.tryLast dataLines with
  | Some last -> last
  | None -> body.Trim()

let private rpc (id: int) (method': string) (parameters: string) =
  sprintf """{"jsonrpc":"2.0","id":%d,"method":"%s","params":%s}""" id method' parameters

/// Calls one MCP tool the way any client does (initialize, initialized, tools/call),
/// closes its MCP session, and returns the tool's first text block.
let callTool (mcpPort: int) (tool: string) : Result<string, string> =
  let url = baseUrl mcpPort + "/"
  try
    let send (json: string) (sessionId: string option) =
      use req = new HttpRequestMessage(HttpMethod.Post, url)
      req.Content <- new StringContent(json, Encoding.UTF8, "application/json")
      req.Headers.TryAddWithoutValidation("Accept", mcpAccept) |> ignore
      sessionId |> Option.iter (fun s -> req.Headers.TryAddWithoutValidation(mcpSessionHeader, s) |> ignore)
      use resp = client.Value.SendAsync(req).GetAwaiter().GetResult()
      let body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
      let sid =
        match resp.Headers.TryGetValues mcpSessionHeader with
        | true, values -> Seq.tryHead values
        | _ -> None
      resp.IsSuccessStatusCode, sid, body
    let initParams =
      sprintf """{"protocolVersion":"%s","capabilities":{},"clientInfo":{"name":"lem-harness","version":"1"}}""" mcpProtocolVersion
    match send (rpc 1 "initialize" initParams) None with
    | false, _, body -> Error (sprintf "MCP initialize was refused: %s" body)
    | true, None, _ -> Error "MCP initialize returned no Mcp-Session-Id"
    | true, Some sid, _ ->
      let close () =
        try
          use req = new HttpRequestMessage(HttpMethod.Delete, url)
          req.Headers.TryAddWithoutValidation(mcpSessionHeader, sid) |> ignore
          use r = client.Value.SendAsync(req).GetAwaiter().GetResult()
          ()
        with _ -> ()
      try
        send """{"jsonrpc":"2.0","method":"notifications/initialized"}""" (Some sid) |> ignore
        let callParams = sprintf """{"name":"%s","arguments":{}}""" tool
        match send (rpc 2 "tools/call" callParams) (Some sid) with
        | false, _, body -> Error (sprintf "tools/call %s was refused: %s" tool body)
        | true, _, body ->
          use doc = JsonDocument.Parse(jsonRpcPayload body)
          let result = prop "result" doc.RootElement
          let text =
            result
            |> Option.bind (prop "content")
            |> Option.bind (fun c -> if c.ValueKind = JsonValueKind.Array && c.GetArrayLength() > 0 then Some c.[0] else None)
            |> Option.bind (str "text")
          match text with
          | Some t -> Ok t
          | None -> Error (sprintf "tools/call %s answered without a text block" tool)
      finally close ()
  with ex -> Error (sprintf "MCP call %s failed: %s" tool ex.Message)

let daemonStatus (mcpPort: int) : Result<DaemonStatus, string> =
  callTool mcpPort "get_daemon_status" |> Result.bind parseDaemonStatus

/// Pure: is there room to dispatch a lemming right now?
let checkCapacity (status: DaemonStatus) : Result<unit, string> =
  match status.MemoryPressure = calmPressure, status.AvailableBytes >= minAvailableBytes with
  | true, true -> Ok ()
  | false, _ -> Error (sprintf "the daemon reports memoryPressure '%s', not '%s'" status.MemoryPressure calmPressure)
  | true, false ->
    Error (sprintf "only %.1f GB of machine memory is available, the floor is %.0f GB"
             (float status.AvailableBytes / 1073741824.0) (float minAvailableBytes / 1073741824.0))

type ReadyDaemon =
  { Health: Health
    Status: DaemonStatus }

/// The whole gate `lem_use_shared_daemon` stands on. Refuses (Error with the reason) when
/// the daemon is unreachable, unhealthy, or short of capacity after the bounded wait.
/// Never starts a daemon.
let useShared (mcpPort: int) (wait: bool) : Result<ReadyDaemon, string> =
  match health mcpPort with
  | Error e -> Error (sprintf "the shared daemon on %d is unreachable: %s. The harness never starts one." mcpPort e)
  | Ok h when not h.Healthy ->
    Error (sprintf "the shared daemon on %d answers but is not healthy (overall: %s)" mcpPort h.Overall)
  | Ok h ->
    let deadline = DateTime.UtcNow + capacityMaxWait
    let rec settle () =
      match daemonStatus mcpPort with
      | Error e -> Error (sprintf "could not read get_daemon_status from %d: %s" mcpPort e)
      | Ok status ->
        match checkCapacity status with
        | Ok () -> Ok { Health = h; Status = status }
        | Error why when wait && DateTime.UtcNow < deadline ->
          eprintfn "waiting for capacity: %s" why
          Threading.Thread.Sleep capacityPollInterval
          settle ()
        | Error why -> Error (sprintf "no room to dispatch a lemming: %s" why)
    settle ()

// ---- sessions ------------------------------------------------------------------------

type SessionInfo =
  { Id: string
    Status: string
    WorkingDirectory: string
    ProjectPaths: string list
    Workflow: string }

let parseSessions (body: string) : Result<SessionInfo list, string> =
  try
    use doc = JsonDocument.Parse body
    let strings (name: string) (el: JsonElement) =
      match prop name el with
      | Some a when a.ValueKind = JsonValueKind.Array ->
        a.EnumerateArray() |> Seq.choose (fun v -> if v.ValueKind = JsonValueKind.String then Some (v.GetString()) else None) |> List.ofSeq
      | _ -> []
    match prop "sessions" doc.RootElement with
    | Some arr when arr.ValueKind = JsonValueKind.Array ->
      arr.EnumerateArray()
      |> Seq.map (fun s ->
        { Id = str "id" s |> Option.defaultValue ""
          Status = str "status" s |> Option.defaultValue "unknown"
          WorkingDirectory = str "workingDirectory" s |> Option.defaultValue ""
          ProjectPaths = strings "loadedProjects" s @ strings "projects" s
          Workflow = str "workflowLabel" s |> Option.defaultValue "" })
      |> List.ofSeq
      |> Ok
    | _ -> Error "/api/sessions had no sessions array"
  with :? JsonException as ex -> Error (sprintf "/api/sessions was not JSON: %s" ex.Message)

/// The sessions list is served on the MCP port (the dashboard port answers 404 for it).
let listSessions (mcpPort: int) : Result<SessionInfo list, string> =
  tryGet (baseUrl mcpPort + "/api/sessions") |> Result.bind parseSessions

let private under (dir: string) (path: string) : bool =
  let root = dir.TrimEnd('/')
  path = root || path.StartsWith(root + "/", StringComparison.Ordinal)

/// A run directory must be absolute and at least three segments deep (/tmp/lem/<id>), so
/// a mistyped or empty argument can never match, and stop, someone else's sessions.
let minRunDirSegments = 3

let validRunDir (runDir: string) : Result<string, string> =
  let segments = runDir.Split('/', StringSplitOptions.RemoveEmptyEntries)
  match runDir.StartsWith "/", segments.Length >= minRunDirSegments with
  | true, true -> Ok (runDir.TrimEnd('/'))
  | _ -> Error (sprintf "refusing to match sessions under '%s': a run directory is absolute and at least %d segments deep" runDir minRunDirSegments)

/// A session belongs to a lemming when its working directory, or any project it loaded,
/// sits under the lemming's run directory. Nothing else is ever matched.
let belongsTo (runDir: string) (session: SessionInfo) : bool =
  under runDir session.WorkingDirectory
  || session.ProjectPaths |> List.exists (under runDir)

/// Stops one session by exact id through the daemon's own HTTP endpoint (the MCP
/// stop_session refuses a session another MCP connection created).
let stopSession (mcpPort: int) (id: string) : Result<unit, string> =
  let body = JsonSerializer.Serialize {| sessionId = id |}
  tryPostJson (baseUrl mcpPort + "/api/sessions/stop") body |> Result.map ignore

type CleanupReport =
  { /// What the lemming left behind: the sessions under the run directory that the harness did
    /// not make itself.
    Found: SessionInfo list
    /// Ids of sessions the harness made for the run (the VS Code runner gives each run its own).
    /// They are stopped like the rest, but they are not residue.
    Own: string list
    Stops: (string * Result<unit, string>) list
    Remaining: SessionInfo list
    Verdict: Cleanup
    Note: string }

/// The verdict for a cleanup, and the residue it names. `found` is every session under the run
/// directory before the stop, `owned` the ids the harness made itself, `left` what the list still
/// showed afterwards. A run that only has its own session is Clean; what is still listed after
/// the stop is a failed cleanup whoever made it.
let cleanupVerdict (found: SessionInfo list) (owned: string list) (left: SessionInfo list) : Cleanup * SessionInfo list =
  let residue = found |> List.filter (fun s -> not (List.contains s.Id owned))
  match found, residue, left with
  | _, _, _ :: _ -> CleanupFailed, residue
  | [], _, [] -> Clean, residue
  | _, [], [] -> Clean, residue
  | _, _ :: _, [] -> ResidueStopped, residue

/// Reads the residue FIRST, then stops exactly the sessions under `runDir`, then reads
/// the list again to prove nothing was left behind in the dashboard. `owned` are the ids of
/// sessions the harness made for this run: stopped, but not counted as residue.
let cleanup (mcpPort: int) (runDir: string) (owned: string list) : CleanupReport =
  let failed note =
    { Found = []; Own = owned; Stops = []; Remaining = []; Verdict = CleanupFailed; Note = note }
  match validRunDir runDir, listSessions mcpPort with
  | Error why, _ -> failed why
  | Ok _, Error e -> failed (sprintf "could not read the sessions list before cleanup: %s" e)
  | Ok _, Ok all ->
    let found = all |> List.filter (belongsTo runDir)
    let stops = found |> List.map (fun s -> s.Id, stopSession mcpPort s.Id)
    let rec verify attempt =
      match listSessions mcpPort with
      | Error e -> Error e
      | Ok after ->
        let left = after |> List.filter (belongsTo runDir)
        match left, attempt >= stopVerifyAttempts with
        | [], _ -> Ok []
        | _, true -> Ok left
        | _, false ->
          Threading.Thread.Sleep stopVerifyInterval
          verify (attempt + 1)
    match verify 1 with
    | Error e -> { failed (sprintf "could not read the sessions list after cleanup: %s" e) with Found = found; Stops = stops }
    | Ok left ->
      let verdict, residue = cleanupVerdict found owned left
      let note =
        match verdict, found with
        | _, [] -> "no sessions under the run directory"
        | Clean, _ -> sprintf "stopped the run's own session(s): %s" (String.Join(", ", found |> List.map (fun s -> s.Id)))
        | ResidueStopped, _ -> sprintf "stopped %d session(s) the lemming left" residue.Length
        | _ -> sprintf "%d session(s) still listed after the stop" left.Length
      { Found = residue; Own = owned; Stops = stops; Remaining = left; Verdict = verdict; Note = note }

// ---- the build the lemming's bridge is, against the build the daemon is -----------------------

/// How many characters of a commit hash are shown.
let private commitWidth = 8

let private versionAndCommit (v: string) : string * string option =
  match v.IndexOf '+' with
  | -1 -> v.Trim(), None
  | i -> v.Substring(0, i).Trim(), Some(v.Substring(i + 1).Trim())

let private shortCommit (c: string option) : string =
  match c with
  | Some hash -> hash.Substring(0, min commitWidth hash.Length)
  | None -> "no commit named"

/// The lemming's bridge (the SageFs.dll copied into the run) and the daemon it talks to are
/// two builds, and nothing made them the same one: the bridge is the main checkout's last
/// build, the daemon is whatever was last started. When both name a commit and the commits
/// differ, or the versions differ, say so, with both. None when they agree, or when there is
/// nothing to compare.
let versionSkew (daemonVersion: string) (bridgeVersion: string) : string option =
  let daemonV, daemonC = versionAndCommit daemonVersion
  let bridgeV, bridgeC = versionAndCommit bridgeVersion
  match daemonV = bridgeV, daemonC, bridgeC with
  | false, _, _ ->
    Some(sprintf "the bridge is version %s and the daemon is %s (%s against %s)" bridgeV daemonV (shortCommit bridgeC) (shortCommit daemonC))
  | true, Some d, Some b when d <> b ->
    Some(sprintf "the bridge and the daemon are the same version (%s) but different commits: bridge %s, daemon %s" daemonV (shortCommit bridgeC) (shortCommit daemonC))
  | _ -> None
