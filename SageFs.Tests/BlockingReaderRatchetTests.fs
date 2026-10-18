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
/// A `Task.Run` can also name the reader instead of spelling it out: `Task.Run readLoop`, `Task.Run(readLoop)`,
/// `Task.Run(fun () -> readLoop ())`, `Task.Factory.StartNew(readLoop, ...)`. FsiHostClient's reply loop was exactly
/// that, a named function blocked in `reader.ReadLine()` on the TCP stream for the life of the host, and the first
/// version of this scan walked past it. So when the spawned name is defined in the SAME file and that definition's
/// body blocks in a `.ReadLine(` / `.ReadToEnd(` / `.Read(` (or waits on a `ReadLineAsync` with `.Result` / `.Wait` /
/// `.GetAwaiter`), the spawn counts.
///
/// Honest limits: a reader that reaches a stream through an alias bound OUTSIDE the `Task.Run` body, a name defined
/// in another file, a name that is a local shadowing a reader of the same name elsewhere in the file, or a spawn that
/// calls the reader as the second statement of a lambda is not seen. An awaited `ReadLineAsync` does not pin a
/// thread, so it is not counted. No thread-pool occupancy is measured here.
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

/// The function a spawn names instead of spelling out: `readLoop` in ` readLoop |> ignore`, `(readLoop)` and
/// `(fun () -> readLoop ())`. A body that starts with anything else (`<| fun`, `let`, a lambda that does work)
/// yields a word that has no definition in the file, which `namedReader` then finds nothing for.
let private namedTarget =
  Regex(@"^\s*\(?\s*(?:fun\s*(?:\(\s*\)|_)\s*->\s*)?([A-Za-z_][A-Za-z0-9_']*)", RegexOptions.Compiled)

/// A read that blocks its thread: `.ReadLine(`, `.ReadToEnd(`, `.Read(`, or a `...Async(...)` read waited on in place.
let private blockingRead =
  Regex(@"\.Read(Line|ToEnd|Block)?\s*\(|\.Read(Line|ToEnd|Block)?Async\s*\([^\n]*?\)\s*(\.Result\b|\.Wait\s*\(|\.GetAwaiter)", RegexOptions.Compiled)

/// The text of every `let [rec] name` definition in `code`: the rest of its line and the lines indented under it.
let private definitionBodies (code: string) (name: string) : string list =
  Regex.Matches(code, @"^[ ]*let\s+(?:rec\s+|inline\s+)*" + Regex.Escape name + @"\b", RegexOptions.Multiline)
  |> Seq.cast<Match>
  |> Seq.map (fun definition -> indented code (definition.Index + definition.Length))
  |> List.ofSeq

/// The spawn's body names a function that this file defines, and the definition blocks in a read.
let private namedReader (code: string) (body: string) : bool =
  match namedTarget.Match body with
  | m when m.Success -> definitionBodies code m.Groups[1].Value |> List.exists blockingRead.IsMatch
  | _ -> false

/// How many `Task.Run` bodies in `text` read a Process stream, spelled out or through a function the file defines.
let countReaders (text: string) : int =
  let code = withoutComments text
  spawnPattern.Matches code
  |> Seq.cast<Match>
  |> Seq.filter (fun m ->
    let body = bodyAfter code (m.Index + m.Length)
    (streamPattern.IsMatch body && readPattern.IsMatch body) || namedReader code body)
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

    testCase "WHY — the scan sees a Task.Run of a NAMED function whose body blocks in a read, because FsiHostClient's reply loop pinned a pool thread per host and the body-only scan walked past it" <| fun _ ->
      let shapes =
        [ "pipe form named", "let readLoop () =\n  let mutable line = reader.ReadLine()\n  while not (isNull line) do\n    line <- reader.ReadLine()\n\ndo Task.Run readLoop |> ignore\n"
          "lambda calls named", "let readLoop () =\n  reader.ReadToEnd() |> ignore\n\nlet t = Task.Run(fun () -> readLoop ())\n"
          "paren named", "let pump () =\n  let n = stream.Read(buffer, 0, buffer.Length)\n  ()\n\nlet t = Task.Run(pump)\n"
          "StartNew named", "let readLoop () =\n  reader.ReadLine() |> ignore\n\nlet t = Task.Factory.StartNew(readLoop, TaskCreationOptions.LongRunning)\n"
          "blocking async read", "let drain () =\n  let mutable line = reader.ReadLineAsync().Result\n  ()\n\nlet t = Task.Run drain\n"
          "rec, in a type", "type S(reader: StreamReader) =\n  let rec readLoop () =\n    match reader.ReadLine() with\n    | null -> ()\n    | _ -> readLoop ()\n\n  do Task.Run readLoop |> ignore\n" ]
      let missed = shapes |> List.filter (fun (_, text) -> countReaders text <> 1) |> List.map fst
      missed |> Expect.isEmpty "every named-function shape counts as exactly one blocking reader"

    testCase "WHY — the scan leaves a named function alone unless it is spawned on the pool AND blocks in a read, so moving a reader to a dedicated thread is what takes it off the count" <| fun _ ->
      let innocent =
        [ "named on a dedicated thread", "let readLoop () =\n  reader.ReadLine() |> ignore\n\nWorkerSpawn.runOnDedicatedThread \"reply\" readLoop |> ignore\n"
          "named function does not read", "let tick () =\n  counter <- counter + 1\n\nlet t = Task.Run tick\n"
          "unrelated name beside a reader", "let readLoop () =\n  reader.ReadLine() |> ignore\n\nlet compute () = 42\nlet t = Task.Run(fun () -> compute ())\n"
          "name with no definition here", "let t = Task.Run(fun () -> elsewhere ())\n"
          "reader only named in a comment", "let tick () = ()\n// readLoop () = reader.ReadLine()\nlet t = Task.Run tick\n"
          "similar name", "let readLoopFast () =\n  reader.ReadLine() |> ignore\n\nlet readLoop () = 1\nlet t = Task.Run readLoop\n"
          "File.ReadAllText", "let load () =\n  File.ReadAllText path |> ignore\n\nlet t = Task.Run load\n"
          "awaited async read", "let pump () =\n  task { let! line = reader.ReadLineAsync() in return line }\n\nlet t = Task.Run pump\n" ]
      let wrong = innocent |> List.filter (fun (_, text) -> countReaders text <> 0) |> List.map fst
      wrong |> Expect.isEmpty "none of these counts"
  ]
  |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant
