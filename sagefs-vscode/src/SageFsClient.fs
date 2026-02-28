module SageFs.Vscode.SageFsClient

open Fable.Core
open Fable.Core.JsInterop

[<Emit("console.warn('[SageFs]', $0, $1)")>]
let logWarn (context: string) (err: obj) : unit = jsNative

type EvalResult =
  { success: bool
    result: string option
    error: string option }

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
      if resp.statusCode <> 200 then
        return { connected = true; healthy = Some false; status = Some "no session" }
      else
        let parsed = jsonParse resp.body
        let h = parsed?healthy
        let s = parsed?status
        return
          { connected = true
            healthy = if isNull h then Some false else Some (unbox<bool> h)
            status = if isNull s then None else Some (unbox<string> s) }
    with _ ->
      return { connected = false; healthy = None; status = None }
  }

let evalCode (code: string) (workingDirectory: string option) (c: Client) =
  promise {
    let payload =
      match workingDirectory with
      | Some wd -> {| code = code; working_directory = wd |}
      | None -> {| code = code; working_directory = "" |}
    try
      let! resp = httpPost c "/exec" (jsonStringify payload) 30000
      let parsed = jsonParse resp.body
      return
        { success = parsed?success |> unbox<bool>
          result = Some (parsed?result |> unbox<string>)
          error =
            let e = parsed?error
            if isNull e then None else Some (unbox<string> e) }
    with err ->
      return { success = false; result = None; error = Some (string err) }
  }

let resetSession (c: Client) =
  promise {
    try
      let! resp = httpPost c "/reset" "{}" 15000
      let parsed = jsonParse resp.body
      return
        { success = parsed?success |> unbox<bool>
          result =
            let m = parsed?message
            if isNull m then None else Some (unbox<string> m)
          error =
            let e = parsed?error
            if isNull e then None else Some (unbox<string> e) }
    with err ->
      return { success = false; result = None; error = Some (string err) }
  }

let hardReset (rebuild: bool) (c: Client) =
  promise {
    try
      let! resp = httpPost c "/hard-reset" (jsonStringify {| rebuild = rebuild |}) 60000
      let parsed = jsonParse resp.body
      return
        { success = parsed?success |> unbox<bool>
          result =
            let m = parsed?message
            if isNull m then None else Some (unbox<string> m)
          error =
            let e = parsed?error
            if isNull e then None else Some (unbox<string> e) }
    with err ->
      return { success = false; result = None; error = Some (string err) }
  }

let listSessions (c: Client) =
  promise {
    try
      let! resp = httpGet c "/api/sessions" 5000
      if resp.statusCode = 200 then
        let parsed = jsonParse resp.body
        let sessions: obj array = parsed?sessions |> unbox
        return
          sessions |> Array.map (fun s ->
            { id = s?id |> unbox<string>
              name = None
              workingDirectory = s?workingDirectory |> unbox<string>
              status = s?status |> unbox<string>
              projects =
                let p = s?projects
                if isNull p then [||] else unbox<string array> p
              evalCount =
                let e = s?evalCount
                if isNull e then 0 else unbox<int> e })
      else
        return [||]
    with ex ->
      logWarn "listSessions" ex
      return [||]
  }

let createSession (projects: string) (workingDirectory: string) (c: Client) =
  promise {
    try
      let payload = {| projects = [| projects |]; workingDirectory = workingDirectory |}
      let! resp = httpPost c "/api/sessions/create" (jsonStringify payload) 30000
      let parsed = jsonParse resp.body
      return
        { success = parsed?success |> unbox<bool>
          result = Some (parsed?message |> unbox<string>)
          error =
            let e = parsed?error
            if isNull e then None else Some (unbox<string> e) }
    with err ->
      return { success = false; result = None; error = Some (string err) }
  }

let switchSession (sessionId: string) (c: Client) =
  promise {
    try
      let payload = {| sessionId = sessionId |}
      let! resp = httpPost c "/api/sessions/switch" (jsonStringify payload) 5000
      let parsed = jsonParse resp.body
      return parsed?success |> unbox<bool>
    with ex ->
      logWarn "switchSession" ex
      return false
  }

let stopSession (sessionId: string) (c: Client) =
  promise {
    try
      let payload = {| sessionId = sessionId |}
      let! resp = httpPost c "/api/sessions/stop" (jsonStringify payload) 10000
      let parsed = jsonParse resp.body
      return parsed?success |> unbox<bool>
    with ex ->
      logWarn "stopSession" ex
      return false
  }

let getSystemStatus (c: Client) =
  promise {
    try
      let! resp = httpGet c "/api/system/status" 3000
      if resp.statusCode = 200 then
        let parsed = jsonParse resp.body
        return
          Some
            { supervised = parsed?supervised |> unbox<bool>
              restartCount = parsed?restartCount |> unbox<int>
              version = parsed?version |> unbox<string> }
      else
        return None
    with ex ->
      logWarn "getSystemStatus" ex
      return None
  }

let getHotReloadState (sessionId: string) (c: Client) =
  promise {
    try
      let! resp = dashHttpGet c (sprintf "/api/sessions/%s/hotreload" sessionId) 5000
      if resp.statusCode = 200 then
        let parsed = jsonParse resp.body
        let rawFiles = parsed?files
        if isNull rawFiles then
          return Some { files = [||]; watchedCount = 0 }
        else
          let files =
            rawFiles
            |> unbox<obj array>
            |> Array.choose (fun f ->
              let p = f?path
              if isNull p then None
              else
                Some
                  { path = unbox<string> p
                    watched =
                      let w = f?watched
                      if isNull w then false else unbox<bool> w })
          let wc = parsed?watchedCount
          return Some { files = files; watchedCount = if isNull wc then 0 else unbox<int> wc }
      else
        return None
    with ex ->
      logWarn "getHotReloadState" ex
      return None
  }

let toggleHotReload (sessionId: string) (path: string) (c: Client) =
  promise {
    try
      let! _ = dashHttpPost c (sprintf "/api/sessions/%s/hotreload/toggle" sessionId) (jsonStringify {| path = path |}) 5000
      return true
    with ex ->
      logWarn "toggleHotReload" ex
      return false
  }

let watchAllHotReload (sessionId: string) (c: Client) =
  promise {
    try
      let! _ = dashHttpPost c (sprintf "/api/sessions/%s/hotreload/watch-all" sessionId) "{}" 5000
      return true
    with ex ->
      logWarn "watchAllHotReload" ex
      return false
  }

let unwatchAllHotReload (sessionId: string) (c: Client) =
  promise {
    try
      let! _ = dashHttpPost c (sprintf "/api/sessions/%s/hotreload/unwatch-all" sessionId) "{}" 5000
      return true
    with ex ->
      logWarn "unwatchAllHotReload" ex
      return false
  }

let watchDirectoryHotReload (sessionId: string) (directory: string) (c: Client) =
  promise {
    try
      let! _ = dashHttpPost c (sprintf "/api/sessions/%s/hotreload/watch-directory" sessionId) (jsonStringify {| directory = directory |}) 5000
      return true
    with ex ->
      logWarn "watchDirectoryHotReload" ex
      return false
  }

let unwatchDirectoryHotReload (sessionId: string) (directory: string) (c: Client) =
  promise {
    try
      let! _ = dashHttpPost c (sprintf "/api/sessions/%s/hotreload/unwatch-directory" sessionId) (jsonStringify {| directory = directory |}) 5000
      return true
    with ex ->
      logWarn "unwatchDirectoryHotReload" ex
      return false
  }

