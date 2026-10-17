/// A reader of a child process's stdout or stderr must not live on the thread pool.
///
/// `Task.Run(fun () -> proc.StandardOutput.ReadLine() ...)` parks a pool thread for the whole life of the process.
/// Two per live session, scaling with session count and bounded by nothing, is what locked the daemon up on
/// 2026-09-22 (`/health` timing out for a minute while Kestrel logged a 60 second heartbeat). The fix was
/// `WorkerSpawn.runOnDedicatedThread`, and nothing stopped the pattern from coming back.
///
/// This pins where it still is. The subject is code structure, so the test reads the source: for every
/// `Task.Run` / `Task.Factory.StartNew` in the product projects it takes the body and counts the ones that
/// mention a Process `StandardOutput`/`StandardError` and call a `Read...` on something. Each file has a budget
/// (`RatchetBudgets.blockingReaders`), the budgets only go DOWN, a file that is not listed has a budget of zero,
/// and a budget above the file's real count is stale.
///
/// Honest limits: a reader that reaches a stream through an alias bound OUTSIDE the `Task.Run` body, or through a
/// named function the body calls, is not seen. No thread-pool occupancy is measured here.
module SageFs.Tests.BlockingReaderRatchetTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

let private repoRoot = RepoPaths.repoPathFull [||]

let private spawnPattern = Regex(@"Task\.(Run|Factory\.StartNew)\b", RegexOptions.Compiled)
let private streamPattern = Regex(@"Standard(Output|Error)\b", RegexOptions.Compiled)
let private readPattern = Regex(@"\bRead(Line|ToEnd|Block)?(Async)?\s*\(", RegexOptions.Compiled)

/// `//` and `///` comments carry the pattern's name all over the product ("the Task.Run+ReadLine pattern");
/// only code counts. A `//` inside `http://` is preceded by `:`, not whitespace, so it stays.
let private withoutComments (text: string) : string =
  Regex.Replace(text, @"(^|\s)//[^\n]*", "$1", RegexOptions.Multiline)

/// The text from `start` to the `)` that closes the `(` at `start`.
let private parenthesized (text: string) (start: int) : string =
  let rec walk (i: int) (depth: int) : int =
    match i >= text.Length with
    | true -> text.Length
    | false ->
      match text[i] with
      | '(' -> walk (i + 1) (depth + 1)
      | ')' when depth = 1 -> i + 1
      | ')' -> walk (i + 1) (depth - 1)
      | _ -> walk (i + 1) depth
  text.Substring(start, walk start 0 - start)

/// The rest of the line from `start`, plus every following line indented deeper than the line that holds `start`
/// (the `Task.Run <| fun () ->` and `Task.Run(fun () ->` newline forms).
let private indented (text: string) (start: int) : string =
  let lineStart = text.LastIndexOf('\n', max 0 (start - 1)) + 1
  let indentOf (line: string) = line.Length - line.TrimStart(' ').Length
  let lines = text.Substring(lineStart).Split('\n')
  let baseIndent = indentOf lines[0]
  let tail =
    lines
    |> Array.skip 1
    |> Array.takeWhile (fun l -> System.String.IsNullOrWhiteSpace l || indentOf l > baseIndent)
  System.String.Join("\n", Array.append [| lines[0].Substring(start - lineStart) |] tail)

let private bodyAfter (text: string) (matchEnd: int) : string =
  let mutable i = matchEnd
  while i < text.Length && text[i] = ' ' do
    i <- i + 1
  match i < text.Length && text[i] = '(' with
  | true -> parenthesized text i
  | false -> indented text matchEnd

/// How many `Task.Run` bodies in `text` read a Process stream.
let countReaders (text: string) : int =
  let code = withoutComments text
  spawnPattern.Matches code
  |> Seq.cast<Match>
  |> Seq.filter (fun m ->
    let body = bodyAfter code (m.Index + m.Length)
    streamPattern.IsMatch body && readPattern.IsMatch body)
  |> Seq.length

/// What each file may still do. Ratchet down, never up.
let private budgets : (string * int) list = RatchetBudgets.blockingReaders

let private actual : (string * int) list =
  [ for project in [ "SageFs"; "SageFs.Core"; "SageFs.Host"; "SageFs.FsiHost" ] do
      let dir = Path.Combine(repoRoot, project)
      match Directory.Exists dir with
      | false -> ()
      | true ->
        for file in Directory.EnumerateFiles(dir, "*.fs", SearchOption.AllDirectories) do
          let relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/')
          match relative.Contains "/obj/" || relative.Contains "/bin/" with
          | true -> ()
          | false ->
            match countReaders (File.ReadAllText file) with
            | 0 -> ()
            | n -> yield relative, n ]

/// The budget table as data, so `--ratchets --tighten` can lower it.
let private budgetTable =
  TestInfrastructure.Ratchet.table
    { Name = "blocking process readers"
      SourceFile = RatchetBudgets.sourceFile
      Budgets = budgets
      Actual = fun () -> actual }

[<Tests>]
let tests =
  testList "Blocking process readers" [

    testCase "WHY — no file parks a thread-pool thread on a process stream beyond its budget, because that pinned two pool threads per session and locked the daemon up" <| fun _ ->
      let allowed = Map.ofList budgets
      let over =
        actual
        |> List.choose (fun (file, count) ->
          let budget = Map.tryFind file allowed |> Option.defaultValue 0
          match count > budget with
          | true -> Some (sprintf "%s has %d, budget %d" file count budget)
          | false -> None)
      over |> Expect.equal "every file is within its budget (read a process stream on WorkerSpawn.runOnDedicatedThread, never in Task.Run)" []

    testCase "WHY — a blocking reader budget above the file's real count is stale, so room that was already won back cannot be spent again" <| fun _ ->
      let counts = Map.ofList actual
      let stale =
        budgets
        |> List.choose (fun (file, budget) ->
          let count = Map.tryFind file counts |> Option.defaultValue 0
          match count < budget with
          | true -> Some (sprintf "%s has %d but its budget is %d: lower it with --ratchets --tighten" file count budget)
          | false -> None)
      stale |> Expect.isEmpty "every budget equals the file's current count"

    testCase "WHY — the scan sees a Task.Run that reads a process stream in each shape the product has used, so the ratchet cannot go blind" <| fun _ ->
      let shapes =
        [ "paren ReadLine", "let t = Task.Run(fun () -> proc.StandardOutput.ReadLine())"
          "paren ReadToEnd", "let t = Task.Run (fun () -> proc.StandardError.ReadToEnd())"
          "multi-line loop",
          "let pump =\n  Task.Run(fun () ->\n    let mutable line = proc.StandardOutput.ReadLine()\n    while not (isNull line) do\n      line <- proc.StandardOutput.ReadLine())\n"
          "pipe form", "let t =\n  Task.Run <| fun () ->\n    proc.StandardError.ReadToEnd()\nlet other = 1\n"
          "StartNew", "let t = Task.Factory.StartNew(fun () -> proc.StandardOutput.ReadToEnd())"
          "async read loop", "Task.Run(fun () -> proc.StandardOutput.ReadLineAsync().Result)" ]
      let missed = shapes |> List.filter (fun (_, text) -> countReaders text <> 1) |> List.map fst
      missed |> Expect.isEmpty "every shape counts as exactly one blocking reader"

    testCase "WHY — the scan leaves alone what is not a pool-pinned process reader, so a budget of zero is reachable" <| fun _ ->
      let innocent =
        [ "dedicated thread", "WorkerSpawn.runOnDedicatedThread \"pump\" (fun () -> proc.StandardOutput.ReadLine() |> ignore)"
          "comment only", "// the Task.Run(fun () -> proc.StandardOutput.ReadLine()) pattern pinned the pool"
          "doc comment", "/// Task.Run + proc.StandardError.ReadToEnd() is what locked the daemon\nlet x = 1"
          "Task.Run, no stream", "let t = Task.Run(fun () -> File.ReadAllText path)"
          "stream, no Task.Run", "let! text = proc.StandardOutput.ReadToEndAsync() |> Async.AwaitTask"
          "Task.Run beside a stream read", "let t = Task.Run(fun () -> compute ())\nlet s = proc.StandardOutput.ReadToEnd()" ]
      let wrong = innocent |> List.filter (fun (_, text) -> countReaders text <> 0) |> List.map fst
      wrong |> Expect.isEmpty "none of these counts"
  ]
  |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant
