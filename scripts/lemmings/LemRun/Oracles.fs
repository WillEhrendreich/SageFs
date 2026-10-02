/// The oracles: after a lemming has finished and its sessions are cleared, the harness decides what
/// happened, outside the sandbox. The oracle is the verdict; the model's own account never is.
///
/// Each oracle is a case of `Oracle` below, a typed command of this harness, not a program run in the
/// sandbox. Only what must execute code the lemming wrote (a test run) goes into the sandbox, through
/// `Context.Sandboxed`: the same sandbox as the lemming's, with no network and no credentials. That
/// keeps the sandbox surface to exactly one thing, `dotnet run` on the lemming's tests, and keeps
/// every check (the diff, the regexes, the stream) in this process where it can be tested.
///
/// To add a task: write tasks/<name>.md as a user would write it (do not explain SageFs in it), add
/// an `Oracle` case, map the task name to it in `forTask`, and give it a function below. A task with
/// no oracle is scored HarnessError, never a pass. Logic bigger than a few lines goes in a function
/// of its own (or F# next to it, like oracles/RingBufferOracle).
module LemRun.Oracles

open System
open System.IO
open System.Text.RegularExpressions
open LemRun.Workspace
open LemScore.Program

/// The checks a task has. Closed: a task is either one of these or has no oracle.
type Oracle =
  | ParseSeed
  | SagefsSmallFix
  | SagefsReplEval
  | Smoke
  /// A task of the Neovim harness (ui-eval, ui-edit-reeval, ui-live-tests, ui-hot-reload, ui-find-help).
  | NeovimTask of task: string

let private neovimTasks = [ "ui-eval"; "ui-edit-reeval"; "ui-live-tests"; "ui-hot-reload"; "ui-find-help" ]

let forTask (task: string) : Oracle option =
  match task with
  | "parse-seed" -> Some ParseSeed
  | "sagefs-small-fix" -> Some SagefsSmallFix
  | "sagefs-repl-eval" -> Some SagefsReplEval
  | "smoke" -> Some Smoke
  | t when List.contains t neovimTasks -> Some (NeovimTask t)
  | _ -> None

/// What an oracle is given. `Sandboxed` runs a command in the lemming's sandbox, isolated from the
/// network, for at most that many seconds, with extra read-only mounts; a test substitutes its own.
type Context =
  { RunDir: string
    Say: string -> unit
    /// seconds, extra bubblewrap arguments, the command.
    Sandboxed: int -> string list -> string list -> Proc.Captured }

let private workdir (c: Context) = Path.Combine(c.RunDir, "w")

let private ansi = Regex(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled)
let stripAnsi (text: string) : string = ansi.Replace(text, "")

let private passingSummary = Regex(@"[1-9][0-9]* passed, [0-9]+ ignored, 0 failed, 0 errored", RegexOptions.Compiled)

/// `echo "$out" | tail -n N`: the last N lines.
let tailLines (n: int) (text: string) : string list =
  text.TrimEnd('\n').Split('\n') |> Array.toList |> List.rev |> List.truncate n |> List.rev

let private changedOrUntracked (c: Context) : string list =
  let lines (r: Proc.Captured) = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
  lines (hardenedGit (workdir c) [ "diff"; "--name-only"; Env.Names.baselineTag ])
  @ lines (hardenedGit (workdir c) [ "ls-files"; "--others"; "--exclude-standard" ])

/// How a test run in the sandbox ended. A clean exit AND a passing summary line are both needed,
/// because an exit 0 with no summary proves nothing.
type Suite =
  | SuitePassed
  | SuiteExited of int
  | SuiteNoSummary

/// Runs a command that runs the project's own Expecto suite, in the sandbox, and prints the end of
/// its output.
let private runSuite (c: Context) (seconds: int) (tailCount: int) (extraBwrap: string list) (command: string list) : Suite =
  let ran = c.Sandboxed seconds extraBwrap command
  let output = stripAnsi (ran.Stdout + ran.Stderr)
  tailLines tailCount output |> List.iter c.Say
  match ran.ExitCode with
  | 0 when passingSummary.IsMatch output -> SuitePassed
  | 0 -> SuiteNoSummary
  | code -> SuiteExited code

/// parse-seed (fixture demoenv): the project's own Expecto suite passes in the lemming's sandbox
/// without network, and the lemming did not touch the tests.
let parseSeed (c: Context) : int =
  let touched = changedOrUntracked c |> List.filter (fun f -> f.StartsWith "DemoEnv.Tests/")
  match touched with
  | _ :: _ ->
    c.Say "the lemming changed the tests instead of the code:"
    touched |> List.iter c.Say
    1
  | [] ->
    match runSuite c Env.Limits.suiteSeconds 15 [] [ "dotnet"; "run"; "--project"; "DemoEnv.Tests" ] with
    | SuitePassed ->
      c.Say "the DemoEnv suite passes"
      0
    | SuiteExited code ->
      c.Say (sprintf "the test suite exited %d" code)
      1
    | SuiteNoSummary ->
      c.Say "the suite exited 0 but no passing summary line was found, so nothing is proven"
      1

/// sagefs-small-fix (fixture sagefs-copy): the diff touches ONLY SageFs.Core/RingBuffer.fs, and the
/// RingBuffer tests pass against the lemming's file, compiled beside the repo's own tests by
/// oracles/RingBufferOracle outside the sandbox and run inside it without network.
let sagefsSmallFix (c: Context) : int =
  let named = "SageFs.Core/RingBuffer.fs"
  let changed = changedOrUntracked c |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
  match changed with
  | [ only ] when only = named ->
    let art = Path.Combine(c.RunDir, "oracle-art")
    let project = Path.Combine(Env.lemDir, "oracles", "RingBufferOracle", "RingBufferOracle.fsproj")
    let build =
      Proc.run (Proc.spec "dotnet" [ "build"; project; "-c"; "Release"; "-nologo"; "-v"; "quiet"; "--artifacts-path"; art; sprintf "-p:CopyRoot=%s" (workdir c) ]) None
    match build.ExitCode with
    | 0 ->
      let dll =
        match Directory.Exists(Path.Combine(art, "bin")) with
        | true -> Directory.EnumerateFiles(Path.Combine(art, "bin"), "RingBufferOracle.dll", SearchOption.AllDirectories) |> Seq.tryHead
        | false -> None
      match dll with
      | None ->
        c.Say "oracle build produced no dll"
        1
      | Some dll ->
        match runSuite c Env.Limits.smallSuiteSeconds 12 [ "--ro-bind"; art; art ] [ "dotnet"; dll; "--summary" ] with
        | SuitePassed ->
          c.Say (sprintf "RingBuffer tests pass and only %s changed" named)
          0
        | SuiteExited code ->
          c.Say (sprintf "the RingBuffer tests exited %d" code)
          1
        | SuiteNoSummary ->
          c.Say "exit 0 but no passing summary line, so nothing is proven"
          1
    | _ ->
      c.Say "the lemming's RingBuffer.fs does not compile with the tests:"
      tailLines 20 (build.Stdout + build.Stderr) |> List.iter c.Say
      1
  | _ ->
    c.Say (sprintf "expected the diff to touch only %s, but it touches:" named)
    c.Say (match changed with [] -> "(nothing)" | files -> String.Join("\n", files))
    1

/// sagefs-repl-eval (fixture sagefs-copy): ANSWER.md exists with the four right values, and no
/// source file was edited. The expected values come from running SageFs.RingBuffer for real:
/// capacity 4, pushes 10..60 leave [60; 50; 40; 30], tryGet 2 is Some 40, evictedCount is 2, and
/// the session host runs on .NET 10 or 11.
let sagefsReplEval (c: Context) : int =
  let edited =
    (hardenedGit (workdir c) [ "diff"; "--name-only"; Env.Names.baselineTag ]).Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
  match edited with
  | _ :: _ ->
    c.Say "the task said not to change source files, but these changed:"
    edited |> List.iter c.Say
    1
  | [] ->
    let answer = Path.Combine(workdir c, "ANSWER.md")
    let patterns =
      [ @"^\W*toList\W*=.*60\D+50\D+40\D+30\D*$"
        @"^\W*tryGet 2\W*=\W*(Some\W*)?40\W*$"
        @"^\W*evictedCount\W*=\W*2\w?\W*$"
        @"^\W*runtimeMajor\W*=\W*(10|11)\W*$" ]
    match File.Exists answer with
    | false ->
      c.Say (sprintf "refused: %s does not exist" answer)
      2
    | true ->
      match patternsMissing (File.ReadAllText answer) patterns with
      | [] ->
        c.Say (sprintf "all %d expected pattern(s) are in %s" patterns.Length answer)
        0
      | missing ->
        missing |> List.iter (fun m -> c.Say (sprintf "missing: %s" m))
        2

/// smoke: the stream must show a get_daemon_status call that completed. (The task asks for one MCP
/// call and a one-sentence answer; whether the call really happened is a fact of the stream, not of
/// what the model says.)
let smoke (c: Context) : int =
  let events = Path.Combine(c.RunDir, "out", "events.ndjson")
  let completed = Regex("\"type\":\"tool_completed\".*\"toolName\":\"mcp__sagefs__get_daemon_status\"", RegexOptions.Compiled)
  match File.Exists events && File.ReadLines events |> Seq.exists completed.IsMatch with
  | true ->
    c.Say "get_daemon_status completed"
    0
  | false ->
    c.Say "no completed get_daemon_status call in the stream"
    1

/// The Neovim harness's tasks: for tasks that change code, the project's own suite runs first in
/// the lemming's sandbox without network; then the editor evidence is checked by the F# oracle in
/// the driver (`LemDrive nvim oracle`). The driver and the plugin the run used are recorded in
/// out/ui/. Exit 0 only when every check passes.
let neovim (c: Context) (task: string) : int =
  let ui = Path.Combine(c.RunDir, "out", "ui")
  let read (name: string) (fallback: string) =
    match File.Exists(Path.Combine(ui, name)) with
    | true -> File.ReadAllText(Path.Combine(ui, name)).Trim()
    | false -> fallback
  let plugin = read "plugin-dir" (Path.Combine(Env.home, "Work", "sagefs.nvim"))
  let drive = read "drive-dll" (Path.Combine(c.RunDir, "bin", "lemdrive", "LemDrive.dll"))
  // No network: this runs code the lemming's edits changed. The session already restored the project
  // in the workspace, so `dotnet run` has nothing to download.
  let suite =
    match task with
    | "ui-edit-reeval" | "ui-live-tests" ->
      match runSuite c Env.Limits.suiteSeconds 8 [] [ "dotnet"; "run"; "--project"; "DemoEnv.Tests" ] with
      | SuitePassed ->
        c.Say "PASS  the DemoEnv suite passes"
        true
      | SuiteExited code ->
        c.Say (sprintf "FAIL  the DemoEnv suite exited %d" code)
        false
      | SuiteNoSummary ->
        c.Say "FAIL  the suite exited 0 but printed no passing summary, so nothing is proven"
        false
    | _ -> true
  match suite with
  | false -> 1
  | true ->
    let driven =
      Proc.run (Proc.spec "dotnet" [ drive; "nvim"; "oracle"; task; "--run"; c.RunDir; "--plugin"; plugin ] |> Proc.withEnv "DOTNET_NOLOGO" "1") None
    c.Say ((driven.Stdout + driven.Stderr).TrimEnd('\n'))
    driven.ExitCode

let run (oracle: Oracle) (c: Context) : int =
  match oracle with
  | ParseSeed -> parseSeed c
  | SagefsSmallFix -> sagefsSmallFix c
  | SagefsReplEval -> sagefsReplEval c
  | Smoke -> smoke c
  | NeovimTask task -> neovim c task