let getWarmupContext (sessionId: string) (c: Client) =
  promise {
    try
      let! resp = dashHttpGet c (sprintf "/api/sessions/%s/warmup-context" sessionId) 5000
      if resp.statusCode = 200 then
        let parsed = jsonParse resp.body
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
        return
          Some
            { SourceFilesScanned = parsed?SourceFilesScanned |> unbox<int>
              AssembliesLoaded = assemblies
              NamespacesOpened = opened
              FailedOpens = failed
              WarmupDurationMs = parsed?WarmupDurationMs |> unbox<int> }
      else
        return None
    with ex ->
      logWarn "getWarmupContext" ex
      return None
  }

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
      if resp.statusCode = 200 then
        let parsed = jsonParse resp.body
        let items = parsed?completions |> unbox<obj array>
        return
          items
          |> Array.map (fun item ->
            { label = item?label |> unbox<string>
              kind = item?kind |> unbox<string>
              insertText = item?insertText |> unbox<string> })
      else
        return [||]
    with ex ->
      logWarn "getCompletions" ex
      return [||]
  }

let runTests (pattern: string) (c: Client) =
  promise {
    try
      let payload = {| pattern = pattern; category = "" |}
      let! resp = httpPost c "/api/live-testing/run" (jsonStringify payload) 60000
      let parsed = jsonParse resp.body
      return
        { success = parsed?success |> unbox<bool>
          result =
            let m = parsed?message
            if isNull m then None else Some (unbox<string> m)
          error =
            let e = parsed?error
            if isNull e then None else Some (unbox<string> e) }
    with err ->
      return { success = false; result = None; error = Some (string err) }
  }

let enableLiveTesting (c: Client) =
  promise {
    try
      let! resp = httpPost c "/api/live-testing/enable" "{}" 5000
      let parsed = jsonParse resp.body
      return
        { success = parsed?success |> unbox<bool>
          result =
            let m = parsed?message
            if isNull m then None else Some (unbox<string> m)
          error =
            let e = parsed?error
            if isNull e then None else Some (unbox<string> e) }
    with err ->
      return { success = false; result = None; error = Some (string err) }
  }

let disableLiveTesting (c: Client) =
  promise {
    try
      let! resp = httpPost c "/api/live-testing/disable" "{}" 5000
      let parsed = jsonParse resp.body
      return
        { success = parsed?success |> unbox<bool>
          result =
            let m = parsed?message
            if isNull m then None else Some (unbox<string> m)
          error =
            let e = parsed?error
            if isNull e then None else Some (unbox<string> e) }
    with err ->
      return { success = false; result = None; error = Some (string err) }
  }

let setRunPolicy (category: string) (policy: string) (c: Client) =
  promise {
    try
      let payload = {| category = category; policy = policy |}
      let! resp = httpPost c "/api/live-testing/policy" (jsonStringify payload) 5000
      let parsed = jsonParse resp.body
      return
        { success = parsed?success |> unbox<bool>
          result =
            let m = parsed?message
            if isNull m then None else Some (unbox<string> m)
          error =
            let e = parsed?error
            if isNull e then None else Some (unbox<string> e) }
    with err ->
      return { success = false; result = None; error = Some (string err) }
  }

let explore(name: string) (c: Client) =
  promise {
    try
      let! resp = httpPost c "/api/explore" (jsonStringify {| name = name |}) 10000
      if resp.statusCode = 200 then
        return Some resp.body
      else
        return None
    with ex ->
      logWarn "explore" ex
      return None
  }

let getRecentEvents (count: int) (c: Client) =
  promise {
    try
      let! resp = httpGet c (sprintf "/api/recent-events?count=%d" count) 10000
      if resp.statusCode = 200 then
        return Some resp.body
      else
        return None
    with ex ->
      logWarn "getRecentEvents" ex
      return None
  }

let getDependencyGraph (symbol: string) (c: Client) =
  promise {
    try
      let path =
        if symbol = "" then "/api/dependency-graph"
        else sprintf "/api/dependency-graph?symbol=%s" (JS.encodeURIComponent symbol)
      let! resp = httpGet c path 10000
      if resp.statusCode = 200 then
        return Some resp.body
      else
        return None
    with ex ->
      logWarn "getDependencyGraph" ex
      return None
  }
