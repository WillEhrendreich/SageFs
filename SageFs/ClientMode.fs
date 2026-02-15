module SageFs.Server.ClientMode

open System
open System.Net.Http
open System.Text
open System.Text.Json
open SageFs.Server
open SageFs

/// Display daemon connection info.
let private showConnectionBanner (info: DaemonInfo) =
  let elapsed = DateTime.UtcNow - info.StartedAt
  let agoText =
    if elapsed.TotalMinutes < 1.0 then "just now"
    elif elapsed.TotalHours < 1.0 then sprintf "%dm ago" (int elapsed.TotalMinutes)
    elif elapsed.TotalDays < 1.0 then sprintf "%dh ago" (int elapsed.TotalHours)
    else sprintf "%dd ago" (int elapsed.TotalDays)
  printfn "\x1b[36m╭─────────────────────────────────────────────╮\x1b[0m"
  printfn "\x1b[36m│\x1b[0m  SageFs v%s — connected to daemon          \x1b[36m│\x1b[0m" info.Version
  printfn "\x1b[36m│\x1b[0m  PID %d • port %d • started %s        \x1b[36m│\x1b[0m" info.Pid info.Port agoText
  printfn "\x1b[36m│\x1b[0m  %s  \x1b[36m│\x1b[0m" info.WorkingDirectory
  printfn "\x1b[36m│\x1b[0m  Dashboard: http://localhost:%d/dashboard   \x1b[36m│\x1b[0m" (info.Port + 1)
  printfn "\x1b[36m╰─────────────────────────────────────────────╯\x1b[0m"
  printfn ""
  printfn "Type F# code to evaluate. End with ';;' and press Enter."
  printfn "Commands: #quit, #reset, #status, #hard-reset"
  printfn ""

/// Start daemon in background, wait for it to be ready.
let startDaemonInBackground (daemonArgs: string) =
  let psi = System.Diagnostics.ProcessStartInfo()
  psi.FileName <- "SageFs"
  psi.Arguments <- sprintf "-d %s" daemonArgs
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true

  let proc = System.Diagnostics.Process.Start(psi)
  if isNull proc then
    Error (SageFsError.DaemonStartFailed "Failed to start daemon")
  else
    let mutable attempts = 0
    let mutable found = false
    while attempts < 30 && not found do
      System.Threading.Thread.Sleep(500)
      match DaemonState.read () with
      | Some _ -> found <- true
      | None -> attempts <- attempts + 1
    if found then Ok ()
    else Error (SageFsError.DaemonStartFailed "Daemon started but did not become ready in 15s")

/// Check if a daemon is running and show its info.
let tryConnect () =
  match DaemonState.read () with
  | Some info ->
    showConnectionBanner info
    Some info
  | None -> None

/// Send F# code to the daemon for evaluation.
let private evalCode (client: HttpClient) (baseUrl: string) (code: string) = task {
  let json = JsonSerializer.Serialize({| code = code |})
  use content = new StringContent(json, Encoding.UTF8, "application/json")
  try
    let! response = client.PostAsync(sprintf "%s/exec" baseUrl, content)
    let! body = response.Content.ReadAsStringAsync()
    let doc = JsonDocument.Parse(body)
    let root = doc.RootElement
    if root.TryGetProperty("success") |> fst then
      let success = root.GetProperty("success").GetBoolean()
      if success then
        let result = root.GetProperty("result").GetString()
        return Ok result
      else
        let error =
          if root.TryGetProperty("error") |> fst then
            root.GetProperty("error").GetString()
          else "Unknown error"
        return Error error
    else
      return Ok body
  with ex ->
    return Error (sprintf "Connection error: %s" ex.Message)
}

/// Send a reset command to the daemon.
let private resetSession (client: HttpClient) (baseUrl: string) = task {
  try
    let! response = client.PostAsync(sprintf "%s/reset" baseUrl, null)
    let! body = response.Content.ReadAsStringAsync()
    return Ok body
  with ex ->
    return Error (sprintf "Connection error: %s" ex.Message)
}

/// Send a hard-reset command to the daemon.
let private hardResetSession (client: HttpClient) (baseUrl: string) = task {
  let json = JsonSerializer.Serialize({| rebuild = true |})
  use content = new StringContent(json, Encoding.UTF8, "application/json")
  try
    let! response = client.PostAsync(sprintf "%s/hard-reset" baseUrl, content)
    let! body = response.Content.ReadAsStringAsync()
    return Ok body
  with ex ->
    return Error (sprintf "Connection error: %s" ex.Message)
}

/// Check daemon health.
let private checkHealth (client: HttpClient) (baseUrl: string) = task {
  try
    let! response = client.GetAsync(sprintf "%s/health" baseUrl)
    let! body = response.Content.ReadAsStringAsync()
    return Ok body
  with ex ->
    return Error (sprintf "Connection error: %s" ex.Message)
}

/// History file path for connect client.
let private historyPath =
  let dir = IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".SageFs")
  IO.Directory.CreateDirectory(dir) |> ignore
  IO.Path.Combine(dir, "connect_history")

/// Load history from disk.
let private loadHistory () =
  if IO.File.Exists(historyPath) then
    IO.File.ReadAllLines(historyPath)
    |> Array.toList
    |> List.rev
    |> List.truncate 500
    |> List.rev
  else []

/// Append an entry to history.
let private appendHistory (entry: string) =
  try IO.File.AppendAllLines(historyPath, [entry])
  with _ -> ()

/// Read a multi-line F# input block (accumulates until ;; is found).
let private readInputBlock () =
  let sb = StringBuilder()
  let mutable reading = true
  let mutable firstLine = true
  while reading do
    if firstLine then
      printf "\x1b[32m> \x1b[0m"
    else
      printf "\x1b[90m. \x1b[0m"
    firstLine <- false
    let line = Console.ReadLine()
    if isNull line then
      reading <- false
    else
      sb.AppendLine(line) |> ignore
      let text = sb.ToString().TrimEnd()
      if text.EndsWith(";;") then
        reading <- false
  let result = sb.ToString().Trim()
  if String.IsNullOrWhiteSpace(result) then None
  else Some result

/// Run the connect REPL loop.
let run (info: DaemonInfo) = task {
  let baseUrl = sprintf "http://localhost:%d" info.Port
  use client = new HttpClient()
  client.Timeout <- TimeSpan.FromMinutes(5.0)

  // Verify connection
  match! checkHealth client baseUrl with
  | Error msg ->
    eprintfn "\x1b[31mCannot connect to daemon: %s\x1b[0m" msg
    return 1
  | Ok _ ->

  let mutable running = true
  while running do
    match readInputBlock () with
    | None -> running <- false
    | Some input ->
      let trimmed = input.Trim()
      match trimmed with
      | "#quit" | "#exit" | "#q" ->
        running <- false
      | "#reset" ->
        match! resetSession client baseUrl with
        | Ok msg -> printfn "\x1b[33m%s\x1b[0m" msg
        | Error msg -> eprintfn "\x1b[31m%s\x1b[0m" msg
      | "#hard-reset" ->
        printfn "\x1b[33mHard resetting (with rebuild)...\x1b[0m"
        match! hardResetSession client baseUrl with
        | Ok msg -> printfn "\x1b[33m%s\x1b[0m" msg
        | Error msg -> eprintfn "\x1b[31m%s\x1b[0m" msg
      | "#status" ->
        match! checkHealth client baseUrl with
        | Ok _ ->
          try
            let! resp = client.GetAsync(sprintf "%s/api/status" baseUrl)
            let! body = resp.Content.ReadAsStringAsync()
            let doc = System.Text.Json.JsonDocument.Parse(body)
            let r = doc.RootElement
            let get (name: string) =
              if r.TryGetProperty(name) |> fst then r.GetProperty(name).ToString() else "?"
            printfn "\x1b[36mSageFs Status\x1b[0m"
            printfn "  Session:    %s (%s)" (get "sessionId") (get "sessionState")
            printfn "  Version:    %s" (get "version")
            printfn "  PID:        %s" (get "pid")
            printfn "  Uptime:     %ss" (get "uptime")
            printfn "  Evals:      %s (avg %sms)" (get "evalCount") (get "avgDurationMs")
            printfn "  Projects:   %s" (get "projectCount")
            printfn "  Directory:  %s" (get "workingDirectory")
          with ex ->
            printfn "Status: connected (details unavailable: %s)" ex.Message
        | Error msg -> eprintfn "\x1b[31m%s\x1b[0m" msg
      | code ->
        appendHistory code
        match! evalCode client baseUrl code with
        | Ok result ->
          if not (String.IsNullOrWhiteSpace result) then
            printfn "%s" result
        | Error msg ->
          eprintfn "\x1b[31m%s\x1b[0m" msg
      printfn ""

  printfn "Disconnected."
  return 0
}
