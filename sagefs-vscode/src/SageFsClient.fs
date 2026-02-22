module SageFs.Vscode.SageFsClient

open Fable.Core
open Fable.Core.JsInterop

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

type SessionInfo =
  { id: string
    name: string option
    workingDirectory: string
    status: string }

[<Emit("new Promise((resolve, reject) => { const http = require('http'); const req = http.get($0, { timeout: $1 }, (res) => { let data = ''; res.on('data', (chunk) => data += chunk); res.on('end', () => resolve({ statusCode: res.statusCode || 0, body: data })); }); req.on('error', reject); req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); }); })")>]
let private httpGetRaw (url: string) (timeout: int) : JS.Promise<{| statusCode: int; body: string |}> = jsNative

[<Emit("new Promise((resolve, reject) => { const http = require('http'); const url = new URL($0); const req = http.request({ hostname: url.hostname, port: url.port, path: url.pathname, method: 'POST', headers: { 'Content-Type': 'application/json' }, timeout: $2 }, (res) => { let data = ''; res.on('data', (chunk) => data += chunk); res.on('end', () => resolve({ statusCode: res.statusCode || 0, body: data })); }); req.on('error', reject); req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); }); req.write($1); req.end(); })")>]
let private httpPostRaw (url: string) (body: string) (timeout: int) : JS.Promise<{| statusCode: int; body: string |}> = jsNative

[<Emit("JSON.parse($0)")>]
let private jsonParse (s: string) : obj = jsNative

[<Emit("JSON.stringify($0)")>]
let private jsonStringify (o: obj) : string = jsNative

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

let private httpGet (c: Client) (path: string) (timeout: int) =
  httpGetRaw (sprintf "%s%s" (baseUrl c) path) timeout

let private httpPost (c: Client) (path: string) (body: string) (timeout: int) =
  httpPostRaw (sprintf "%s%s" (baseUrl c) path) body timeout

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
        return
          { connected = true
            healthy = Some (parsed?healthy |> unbox<bool>)
            status = Some (parsed?status |> unbox<string>) }
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
        return jsonParse resp.body |> unbox<SessionInfo array>
      else
        return [||]
    with _ ->
      return [||]
  }

let createSession (projects: string) (workingDirectory: string) (c: Client) =
  promise {
    try
      let payload = {| projects = projects; working_directory = workingDirectory |}
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
    with _ ->
      return None
  }
