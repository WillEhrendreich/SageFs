/// The harness's own commands on the VS Code window, run OUTSIDE the sandbox by the
/// runner (`LemDrive host <verb> ...`). The lemming never sees these.
///   host events --port N --out FILE        record the daemon's eval output for the run
///   host ready --cdp-port P [--wait S]     wait until the SageFs extension is up in the window
///   host snapshot --cdp-port P --out FILE  write the window as text (the end-of-run evidence)
///   host sessions --port N --out FILE      write the shared daemon's sessions, one line each
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
            let! f = Cdp.facts c
            match extensionUp f && hasSageFsActivity f with
            | true ->
              // The views register after the status item; give them their time.
              do! Task.Delay TreeViewRegistrationSettleMs
              let! settled = Cdp.facts c
              outcome <- Some(Output(render settled))
            | false -> last <- sprintf "the window is up but the SageFs extension has not activated (status bar: %s)" (String.Join(" | ", f.StatusBar))
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

/// `LemDrive host <verb> ...`
let cli (args: string list) : Outcome =
  match args with
  | "foreign" :: rest -> foreign rest
  | "events" :: rest -> (events rest).GetAwaiter().GetResult()
  | "ready" :: rest -> (ready rest).GetAwaiter().GetResult()
  | "snapshot" :: rest -> (snapshot rest).GetAwaiter().GetResult()
  | "sessions" :: rest -> (sessions rest).GetAwaiter().GetResult()
  | _ -> BadUsage "usage: LemDrive host ready|snapshot|sessions ..."
