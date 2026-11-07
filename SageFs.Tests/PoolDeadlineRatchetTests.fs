/// A deadline on work that starts on the thread pool times the pool's queue as well as the work.
///
/// `Task.Run(work).WaitAsync(deadline)` starts its clock when the item is queued. The test process runs thousands of
/// cases at once, some of them block, and a queued item waits until a pool thread is free, so under load the wait
/// can pass the deadline while `work` takes a fraction of a second. `ThreadSandbox`'s "the filtered thread still
/// allocates" case lost two gate runs that way: 30 s on a case that takes 0.16 s alone. The same holds for
/// `Async.StartAsTask`, which queues the first step of the computation, and for a case that proves something stays
/// pending: it passes because a thread was late, not because the system under test held back.
///
/// Work a test puts a deadline (or a window) on starts without the pool: `TestInfrastructure.Started.asTask` runs the
/// first step of an async computation on the calling thread, and `TestInfrastructure.DedicatedThread.run` gives
/// blocking work a thread of its own.
///
/// The subject is code structure, so the test reads the source. A spawn is `Task.Run`, `Task.Factory.StartNew` (not
/// with `LongRunning`, which makes its own thread), `Async.StartAsTask` and `Async.Start`. A spawn is counted when
/// its task is handed to a deadline:
///   - `.WaitAsync`, or a blocking `.Wait` with a timeout, is called on it;
///   - it is piped into, or is an argument of, a deadline helper (any function the sources define whose name says
///     `within` or `await`, or `stopsStarted`, and whose body holds `Task.WhenAny`, `.WaitAsync` or a blocking wait),
///     or sits in a `Task.WhenAny(` on the same line;
///   - it is bound to a name (`let work = ... |> Async.StartAsTask`) and that name appears on a deadline line later in
///     the file, or is returned by a function whose callers do the same (`let postAndReply ... = ... |> Async.StartAsTask`);
///   - nobody awaits it (`|> ignore`, an `_`-name, `Async.Start`) in a file that waits on a deadline or sleeps a
///     window: the signal that case waits on is raised by work that may not have started.
/// The budget is zero for every file. A spawn that cannot move goes in `exceptions` with the reason, never silently.
///
/// Honest limits: a spawn whose task travels through a record field or a tuple to a deadline in another function is
/// not seen, and neither is a deadline reached through a helper this file does not define under the names above.
module SageFs.Tests.PoolDeadlineRatchetTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

let private repoRoot = RepoPaths.repoPathFull [||]

/// How the scan knows a spawn's task carries a deadline. A closed set; `describe` is its one to-text function.
type Reason =
  | ChainedWait
  | PipedIntoDeadline
  | ArgumentOfDeadline
  | BoundAndDeadlined of name: string
  | UnawaitedBesideAWindow

let describe (reason: Reason) : string =
  match reason with
  | ChainedWait -> "a wait with a timeout is called on it"
  | PipedIntoDeadline -> "it is piped into a deadline helper"
  | ArgumentOfDeadline -> "it is the argument of a deadline"
  | BoundAndDeadlined name -> sprintf "it is bound to %s, which a deadline waits on" name
  | UnawaitedBesideAWindow -> "nothing awaits it, in a file that waits on a deadline or a window"

type Site = { Line: int; Spawn: string; Reason: Reason }

/// A spawn a file may keep, with why. Empty: every site moved.
let exceptions : (string * int * string) list = []

let private spawnPattern = @"Async\.StartAsTask|Async\.Start\b|Task\.Run\b|Task\.Factory\.StartNew\b"

/// What gives a task a deadline on its own line: a race against anything, or a wait with a timeout.
let private deadlineLine = Regex(@"Task\.WhenAny\s*\(|\.WaitAsync\b|\.Wait\s*\([A-Za-z]|CancelAfter", RegexOptions.Compiled)
/// A window a case waits out instead of waiting for an event.
let private windowLine = Regex(@"Task\.Delay|Thread\.Sleep|Async\.Sleep", RegexOptions.Compiled)
/// The names a deadline helper goes by: it takes a task and answers when the task does or the bound passes.
let private helperName = Regex(@"[Ww]ithin|^await|^stopsStarted$|^answersSoon$|^firstOutcome$|WithTimeout", RegexOptions.Compiled)
let private definition = Regex(@"^([ ]*)let\s+(?:rec\s+|inline\s+|private\s+)*(?<name>[A-Za-z_][\w']*)(?<args>[^=\n]*)=", RegexOptions.Multiline ||| RegexOptions.Compiled)
let private binding = Regex(@"^[ ]*(let|use)\s+(?:private\s+|mutable\s+|inline\s+)*(?<name>[A-Za-z_][\w']*)\b[^=\n]*=", RegexOptions.Compiled)
let private awaitedStatement = Regex(@"^\s*(let!|use!|do!|match!|return!|and!)", RegexOptions.Compiled)
let private longRunning = Regex(@"\bLongRunning\b", RegexOptions.Compiled)
/// A line that ends by handing the next line's expression on (`yield`, `=`, `->`, an opening bracket): a spawn that
/// starts the line below it is the value being produced, not a statement of its own.
let private handsOn = Regex(@"(\byield|=|->|\(|\[\||\[|<\|)\s*$", RegexOptions.Compiled)
let private linesAbove = 3
let private lookUpFor = 8

let private lineStartOf (text: string) (index: int) : int =
  text.LastIndexOf('\n', max 0 (index - 1)) + 1

let private lineEndOf (text: string) (index: int) : int =
  match text.IndexOf('\n', index) with
  | -1 -> text.Length
  | e -> e

let private lineNumberOf (text: string) (index: int) : int =
  text.Substring(0, index) |> Seq.filter (fun c -> c = '\n') |> Seq.length |> (+) 1

let private indentOf (line: string) : int = line.Length - line.TrimStart(' ').Length

/// The `let` a definition opens and everything indented under it.
let private bodyOf (text: string) (m: Match) : string =
  let indent = m.Groups[1].Length
  let lines = text.Substring(m.Index + m.Length).Split('\n')
  let under = lines |> Array.skip 1 |> Array.takeWhile (fun l -> System.String.IsNullOrWhiteSpace l || indentOf l > indent)
  System.String.Join("\n", Array.append [| lines[0] |] under)

/// What follows a binding's name up to its `=` names parameters unless it is empty or only a type annotation.
let private namesParameters (afterName: string) : bool =
  match afterName.Trim() with
  | "" -> false
  | annotation when annotation.StartsWith ":" -> false
  | _ -> true

let private hasParameters (m: Match) : bool = namesParameters m.Groups["args"].Value

/// Code only: no comments, and no text inside a one-line string literal (a message may name `Async.Start`).
let private strip (text: string) : string =
  Regex.Replace(BlockingReaderRatchetTests.withoutComments text, "\"(?:[^\"\\\\\\n]|\\\\.)*\"", "\"\"")

/// The names of the deadline helpers the given sources define.
let deadlineHelpers (sources: string list) : Set<string> =
  set
    [ for source in sources do
        let code = strip source
        for m in definition.Matches code |> Seq.cast<Match> do
          match hasParameters m && helperName.IsMatch m.Groups["name"].Value && deadlineLine.IsMatch (bodyOf code m) with
          | true -> yield m.Groups["name"].Value
          | false -> () ]

let private helperPattern (helpers: Set<string>) : Regex =
  let names = Set.add "awaitWithin" helpers |> Set.toList |> List.map Regex.Escape
  Regex(@"\b(" + System.String.Join("|", names) + @")\b", RegexOptions.Compiled)

/// The names of functions that return a task a spawn started: a definition with parameters whose last line is a
/// spawn and whose body holds no deadline of its own.
let private starters (code: string) : string list =
  [ for m in definition.Matches code |> Seq.cast<Match> do
      match hasParameters m with
      | false -> ()
      | true ->
        let body = bodyOf code m
        let last = body.Split('\n') |> Array.filter (fun l -> not (System.String.IsNullOrWhiteSpace l)) |> Array.tryLast
        match last with
        | Some l when Regex.IsMatch(l, spawnPattern) && not (deadlineLine.IsMatch body) -> yield m.Groups["name"].Value
        | _ -> () ]

/// The name the statement holding `index` binds to, and whether it is a function (has parameters). None when the
/// statement awaits its spawn in place (`let!`, `do!`, `match!`) or binds nothing.
let private boundName (code: string) (index: int) : (string * bool) option =
  let own = indentOf (code.Substring(lineStartOf code index, lineEndOf code index - lineStartOf code index))
  let lines = code.Substring(0, lineEndOf code index).Split('\n')
  let rec up k =
    match k < 0 || lines.Length - 1 - k > lookUpFor with
    | true -> None
    | false ->
      let line = lines[k]
      match awaitedStatement.IsMatch line && indentOf line <= own with
      | true -> None
      | false ->
        let m = binding.Match line
        match m.Success && indentOf line <= own with
        | true ->
          let nameEnd = m.Groups["name"].Index + m.Groups["name"].Length
          Some (m.Groups["name"].Value, namesParameters (line.Substring(nameEnd, m.Length - nameEnd - 1)))
        | false -> up (k - 1)
  up (lines.Length - 1)

/// The sites in `code` (already free of comments) whose spawn carries a deadline.
let private sitesIn (helpers: Regex) (code: string) : Site list =
  let spawns = Regex(match starters code with [] -> spawnPattern | names -> @"(?<![\w.])(" + System.String.Join("|", names |> List.map Regex.Escape) + @")\b|" + spawnPattern)
  let isDeadline (text: string) = deadlineLine.IsMatch text || helpers.IsMatch text
  let waitsOrWindows = isDeadline code || windowLine.IsMatch code
  let codeLines = code.Split('\n')
  [ for m in spawns.Matches code |> Seq.cast<Match> do
      let lineStart = lineStartOf code m.Index
      let lineText = code.Substring(lineStart, lineEndOf code m.Index - lineStart)
      let isItsOwnDefinition = definition.IsMatch lineText && Regex.IsMatch(lineText, @"let\s+(?:rec\s+|inline\s+|private\s+)*" + Regex.Escape m.Value + @"\b")
      let afterName = m.Index + m.Length
      let finish =
        let opening = Regex.Match(code.Substring afterName, @"^(<[^()\n]*>)?[ ]*\(")
        match opening.Success with
        | true ->
          let open' = afterName + opening.Length - 1
          open' + (BlockingReaderRatchetTests.parenthesized code open').Length
        | false -> afterName
      match isItsOwnDefinition || longRunning.IsMatch (code.Substring(m.Index, finish - m.Index)) with
      | true -> ()
      | false ->
        let tail = code.Substring finish
        let tailLine = tail.Split('\n')[0]
        let chained = Regex.IsMatch(tail, @"^[\s)]*\.(WaitAsync|Wait)\b")
        let piped =
          let next = Regex.Match(tail, @"^\s*\|>\s*([\w.]+)")
          next.Success && helpers.IsMatch next.Groups[1].Value
        let argument =
          let start = List.fold (fun at _ -> lineStartOf code (max 0 (at - 1))) lineStart [ 1 .. linesAbove ]
          let racedOnLine = Regex.IsMatch(code.Substring(lineStart, m.Index - lineStart), @"Task\.WhenAny\s*\(")
          racedOnLine || helpers.IsMatch (code.Substring(start, m.Index - start))
        let name = boundName code m.Index
        let startsStatement =
          let before = code.Substring(0, lineStart).TrimEnd().Split('\n') |> Array.last
          lineText.TrimStart().StartsWith m.Value
          && not (Regex.IsMatch(lineText, @"^\s*(let|use|do!|let!|return)"))
          && not (handsOn.IsMatch before)
        let discarded =
          m.Value = "Async.Start"
          || Regex.IsMatch(tailLine, @"\|>\s*ignore\b")
          || (match name with Some (n, _) -> n.StartsWith "_" | None -> false)
          || startsStatement
        let reason =
          match chained, piped, argument with
          | true, _, _ -> Some ChainedWait
          | _, true, _ -> Some PipedIntoDeadline
          | _, _, true -> Some ArgumentOfDeadline
          | _ when discarded && waitsOrWindows -> Some UnawaitedBesideAWindow
          | _ ->
            match name with
            | Some (n, false) ->
              let nameLine = Regex(@"\b" + Regex.Escape n + @"\b")
              let firstAfter = lineNumberOf code (lineEndOf code m.Index)
              let later = codeLines |> Array.skip (min codeLines.Length firstAfter)
              let named = later |> Array.exists (fun l -> nameLine.IsMatch l && isDeadline l)
              let underMarker = later |> Array.windowed 3 |> Array.exists (fun w -> nameLine.IsMatch w[2] && (isDeadline w[0] || isDeadline w[1]))
              match named || underMarker with
              | true -> Some (BoundAndDeadlined n)
              | false -> None
            | _ -> None
        match reason with
        | Some r -> yield { Line = lineNumberOf code m.Index; Spawn = m.Value; Reason = r }
        | None -> () ]

/// The sites in `text`, given the deadline helpers the rest of the sources define.
let poolDeadlineSites (helpers: Set<string>) (text: string) : Site list =
  sitesIn (helperPattern helpers) (strip text)

let private testSources : (string * string) list =
  let dir = Path.Combine(repoRoot, "SageFs.Tests")
  let ownSubject (relative: string) =
    // These read test source text and carry spawns in string literals.
    relative.EndsWith "PoolDeadlineRatchetTests.fs" || relative.EndsWith "BlockingReaderRatchetTests.fs"
  [ for file in Directory.EnumerateFiles(dir, "*.fs", SearchOption.AllDirectories) do
      let relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/')
      match relative.Contains "/obj/" || relative.Contains "/bin/" || relative.Contains "/fixtures/" || ownSubject relative with
      | true -> ()
      | false -> yield relative, File.ReadAllText file ]

let private helpersOfTheSuite : Set<string> = deadlineHelpers (testSources |> List.map snd)

let private sitesByFile : (string * Site list) list =
  testSources |> List.map (fun (relative, text) -> relative, poolDeadlineSites helpersOfTheSuite text)

[<Tests>]
let tests =
  testList "Pool deadlines" [

    testCase "WHY — no test puts a deadline on pool work, because the deadline then times the pool's queue and a starved pool fails a case whose work took milliseconds" <| fun _ ->
      let allowed (file: string) = exceptions |> List.sumBy (fun (f, n, _) -> match f = file with true -> n | false -> 0)
      let over =
        sitesByFile
        |> List.collect (fun (file, sites) ->
          match sites.Length > allowed file with
          | true -> [ sprintf "%s has %d (budget %d): %s" file sites.Length (allowed file) (System.String.Join("; ", sites |> List.map (fun s -> sprintf "line %d %s, %s" s.Line s.Spawn (describe s.Reason)))) ]
          | false -> [])
      over |> Expect.equal "every spawn a deadline waits on starts with Started.asTask or DedicatedThread.run" []

    testCase "WHY — an exception is a file that still has the spawn it names, so the table cannot hold a reason for something that moved" <| fun _ ->
      let stale =
        exceptions
        |> List.filter (fun (file, count, _) ->
          let found = sitesByFile |> List.tryFind (fun (f, _) -> f = file) |> Option.map (fun (_, sites) -> sites.Length) |> Option.defaultValue 0
          found <> count)
        |> List.map (fun (file, count, _) -> sprintf "%s budgets %d" file count)
      stale |> Expect.equal "every exception matches the real count" []

    testCase "WHY — the scan sees pool work with a deadline in each shape the suite has used, so the ratchet cannot go blind" <| fun _ ->
      let helpers = Set.ofList [ "within"; "answeredWithin"; "completesWithin"; "stopsStarted" ]
      let shapes =
        [ "Task.Run, WaitAsync chained", "let r = Task.Run(fun () -> work ()).WaitAsync(TestTimeouts.patience)"
          "Task.Run, WaitAsync on the next line", "let r =\n  Task.Run(fun () -> work ())\n    .WaitAsync(TestTimeouts.patience)\n"
          // Spelled in two pieces so the blocking-call budget does not count this line as a use.
          "Task.Run, blocking Wait with a timeout", "let ok = Task.Run(fun () -> work ())." + "Wait(TestTimeouts.patience)"
          "StartNew then WaitAsync", "let r = Task.Factory.StartNew(fun () -> work ()).WaitAsync(patience)"
          "Task.Run raced against a delay", "let! first = Task.WhenAny(Task.Run(fun () -> work ()) :> Task, Task.Delay patience)"
          "Task.Run handed to awaitWithin, three lines", "let! scanning =\n  awaitWithin\n    \"the scan\"\n    (Task.Run<Task<int>>(fun () -> Task.FromResult 1))\n"
          "StartAsTask chained", "let r = (proxy msg |> Async.StartAsTask).WaitAsync TestTimeouts.patience"
          "StartAsTask bound, then raced", "let work = mailbox.PostAndAsyncReply(build) |> Async.StartAsTask\nlet! winner = Task.WhenAny(work :> Task, Task.Delay bound)\n"
          "StartAsTask bound over lines, then WaitAsync", "let waiting =\n  proxy msg\n  |> Async.StartAsTask\ndo! waiting.WaitAsync patience\n"
          "StartAsTask bound, then a helper", "let running = monitor |> Async.StartAsTask\ndo! within \"it\" running\n"
          "StartAsTask piped into a helper", "let! r = build |> Async.StartAsTask |> completesWithin 5000\n"
          "StartAsTask as the argument of a helper", "let! r = within patience (build |> Async.StartAsTask)\n"
          "a function that returns a started task, called then raced", "let postAndReply mailbox build =\n  mailbox.PostAndAsyncReply(build) |> Async.StartAsTask\n\nlet restart = postAndReply mailbox build\nlet! first = Task.WhenAny(restart :> Task, Task.Delay bound)\n"
          "Async.Start beside a window", "Async.Start serve\ndo! Task.Delay settle\n"
          "Task.Run ignored beside a deadline", "Task.Run serve |> ignore\nlet! r = signal.WaitAsync patience\n"
          "an underscore name beside a window", "let _running = monitor |> Async.StartAsTask\ndo! Task.Delay(poll * 3)\n" ]
      let missed = shapes |> List.filter (fun (_, text) -> (poolDeadlineSites helpers text).Length <> 1) |> List.map fst
      missed |> Expect.isEmpty "every shape counts as exactly one"

    testCase "WHY — the scan leaves alone what has no deadline on the pool's queue, so a budget of zero is reachable" <| fun _ ->
      let helpers = Set.ofList [ "within"; "answeredWithin"; "completesWithin"; "stopsStarted" ]
      let innocent =
        [ "no deadline", "let t = Task.Run(fun () -> work ())"
          "awaited without a deadline", "let! r = Task.Run(fun () -> work ())"
          "StartAsTask awaited in place", "let! r = build |> Async.StartAsTask\n"
          "StartAsTask awaited in place beside a deadline on something else", "let! r = build |> Async.StartAsTask\nlet! s = signal.WaitAsync patience\n"
          "WhenAll", "let! all = Task.WhenAll [ Task.Run(fun () -> a ()); Task.Run(fun () -> b ()) ]"
          "dedicated thread", "let r = TestInfrastructure.DedicatedThread.run \"x\" work |> fun t -> t.WaitAsync(patience)"
          "started on the calling thread", "let work = Started.asTask build\nlet! winner = Task.WhenAny(work :> Task, Task.Delay bound)\n"
          "LongRunning", "let r = Task.Factory.StartNew((fun () -> work ()), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(patience)"
          "comment only", "// Task.Run(fun () -> work ()).WaitAsync(patience) lost to a starved pool"
          "WaitAsync on something else", "do! gate.Task.WaitAsync(patience)\nlet t = Task.Run(fun () -> work ())\n"
          "WhenAny on another line", "let! first = Task.WhenAny(a, Task.Delay patience)\nlet t = Task.Run(fun () -> work ())\n"
          "a bound name that no deadline mentions", "let work = build |> Async.StartAsTask\nlet! r = work\nlet! s = other.WaitAsync patience\n"
          "a task bound and waited on in a file with no deadline", "let work = build |> Async.StartAsTask\nlet! r = work\n"
          "Async.Start in a file with no deadline and no window", "Async.Start serve\nlet! r = done'\n" ]
      let wrong = innocent |> List.filter (fun (_, text) -> (poolDeadlineSites helpers text).Length <> 0) |> List.map fst
      wrong |> Expect.isEmpty "none of these counts"

    testCase "WHY — the scan finds the deadline helpers the sources define, whatever file they are in, so a helper added later is seen" <| fun _ ->
      let source =
        "let private within (what: string) (work: Task<'a>) : Task<'a> =\n  task {\n    let! first = Task.WhenAny(work :> Task, Task.Delay patience)\n    return! work\n  }\n\nlet private compute (n: int) : int =\n  n + 1\n\nlet private awaitText (task: Task) =\n  task.WaitAsync patience\n"
      let found = deadlineHelpers [ source ]
      found |> Expect.equal "the two that bound a task, not the one that computes" (Set.ofList [ "within"; "awaitText" ])
  ]
  |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant
