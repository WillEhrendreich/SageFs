/// The VS Code lemmings' oracles. The harness runs them OUTSIDE the sandbox, after the
/// lemming is done and BEFORE its sessions are cleaned up, and never trusts what the
/// model said: the evidence is the shared daemon's own session list, the status bar of
/// the real window, files on disk, and a test run the harness did itself.
/// `LemDrive oracle <task> --run-dir D [--port N]` exits 0 when every check passes and
/// prints one line per check either way.
module LemDrive.Oracle

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open LemDrive.Snapshot

/// The shared dev daemon's MCP port.
[<Literal>]
let DefaultDaemonPort = 37749

/// How long one daemon read may take.
[<Literal>]
let DaemonReadSeconds = 10

/// How many recent evals the history check reads.
[<Literal>]
let HistoryDepth = 30

/// The fewest "SageFs:" command titles an answer must name to count as having found the
/// extension's commands.
[<Literal>]
let FewestCommandsNamed = 5

type LemTask =
  | UiEval
  | UiEditReeval
  | UiLiveTests
  | UiHotReload
  | UiFindHelp

module LemTask =
  let all : LemTask list = [ UiEval; UiEditReeval; UiLiveTests; UiHotReload; UiFindHelp ]

  let name (t: LemTask) : string =
    match t with
    | UiEval -> "ui-eval"
    | UiEditReeval -> "ui-edit-reeval"
    | UiLiveTests -> "ui-live-tests"
    | UiHotReload -> "ui-hot-reload"
    | UiFindHelp -> "ui-find-help"

  let tryParse (text: string) : Result<LemTask, string> =
    match all |> List.tryFind (fun t -> name t = text) with
    | Some t -> Ok t
    | None -> Result.Error(sprintf "unknown task '%s' (known: %s)" text (String.Join(", ", all |> List.map name)))

/// One thing the harness checks.
type Check =
  | SessionEvaled of minEvals: int
  | WorkflowIs of pattern: string
  | StatusBarShows of pattern: string
  | FixtureTestsPass
  | AnswerNamesCommands of file: string
  /// The daemon's own eval history for the run's session (code and result) matches.
  | EvalHistoryMatches of pattern: string

module Check =
  let describe (c: Check) : string =
    match c with
    | SessionEvaled n -> sprintf "a session under the run directory evaluated at least %d time(s)" n
    | WorkflowIs p -> sprintf "a session under the run directory has a workflow matching /%s/" p
    | StatusBarShows p -> sprintf "the status bar shows /%s/" p
    | FixtureTestsPass -> "the project's test suite passes (the harness ran it)"
    | AnswerNamesCommands f -> sprintf "%s names at least %d SageFs commands from the extension's package.json" f FewestCommandsNamed
    | EvalHistoryMatches p -> sprintf "the daemon's eval history for the run's session matches /%s/" p

/// What parseSeed (Some "7") prints in the history: the code, then "int option = Some 7".
[<Literal>]
let EvalOfParseSeedSeven = @"(?s)parseSeed.*7.*Some 7"

/// After the fix, parseSeed on a negative string evaluates to None.
[<Literal>]
let EvalOfParseSeedNegative = @"(?s)parseSeed.*-.*None"

let checksFor (t: LemTask) : Check list =
  match t with
  | UiEval -> [ SessionEvaled 1; EvalHistoryMatches EvalOfParseSeedSeven ]
  | UiEditReeval -> [ SessionEvaled 1; EvalHistoryMatches EvalOfParseSeedNegative; FixtureTestsPass ]
  | UiLiveTests -> [ SessionEvaled 0; StatusBarShows @"\d+/\d+" ]
  | UiHotReload -> [ WorkflowIs "(?i)hot" ]
  | UiFindHelp -> [ AnswerNamesCommands "ANSWER.md" ]

/// What one check found.
type Verdict =
  | Met of evidence: string
  | NotMet of why: string

module Verdict =
  let isMet (v: Verdict) : bool =
    match v with
    | Met _ -> true
    | NotMet _ -> false

  let text (v: Verdict) : string =
    match v with
    | Met e -> sprintf "PASS  %s" e
    | NotMet w -> sprintf "FAIL  %s" w

type DaemonSession = Daemon.DaemonSession

/// A session belongs to the lemming when its working directory (or a project it loaded) is the run's workspace or under it.
let underWorkspace (workspace: string) (s: DaemonSession) : bool = Daemon.belongsTo workspace s

let private readSessions (port: int) : Task<Result<DaemonSession list, string>> =
  task { return Daemon.sessions port }

/// The `SageFs:` titles in the extension's package.json.
let commandTitles (packageJson: string) : string list =
  use doc = JsonDocument.Parse packageJson
  [ for c in doc.RootElement.GetProperty("contributes").GetProperty("commands").EnumerateArray() do
      let t = c.GetProperty("title").GetString() |> Option.ofObj |> Option.defaultValue ""
      if t.StartsWith "SageFs:" then t ]

