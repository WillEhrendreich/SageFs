/// A deadline on work that starts on the thread pool times the pool's queue as well as the work.
///
/// `Task.Run(work).WaitAsync(deadline)` starts its clock when the item is queued. The test process runs thousands of
/// cases at once, some of them block, and a queued item waits until a pool thread is free, so under load the wait
/// can pass the deadline while `work` takes a fraction of a second. `ThreadSandbox`'s "the filtered thread still
/// allocates" case lost two gate runs that way: 30 s on a case that takes 0.16 s alone. Work a test puts a deadline
/// on starts with `TestInfrastructure.DedicatedThread.run`, which needs no pool thread to begin.
///
/// The subject is code structure, so the test reads the source. For every `Task.Run` and `Task.Factory.StartNew` in
/// the test project it counts the ones whose task is handed to a deadline in the same expression: `.WaitAsync(` or
/// a blocking wait with a timeout called on what the spawn returns, a `Task.WhenAny(` on the same line, or an `awaitWithin` on the
/// same line or the three above. A `StartNew` with `LongRunning` makes its own thread and is not counted. The budget is
/// zero for every file.
///
/// Honest limits: a spawned task bound to a name and waited on in a later statement is not seen, and neither is an
/// `Async.StartAsTask`, which also starts on the pool.
module SageFs.Tests.PoolDeadlineRatchetTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

let private repoRoot = RepoPaths.repoPathFull [||]

let private spawnPattern = Regex(@"Task\.(Run|Factory\.StartNew)\b", RegexOptions.Compiled)
/// What follows the spawn's name: optional type arguments (`Task.Run<Task<'a>>(`), then the opening parenthesis.
let private callOpening = Regex(@"^(<[^()\n]*>)?[ ]*\(", RegexOptions.Compiled)
/// A deadline called on the task the spawn returns: `WaitAsync`, or the blocking `Wait` with its own argument list.
let private deadlineChained = Regex(@"^\s*\.(WaitAsync|Wait)\s*\(", RegexOptions.Compiled)
/// A race against a delay, on the line that holds the spawn.
let private racedOnLine = Regex(@"Task\.WhenAny\s*\(", RegexOptions.Compiled)
/// A helper that gives a task a deadline, on the line that holds the spawn or the three above it (its name, a
/// description and the argument often sit on lines of their own).
let private awaitedWithin = Regex(@"\bawaitWithin\b", RegexOptions.Compiled)
let private linesAbove = 3
let private longRunning = Regex(@"\bLongRunning\b", RegexOptions.Compiled)

let private lineStartOf (text: string) (index: int) : int =
  text.LastIndexOf('\n', max 0 (index - 1)) + 1

/// The text from the start of the line `linesAbove` lines above the one that holds `index`, up to `index`.
let private precedingLines (text: string) (index: int) : string =
  let start = List.fold (fun at _ -> lineStartOf text (max 0 (at - 1))) (lineStartOf text index) [ 1 .. linesAbove ]
  text.Substring(start, index - start)

/// How many spawns in `text` carry a deadline that the pool's queue counts against.
let countPoolDeadlines (text: string) : int =
  let code = BlockingReaderRatchetTests.withoutComments text
  spawnPattern.Matches code
  |> Seq.cast<Match>
  |> Seq.filter (fun m ->
    let afterName = m.Index + m.Length
    let opening = callOpening.Match(code.Substring afterName)
    let call =
      match opening.Success with
      | true ->
        let open' = afterName + opening.Length - 1
        let args = BlockingReaderRatchetTests.parenthesized code open'
        Some (open' + args.Length, args)
      | false -> None
    match call with
    | Some (_, args) when longRunning.IsMatch args -> false
    | Some (finish, _) when deadlineChained.IsMatch (code.Substring finish) -> true
    | _ ->
      let sameLine = code.Substring(lineStartOf code m.Index, m.Index - lineStartOf code m.Index)
      racedOnLine.IsMatch sameLine || awaitedWithin.IsMatch (precedingLines code m.Index))
  |> Seq.length

let private testSources : string list =
  let dir = Path.Combine(repoRoot, "SageFs.Tests")
  [ for file in Directory.EnumerateFiles(dir, "*.fs", SearchOption.AllDirectories) do
      let relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/')
      match relative.Contains "/obj/" || relative.Contains "/bin/" || relative.EndsWith "PoolDeadlineRatchetTests.fs" with
      | true -> ()
      | false -> yield relative ]

[<Tests>]
let tests =
  testList "Pool deadlines" [

    testCase "WHY — no test puts a deadline on a Task.Run, because the deadline then times the pool's queue and a starved pool fails a case whose work took milliseconds" <| fun _ ->
      let over =
        testSources
        |> List.choose (fun relative ->
          match countPoolDeadlines (File.ReadAllText(Path.Combine(repoRoot, relative))) with
          | 0 -> None
          | n -> Some (sprintf "%s has %d" relative n))
      over |> Expect.equal "every deadline is on work that starts with TestInfrastructure.DedicatedThread.run" []

    testCase "WHY — the scan sees a deadline on a Task.Run in each shape the suite has used, so the ratchet cannot go blind" <| fun _ ->
      let shapes =
        [ "WaitAsync chained", "let r = Task.Run(fun () -> work ()).WaitAsync(TestTimeouts.patience)"
          "WaitAsync on the next line", "let r =\n  Task.Run(fun () -> work ())\n    .WaitAsync(TestTimeouts.patience)\n"
          // Spelled in two pieces so the blocking-call budget does not count this line as a use.
          "blocking Wait with a timeout", "let ok = Task.Run(fun () -> work ())." + "Wait(TestTimeouts.patience)"
          "StartNew then WaitAsync", "let r = Task.Factory.StartNew(fun () -> work ()).WaitAsync(patience)"
          "raced against a delay", "let! first = Task.WhenAny(Task.Run(fun () -> work ()) :> Task, Task.Delay patience)"
          "handed to awaitWithin, three lines", "let! scanning =\n  awaitWithin\n    \"the scan\"\n    (Task.Run<Task<int>>(fun () -> Task.FromResult 1))\n"
          "awaitWithin on the same line", "let! x = awaitWithin \"it\" (Task.Run(fun () -> 1))" ]
      let missed = shapes |> List.filter (fun (_, text) -> countPoolDeadlines text <> 1) |> List.map fst
      missed |> Expect.isEmpty "every shape counts as exactly one"

    testCase "WHY — the scan leaves alone what has no deadline on the pool's queue, so a budget of zero is reachable" <| fun _ ->
      let innocent =
        [ "no deadline", "let t = Task.Run(fun () -> work ())"
          "awaited without a deadline", "let! r = Task.Run(fun () -> work ())"
          "WhenAll", "let! all = Task.WhenAll [ Task.Run(fun () -> a ()); Task.Run(fun () -> b ()) ]"
          "dedicated thread", "let r = TestInfrastructure.DedicatedThread.run \"x\" work |> fun t -> t.WaitAsync(patience)"
          "LongRunning", "let r = Task.Factory.StartNew((fun () -> work ()), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(patience)"
          "comment only", "// Task.Run(fun () -> work ()).WaitAsync(patience) lost to a starved pool"
          "WaitAsync on something else", "do! gate.Task.WaitAsync(patience)\nlet t = Task.Run(fun () -> work ())\n"
          "WhenAny on another line", "let! first = Task.WhenAny(a, Task.Delay patience)\nlet t = Task.Run(fun () -> work ())\n" ]
      let wrong = innocent |> List.filter (fun (_, text) -> countPoolDeadlines text <> 0) |> List.map fst
      wrong |> Expect.isEmpty "none of these counts"
  ]
  |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant
