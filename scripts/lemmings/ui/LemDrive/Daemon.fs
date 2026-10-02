/// What the harness reads from the ONE shared SageFs daemon (Will's dev daemon on
/// 37749). Read only: the sessions list, and a few MCP tools called the way any
/// client calls them. It never starts, stops or reconfigures the daemon, and it
/// never stops a session (that is `lem_cleanup_sessions`, in LemScore).
module LemDrive.Daemon

open System
open System.Net.Http
open System.Text
open System.Text.Json

/// The shared dev daemon's MCP/HTTP port; the dashboard is on the next one.
[<Literal>]
let DefaultMcpPort = 37749

/// Every request is bounded: a daemon that does not answer this fast is unreachable.
let private requestTimeout = TimeSpan.FromSeconds 15.0

let private mcpAccept = "application/json, text/event-stream"
let private mcpSessionHeader = "Mcp-Session-Id"
let private mcpProtocolVersion = "2025-03-26"

let private client = lazy (new HttpClient(Timeout = requestTimeout))

let private baseUrl (port: int) = sprintf "http://localhost:%d" port

/// What the daemon says about one session. Only what the oracles read.
type DaemonSession =
  { Id: string
    Status: string
    WorkingDirectory: string
    ProjectPaths: string list
    Workflow: string
    EvalCount: int
    Health: string
    LastReload: string }

let private str (name: string) (el: JsonElement) : string option =
  match el.ValueKind with
  | JsonValueKind.Object ->
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString() |> Option.ofObj |> Option.defaultValue "")
    | _ -> None
  | _ -> None

let private prop (name: string) (el: JsonElement) : JsonElement option =
  match el.ValueKind with
  | JsonValueKind.Object ->
    match el.TryGetProperty name with
    | true, v -> Some v
    | _ -> None
  | _ -> None

let private strings (name: string) (el: JsonElement) : string list =
  match prop name el with
  | Some a when a.ValueKind = JsonValueKind.Array ->
    a.EnumerateArray()
    |> Seq.choose (fun v -> match v.ValueKind with | JsonValueKind.String -> v.GetString() |> Option.ofObj | _ -> None)
    |> List.ofSeq
  | _ -> []

let private getText (url: string) : Result<string, string> =
  try
    use resp = client.Value.GetAsync(url).GetAwaiter().GetResult()
    let body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    match resp.IsSuccessStatusCode with
    | true -> Ok body
    | false -> Result.Error(sprintf "GET %s answered HTTP %d" url (int resp.StatusCode))
  with ex -> Result.Error(sprintf "GET %s failed: %s" url ex.Message)

/// Reads GET /api/sessions (served on the MCP port; the dashboard port answers 404).
let parseSessions (body: string) : Result<DaemonSession list, string> =
  try
    use doc = JsonDocument.Parse body
    match prop "sessions" doc.RootElement with
    | Some arr when arr.ValueKind = JsonValueKind.Array ->
      arr.EnumerateArray()
      |> Seq.map (fun s ->
        { Id = str "id" s |> Option.defaultValue ""
          Status = str "status" s |> Option.defaultValue "unknown"
          WorkingDirectory = str "workingDirectory" s |> Option.defaultValue ""
          ProjectPaths = strings "loadedProjects" s @ strings "projects" s
          Workflow = str "workflowLabel" s |> Option.defaultValue ""
          EvalCount = prop "evalCount" s |> Option.bind (fun v -> match v.ValueKind with | JsonValueKind.Number -> Some(v.GetInt32()) | _ -> None) |> Option.defaultValue 0
          Health = prop "health" s |> Option.bind (str "status") |> Option.defaultValue ""
          LastReload = prop "lastReload" s |> Option.bind (str "outcome") |> Option.defaultValue "" })
      |> List.ofSeq
      |> Ok
    | _ -> Result.Error "/api/sessions had no sessions array"
  with :? JsonException as ex -> Result.Error(sprintf "/api/sessions was not JSON: %s" ex.Message)

let sessions (port: int) : Result<DaemonSession list, string> =
  getText (baseUrl port + "/api/sessions") |> Result.bind parseSessions

let private under (dir: string) (path: string) : bool =
  let root = dir.TrimEnd('/')
  path = root || path.StartsWith(root + "/", StringComparison.Ordinal)

/// A session belongs to a run when its working directory or a project it loaded is under it.
let belongsTo (runDir: string) (s: DaemonSession) : bool =
  under runDir s.WorkingDirectory || s.ProjectPaths |> List.exists (under runDir)

/// The payload of an MCP streamable-HTTP reply: plain JSON, or the last SSE `data:` line.
let private jsonRpcPayload (body: string) : string =
  let data =
    body.Split('\n')
    |> Array.map (fun l -> l.TrimEnd('\r'))
    |> Array.filter (fun l -> l.StartsWith "data:")
    |> Array.map (fun l -> l.Substring(5).Trim())
  match Array.tryLast data with
  | Some last -> last
  | None -> body.Trim()

/// Calls one MCP tool the way any client does (initialize, initialized, tools/call), closes
/// its MCP session, and returns the tool's first text block.
let callTool (port: int) (tool: string) (arguments: (string * string) list) : Result<string, string> =
  let url = baseUrl port + "/"
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
    let rpc (id: int) (methodName: string) (parameters: string) =
      sprintf """{"jsonrpc":"2.0","id":%d,"method":"%s","params":%s}""" id methodName parameters
    let initParams =
      sprintf """{"protocolVersion":"%s","capabilities":{},"clientInfo":{"name":"lem-oracle","version":"1"}}""" mcpProtocolVersion
    match send (rpc 1 "initialize" initParams) None with
    | false, _, body -> Result.Error(sprintf "MCP initialize was refused: %s" body)
    | true, None, _ -> Result.Error "MCP initialize returned no Mcp-Session-Id"
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
        let args =
          arguments
          |> List.map (fun (k, v) -> sprintf "%s:%s" (JsonSerializer.Serialize k) (JsonSerializer.Serialize v))
          |> String.concat ","
        let callParams = sprintf """{"name":"%s","arguments":{%s}}""" tool args
        match send (rpc 2 "tools/call" callParams) (Some sid) with
        | false, _, body -> Result.Error(sprintf "tools/call %s was refused: %s" tool body)
        | true, _, body ->
          use doc = JsonDocument.Parse(jsonRpcPayload body)
          let text =
            prop "result" doc.RootElement
            |> Option.bind (prop "content")
            |> Option.bind (fun c -> if c.ValueKind = JsonValueKind.Array && c.GetArrayLength() > 0 then Some c[0] else None)
            |> Option.bind (str "text")
          match text with
          | Some t -> Ok t
          | None -> Result.Error(sprintf "tools/call %s answered without a text block" tool)
      finally close ()
  with ex -> Result.Error(sprintf "MCP call %s failed: %s" tool ex.Message)

/// The one kind of daemon event the harness keeps: new lines of eval output in a session.
[<Literal>]
let EvalDiffEvent = "eval_diff"

/// The file in out/ where the runner records the daemon's eval output for the run.
[<Literal>]
let RecordedEvalsFile = "daemon-evals.tsv"

/// One thing the daemon said about evaluation, with when the harness heard it.
type EvalOutput =
  { AtMs: int64
    SessionId: string
    /// The lines of output that the evaluation added, joined with a newline.
    Added: string }

/// Reads one `eval_diff` event's data: the session and the output lines it added. None when
/// it is not that shape.
let evalOutputOfEventData (atMs: int64) (data: string) : EvalOutput option =
  try
    use doc = JsonDocument.Parse data
    match prop "SessionId" doc.RootElement, prop "Lines" doc.RootElement with
    | Some sid, Some lines when sid.ValueKind = JsonValueKind.String && lines.ValueKind = JsonValueKind.Array ->
      let added =
        lines.EnumerateArray()
        |> Seq.choose (fun l ->
          match str "Kind" l, str "Text" l with
          | Some "added", Some t -> Some t
          | _ -> None)
        |> List.ofSeq
      Some { AtMs = atMs; SessionId = sid.GetString() |> Option.ofObj |> Option.defaultValue ""; Added = String.Join("\n", added) }
    | _ -> None
  with :? JsonException -> None

/// One recorded event as a line: epoch ms, session id, then the added text with newlines and
/// tabs escaped so it stays on the one line.
let toRecordedLine (e: EvalOutput) : string =
  let flat = e.Added.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", " ")
  sprintf "%d\t%s\t%s" e.AtMs e.SessionId flat

let fromRecordedLine (line: string) : EvalOutput option =
  match line.Split('\t') with
  | [| ms; sid; text |] ->
    match Int64.TryParse ms with
    | true, at ->
      let unescaped = text.Replace("\\n", "\n").Replace("\\\\", "\\")
      Some { AtMs = at; SessionId = sid; Added = unescaped }
    | false, _ -> None
  | _ -> None

/// The eval output the runner recorded from the daemon's `/events` stream for the whole run,
/// because the daemon keeps no per-session eval history that another client can read.
let readRecorded (path: string) : EvalOutput list =
  match IO.File.Exists path with
  | false -> []
  | true -> IO.File.ReadAllLines path |> Array.choose fromRecordedLine |> List.ofArray

/// The eval output of the given sessions.
let evalOutputsIn (recorded: EvalOutput list) (sessionIds: string list) : string list =
  recorded |> List.filter (fun e -> sessionIds |> List.contains e.SessionId) |> List.map (fun e -> e.Added)

/// How long after a driver call ended an evaluation it started may still print: the result
/// of an eval arrives after the call (which only presses the keys and reads the window).
[<Literal>]
let EvalLagMs = 8000L

/// The sessions that printed eval output during any of the (start, end) spans, each widened
/// by the lag: where the lemming's evaluation attempts actually went.
let sessionsEvaluatingDuring (recorded: EvalOutput list) (spans: (int64 * int64) list) : string list =
  recorded
  |> List.filter (fun e -> spans |> List.exists (fun (s, f) -> e.AtMs >= s && e.AtMs <= f + EvalLagMs))
  |> List.map (fun e -> e.SessionId)
  |> List.distinct

/// Sessions outside the run directory whose evaluation count rose between two readings of
/// the sessions list, with how much. Other agents use the shared daemon at the same time, so
/// this is a lead and never proof by itself: it matters when the lemming's own session did
/// nothing while the lemming was evaluating.
let foreignEvalDeltas (runDir: string) (before: DaemonSession list) (after: DaemonSession list) : (DaemonSession * int) list =
  after
  |> List.filter (fun s -> not (belongsTo runDir s))
  |> List.choose (fun s ->
    let was = before |> List.tryFind (fun b -> b.Id = s.Id) |> Option.map (fun b -> b.EvalCount) |> Option.defaultValue 0
    match s.EvalCount - was with
    | d when d > 0 -> Some(s, d)
    | _ -> None)
