/// Product code does not block a thread on a Task or an Async. Waiting synchronously on one (running an Async
/// synchronously, taking an awaiter's result, waiting on a task or handle, sleeping, waiting for all) in SageFs/,
/// SageFs.Core/ and SageFs.Host/ is where the thread pool starves and a handler deadlocks under load: a request
/// that blocks a pool thread on work that needs a pool thread is the whole bug. A request handler awaits, and a
/// synchronous function a handler calls returns a Task.
///
/// What is left is a real synchronous boundary (a process entry point, a dedicated thread, a callback an outside
/// library calls synchronously, a synchronous API a test pins), and each one carries a sentence saying why blocking
/// cannot starve anything there. This pins the count of those, per call. The allowances only go DOWN, and one above
/// the real count is stale, so room that was already won back cannot be spent again. Comments and string literals
/// that merely NAME a call (EvalLens.fs's list of forbidden calls, NonBlockingRun.fs's printfn hint) are not
/// counted: the scan blanks them first.
///
/// The calls are assembled from halves below, and the table is not registered with `Ratchet.table`. The test-body
/// ratchets count raw lines that spell a call whole, and this file is a test body; the registry test pins exactly four
/// registered tables, so lowering these is by hand, to the number the stale case prints.
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

/// The ways a thread waits synchronously on something that completes asynchronously.
[<RequireQualifiedAccess>]
type BlockingCall =
  | RunAsyncSynchronously
  | AwaiterResult
  | WaitOnTask
  | Sleep
  | WaitForAll

module BlockingCall =
  let all =
    [ BlockingCall.RunAsyncSynchronously
      BlockingCall.AwaiterResult
      BlockingCall.WaitOnTask
      BlockingCall.Sleep
      BlockingCall.WaitForAll ]

  /// The text that spells the call in code, in halves (see the module doc for why).
  let spelling (call: BlockingCall) : string =
    match call with
    | BlockingCall.RunAsyncSynchronously -> "Async" + "." + "RunSynchronously"
    | BlockingCall.AwaiterResult -> "." + "GetResult" + "()"
    | BlockingCall.WaitOnTask -> "." + "Wait" + "("
    | BlockingCall.Sleep -> "Thread" + "." + "Sleep"
    | BlockingCall.WaitForAll -> "Wait" + "All("

/// What the product may still block on, per call. Ratchet down, never up.
let private allowances : (BlockingCall * int) list =
  [ BlockingCall.RunAsyncSynchronously, 11
    BlockingCall.AwaiterResult, 2
    BlockingCall.WaitOnTask, 1
    BlockingCall.Sleep, 1
    BlockingCall.WaitForAll, 0 ]

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

/// Every place each call is still written in code, as `file:line`.
let private sitesOf () : (BlockingCall * string list) list =
  let scanned =
    productFiles
    |> List.map (fun relative -> relative, codeOnly (File.ReadAllText(Path.Combine(repoRoot, relative))))
  [ for call in BlockingCall.all ->
      call,
      [ for (relative, code) in scanned do
          for line in linesOf (BlockingCall.spelling call) code -> sprintf "%s:%d" relative line ] ]

/// What `git rev-parse --path-format=absolute --git-common-dir` says about `dir`, reduced the way `commonRepositoryRoot` reduces it.
/// This is the answer the disk reading replaced, so it is the oracle.
let private commonRootAccordingToGit (dir: string) : string option =
  let psi = System.Diagnostics.ProcessStartInfo("git", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir)
  for a in [ "rev-parse"; "--path-format=absolute"; "--git-common-dir" ] do
    psi.ArgumentList.Add a
  use proc = System.Diagnostics.Process.Start psi
  let out = proc.StandardOutput.ReadToEnd()
  proc.WaitForExit()
  match out.Trim() with
  | "" -> None
  | answer ->
    let full = Path.TrimEndingDirectorySeparator(Path.GetFullPath answer)
    match Path.GetFileName full with
    | ".git" -> Path.GetDirectoryName full |> Option.ofObj
    | _ -> Some full

let private runGit (dir: string) (args: string list) : unit =
  let psi = System.Diagnostics.ProcessStartInfo("git", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir)
  for a in args do
    psi.ArgumentList.Add a
  use proc = System.Diagnostics.Process.Start psi
  proc.StandardOutput.ReadToEnd() |> ignore
  proc.StandardError.ReadToEnd() |> ignore
  proc.WaitForExit()

[<Tests>]
let commonRepositoryRootTests =
  testList "Common repository root" [

    testCase "WHY — the common repository root is read off the disk and still answers exactly what git rev-parse does, so no cohort call needs a git process" <| fun _ ->
      let tmp = Path.Combine(Path.GetTempPath(), "common-root-" + System.Guid.NewGuid().ToString("N").Substring(0, 8))
      Directory.CreateDirectory tmp |> ignore
      try
        let repo = Path.Combine(tmp, "repo")
        Directory.CreateDirectory(Path.Combine(repo, "sub", "deep")) |> ignore
        runGit repo [ "init"; "-q"; "-b"; "main" ]
        runGit repo [ "-c"; "user.name=t"; "-c"; "user.email=t@t"; "commit"; "-q"; "--allow-empty"; "-m"; "x" ]
        let worktree = Path.Combine(tmp, "wt")
        runGit repo [ "worktree"; "add"; "-q"; worktree; "-b"; "feat" ]
        Directory.CreateDirectory(Path.Combine(worktree, "sub")) |> ignore
        let link = Path.Combine(tmp, "link")
        Directory.CreateSymbolicLink(link, repo) |> ignore
        let bare = Path.Combine(tmp, "bare.git")
        runGit tmp [ "clone"; "-q"; "--bare"; repo; bare ]
        let notARepository = Path.Combine(tmp, "plain")
        Directory.CreateDirectory notARepository |> ignore
        let cases =
          [ "the checkout", repo
            "a subdirectory of it", Path.Combine(repo, "sub", "deep")
            "a linked worktree", worktree
            "a subdirectory of the worktree", Path.Combine(worktree, "sub")
            "a symlink to the checkout", link
            "a subdirectory through the symlink", Path.Combine(link, "sub")
            "a bare repository", bare
            "a directory that is not a repository", notARepository ]
        for (name, dir) in cases do
          SageFs.Features.CohortGit.commonRepositoryRoot dir
          |> Expect.equal (sprintf "%s: the same answer git gives" name) (commonRootAccordingToGit dir)
        SageFs.Features.CohortGit.commonRepositoryRoot (Path.Combine(tmp, "missing"))
        |> Expect.isNone "a directory that does not exist has no repository"
      finally
        (try Directory.Delete(Path.Combine(tmp, "link")) with _ -> ())
        (try Directory.Delete(tmp, true) with _ -> ())
  ]

[<Tests>]
let reloadPlanningAwaiting =
  let declsOf (source: string) =
    match SageFs.Features.ReloadPlanning.extractDecls source with
    | Ok decls -> decls
    | Error reason -> failtestf "the fixture should parse: %s" reason
  testList "ReloadPlanning awaiting the compiler's check" [

    testAsync "WHY — planReloadAsync plans exactly what planReload does, so the host's reload step can await the check without changing a decision" {
      let source =
        "module Demo.Access\n\nlet private secret () = 41\n\nlet answer () =\n  secret () + 1\n\nlet shout (s: string) = s.ToUpper()\n"
      let edits =
        [ "a patch that uses a private member (a restart)", source.Replace("secret () + 1", "secret () + 2")
          "a patch that uses only public members", source.Replace("s.ToUpper()", "s.ToLower()") ]
      for (name, edited) in edits do
        let baseline = declsOf source
        let current = declsOf edited
        let! awaited = SageFs.Features.ReloadPlanning.planReloadAsync baseline current
        awaited |> Expect.equal name (SageFs.Features.ReloadPlanning.planReload baseline current)
    }
  ]

[<Tests>]
let configHostAwaiting =
  testList "ConfigHost awaiting the FSI host" [

    testAsync "WHY — ConfigHost.evaluateAsync evaluates a config script in a real FSI host and answers its value, so a dashboard handler can await it instead of blocking on it" {
      let! evaluated =
        SageFs.ConfigHost.evaluateAsync System.Environment.CurrentDirectory "{ DirectoryConfig.empty with AutoOpenNamespaces = false }"
      match evaluated with
      | Ok config -> config.AutoOpenNamespaces |> Expect.isFalse "the script's own value came back"
      | Error reason -> failtestf "the config should evaluate: %s" (SageFs.ConfigHost.describeError reason)
    }
  ]

[<Tests>]
let productBlockingCalls =
  testList "Product blocking calls" [

    testCase "WHY — the scan blanks comments and string literals, so a file that only NAMES a blocking call is not counted" <| fun _ ->
      let sleep = BlockingCall.spelling BlockingCall.Sleep
      let sync = BlockingCall.spelling BlockingCall.RunAsyncSynchronously
      let source =
        String.concat "\n" [
          sprintf "let a = \"%s\" // %s" sync sync
          sprintf "let b = (* %s *) %s 1" sleep sleep
          sprintf "let c = @\"x\"\"%s\" + '\"' + %s 2" sleep sleep
          sprintf "let d = \"\"\"%s\"\"\" " sleep
          sprintf "let e = \"\\\"\" + %s 3" sleep ]
      codeOnly source
      |> linesOf sleep
      |> Expect.equal "only the three real calls, on lines 2, 3 and 5" [ 2; 3; 5 ]

    testCase "WHY — no blocking call appears in product code more often than its allowance, so a new one cannot land quietly" <| fun _ ->
      let over =
        sitesOf ()
        |> List.choose (fun (call, sites) ->
          let allowed = allowances |> List.find (fun (c, _) -> c = call) |> snd
          match List.length sites > allowed with
          | true -> Some (sprintf "'%s' is written %d times, allowed %d: %s" (BlockingCall.spelling call) (List.length sites) allowed (String.concat ", " sites))
          | false -> None)
      over |> Expect.isEmpty "every call is within its allowance (await it, or return a Task; a real synchronous boundary says why in a comment, and the allowance is not raised without that)"

    testCase "WHY — an allowance above the real count is stale, so room that was already won back cannot be spent again" <| fun _ ->
      let stale =
        sitesOf ()
        |> List.choose (fun (call, sites) ->
          let allowed = allowances |> List.find (fun (c, _) -> c = call) |> snd
          match List.length sites < allowed with
          | true -> Some (sprintf "'%s' is written %d times but its allowance is %d: lower it" (BlockingCall.spelling call) (List.length sites) allowed)
          | false -> None)
      stale |> Expect.isEmpty "every allowance equals the call's current count"

    testCase "WHY — every blocking call has an allowance, so adding a case to the set cannot leave it unpinned" <| fun _ ->
      allowances |> List.map fst |> List.sort
      |> Expect.equal "the allowances name exactly the calls the scan looks for" (List.sort BlockingCall.all)
  ]
  |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant
