/// The harness's own commands on the VS Code window, run OUTSIDE the sandbox by the
/// runner (`LemDrive host <verb> ...`). The lemming never sees these.
///   host events --port N --out FILE        record the daemon's eval output for the run
///   host ready --cdp-port P [--wait S]     wait until the SageFs extension is up in the window
///   host snapshot --cdp-port P --out FILE  write the window as text (the end-of-run evidence)
///   host sessions --port N --out FILE      write the shared daemon's sessions, one line each
///   host session --port N --run-dir D      create the run's own session and wait until it is Ready
///   host bind --cdp-port P --run-dir D     attach the window to it, and prove it from the Sessions view
///   host output --cdp-port P [--pattern R] read the extension's SageFs Output channel
///   host leftovers --run-dir D --out FILE  the run's processes still alive (see Leftovers.fs)
///   host foreign / host events             sessions outside the run that evaluated; the eval stream
module LemDrive.VscHost

open System
open System.IO
open System.Threading.Tasks
open LemDrive.Calls
open LemDrive.Snapshot

/// How long the window may take to come up and activate the extension.
[<Literal>]
let DefaultReadySeconds = 90

/// The extension's four tree views register this long after its status item appears. A
/// container opened earlier shows only the Sessions view, and VS Code remembers the partial
/// list (measured by the Electron journeys: 8 to 10 s). The one bounded settle the harness keeps.
[<Literal>]
let TreeViewRegistrationSettleMs = 10000

/// How often readiness is checked.
[<Literal>]
let ReadyPollMs = 500

let private flags (args: string list) : Map<string, string> =
  args
  |> List.chunkBySize 2
  |> List.choose (function [ k; v ] when k.StartsWith "--" -> Some(k.Substring 2, v) | _ -> None)
  |> Map.ofList

let private intFlag (m: Map<string, string>) (key: string) (fallback: int) : int =
  match Map.tryFind key m |> Option.bind (fun v -> match Int32.TryParse v with | true, n -> Some n | _ -> None) with
  | Some n -> n
  | None -> fallback

/// True once the status bar carries a SageFs item: the extension has activated.
let private extensionUp (f: Facts) : bool =
  f.StatusBar |> List.exists (fun s -> s.Contains "SageFs")

let private hasSageFsActivity (f: Facts) : bool =
  f.ActivityBar |> List.exists (fun t -> t.Label = "SageFs")

let private ready (args: string list) : Task<Outcome> =
  task {
    let m = flags args
    let wait = intFlag m "wait" DefaultReadySeconds
    match Map.tryFind "cdp-port" m |> Option.bind (fun v -> match Int32.TryParse v with | true, n -> Some n | _ -> None) with
    | None -> return BadUsage "host ready needs --cdp-port P"
    | Some port ->
      let started = Diagnostics.Stopwatch.StartNew()
      let mutable last = "VS Code has not answered on CDP yet"
      let mutable outcome : Outcome option = None
      while outcome.IsNone && started.Elapsed.TotalSeconds < float wait do
        let! conn = Cdp.connect port
        match conn with
        | Result.Error e -> last <- e
        | Ok c ->
          try
            try
              let! f = Cdp.facts c
              match extensionUp f && hasSageFsActivity f with
              | true ->
                // The views register after the status item; give them their time.
                do! Task.Delay TreeViewRegistrationSettleMs
                let! settled = Cdp.facts c
                outcome <- Some(Output(render settled))
              | false -> last <- sprintf "the window is up but the SageFs extension has not activated (status bar: %s)" (String.Join(" | ", f.StatusBar))
            with ex ->
              // A read that fails while the window is still loading is another poll, not the end.
              last <- sprintf "reading the window failed: %s" (ex.Message.Split('\n')[0])
          finally
            c.Playwright.Dispose()
        match outcome with
        | Some _ -> ()
        | None -> do! Task.Delay ReadyPollMs
      return
        match outcome with
        | Some o -> o
        | None -> DriveFailed(sprintf "VS Code was not ready in %d s: %s" wait last)
  }

let private snapshot (args: string list) : Task<Outcome> =
  task {
    let m = flags args
    match Map.tryFind "cdp-port" m |> Option.bind (fun v -> match Int32.TryParse v with | true, n -> Some n | _ -> None), Map.tryFind "out" m with
    | Some port, Some out ->
      let! conn = Cdp.connect port
      match conn with
      | Result.Error e -> return DriveFailed e
      | Ok c ->
        try
          let! f = Cdp.facts c
          Directory.CreateDirectory(Path.GetDirectoryName out |> Option.ofObj |> Option.defaultValue ".") |> ignore
          File.WriteAllText(out, render f + "\n")
          return Output(sprintf "wrote %s" out)
        finally
          c.Playwright.Dispose()
    | _ -> return BadUsage "host snapshot needs --cdp-port P --out FILE"
  }

let private sessions (args: string list) : Task<Outcome> =
  task {
    let m = flags args
    let port = intFlag m "port" Daemon.DefaultMcpPort
    match Map.tryFind "out" m with
    | None -> return BadUsage "host sessions needs --out FILE"
    | Some out ->
      match Daemon.sessions port with
      | Result.Error e -> return DriveFailed e
      | Ok all ->
        let lines =
          all
          |> List.map (fun s -> sprintf "%s\t%s\t%d\t%s\t%s" s.Id s.Status s.EvalCount s.Workflow s.WorkingDirectory)
        Directory.CreateDirectory(Path.GetDirectoryName out |> Option.ofObj |> Option.defaultValue ".") |> ignore
        File.WriteAllText(out, String.Join("\n", "id\tstatus\tevals\tworkflow\tworkingDirectory" :: lines) + "\n")
        return Output(sprintf "wrote %d session(s) to %s" all.Length out)
  }

/// How long `host events` listens when no --seconds is given: longer than any run.
[<Literal>]
let DefaultListenSeconds = 3600

/// host events --port N --out FILE [--seconds S]
/// Listens to the daemon's /events stream (the stream the dashboard uses) and writes one line
/// per `eval_diff` event: when it was heard, which session, and the output lines it added.
/// Run in the background by the runner for the whole trial and stopped by exact pid.
let private events (args: string list) : Task<Outcome> =
  task {
    let m = flags args
    let port = intFlag m "port" Daemon.DefaultMcpPort
    let seconds = intFlag m "seconds" DefaultListenSeconds
    match Map.tryFind "out" m with
    | None -> return BadUsage "host events needs --out FILE"
    | Some out ->
      let mutable count = 0
      try
        use client = new Net.Http.HttpClient(Timeout = Threading.Timeout.InfiniteTimeSpan)
        use cts = new Threading.CancellationTokenSource(TimeSpan.FromSeconds(float seconds))
        let! resp = client.GetAsync(sprintf "http://localhost:%d/events" port, Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token)
        use resp = resp
        let! stream = resp.Content.ReadAsStreamAsync cts.Token
        use reader = new StreamReader(stream)
        use writer = new StreamWriter(out, false, Text.UTF8Encoding(false))
        writer.AutoFlush <- true
        let mutable current = ""
        let mutable reading = true
        while reading do
          let! line = reader.ReadLineAsync cts.Token
          match line with
          | null -> reading <- false
          | l when l.StartsWith "event:" -> current <- l.Substring(6).Trim()
          | l when l.StartsWith "data:" && current = Daemon.EvalDiffEvent ->
            match Daemon.evalOutputOfEventData (Timeline.nowMs ()) (l.Substring(5).Trim()) with
            | Some e ->
              writer.WriteLine(Daemon.toRecordedLine e)
              count <- count + 1
            | None -> ()
          | _ -> ()
        return Output(sprintf "the event stream ended after %d eval event(s)" count)
      with
      | :? OperationCanceledException -> return Output(sprintf "listened for %d s, %d eval event(s)" seconds count)
      | ex -> return DriveFailed(sprintf "could not listen to the daemon's events on %d: %s" port ex.Message)
  }

/// Reads back what `host sessions` wrote.
let private readSessionsTsv (path: string) : Daemon.DaemonSession list =
  match File.Exists path with
  | false -> []
  | true ->
    File.ReadAllLines path
    |> Array.skip 1
    |> Array.choose (fun line ->
      match line.Split('\t') with
      | [| id; status; evals; workflow; workdir |] ->
        let session : Daemon.DaemonSession =
          { Id = id
            Status = status
            WorkingDirectory = workdir
            ProjectPaths = []
            Workflow = workflow
            EvalCount = (match Int32.TryParse evals with | true, n -> n | _ -> 0)
            Health = ""
            LastReload = "" }
        Some session
      | _ -> None)
    |> List.ofArray

/// host foreign --run-dir D --before F --after F --out F
/// Writes one line per session outside the run directory whose evals rose: id, working directory, rise.
let private foreign (args: string list) : Outcome =
  let m = flags args
  match Map.tryFind "run-dir" m, Map.tryFind "before" m, Map.tryFind "after" m, Map.tryFind "out" m with
  | Some runDir, Some before, Some after, Some out ->
    let deltas = Daemon.foreignEvalDeltas runDir (readSessionsTsv before) (readSessionsTsv after)
    let lines = deltas |> List.map (fun (s, d) -> sprintf "%s\t%s\t%d" s.Id s.WorkingDirectory d)
    File.WriteAllText(out, String.Join("\n", lines) + (match lines with | [] -> "" | _ -> "\n"))
    Output(sprintf "%d session(s) outside the run directory evaluated during the run" deltas.Length)
  | _ -> BadUsage "host foreign needs --run-dir D --before F --after F --out F"

// ---- the run's own session ------------------------------------------------------------

/// How long a new session may take to become Ready: the daemon builds the project first.
[<Literal>]
let DefaultSessionSeconds = 300

/// How long the Sessions view may take to show the run's session as the active one.
[<Literal>]
let DefaultBindSeconds = 45

/// How long a picker may take to list the sessions after its command ran.
[<Literal>]
let PickerListMs = 10000

let private workspaceOf (runDir: string) : string = Path.Combine(runDir, "w")

/// The project files in the workspace, relative to it, without build output.
let private projectFiles (workspace: string) : string list =
  Directory.GetFiles(workspace, "*.fsproj", SearchOption.AllDirectories)
  |> Array.map (fun f -> Path.GetRelativePath(workspace, f).Replace('\\', '/'))
  |> Array.filter (fun f -> not (f.Contains "/bin/" || f.Contains "/obj/"))
  |> Array.sort
  |> List.ofArray

/// host session --port N --run-dir D [--wait S] [--out FILE]
/// Creates the run's own session on the shared daemon (working directory <run>/w, the
/// project the fixture's tests live in) and waits until it is Ready. Writes its id to --out.
/// A session that faults, or does not come up in time, fails the call, and the runner then
/// does not start the window or the lemming.
let private session (args: string list) : Task<Outcome> =
  task {
    let m = flags args
    let port = intFlag m "port" Daemon.DefaultMcpPort
    let wait = intFlag m "wait" DefaultSessionSeconds
    match Map.tryFind "run-dir" m with
    | None -> return BadUsage "host session needs --run-dir D"
    | Some runDir ->
      let workspace = workspaceOf runDir
      let body = Binding.createBody workspace (Binding.projectFor (projectFiles workspace))
      match Daemon.createSession port body with
      | Result.Error e -> return DriveFailed e
      | Ok id ->
        let started = Diagnostics.Stopwatch.StartNew()
        let mutable outcome : Outcome option = None
        let mutable last = "the daemon does not list the session yet"
        while outcome.IsNone && started.Elapsed.TotalSeconds < float wait do
          match Daemon.sessions port with
          | Result.Error e -> last <- e
          | Ok all ->
            match all |> List.tryFind (fun s -> s.Id = id) with
            | None -> last <- sprintf "the daemon does not list session %s yet" id
            | Some s ->
              match Binding.readiness s with
              | Binding.Ready ->
                Map.tryFind "out" m |> Option.iter (fun out -> File.WriteAllText(out, id + "\n"))
                outcome <- Some(Output(sprintf "session %s is Ready under %s" id workspace))
              | Binding.Refused why -> outcome <- Some(DriveFailed why)
              | Binding.Waiting -> last <- sprintf "session %s is %s" id s.Status
          match outcome with
          | Some _ -> ()
          | None -> do! Task.Delay ReadyPollMs
        return
          match outcome with
          | Some o -> o
          | None -> DriveFailed(sprintf "session %s was not Ready in %d s (%s)" id wait last)
  }

/// Opens the SageFs activity-bar container when the side bar shows something else, as a person
/// would by clicking its icon, so the Sessions view is in the page.
let private openSageFsContainer (c: Cdp.Connection) : Task<unit> =
  task {
    let! before = Cdp.facts c
    let showing = before.SideBar |> Option.exists (fun s -> s.Title.ToUpperInvariant().Contains "SAGEFS")
    match showing with
    | true -> ()
    | false ->
      do! c.Page.GetByLabel("SageFs", Microsoft.Playwright.PageGetByLabelOptions(Exact = true)).First.ClickAsync()
      do! Task.Delay 2500
  }

/// The labels of the rows of the Sessions view.
let private sessionRows (f: Facts) : string list =
  f.SideBar
  |> Option.bind (fun s -> s.Panes |> List.tryFind (fun p -> p.Title.ToUpperInvariant().Contains "SESSIONS"))
  |> Option.map (fun p -> p.Rows |> List.map (fun r -> r.Label))
  |> Option.defaultValue []

let private pressKey (c: Cdp.Connection) (key: string) : Task = c.Page.Keyboard.PressAsync key

/// Waits for the picker to hold a row that satisfies `wanted`; the rows it holds then.
let private pickerRows (c: Cdp.Connection) (wanted: string -> bool) (ms: int) : Task<QuickRow list> =
  task {
    let started = Diagnostics.Stopwatch.StartNew()
    let mutable rows : QuickRow list = []
    let mutable met = false
    while not met && started.ElapsedMilliseconds < int64 ms do
      let! f = Cdp.facts c
      rows <- f.Quick |> Option.map (fun q -> q.Rows) |> Option.defaultValue []
      met <- rows |> List.exists (fun r -> wanted r.Label)
      match met with
      | true -> ()
      | false -> do! Task.Delay 250
    return rows
  }

/// Picks the run's session in the extension's own "SageFs: Switch Session" picker: the palette
/// row by its exact title, then the session row by the session id.
let private pickSession (c: Cdp.Connection) (sessionId: string) : Task<Result<unit, string>> =
  task {
    let escape () = pressKey c "Escape"
    do! pressKey c "Control+Shift+P"
    let! palette = pickerRows c (fun _ -> true) 5000
    match palette with
    | [] -> return Result.Error "the command palette did not open"
    | _ ->
      let title = "SageFs: Switch Session"
      do! c.Page.Keyboard.TypeAsync title
      do! Task.Delay 800
      let! typed = Cdp.facts c
      let rows = typed.Quick |> Option.map (fun q -> q.Rows) |> Option.defaultValue []
      match VscCommand.exactRowIndex (rows |> List.map (fun r -> r.Label)) title, rows |> List.tryFindIndex (fun r -> r.Focused) with
      | Some wanted, Some focused when wanted = focused ->
        do! pressKey c "Enter"
        let! sessions = pickerRows c (fun l -> l.Contains sessionId) PickerListMs
        match sessions |> List.exists (fun r -> r.Label.Contains sessionId) with
        | false ->
          do! escape ()
          return Result.Error(sprintf "the session picker did not list %s (it lists: %s)" sessionId (String.Join("; ", sessions |> List.truncate 5 |> List.map (fun r -> r.Label))))
        | true ->
          do! c.Page.Keyboard.TypeAsync sessionId
          do! Task.Delay 800
          let! filtered = Cdp.facts c
          let matching = filtered.Quick |> Option.map (fun q -> q.Rows) |> Option.defaultValue [] |> List.filter (fun r -> r.Label.Contains sessionId)
          match matching with
          | [ only ] when only.Focused ->
            do! pressKey c "Enter"
            return Ok()
          | other ->
            do! escape ()
            return Result.Error(sprintf "expected one picker row for %s, found %d" sessionId (List.length other))
      | _ ->
        do! escape ()
        return Result.Error(sprintf "the palette did not put \"%s\" first (it showed: %s)" title (String.Join("; ", rows |> List.truncate 4 |> List.map (fun r -> r.Label))))
  }

/// host bind --cdp-port P --run-dir D [--port N] [--wait S]
/// Makes the window use the run's own session, and proves it: the active row of the Sessions
/// view must be the run's session's place in the daemon's list. Fails when it cannot, and the
/// runner then does not start the lemming.
let private bind (args: string list) : Task<Outcome> =
  task {
    let m = flags args
    let port = intFlag m "port" Daemon.DefaultMcpPort
    let wait = intFlag m "wait" DefaultBindSeconds
    match Map.tryFind "cdp-port" m |> Option.bind (fun v -> match Int32.TryParse v with | true, n -> Some n | _ -> None), Map.tryFind "run-dir" m with
    | Some cdp, Some runDir ->
      let workspace = workspaceOf runDir
      let mine () =
        match Daemon.sessions port with
        | Result.Error e -> Result.Error e
        | Ok all ->
          match all |> List.filter (Daemon.belongsTo workspace) with
          | s :: _ -> Ok(s.Id, all)
          | [] -> Result.Error(sprintf "the daemon lists no session under %s" workspace)
      let! conn = Cdp.connect cdp
      match conn, mine () with
      | Result.Error e, _ -> return DriveFailed e
      | _, Result.Error e -> return DriveFailed e
      | Ok c, Ok(id, _) ->
        try
          do! openSageFsContainer c
          let check () =
            task {
              let! f = Cdp.facts c
              return
                match mine () with
                | Result.Error e -> Result.Error e
                | Ok(_, all) -> Binding.boundTo all id (sessionRows f)
            }
          let! first = check ()
          let! picked =
            task {
              match first with
              | Ok() -> return Ok "the window was already attached to it"
              | Result.Error _ ->
                let! r = pickSession c id
                return r |> Result.map (fun () -> "picked it in SageFs: Switch Session")
            }
          match picked with
          | Result.Error e -> return DriveFailed(sprintf "the window could not be attached to session %s: %s" id e)
          | Ok how ->
            let started = Diagnostics.Stopwatch.StartNew()
            let mutable verdict : Result<unit, string> = Result.Error "not checked"
            let mutable holds = false
            while not holds && started.Elapsed.TotalSeconds < float wait do
              let! r = check ()
              verdict <- r
              match r with
              | Ok() -> holds <- true
              | Result.Error _ -> do! Task.Delay 1000
            match holds with
            | true -> return Output(sprintf "the window is attached to session %s (%s)" id how)
            | false -> return DriveFailed(sprintf "the window is not attached to session %s after %d s: %s" id wait (match verdict with | Result.Error e -> e | Ok() -> ""))
        finally
          c.Playwright.Dispose()
    | _ -> return BadUsage "host bind needs --cdp-port P --run-dir D"
  }

/// host leftovers --run-dir D [--pids a,b] [--sids a,b] [--wait S] --out FILE
/// Writes the run's processes that are still alive, one line each, after waiting up to S
/// seconds for them to go. Prints how many. Ownership is a pid the runner started, the
/// session it led, or a working directory inside the run (see Leftovers).
let private leftovers (args: string list) : Task<Outcome> =
  task {
    let m = flags args
    let ints (key: string) =
      Map.tryFind key m
      |> Option.map (fun v -> v.Split(',', StringSplitOptions.RemoveEmptyEntries) |> Array.choose (fun s -> match Int32.TryParse(s.Trim()) with | true, n when n > 1 -> Some n | _ -> None) |> List.ofArray)
      |> Option.defaultValue []
    match Map.tryFind "run-dir" m, Map.tryFind "out" m with
    | Some runDir, Some out ->
      let wait = intFlag m "wait" 0
      let pids = ints "pids"
      let sids = ints "sids" @ pids
      let started = Diagnostics.Stopwatch.StartNew()
      let mutable left = Leftovers.now runDir pids sids
      while not (List.isEmpty left) && started.Elapsed.TotalSeconds < float wait do
        do! Task.Delay 1000
        left <- Leftovers.now runDir pids sids
      File.WriteAllText(out, String.Join("\n", left |> List.map Leftovers.describe) + (match left with | [] -> "" | _ -> "\n"))
      return Output(sprintf "%d" (List.length left))
    | _ -> return BadUsage "host leftovers needs --run-dir D --out FILE"
  }

/// host output --cdp-port P [--out FILE] [--pattern REGEX]
/// The lines of the extension's SageFs Output channel, read the way the oracle reads them: from
/// the end, until the pattern shows (or all of the channel when there is none).
let private output (args: string list) : Task<Outcome> =
  task {
    let m = flags args
    match Map.tryFind "cdp-port" m |> Option.bind (fun v -> match Int32.TryParse v with | true, n -> Some n | _ -> None) with
    | None -> return BadUsage "host output needs --cdp-port P"
    | Some port ->
      let! conn = Cdp.connect port
      match conn with
      | Result.Error e -> return DriveFailed e
      | Ok c ->
        try
          let wanted =
            match Map.tryFind "pattern" m with
            | Some pattern -> fun (text: string) -> Text.RegularExpressions.Regex.IsMatch(text, pattern)
            | None -> fun _ -> false
          let! text = OutputChannel.read c.Page wanted
          match text with
          | Result.Error e -> return DriveFailed e
          | Ok t ->
            Map.tryFind "out" m |> Option.iter (fun out -> File.WriteAllText(out, t + "\n"))
            return Output t
        finally
          c.Playwright.Dispose()
  }

/// `LemDrive host <verb> ...`
let cli (args: string list) : Outcome =
  match args with
  | "foreign" :: rest -> foreign rest
  | "events" :: rest -> (events rest).GetAwaiter().GetResult()
  | "ready" :: rest -> (ready rest).GetAwaiter().GetResult()
  | "snapshot" :: rest -> (snapshot rest).GetAwaiter().GetResult()
  | "sessions" :: rest -> (sessions rest).GetAwaiter().GetResult()
  | "session" :: rest -> (session rest).GetAwaiter().GetResult()
  | "bind" :: rest -> (bind rest).GetAwaiter().GetResult()
  | "leftovers" :: rest -> (leftovers rest).GetAwaiter().GetResult()
  | "output" :: rest -> (output rest).GetAwaiter().GetResult()
  | _ -> BadUsage "usage: LemDrive host ready|snapshot|sessions|session|bind|leftovers|events|foreign ..."
