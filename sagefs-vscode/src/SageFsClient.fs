module SageFs.Vscode.SageFsClient

open Fable.Core
open Fable.Core.JsInterop

[<Emit("console.warn('[SageFs]', $0, $1)")>]
let logWarn (context: string) (err: obj) : unit = jsNative

/// Command result: either succeeded with optional message, or failed with error.
/// Replaces the old { success; result; error } bag-of-optionals — illegal states now unrepresentable.
type ApiOutcome =
  | Succeeded of message: string option
  | Failed of error: string

module ApiOutcome =
  let message = function Succeeded m -> m | Failed _ -> None
  let error = function Failed e -> Some e | Succeeded _ -> None
  let isOk = function Succeeded _ -> true | Failed _ -> false
  let messageOrDefault fallback = function Succeeded (Some m) -> m | Succeeded None -> fallback | Failed e -> e

type SageFsStatus =
  { connected: bool
    healthy: bool option
    status: string option }

type SystemStatus =
  { supervised: bool
    restartCount: int
    version: string }

type HotReloadFile =
  { path: string
    watched: bool }

type HotReloadState =
  { files: HotReloadFile array
    watchedCount: int }

type SessionInfo =
  { id: string
    name: string option
    workingDirectory: string
    status: string
    projects: string array
    evalCount: int }

type LoadedAssemblyInfo =
  { Name: string
    Path: string
    NamespaceCount: int
    ModuleCount: int }

type OpenedBindingInfo =
  { Name: string
    IsModule: bool
    Source: string }

type WarmupContextInfo =
  { SourceFilesScanned: int
    AssembliesLoaded: LoadedAssemblyInfo array
    NamespacesOpened: OpenedBindingInfo array
    FailedOpens: string array array
    WarmupDurationMs: int }

[<Emit("new Promise((resolve, reject) => { const http = require('http'); const req = http.get($0, { timeout: $1 }, (res) => { let data = ''; res.on('data', (chunk) => data += chunk); res.on('end', () => resolve({ statusCode: res.statusCode || 0, body: data })); }); req.on('error', reject); req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); }); })")>]
let httpGetRaw (url: string) (timeout: int) : JS.Promise<{| statusCode: int; body: string |}> = jsNative

[<Emit("new Promise((resolve, reject) => { const http = require('http'); const url = new URL($0); const req = http.request({ hostname: url.hostname, port: url.port, path: url.pathname, method: 'POST', headers: { 'Content-Type': 'application/json' }, timeout: $2 }, (res) => { let data = ''; res.on('data', (chunk) => data += chunk); res.on('end', () => resolve({ statusCode: res.statusCode || 0, body: data })); }); req.on('error', reject); req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); }); req.write($1); req.end(); })")>]
let httpPostRaw (url: string) (body: string) (timeout: int) : JS.Promise<{| statusCode: int; body: string |}> = jsNative

[<Emit("JSON.parse($0)")>]
let jsonParse (s: string) : obj = jsNative

[<Emit("JSON.stringify($0)")>]
let jsonStringify (o: obj) : string = jsNative

type Client =
  { mutable mcpPort: int
    mutable dashboardPort: int }

let create (mcpPort: int) (dashboardPort: int) =
  { mcpPort = mcpPort; dashboardPort = dashboardPort }

let baseUrl (c: Client) = sprintf "http://localhost:%d" c.mcpPort
let dashboardUrl (c: Client) = sprintf "http://localhost:%d/dashboard" c.dashboardPort

let updatePorts (mcpPort: int) (dashboardPort: int) (c: Client) =
  c.mcpPort <- mcpPort
  c.dashboardPort <- dashboardPort

let httpGet (c: Client) (path: string) (timeout: int) =
  httpGetRaw (sprintf "%s%s" (baseUrl c) path) timeout

let httpPost (c: Client) (path: string) (body: string) (timeout: int) =
  httpPostRaw (sprintf "%s%s" (baseUrl c) path) body timeout

let dashHttpGet (c: Client) (path: string) (timeout: int) =
  httpGetRaw (sprintf "http://localhost:%d%s" c.dashboardPort path) timeout

let dashHttpPost (c: Client) (path: string) (body: string) (timeout: int) =
  httpPostRaw (sprintf "http://localhost:%d%s" c.dashboardPort path) body timeout

// ── JSON field helpers (null-safe boundary parsing) ──────────────────────

/// Convert a potentially-null value to Option.
/// In Fable/JS: null and undefined → None, everything else → Some.
let inline tryOfObj (x: 'a) : 'a option =
  if isNull (box x) then None else Some x

let tryField<'T> (name: string) (obj: obj) : 'T option =
  let v = obj?(name)
  if isNull v then None else Some (unbox<'T> v)

let parseOutcome (parsed: obj) : ApiOutcome =
  let success = tryField<bool> "success" parsed |> Option.defaultValue false
  if success then
    tryField<string> "message" parsed
    |> Option.orElse (tryField<string> "result" parsed)
    |> Succeeded
  else
    tryField<string> "error" parsed
    |> Option.defaultValue "Unknown error"
    |> Failed

/// POST a command, parse the standard { success, message/result, error } response.
let postCommand (c: Client) (path: string) (body: string) (timeout: int) : JS.Promise<ApiOutcome> =
  promise {
    try
      let! resp = httpPost c path body timeout
      return jsonParse resp.body |> parseOutcome
    with err ->
      return Failed (string err)
  }

// ── HTTP helpers (compress repeated GET/POST patterns) ───────────────────

/// GET from MCP port, parse JSON on 200, None otherwise.
let getJson<'a> (ctx: string) (path: string) (timeout: int) (parse: obj -> 'a) (c: Client) : JS.Promise<'a option> =
  promise {
    try
      let! resp = httpGet c path timeout
      match resp.statusCode with
      | 200 -> return jsonParse resp.body |> parse |> Some
      | _ -> return None
    with ex ->
      logWarn ctx ex
      return None
  }

/// GET raw body from MCP port on 200, None otherwise.
let getRaw (ctx: string) (path: string) (timeout: int) (c: Client) : JS.Promise<string option> =
  promise {
    try
      let! resp = httpGet c path timeout
      match resp.statusCode with
      | 200 -> return Some resp.body
      | _ -> return None
    with ex ->
      logWarn ctx ex
      return None
  }

/// GET from dashboard port, parse JSON on 200, None otherwise.
let dashGetJson<'a> (ctx: string) (path: string) (timeout: int) (parse: obj -> 'a) (c: Client) : JS.Promise<'a option> =
  promise {
    try
      let! resp = dashHttpGet c path timeout
      match resp.statusCode with
      | 200 -> return jsonParse resp.body |> parse |> Some
      | _ -> return None
    with ex ->
      logWarn ctx ex
      return None
  }

/// POST to dashboard port, succeed on 2xx, fail otherwise.
let dashPostOutcome (ctx: string) (path: string) (body: string) (timeout: int) (c: Client) : JS.Promise<ApiOutcome> =
  promise {
    try
      let! resp = dashHttpPost c path body timeout
      match resp.statusCode with
      | s when s >= 200 && s < 300 -> return Succeeded None
      | _ -> return Failed (sprintf "%s: HTTP %d" ctx resp.statusCode)
    with err ->
      return Failed (sprintf "%s: %s" ctx (string err))
  }

let isRunning (c: Client) =
  promise {
    try
      let! resp = httpGet c "/health" 3000
      return resp.statusCode > 0
    with _ ->
      return false
  }

let getStatus (c: Client) =
  promise {
    try
      let! resp = httpGet c "/health" 3000
      match resp.statusCode with
      | 200 ->
        let parsed = jsonParse resp.body
        return
          { connected = true
            healthy = tryField<bool> "healthy" parsed |> Option.orElse (Some false)
            status = tryField<string> "status" parsed }
      | _ ->
        return { connected = true; healthy = Some false; status = Some "no session" }
    with _ ->
      return { connected = false; healthy = None; status = None }
  }

let evalCode (code: string) (workingDirectory: string option) (c: Client) =
  let wd = workingDirectory |> Option.defaultValue ""
  postCommand c "/exec" (jsonStringify {| code = code; working_directory = wd |}) 30000

let resetSession (c: Client) =
  postCommand c "/reset" "{}" 15000

let hardReset (rebuild: bool) (c: Client) =
  postCommand c "/hard-reset" (jsonStringify {| rebuild = rebuild |}) 60000

let parseSessions (parsed: obj) =
  let sessions: obj array = parsed?sessions |> unbox
  sessions |> Array.map (fun s ->
    { id = s?id |> unbox<string>
      name = None
      workingDirectory = s?workingDirectory |> unbox<string>
      status = s?status |> unbox<string>
      projects = tryField<string array> "projects" s |> Option.defaultValue [||]
      evalCount = tryField<int> "evalCount" s |> Option.defaultValue 0 })

let listSessions (c: Client) =
  promise {
    let! result = getJson "listSessions" "/api/sessions" 5000 parseSessions c
    return result |> Option.defaultValue [||]
  }

let createSession (projects: string) (workingDirectory: string) (c: Client) =
  postCommand c "/api/sessions/create" (jsonStringify {| projects = [| projects |]; workingDirectory = workingDirectory |}) 30000

let switchSession (sessionId: string) (c: Client) =
  postCommand c "/api/sessions/switch" (jsonStringify {| sessionId = sessionId |}) 5000

let stopSession (sessionId: string) (c: Client) =
  postCommand c "/api/sessions/stop" (jsonStringify {| sessionId = sessionId |}) 10000

let parseSystemStatus (parsed: obj) =
  { supervised = parsed?supervised |> unbox<bool>
    restartCount = parsed?restartCount |> unbox<int>
    version = parsed?version |> unbox<string> }

let getSystemStatus (c: Client) =
  getJson "getSystemStatus" "/api/system/status" 3000 parseSystemStatus c

let parseHotReloadState (parsed: obj) =
  let files =
    tryOfObj (parsed?files)
    |> Option.map (fun rawFiles ->
      rawFiles
      |> unbox<obj array>
      |> Array.choose (fun f ->
        tryField<string> "path" f
        |> Option.map (fun p ->
          { path = p
            watched = tryField<bool> "watched" f |> Option.defaultValue false })))
    |> Option.defaultValue [||]
  let wc = tryField<int> "watchedCount" parsed |> Option.defaultValue 0
  { files = files; watchedCount = wc }

let getHotReloadState (sessionId: string) (c: Client) =
  dashGetJson "getHotReloadState" (sprintf "/api/sessions/%s/hotreload" sessionId) 5000 parseHotReloadState c

let toggleHotReload (sessionId: string) (path: string) (c: Client) =
  dashPostOutcome "toggleHotReload" (sprintf "/api/sessions/%s/hotreload/toggle" sessionId) (jsonStringify {| path = path |}) 5000 c

let watchAllHotReload (sessionId: string) (c: Client) =
  dashPostOutcome "watchAllHotReload" (sprintf "/api/sessions/%s/hotreload/watch-all" sessionId) "{}" 5000 c

let unwatchAllHotReload (sessionId: string) (c: Client) =
  dashPostOutcome "unwatchAllHotReload" (sprintf "/api/sessions/%s/hotreload/unwatch-all" sessionId) "{}" 5000 c

let watchDirectoryHotReload (sessionId: string) (directory: string) (c: Client) =
  dashPostOutcome "watchDirectoryHotReload" (sprintf "/api/sessions/%s/hotreload/watch-directory" sessionId) (jsonStringify {| directory = directory |}) 5000 c

let unwatchDirectoryHotReload (sessionId: string) (directory: string) (c: Client) =
  dashPostOutcome "unwatchDirectoryHotReload" (sprintf "/api/sessions/%s/hotreload/unwatch-directory" sessionId) (jsonStringify {| directory = directory |}) 5000 c

let parseWarmupContext (parsed: obj) =
  let assemblies =
    parsed?AssembliesLoaded
    |> unbox<obj array>
    |> Array.map (fun a ->
      { Name = a?Name |> unbox<string>
        Path = a?Path |> unbox<string>
        NamespaceCount = a?NamespaceCount |> unbox<int>
        ModuleCount = a?ModuleCount |> unbox<int> })
  let opened =
    parsed?NamespacesOpened
    |> unbox<obj array>
    |> Array.map (fun b ->
      { Name = b?Name |> unbox<string>
        IsModule = b?IsModule |> unbox<bool>
        Source = b?Source |> unbox<string> })
  let failed =
    parsed?FailedOpens
    |> unbox<obj array>
    |> Array.map (fun f -> f |> unbox<string array>)
  { SourceFilesScanned = parsed?SourceFilesScanned |> unbox<int>
    AssembliesLoaded = assemblies
    NamespacesOpened = opened
    FailedOpens = failed
    WarmupDurationMs = parsed?WarmupDurationMs |> unbox<int> }

let getWarmupContext (sessionId: string) (c: Client) =
  dashGetJson "getWarmupContext" (sprintf "/api/sessions/%s/warmup-context" sessionId) 5000 parseWarmupContext c

type CompletionResult =
  { label: string
    kind: string
    insertText: string }

let getCompletions (code: string) (cursorPosition: int) (workingDirectory: string option) (c: Client) =
  promise {
    try
      let payload =
        {| code = code
           cursor_position = cursorPosition
           working_directory = workingDirectory |> Option.defaultValue "" |}
      let! resp = dashHttpPost c "/dashboard/completions" (jsonStringify payload) 10000
      match resp.statusCode with
      | 200 ->
        let parsed = jsonParse resp.body
        let items = parsed?completions |> unbox<obj array>
        return
          items
          |> Array.map (fun item ->
            { label = item?label |> unbox<string>
              kind = item?kind |> unbox<string>
              insertText = item?insertText |> unbox<string> })
      | _ ->
        return [||]
    with ex ->
      logWarn "getCompletions" ex
      return [||]
  }

let runTests (pattern: string) (c: Client) =
  postCommand c "/api/live-testing/run" (jsonStringify {| pattern = pattern; category = "" |}) 60000

let enableLiveTesting (c: Client) =
  postCommand c "/api/live-testing/enable" "{}" 5000

let disableLiveTesting (c: Client) =
  postCommand c "/api/live-testing/disable" "{}" 5000

let setRunPolicy (category: string) (policy: string) (c: Client) =
  postCommand c "/api/live-testing/policy" (jsonStringify {| category = category; policy = policy |}) 5000

let explore (name: string) (c: Client) =
  promise {
    try
      let! resp = httpPost c "/api/explore" (jsonStringify {| name = name |}) 10000
      match resp.statusCode with
      | 200 -> return Some resp.body
      | _ -> return None
    with ex ->
      logWarn "explore" ex
      return None
  }

let getRecentEvents (count: int) (c: Client) =
  getRaw "getRecentEvents" (sprintf "/api/recent-events?count=%d" count) 10000 c

let getDependencyGraph (symbol: string) (c: Client) =
  let path =
    match symbol with
    | "" -> "/api/dependency-graph"
    | s -> sprintf "/api/dependency-graph?symbol=%s" (JS.encodeURIComponent s)
  getRaw "getDependencyGraph" path 10000 c

let cancelEval (c: Client) =
  postCommand c "/api/cancel-eval" "{}" 5000

let loadScript (filePath: string) (c: Client) =
  let code = sprintf "#load @\"%s\";;" filePath
  postCommand c "/exec" (jsonStringify {| code = code; working_directory = "" |}) 30000

let getPipelineTrace (c: Client) =
  getRaw "getPipelineTrace" "/api/live-testing/pipeline-trace" 5000 c

type ExportResult =
  { content: string
    evalCount: int }

let exportSessionAsFsx (sessionId: string) (c: Client) =
  getJson "exportSessionAsFsx" (sprintf "/api/sessions/%s/export-fsx" (JS.encodeURIComponent sessionId)) 15000 (fun p -> !!p : ExportResult) c
