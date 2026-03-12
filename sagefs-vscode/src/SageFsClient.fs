module SageFs.Vscode.SageFsClient

open Fable.Core
open Fable.Core.JsInterop
open SageFs.Vscode.JsHelpers
open SageFs.Vscode.SafeInterop

[<Emit("console.warn('[SageFs]', $0 + ':', $1)")>]
let private consoleWarn (context: string) (err: obj) : unit = jsNative

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

type HealthError =
  { case: string
    message: string
    suggestedAction: string }

type SageFsStatus =
  { connected: bool
    healthy: bool option
    status: string option
    apiVersion: int option
    features: string list
    error: HealthError option }

type SystemStatus =
  { supervised: bool
    restartCount: int
    version: string
    apiVersion: int
    mcpPort: int option
    dashboardPort: int option }

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

[<Import("httpGet", "./http-helpers.js")>]
let httpGetRaw (url: string) (timeout: int) : JS.Promise<{| statusCode: int; body: string |}> = jsNative

[<Import("httpPost", "./http-helpers.js")>]
let httpPostRaw (url: string) (body: string) (timeout: int) : JS.Promise<{| statusCode: int; body: string |}> = jsNative

type Client =
  { mutable mcpPort: int
    mutable dashboardPort: int
    log: string -> unit }

let create (mcpPort: int) (dashboardPort: int) (log: string -> unit) =
  { mcpPort = mcpPort; dashboardPort = dashboardPort; log = log }

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

let parseOutcome (parsed: obj) : ApiOutcome =
  let success = fieldBool "success" parsed |> Option.defaultValue false
  match success with
  | true ->
    fieldString "message" parsed
    |> Option.orElse (fieldString "result" parsed)
    |> Succeeded
  | false ->
    fieldString "error" parsed
    |> Option.defaultValue "Unknown error"
    |> Failed

/// POST a command, parse the standard { success, message/result, error } response.
let postCommand (c: Client) (path: string) (body: string) (timeout: int) : JS.Promise<ApiOutcome> =
  promise {
    try
      let! resp = httpPost c path body timeout
      match resp.statusCode with
      | s when s >= 200 && s < 300 ->
        return jsonParse resp.body |> parseOutcome
      | s ->
        return Failed (sprintf "HTTP %d: %s" s (resp.body.Substring(0, min 200 resp.body.Length)))
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
      c.log (sprintf "[warn] %s: %O" ctx ex)
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
      c.log (sprintf "[warn] %s: %O" ctx ex)
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
      c.log (sprintf "[warn] %s: %O" ctx ex)
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
      let! resp = httpGet c "/health" 15000
      return resp.statusCode = 200
    with _ ->
      return false
  }

let parseHealthError (parsed: obj) : HealthError option =
  let errObj = fieldObj "error" parsed
  match errObj with
  | None -> None
  | Some e ->
    Some
      { case = fieldString "case" e |> Option.defaultValue "Unknown"
        message = fieldString "message" e |> Option.defaultValue ""
        suggestedAction = fieldString "suggestedAction" e |> Option.defaultValue "" }

let getStatus (c: Client) =
  promise {
    try
      let! resp = httpGet c "/health" 15000
      match resp.statusCode with
      | 200 ->
        let parsed = jsonParse resp.body
        let features =
          fieldArray "features" parsed
          |> Option.map (Array.choose tryCastString >> Array.toList)
          |> Option.defaultValue []
        return
          { connected = true
            healthy = fieldBool "healthy" parsed |> Option.orElse (Some false)
            status = fieldString "status" parsed
            apiVersion = fieldInt "apiVersion" parsed
            features = features
            error = parseHealthError parsed }
      | _ ->
        return { connected = true; healthy = Some false; status = Some "no session"; apiVersion = None; features = []; error = None }
    with _ ->
      return { connected = false; healthy = None; status = None; apiVersion = None; features = []; error = None }
  }

let isReady (c: Client) =
  promise {
    let! s = getStatus c
    match s.connected, s.status with
    | true, (Some "Ready" | Some "Evaluating") -> return true
    | _ -> return false
  }

/// Returns the features list from the daemon health endpoint, or empty list if unreachable.
let getFeatures (c: Client) =
  promise {
    let! s = getStatus c
    return s.features
  }

let evalCode (code: string) (workingDirectory: string option) (filePath: string option) (evalMode: string option) (blockStartLine: int option) (c: Client) =
  let wd = workingDirectory |> Option.defaultValue ""
  let fp = filePath |> Option.defaultValue ""
  let em = evalMode |> Option.defaultValue ""
  let bsl = blockStartLine |> Option.defaultValue 0
  postCommand c "/exec" (jsonStringify {| code = code; working_directory = wd; file_path = fp; eval_mode = em; block_start_line = bsl |}) 30000

let resetSession (c: Client) =
  postCommand c "/reset" "{}" 15000

let hardReset (rebuild: bool) (c: Client) =
  postCommand c "/hard-reset" (jsonStringify {| rebuild = rebuild |}) 60000

let parseSessions (parsed: obj) =
  fieldArray "sessions" parsed
  |> Option.defaultValue [||]
  |> Array.map (fun s ->
    { id = fieldString "id" s |> Option.defaultValue ""
      name = None
      workingDirectory = fieldString "workingDirectory" s |> Option.defaultValue ""
      status = fieldString "status" s |> Option.defaultValue "unknown"
      projects = fieldStringArray "projects" s |> Option.defaultValue [||]
      evalCount = fieldInt "evalCount" s |> Option.defaultValue 0 })

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
  { supervised = fieldBool "supervised" parsed |> Option.defaultValue false
    restartCount = fieldInt "restartCount" parsed |> Option.defaultValue 0
    version = fieldString "version" parsed |> Option.defaultValue "?"
    apiVersion = fieldInt "apiVersion" parsed |> Option.defaultValue 0
    mcpPort = fieldInt "mcpPort" parsed
    dashboardPort = fieldInt "dashboardPort" parsed }

let private syncDiscoveredPorts (status: SystemStatus) (c: Client) =
  let currentMcp = c.mcpPort
  let currentDashboard = c.dashboardPort
  let nextMcp = status.mcpPort |> Option.defaultValue currentMcp
  let nextDashboard =
    status.dashboardPort
    |> Option.orElse (status.mcpPort |> Option.map (fun port -> port + 1))
    |> Option.defaultValue currentDashboard

  match nextMcp <> currentMcp || nextDashboard <> currentDashboard with
  | true ->
    let message =
      sprintf
        "[info] syncDiscoveredPorts: daemon reported mcpPort=%d dashboardPort=%d (was mcpPort=%d dashboardPort=%d)"
        nextMcp
        nextDashboard
        currentMcp
        currentDashboard
    c.log message
    updatePorts nextMcp nextDashboard c
  | false -> ()

let [<Literal>] expectedApiVersion = 1

let checkVersion (status: SystemStatus) : Result<unit, string> =
  match status.apiVersion with
  | v when v = expectedApiVersion -> Ok ()
  | v -> Error $"SageFs daemon apiVersion={v} is incompatible with this extension (requires apiVersion={expectedApiVersion}). Run 'dotnet tool update --global SageFs' then reload VS Code."

let getSystemStatus (c: Client) =
  promise {
    let! result = getJson "getSystemStatus" "/api/system/status" 15000 parseSystemStatus c
    result |> Option.iter (fun status -> syncDiscoveredPorts status c)
    return result
  }

let parseHotReloadState (parsed: obj) =
  let files =
    fieldArray "files" parsed
    |> Option.map (fun rawFiles ->
      rawFiles
      |> Array.choose (fun f ->
        fieldString "path" f
        |> Option.map (fun p ->
          { path = p
            watched = fieldBool "watched" f |> Option.defaultValue false })))
    |> Option.defaultValue [||]
  let wc = fieldInt "watchedCount" parsed |> Option.defaultValue 0
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
    fieldArray "assembliesLoaded" parsed
    |> Option.defaultValue [||]
    |> Array.map (fun a ->
      { Name = fieldString "name" a |> Option.defaultValue ""
        Path = fieldString "path" a |> Option.defaultValue ""
        NamespaceCount = fieldInt "namespaceCount" a |> Option.defaultValue 0
        ModuleCount = fieldInt "moduleCount" a |> Option.defaultValue 0 })
  let opened =
    fieldArray "namespacesOpened" parsed
    |> Option.defaultValue [||]
    |> Array.map (fun b ->
      { Name = fieldString "name" b |> Option.defaultValue ""
        IsModule = fieldBool "isModule" b |> Option.defaultValue false
        Source = fieldString "source" b |> Option.defaultValue "" })
  let failed =
    fieldArray "failedOpens" parsed
    |> Option.defaultValue [||]
    |> Array.map (fun f -> tryCastStringArray f |> Option.defaultValue [||])
  let timing = fieldObj "phaseTiming" parsed
  let totalMs =
    match timing with
    | None -> fieldInt "warmupDurationMs" parsed |> Option.defaultValue 0
    | Some t -> fieldInt "totalMs" t |> Option.defaultValue 0
  { SourceFilesScanned = fieldInt "sourceFilesScanned" parsed |> Option.defaultValue 0
    AssembliesLoaded = assemblies
    NamespacesOpened = opened
    FailedOpens = failed
    WarmupDurationMs = totalMs }

let getWarmupContext (sessionId: string) (c: Client) =
  dashGetJson "getWarmupContext" (sprintf "/api/sessions/%s/warmup-context" sessionId) 5000 parseWarmupContext c

type CompletionResult =
  { label: string
    kind: string
    insertText: string
    detail: string option }

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
        let items = fieldArray "completions" parsed |> Option.defaultValue [||]
        return
          items
          |> Array.map (fun item ->
            { label = fieldString "label" item |> Option.defaultValue ""
              kind = fieldString "kind" item |> Option.defaultValue ""
              insertText = fieldString "insertText" item |> Option.defaultValue ""
              detail = fieldString "detail" item })
      | _ ->
        return [||]
    with ex ->
      c.log (sprintf "[warn] getCompletions: %O" ex)
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
      c.log (sprintf "[warn] explore: %O" ex)
      return None
  }

/// Call /dashboard/completions with "Name." to get structured JSON:
/// { completions: [{ label, kind, insertText, detail? }], count }
let exploreCompletions (qualifiedName: string) (sessionId: string) (c: Client) =
  promise {
    try
      let code = sprintf "%s." qualifiedName
      let body = jsonStringify {| code = code; cursorPos = code.Length; sessionId = sessionId |}
      let! resp = dashHttpPost c "/dashboard/completions" body 10000
      match resp.statusCode with
      | 200 -> return Some resp.body
      | _ ->
        c.log (sprintf "[warn] exploreCompletions %s: status %d" qualifiedName resp.statusCode)
        return None
    with ex ->
      c.log (sprintf "[warn] exploreCompletions: %O" ex)
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

let getTestTrace (c: Client) =
  getRaw "getTestTrace" "/api/live-testing/test-trace" 5000 c

type ExportResult =
  { content: string
    evalCount: int }

let exportSessionAsFsx (sessionId: string) (c: Client) =
  getJson "exportSessionAsFsx" (sprintf "/api/sessions/%s/export-fsx" (JS.encodeURIComponent sessionId)) 15000 (fun p -> !!p : ExportResult) c