/// How many distinct command titles an answer names (title text after "SageFs:", case-insensitive).
let titlesNamed (titles: string list) (answer: string) : string list =
  let lower = answer.ToLowerInvariant()
  titles
  |> List.filter (fun t ->
    let bare = t.Substring("SageFs:".Length).Trim().ToLowerInvariant()
    bare.Length > 3 && lower.Contains bare)
  |> List.distinct

/// The workspace's test run, as the runner recorded it: out/tests.exit holds the exit code.
let private testRun (runDir: string) : Verdict =
  let path = Path.Combine(runDir, "out", "tests.exit")
  match File.Exists path with
  | false -> NotMet "the harness did not run the tests (no out/tests.exit)"
  | true ->
    match File.ReadAllText(path).Trim() with
    | "0" -> Met "the harness ran the test suite and it passed"
    | other -> NotMet(sprintf "the harness ran the test suite and it exited %s (see out/tests.out)" other)

let private statusBar (port: int) : Task<Result<string list, string>> =
  task {
    match Environment.GetEnvironmentVariable(Calls.DriverEnv.name Calls.CdpPort) with
    | null
    | "" -> return Result.Error "LEM_CDP_PORT is not set, so the window cannot be read"
    | p ->
      let! conn = Cdp.connect (int p)
      match conn with
      | Result.Error e -> return Result.Error e
      | Ok c ->
        try
          let! f = Cdp.facts c
          return Ok f.StatusBar
        finally
          c.Playwright.Dispose()
  }

let private runCheck (runDir: string) (workspace: string) (port: int) (sessions: Result<DaemonSession list, string>) (check: Check) : Task<Verdict> =
  task {
    match check with
    | FixtureTestsPass -> return testRun runDir
    | SessionEvaled n ->
      return
        match sessions with
        | Result.Error e -> NotMet e
        | Ok all ->
          match all |> List.filter (underWorkspace workspace) with
          | [] -> NotMet(sprintf "no session under %s on the shared daemon" workspace)
          | mine ->
            let best = mine |> List.maxBy (fun s -> s.EvalCount)
            match best.EvalCount >= n with
            | true -> Met(sprintf "session %s under the run directory has %d eval(s)" best.Id best.EvalCount)
            | false -> NotMet(sprintf "session %s has %d eval(s), wanted %d" best.Id best.EvalCount n)
    | EvalHistoryMatches pattern ->
      // The history tool routes by working directory, which must then name exactly one session.
      return
        match Daemon.recentEvents port workspace HistoryDepth with
        | Result.Error e -> NotMet(sprintf "could not read the eval history for %s: %s" workspace e)
        | Ok history ->
          match Regex.IsMatch(history, pattern) with
          | true -> Met "the eval history shows the evaluation and its result"
          | false -> NotMet(sprintf "no eval in the run's history matches /%s/; history: %s" pattern (history.Replace('\n', ' ')))
    | WorkflowIs pattern ->
      return
        match sessions with
        | Result.Error e -> NotMet e
        | Ok all ->
          match all |> List.filter (underWorkspace workspace) |> List.tryFind (fun s -> Regex.IsMatch(s.Workflow, pattern)) with
          | Some s -> Met(sprintf "session %s workflow is \"%s\"" s.Id s.Workflow)
          | None -> NotMet(sprintf "no session under %s has a workflow matching /%s/" workspace pattern)
    | StatusBarShows pattern ->
      let! bar = statusBar port
      return
        match bar with
        | Result.Error e -> NotMet e
        | Ok items ->
          match items |> List.tryFind (fun i -> Regex.IsMatch(i, pattern)) with
          | Some i -> Met(sprintf "status bar item \"%s\"" i)
          | None -> NotMet(sprintf "no status bar item matches /%s/; items: %s" pattern (String.Join(" | ", items)))
    | AnswerNamesCommands file ->
      let answer = Path.Combine(workspace, file)
      let packageJson = Path.Combine(AppContext.BaseDirectory, "package.json")
      return
        match File.Exists answer, File.Exists packageJson with
        | false, _ -> NotMet(sprintf "%s was not written" file)
        | _, false -> NotMet(sprintf "the harness has no package.json to compare with (%s)" packageJson)
        | true, true ->
          let named = titlesNamed (commandTitles (File.ReadAllText packageJson)) (File.ReadAllText answer)
          match List.length named >= FewestCommandsNamed with
          | true -> Met(sprintf "%s names %d real commands: %s" file (List.length named) (String.Join("; ", named |> List.truncate 8)))
          | false -> NotMet(sprintf "%s names %d real command(s), wanted %d" file (List.length named) FewestCommandsNamed)
  }

/// Runs every check for a task and writes one line each to stdout.
let run (task': LemTask) (runDir: string) (port: int) : Task<int> =
  task {
    let workspace = Path.Combine(runDir, "w")
    let! sessions = readSessions port
    let results = ResizeArray<Check * Verdict>()
    for check in checksFor task' do
      let! v = runCheck runDir workspace port sessions check
      results.Add((check, v))
    for (check, v) in results do
      printfn "%s  [%s]" (Verdict.text v) (Check.describe check)
    return (match results |> Seq.forall (snd >> Verdict.isMet) with | true -> 0 | false -> 1)
  }
