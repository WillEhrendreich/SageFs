module SageFs.Server.ClientMode

open System
open System.Net.Http
open System.Text
open System.Text.Json
open SageFs.Server
open SageFs

/// Parsed session info from the API.
type SessionInfo = {
  Id: string
  Status: string
  EvalCount: int
  AvgMs: float
  WorkingDirectory: string
  Projects: string list
}

/// Parsed REPL command — pure data, no side effects.
type ReplCommand =
  | Quit
  | Help
  | Clear
  | ListSessions
  | SwitchSession of sessionId: string
  | StopSession of sessionId: string
  | CreateSession of workingDir: string option
  | ShowDiagnostics
  | Reset
  | HardReset
  | ShowStatus
  | EvalCode of code: string

module ReplCommand =
  /// Parse a trimmed input string into a REPL command.
  let parse (input: string) : ReplCommand =
    match input with
    | "#quit" | "#exit" | "#q" -> Quit
    | "#help" -> Help
    | "#clear" -> Clear
    | "#sessions" -> ListSessions
    | "#diag" | "#diagnostics" -> ShowDiagnostics
    | "#reset" -> Reset
    | "#hard-reset" -> HardReset
    | "#status" -> ShowStatus
    | s when s.StartsWith("#switch", System.StringComparison.Ordinal) ->
      let sid = s.Substring(7).Trim()
      if String.IsNullOrWhiteSpace(sid) then SwitchSession ""
      else SwitchSession sid
    | s when s.StartsWith("#stop", System.StringComparison.Ordinal) ->
      let sid = (if s.Length > 5 then s.Substring(5) else "").Trim()
      if String.IsNullOrWhiteSpace(sid) then StopSession ""
      else StopSession sid
    | s when s.StartsWith("#create", System.StringComparison.Ordinal) ->
      let arg = s.Substring(7).Trim()
      if String.IsNullOrWhiteSpace(arg) then CreateSession None
      else CreateSession (Some arg)
    | code -> EvalCode code

/// Display daemon connection info.
let showConnectionBanner (info: DaemonInfo) =
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
  printfn "Commands: #help, #sessions, #switch, #status, #quit"
  printfn ""

/// Start daemon in background, wait for it to be ready.
let startDaemonInBackground (daemonArgs: string) =
  let psi = System.Diagnostics.ProcessStartInfo()
  psi.FileName <- "sagefs"
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
let evalCode (client: HttpClient) (baseUrl: string) (code: string) = task {
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
let resetSession (client: HttpClient) (baseUrl: string) = task {
  try
    let! response = client.PostAsync(sprintf "%s/reset" baseUrl, null)
    let! body = response.Content.ReadAsStringAsync()
    return Ok body
  with ex ->
    return Error (sprintf "Connection error: %s" ex.Message)
}

/// Send a hard-reset command to the daemon.
let hardResetSession (client: HttpClient) (baseUrl: string) = task {
  let json = JsonSerializer.Serialize({| rebuild = true |})
  use content = new StringContent(json, Encoding.UTF8, "application/json")
  try
    let! response = client.PostAsync(sprintf "%s/hard-reset" baseUrl, content)
    let! body = response.Content.ReadAsStringAsync()
    return Ok body
  with ex ->
    return Error (sprintf "Connection error: %s" ex.Message)
}

/// Fetch structured session list from the daemon.
let fetchSessions (client: HttpClient) (baseUrl: string) = task {
  try
    let! response = client.GetAsync(sprintf "%s/api/sessions" baseUrl)
    let! body = response.Content.ReadAsStringAsync()
    let doc = JsonDocument.Parse(body)
    let root = doc.RootElement
    if root.TryGetProperty("sessions") |> fst then
      let sessions =
        root.GetProperty("sessions").EnumerateArray()
        |> Seq.map (fun s ->
          { Id = s.GetProperty("id").GetString()
            Status = s.GetProperty("status").GetString()
            EvalCount = s.GetProperty("evalCount").GetInt32()
            AvgMs = s.GetProperty("avgDurationMs").GetDouble()
            WorkingDirectory = s.GetProperty("workingDirectory").GetString()
            Projects = s.GetProperty("projects").EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Seq.toList })
        |> Seq.toList
      return Ok sessions
    else
      return Ok []
  with ex ->
    return Error (sprintf "Connection error: %s" ex.Message)
}

/// Resolve a partial session ID or numeric index to a full session ID.
let internal resolveSessionId (sessions: SessionInfo list) (input: string) =
  // Try numeric index first (1-based)
  match Int32.TryParse(input) with
  | true, n when n >= 1 && n <= sessions.Length -> Some sessions.[n - 1].Id
  | _ ->
    // Try exact prefix match
    let matches = sessions |> List.filter (fun s -> s.Id.StartsWith(input, StringComparison.OrdinalIgnoreCase))
    match matches with
    | [single] -> Some single.Id
    | _ ->
      // Try contains match
      let contains = sessions |> List.filter (fun s -> s.Id.Contains(input, StringComparison.OrdinalIgnoreCase))
      match contains with
      | [single] -> Some single.Id
      | _ -> None

/// Format a session for display with a 1-based index.
let formatSession (i: int) (s: SessionInfo) =
  let marker = " "  // no global "active" concept
  let statusColor = if s.Status = "Ready" then "\x1b[32m" elif s.Status = "WarmingUp" then "\x1b[33m" else "\x1b[31m"
  let projects =
    if s.Projects.IsEmpty then ""
    else sprintf " (%s)" (s.Projects |> List.map IO.Path.GetFileNameWithoutExtension |> String.concat ", ")
  let shortId = s.Id.[..min 7 (s.Id.Length - 1)]
  let line1 = sprintf "  %s \x1b[36m%d\x1b[0m  %s %s%s\x1b[0m  evals:%d avg:%.0fms%s" marker (i + 1) shortId statusColor s.Status s.EvalCount s.AvgMs projects
  sprintf "%s\n    dir: %s" line1 s.WorkingDirectory

/// Interactive session picker — shows numbered list, reads a number or partial ID.
let pickSession (sessions: SessionInfo list) (prompt: string) =
  printfn "\x1b[36m%s\x1b[0m" prompt
  sessions |> List.iteri (fun i s -> printfn "%s" (formatSession i s))
  printfn ""
  printf "\x1b[33mEnter number or ID (or blank to cancel): \x1b[0m"
  let input = Console.ReadLine()
  if String.IsNullOrWhiteSpace(input) then None
  else resolveSessionId sessions input

/// List sessions from the daemon.
let listSessions (client: HttpClient) (baseUrl: string) = task {
  match! fetchSessions client baseUrl with
  | Error msg -> return Error msg
  | Ok [] -> return Ok "No sessions running."
  | Ok sessions ->
    let sb = StringBuilder()
    sb.AppendLine("\x1b[36mSessions:\x1b[0m") |> ignore
    sessions |> List.iteri (fun i s -> sb.AppendLine(formatSession i s) |> ignore)
    sb.AppendLine() |> ignore
    sb.Append("\x1b[90mUse #switch <number|id> or #stop <number|id>\x1b[0m") |> ignore
    return Ok (sb.ToString().TrimEnd())
}

/// Switch to a different session.
let switchSession (client: HttpClient) (baseUrl: string) (sessionId: string) = task {
  let json = JsonSerializer.Serialize({| sessionId = sessionId |})
  use content = new StringContent(json, Encoding.UTF8, "application/json")
  try
    let! response = client.PostAsync(sprintf "%s/api/sessions/switch" baseUrl, content)
    let! body = response.Content.ReadAsStringAsync()
    let doc = JsonDocument.Parse(body)
    if doc.RootElement.GetProperty("success").GetBoolean() then
      return Ok (sprintf "Switched to session '%s'" sessionId)
    else
      return Error "Switch failed"
  with ex ->
    return Error (sprintf "Connection error: %s" ex.Message)
}

/// Stop a session.
let stopSession (client: HttpClient) (baseUrl: string) (sessionId: string) = task {
  let json = JsonSerializer.Serialize({| sessionId = sessionId |})
  use content = new StringContent(json, Encoding.UTF8, "application/json")
  try
    let! response = client.PostAsync(sprintf "%s/api/sessions/stop" baseUrl, content)
    let! body = response.Content.ReadAsStringAsync()
    let doc = JsonDocument.Parse(body)
    if doc.RootElement.TryGetProperty("success") |> fst && doc.RootElement.GetProperty("success").GetBoolean() then
      let msg = doc.RootElement.GetProperty("message").GetString()
      return Ok msg
    else
      let err = if doc.RootElement.TryGetProperty("error") |> fst then doc.RootElement.GetProperty("error").GetString() else "Stop failed"
      return Error err
  with ex ->
    return Error (sprintf "Connection error: %s" ex.Message)
}

/// Create a new session.
let createSession (client: HttpClient) (baseUrl: string) (workingDir: string) (projects: string list) = task {
  let json = JsonSerializer.Serialize({| workingDirectory = workingDir; projects = projects |})
  use content = new StringContent(json, Encoding.UTF8, "application/json")
  try
    let! response = client.PostAsync(sprintf "%s/api/sessions/create" baseUrl, content)
    let! body = response.Content.ReadAsStringAsync()
    let doc = JsonDocument.Parse(body)
    if doc.RootElement.TryGetProperty("success") |> fst && doc.RootElement.GetProperty("success").GetBoolean() then
      let msg = doc.RootElement.GetProperty("message").GetString()
      return Ok msg
    else
      let err = if doc.RootElement.TryGetProperty("error") |> fst then doc.RootElement.GetProperty("error").GetString() else "Create failed"
      return Error err
  with ex ->
    return Error (sprintf "Connection error: %s" ex.Message)
}

/// Get diagnostics from the daemon.
let getDiagnostics (client: HttpClient) (baseUrl: string) = task {
  try
    let! resp = client.GetAsync(sprintf "%s/api/status" baseUrl)
    let! body = resp.Content.ReadAsStringAsync()
    let doc = JsonDocument.Parse(body)
    let root = doc.RootElement
    if root.TryGetProperty("regions") |> fst then
      let regions = root.GetProperty("regions").EnumerateArray() |> Seq.toList
      let diagRegion =
        regions |> List.tryFind (fun r ->
          r.GetProperty("id").GetString() = "diagnostics")
      match diagRegion with
      | Some r ->
        let content = r.GetProperty("content").GetString()
        if String.IsNullOrWhiteSpace(content) then
          return Ok "No diagnostics."
        else
          return Ok (sprintf "\x1b[36mDiagnostics:\x1b[0m\n%s" content)
      | None -> return Ok "No diagnostics available."
    else
      return Ok "No diagnostics available."
  with ex ->
    return Error (sprintf "Connection error: %s" ex.Message)
}

/// Show help for available REPL commands.
let showHelp () =
  printfn "\x1b[36mSageFs Connect REPL Commands:\x1b[0m"
  printfn ""
  printfn "  \x1b[33m#help\x1b[0m              Show this help"
  printfn "  \x1b[33m#status\x1b[0m            Show daemon and session status"
  printfn "  \x1b[33m#sessions\x1b[0m          List all sessions (numbered)"
  printfn "  \x1b[33m#switch [id|#]\x1b[0m     Switch session (interactive picker if no arg)"
  printfn "  \x1b[33m#create [dir]\x1b[0m      Create a new session (auto-discovers projects)"
  printfn "  \x1b[33m#stop [id|#]\x1b[0m       Stop a session (interactive picker if no arg)"
  printfn "  \x1b[33m#diag\x1b[0m              Show current diagnostics"
  printfn "  \x1b[33m#reset\x1b[0m             Soft reset current session"
  printfn "  \x1b[33m#hard-reset\x1b[0m        Hard reset with rebuild"
  printfn "  \x1b[33m#clear\x1b[0m             Clear console output"
  printfn "  \x1b[33m#quit\x1b[0m              Disconnect from daemon"
  printfn ""
  printfn "  F# code ending with \x1b[33m;;\x1b[0m is evaluated in the active session."

/// Check daemon health.
let checkHealth (client: HttpClient) (baseUrl: string) = task {
  try
    let! response = client.GetAsync(sprintf "%s/health" baseUrl)
    let! body = response.Content.ReadAsStringAsync()
    return Ok body
  with ex ->
    return Error (sprintf "Connection error: %s" ex.Message)
}

/// History file path for connect client.
let historyPath =
  let dir = IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".SageFs")
  IO.Directory.CreateDirectory(dir) |> ignore
  IO.Path.Combine(dir, "connect_history")

/// Load history from disk.
let loadHistory () =
  if IO.File.Exists(historyPath) then
    IO.File.ReadAllLines(historyPath)
    |> Array.toList
    |> List.rev
    |> List.truncate 500
    |> List.rev
  else []

/// Append an entry to history.
let appendHistory (entry: string) =
  try IO.File.AppendAllLines(historyPath, [entry])
  with _ -> ()

/// Read a multi-line F# input block (accumulates until ;; is found).
let readInputBlock (evalCount: int) =
  let sb = StringBuilder()
  let mutable reading = true
  let mutable firstLine = true
  while reading do
    if firstLine then
      if evalCount > 0 then
        printf "\x1b[90m[%d]\x1b[0m \x1b[32m> \x1b[0m" evalCount
      else
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
      if text.EndsWith(";;", System.StringComparison.Ordinal) then
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
  let mutable evalCount = 0
  while running do
    match readInputBlock evalCount with
    | None -> running <- false
    | Some input ->
      let trimmed = input.Trim()
      match ReplCommand.parse trimmed with
      | Quit -> running <- false
      | Help -> showHelp ()
      | Clear -> Console.Clear()
      | ListSessions ->
        match! listSessions client baseUrl with
        | Ok msg -> printfn "%s" msg
        | Error msg -> eprintfn "\x1b[31m%s\x1b[0m" msg
      | SwitchSession sid ->
        if String.IsNullOrEmpty(sid) then
          // Interactive picker
          match! fetchSessions client baseUrl with
          | Error msg -> eprintfn "\x1b[31m%s\x1b[0m" msg
          | Ok [] -> printfn "No sessions running."
          | Ok sessions ->
            match pickSession sessions "Switch to session:" with
            | Some resolvedId ->
              match! switchSession client baseUrl resolvedId with
              | Ok msg -> printfn "\x1b[33m%s\x1b[0m" msg
              | Error msg -> eprintfn "\x1b[31m%s\x1b[0m" msg
            | None -> printfn "\x1b[90mCancelled.\x1b[0m"
        else
          // Partial ID matching
          match! fetchSessions client baseUrl with
          | Error _ ->
            match! switchSession client baseUrl sid with
            | Ok msg -> printfn "\x1b[33m%s\x1b[0m" msg
            | Error msg -> eprintfn "\x1b[31m%s\x1b[0m" msg
          | Ok sessions ->
            match resolveSessionId sessions sid with
            | Some resolvedId ->
              match! switchSession client baseUrl resolvedId with
              | Ok msg -> printfn "\x1b[33m%s\x1b[0m" msg
              | Error msg -> eprintfn "\x1b[31m%s\x1b[0m" msg
            | None ->
              eprintfn "\x1b[31mNo session matching '%s'. Use #sessions to list.\x1b[0m" sid
      | StopSession sid ->
        if String.IsNullOrEmpty(sid) then
          // Interactive picker
          match! fetchSessions client baseUrl with
          | Error msg -> eprintfn "\x1b[31m%s\x1b[0m" msg
          | Ok [] -> printfn "No sessions running."
          | Ok sessions ->
            match pickSession sessions "Stop session:" with
            | Some resolvedId ->
              match! stopSession client baseUrl resolvedId with
              | Ok msg -> printfn "\x1b[33m%s\x1b[0m" msg
              | Error msg -> eprintfn "\x1b[31m%s\x1b[0m" msg
            | None -> printfn "\x1b[90mCancelled.\x1b[0m"
        else
          match! fetchSessions client baseUrl with
          | Error _ ->
            match! stopSession client baseUrl sid with
            | Ok msg -> printfn "\x1b[33m%s\x1b[0m" msg
            | Error msg -> eprintfn "\x1b[31m%s\x1b[0m" msg
          | Ok sessions ->
            match resolveSessionId sessions sid with
            | Some resolvedId ->
              match! stopSession client baseUrl resolvedId with
              | Ok msg -> printfn "\x1b[33m%s\x1b[0m" msg
              | Error msg -> eprintfn "\x1b[31m%s\x1b[0m" msg
            | None ->
              eprintfn "\x1b[31mNo session matching '%s'. Use #sessions to list.\x1b[0m" sid
      | CreateSession dirOpt ->
        let workDir = dirOpt |> Option.defaultValue Environment.CurrentDirectory
        printfn "\x1b[33m⏳ Creating session in %s...\x1b[0m" workDir
        match! createSession client baseUrl workDir [] with
        | Ok msg -> printfn "\x1b[33m%s\x1b[0m" msg
        | Error msg -> eprintfn "\x1b[31m%s\x1b[0m" msg
      | ShowDiagnostics ->
        match! getDiagnostics client baseUrl with
        | Ok msg -> printfn "%s" msg
        | Error msg -> eprintfn "\x1b[31m%s\x1b[0m" msg
      | Reset ->
        match! resetSession client baseUrl with
        | Ok msg -> printfn "\x1b[33m%s\x1b[0m" msg
        | Error msg -> eprintfn "\x1b[31m%s\x1b[0m" msg
      | HardReset ->
        printfn "\x1b[33mHard resetting (with rebuild)...\x1b[0m"
        match! hardResetSession client baseUrl with
        | Ok msg -> printfn "\x1b[33m%s\x1b[0m" msg
        | Error msg -> eprintfn "\x1b[31m%s\x1b[0m" msg
      | ShowStatus ->
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
      | EvalCode code ->
        appendHistory code
        let sw = System.Diagnostics.Stopwatch.StartNew()
        match! evalCode client baseUrl code with
        | Ok result ->
          sw.Stop()
          evalCount <- evalCount + 1
          if not (String.IsNullOrWhiteSpace result) then
            printfn "%s" result
          printfn "\x1b[90m(%dms)\x1b[0m" sw.ElapsedMilliseconds
        | Error msg ->
          sw.Stop()
          evalCount <- evalCount + 1
          eprintfn "\x1b[31m%s\x1b[0m" msg
      printfn ""

  printfn "Disconnected."
  return 0
}
