/// Product code does not block a thread on a Task or an Async. `Async.RunSynchronously`, `.GetAwaiter().GetResult()`,
/// `.Wait(`, `Thread.Sleep` and `Task.WaitAll` in SageFs/, SageFs.Core/ and SageFs.Host/ are where the thread pool
/// starves and a handler deadlocks under load: a request that blocks a pool thread on work that needs a pool thread
/// is the whole bug. A request handler awaits, and a synchronous function a handler calls returns a Task.
///
/// What is left is a real synchronous boundary (a process entry point, a dedicated thread, a callback an outside
/// library calls synchronously, a synchronous API a test pins), and each one carries a sentence saying why blocking
/// cannot starve anything there. This pins the count of those, per pattern. The budgets only go DOWN, and a budget
/// above the real count is stale, so room that was already won back cannot be spent again. Comments and string
/// literals that merely NAME a pattern (EvalLens.fs's list of forbidden calls, NonBlockingRun.fs's printfn hint)
/// are not counted: the scan blanks them first.
module SageFs.Tests.ProductBlockingCallsTests

open System.IO
open System.Text
open Expecto
open Expecto.Flip

let private repoRoot = RepoPaths.repoPathFull [||]

/// `text` with every comment and string literal blanked to spaces (newlines kept, so a line number still holds).
/// Handles line comments, nested block comments, regular, verbatim and triple-quoted strings, and the `'"'` char.
let codeOnly (text: string) : string =
  let n = text.Length
  let out = StringBuilder(n)
  let at (i: int) = match i < n with | true -> text[i] | false -> '\000'
  let blank (c: char) = match c with | '\n' | '\r' -> c | _ -> ' '
  let mutable i = 0
  while i < n do
    let c = text[i]
    match c with
    | '(' when at (i + 1) = '*' && at (i + 2) <> ')' ->
      let mutable depth = 1
      out.Append "  " |> ignore
      i <- i + 2
      while depth > 0 && i < n do
        match text[i], at (i + 1) with
        | '(', '*' -> depth <- depth + 1; out.Append "  " |> ignore; i <- i + 2
        | '*', ')' -> depth <- depth - 1; out.Append "  " |> ignore; i <- i + 2
        | ch, _ -> out.Append(blank ch) |> ignore; i <- i + 1
    | '/' when at (i + 1) = '/' ->
      while i < n && text[i] <> '\n' do
        out.Append ' ' |> ignore
        i <- i + 1
    | '@' when at (i + 1) = '"' ->
      out.Append "  " |> ignore
      i <- i + 2
      let mutable closed = false
      while not closed && i < n do
        match text[i], at (i + 1) with
        | '"', '"' -> out.Append "  " |> ignore; i <- i + 2
        | '"', _ -> out.Append ' ' |> ignore; i <- i + 1; closed <- true
        | ch, _ -> out.Append(blank ch) |> ignore; i <- i + 1
    | '"' when at (i + 1) = '"' && at (i + 2) = '"' ->
      out.Append "   " |> ignore
      i <- i + 3
      let mutable closed = false
      while not closed && i < n do
        match text[i], at (i + 1), at (i + 2) with
        | '"', '"', '"' -> out.Append "   " |> ignore; i <- i + 3; closed <- true
        | ch, _, _ -> out.Append(blank ch) |> ignore; i <- i + 1
    | '"' ->
      out.Append ' ' |> ignore
      i <- i + 1
      let mutable closed = false
      while not closed && i < n do
        match text[i] with
        | '\\' -> out.Append "  " |> ignore; i <- i + 2
        | '"' -> out.Append ' ' |> ignore; i <- i + 1; closed <- true
        | ch -> out.Append(blank ch) |> ignore; i <- i + 1
    | '\'' when at (i + 1) = '"' && at (i + 2) = '\'' ->
      out.Append "   " |> ignore
      i <- i + 3
    | '\'' when at (i + 1) = '\\' && at (i + 2) = '"' && at (i + 3) = '\'' ->
      out.Append "    " |> ignore
      i <- i + 4
    | _ ->
      out.Append c |> ignore
      i <- i + 1
  out.ToString()

/// Each place `pattern` occurs in `code`, as a 1-based line number.
let linesOf (pattern: string) (code: string) : int list =
  code.Split('\n')
  |> Array.mapi (fun index line ->
    let mutable hits = []
    let mutable from = 0
    let mutable go = true
    while go do
      match line.IndexOf(pattern, from, System.StringComparison.Ordinal) with
      | -1 -> go <- false
      | hit ->
        hits <- (index + 1) :: hits
        from <- hit + pattern.Length
    hits)
  |> Array.toList
  |> List.concat

/// What the product may still block on, per pattern. Ratchet down, never up.
let private budgets : (string * int) list =
  [ "Async.RunSynchronously", 11
    ".GetResult()", 4
    ".Wait(", 1
    "Thread.Sleep", 1
    "WaitAll(", 1 ]

let private productFiles : string list =
  [ for project in [ "SageFs"; "SageFs.Core"; "SageFs.Host" ] do
      let dir = Path.Combine(repoRoot, project)
      match Directory.Exists dir with
      | false -> ()
      | true ->
        for file in Directory.EnumerateFiles(dir, "*.fs", SearchOption.AllDirectories) do
          let relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/')
          match relative.Contains "/obj/" || relative.Contains "/bin/" with
          | true -> ()
          | false -> yield relative ]

/// Every place each pattern is still written in code, as `file:line`.
let private sitesOf () : (string * string list) list =
  let scanned =
    productFiles
    |> List.map (fun relative -> relative, codeOnly (File.ReadAllText(Path.Combine(repoRoot, relative))))
  [ for (pattern, _) in budgets ->
      pattern,
      [ for (relative, code) in scanned do
          for line in linesOf pattern code -> sprintf "%s:%d" relative line ] ]

let private budgetTable =
  TestInfrastructure.Ratchet.table
    { Name = "product blocking calls"
      SourceFile = "SageFs.Tests/ProductBlockingCallsTests.fs"
      Budgets = budgets
      Actual = fun () -> sitesOf () |> List.map (fun (pattern, sites) -> pattern, List.length sites) }

[<Tests>]
let tests =
  testList "Product blocking calls" [

    testCase "WHY — the scan blanks comments and string literals, so a file that only NAMES a blocking call is not counted" <| fun _ ->
      let source =
        String.concat "\n" [
          "let a = \"Async.RunSynchronously\" // Async.RunSynchronously"
          "let b = (* Thread.Sleep *) Thread.Sleep 1"
          "let c = @\"x\"\"Thread.Sleep\" + '\"' + Thread.Sleep 2"
          "let d = \"\"\"Thread.Sleep\"\"\" "
          "let e = \"\\\"\" + Thread.Sleep 3" ]
      codeOnly source
      |> linesOf "Thread.Sleep"
      |> Expect.equal "only the three real calls, on lines 2, 3 and 5" [ 2; 3; 5 ]

    testCase "WHY — no blocking pattern appears in product code more often than its budget, so a new one cannot land quietly" <| fun _ ->
      let allowed = Map.ofList budgets
      let over =
        sitesOf ()
        |> List.choose (fun (pattern, sites) ->
          let budget = Map.find pattern allowed
          match List.length sites > budget with
          | true -> Some (sprintf "'%s' is written %d times, budget %d: %s" pattern (List.length sites) budget (String.concat ", " sites))
          | false -> None)
      over |> Expect.isEmpty "every pattern is within its budget (await it, or return a Task; a real synchronous boundary says why in a comment, and the budget is not raised without that)"

    testCase "WHY — a budget above the real count is stale, so room that was already won back cannot be spent again" <| fun _ ->
      let allowed = Map.ofList budgets
      let stale =
        sitesOf ()
        |> List.choose (fun (pattern, sites) ->
          let budget = Map.find pattern allowed
          match List.length sites < budget with
          | true -> Some (sprintf "'%s' is written %d times but its budget is %d: lower it" pattern (List.length sites) budget)
          | false -> None)
      stale |> Expect.isEmpty "every budget equals the pattern's current count"
  ]
  |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant
