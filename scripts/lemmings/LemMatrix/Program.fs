/// run-matrix: plans and dispatches a list of lemming runs, with a concurrency cap and
/// free-model quota awareness, then prints a table.
///
///   run-matrix <plan-file|-> [--concurrency N] [--max-turns N] [--quota-limit N]
///                            [--root DIR] [--runner PATH] [--dry-run]
///
/// A plan line is `<fixture> <task> <model> [reps]`. Nothing is retried: a run that fails is
/// a result, and a model that hits its provider quota twice gets no more runs.
module LemMatrix.Program

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open LemScore
open LemScore.Types
open LemMatrix.Plan
open LemMatrix.Scheduler
open LemMatrix.Report

module ExitCode =
  let ok = 0
  let someUnscored = 1
  let usage = 2
  let sharedDaemonWontDo = 3

/// How long the matrix lets one run go (the runner itself caps cmdc at 25 minutes and the
/// oracle at 15) before it kills that run's process tree.
let runDeadline = TimeSpan.FromMinutes 45.0

/// run-lemming-cmd exit codes the matrix reacts to (see LemRun's Failure.fs: 3 shared daemon, 4 tool).
let private runnerAbortsMatrix (exitCode: int) : bool =
  exitCode = 3 || exitCode = 4

type Options =
  { PlanPath: string
    Concurrency: int
    MaxTurns: int
    QuotaLimit: int
    Root: string
    /// A program to run instead of LemRun's run-cmd, given the same arguments (a fixture, a task, a model, an id, the turns).
    Runner: string option
    DryRun: bool }

let defaultMaxTurns = 60
let defaultRoot = "/tmp/lem"

let parseOptions (args: string list) : Result<Options, string> =
  let rec go (opts: Options) (rest: string list) =
    let intArg (name: string) (value: string) (apply: int -> Options) =
      match Int32.TryParse value with
      | true, n when n >= 1 -> Ok (apply n)
      | _ -> Error (sprintf "%s needs a whole number of at least 1, not '%s'" name value)
    match rest with
    | [] -> Ok opts
    | "--concurrency" :: v :: tail -> intArg "--concurrency" v (fun n -> { opts with Concurrency = n }) |> Result.bind (fun o -> go o tail)
    | "--max-turns" :: v :: tail -> intArg "--max-turns" v (fun n -> { opts with MaxTurns = n }) |> Result.bind (fun o -> go o tail)
    | "--quota-limit" :: v :: tail -> intArg "--quota-limit" v (fun n -> { opts with QuotaLimit = n }) |> Result.bind (fun o -> go o tail)
    | "--root" :: v :: tail -> go { opts with Root = v } tail
    | "--runner" :: v :: tail -> go { opts with Runner = Some v } tail
    | "--dry-run" :: tail -> go { opts with DryRun = true } tail
    | flag :: _ when flag.StartsWith "--" -> Error (sprintf "unknown option %s" flag)
    | path :: tail when opts.PlanPath = "" -> go { opts with PlanPath = path } tail
    | extra :: _ -> Error (sprintf "unexpected argument '%s'" extra)
  go { PlanPath = ""; Concurrency = defaultConcurrency; MaxTurns = defaultMaxTurns; QuotaLimit = defaultQuotaLimit
       Root = defaultRoot; Runner = None; DryRun = false } args
  |> Result.bind (fun o -> if o.PlanPath = "" then Error "no plan file given (use - for stdin)" else Ok o)

/// How one run is started: a program and the arguments that come before the run's own.
type Runner =
  { File: string
    LeadingArgs: string list
    /// Where the tasks are, for refusing a plan whose task has no file before anything runs.
    TasksDir: string }

/// The runner is LemRun's `run-cmd`, which sits beside this binary; --runner replaces it with a program
/// of the caller's own (its tasks are expected beside it).
let findRunner (given: string option) : Result<Runner, string> =
  match given with
  | Some path ->
    Ok { File = path; LeadingArgs = []; TasksDir = Path.Combine(Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue ".", "tasks") }
  | None ->
    let dll = Path.Combine(AppContext.BaseDirectory, "LemRun.dll")
    match File.Exists dll with
    | true -> Ok { File = "dotnet"; LeadingArgs = [ dll; "run-cmd" ]; TasksDir = Path.Combine(LemRun.Env.lemDir, "tasks") }
    | false -> Error "could not find LemRun.dll beside the binary (pass --runner)"

/// Starts one run and returns its exit code. Output goes to a log under the root, not the terminal.
let private runOne (runner: Runner) (opts: Options) (cell: Cell) : Task<int> =
  task {
    let logDir = Path.Combine(opts.Root, ".matrix-logs")
    Directory.CreateDirectory logDir |> ignore
    let psi = ProcessStartInfo(runner.File, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
    runner.LeadingArgs @ [ cell.Fixture; cell.Task; cell.Model; cell.RunId; string opts.MaxTurns ] |> List.iter psi.ArgumentList.Add
    psi.Environment["LEM_ROOT"] <- opts.Root
    use p = Process.Start psi
    use log = new StreamWriter(Path.Combine(logDir, cell.RunId + ".log"))
    let pump (reader: StreamReader) =
      task {
        let mutable line = reader.ReadLine()
        while not (isNull line) do
          lock log (fun () -> log.WriteLine line; log.Flush())
          line <- reader.ReadLine()
      }
    let pumps = Task.WhenAll [ pump p.StandardOutput; pump p.StandardError ]
    use cts = new Threading.CancellationTokenSource(runDeadline)
    try
      do! p.WaitForExitAsync cts.Token
      let! _ = pumps
      return p.ExitCode
    with :? OperationCanceledException ->
      // The one place the matrix kills anything: the exact process it started, and its children.
      p.Kill true
      return -1
  }

let private checkModels (lines: Line list) : Result<unit, string> =
  match Catalog.liveText () with
  | Error e -> Error e
  | Ok text ->
    let catalog = Catalog.parse text
    models lines
    |> List.map (Catalog.checkFree catalog)
    |> List.tryPick (function Error e -> Some e | Ok _ -> None)
    |> function Some e -> Error e | None -> Ok ()

let private run (opts: Options) (runner: Runner) (cells: Cell list) : Row list =
  let rows = ResizeArray<Row>()
  let mutable state = start cells
  let running = ResizeArray<Task<Cell * int>>()
  let mutable finished = false
  while not finished do
    let step, next' = next opts.QuotaLimit opts.Concurrency state
    state <- next'
    match step with
    | Skip (cell, reason) ->
      printfn "skip  %s: %s" cell.RunId reason
      rows.Add(emptyRow cell.RunId cell.Model cell.Fixture cell.Task (Skipped reason))
    | Dispatch cell ->
      printfn "start %s (%d running)" cell.RunId state.Running
      running.Add(task { let! code = runOne runner opts cell in return cell, code })
    | Wait ->
      let done' = Task.WhenAny(running |> Seq.cast<Task>).GetAwaiter().GetResult()
      let finishedTask = running |> Seq.find (fun t -> obj.ReferenceEquals(t, done'))
      running.Remove finishedTask |> ignore
      let cell, code = finishedTask.Result
      match readSummary opts.Root cell.RunId cell.Model cell.Fixture cell.Task with
      | Ok row ->
        printfn "done  %s: %s" cell.RunId (statusText row.Status)
        rows.Add row
        let outcome = match row.Status with Scored o -> Some o | _ -> None
        state <- Scheduler.finished outcome cell.Model state
      | Error why ->
        let reason = sprintf "runner exit %d; %s" code why
        printfn "done  %s: no summary (%s)" cell.RunId reason
        rows.Add(emptyRow cell.RunId cell.Model cell.Fixture cell.Task (Unscored reason))
        state <- Scheduler.finished None cell.Model state
        if runnerAbortsMatrix code then state <- Scheduler.abort (sprintf "%s exited %d" cell.RunId code) state
    | Finished -> finished <- true
  rows |> Seq.toList

let main' (argv: string[]) : int =
  match parseOptions (List.ofArray argv) with
  | Error e ->
    eprintfn "run-matrix: %s" e
    eprintfn "usage: run-matrix <plan-file|-> [--concurrency N] [--max-turns N] [--quota-limit N] [--root DIR] [--runner PATH] [--dry-run]"
    ExitCode.usage
  | Ok opts ->
    let planText = if opts.PlanPath = "-" then Console.In.ReadToEnd() else File.ReadAllText opts.PlanPath
    let runnerResult = findRunner opts.Runner
    let planned =
      Plan.parse planText
      |> Result.bind (fun lines ->
        runnerResult
        |> Result.bind (fun runner -> Plan.checkTasks runner.TasksDir lines)
        |> Result.bind (fun () -> checkModels lines)
        |> Result.map (fun () -> lines))
    match planned, runnerResult with
    | Error e, _ | _, Error e ->
      eprintfn "run-matrix: refused: %s" e
      ExitCode.usage
    | Ok lines, Ok runner ->
      let cells = Plan.expand opts.Root lines
      printfn "plan: %d run(s), concurrency %d, quota limit %d per model" cells.Length opts.Concurrency opts.QuotaLimit
      match opts.DryRun with
      | true ->
        cells |> List.iter (fun c -> printfn "  %s  %s %s %s" c.RunId c.Fixture c.Task c.Model)
        ExitCode.ok
      | false ->
        match SharedDaemon.health SharedDaemon.defaultMcpPort with
        | Error e ->
          eprintfn "run-matrix: refusing to start, the shared daemon is not answering: %s" e
          ExitCode.sharedDaemonWontDo
        | Ok _ ->
          let rows = run opts runner cells
          printfn ""
          printfn "%s" (renderTable rows)
          printfn ""
          printfn "%s" (tally rows)
          let unscored = rows |> List.exists (fun r -> match r.Status with Unscored _ -> true | _ -> false)
          if unscored then ExitCode.someUnscored else ExitCode.ok

[<EntryPoint>]
let main argv = main' argv
