/// Pure-IO performer for cohort landing git operations
/// (sagefs-multiagent-vision.md item 14a): rebasing a member's commits onto
/// the integration branch, fast-forwarding the integration branch, diffing
/// files between two commits, and managing the per-cohort integration
/// worktree.
///
/// This module is standalone and wired into NOTHING yet — a later slice
/// injects it into `SageFs.Core.Features.CohortOwner`'s effect loop as the
/// performer for `CohortEffect.Rebase`/`FastForward`/`ComputeAffected`
/// (`ComputeAffected` maps onto `diffNames`; `Cohort.CohortCommand
/// .RebaseCompleted`'s payload is `Result<string, string list>`, exactly
/// `rebase`'s return type, so the wiring slice can pass it straight
/// through).
///
/// Mirrors `SessionManager.runBuildAsync`'s process shape (SessionManager.fs
/// ~563-627): `ProcessStartInfo` with `ArgumentList` (never a shell string —
/// no value here is ever interpolated into a command line), stdout/stderr
/// drained on background `Task.Run` threads, a bounded timeout racing the
/// process's `Exited` event via `Task.WhenAny`, and
/// `proc.Kill(entireProcessTree = true)` on timeout. Every public function is
/// total: a failed process start, a nonzero exit, or a timeout are all
/// caught and returned as `Error`, never thrown.
///
/// `Timeouts` (SageFs.Core/Timeouts.fs) has no constant shaped for a single
/// git subprocess — its nearest relative, `buildCompletion`, is a 10-minute,
/// env-overridable budget for `dotnet build`, which is a different
/// operation with a different cost profile. This module defines its own
/// local, conservative budgets instead: short-lived plumbing commands
/// (rev-parse, diff, update-ref, worktree remove) get 30s; a rebase — which
/// may run hooks and touch many commits — gets 5 minutes; `worktree add`
/// (a checkout of a whole tree) gets 2 minutes.
module SageFs.Features.CohortGit

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks

let private shortTimeout = TimeSpan.FromSeconds 30.0
let private rebaseTimeout = TimeSpan.FromMinutes 5.0
let private worktreeTimeout = TimeSpan.FromMinutes 2.0

let private trim (s: string) = s.Trim()

let private splitLines (text: string) : string list =
  text.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
  |> Array.toList

let private mapOk (f: 'a -> 'b) (result: Async<Result<'a, 'e>>) : Async<Result<'b, 'e>> =
  async {
    let! r = result
    return Result.map f r
  }

/// Runs `git <args>` in `repoDir` with a bounded timeout, returning the raw
/// (exitCode, stdout, stderr) — never throws. exitCode -1 marks a timeout or
/// a process-start failure (git missing, `repoDir` unreadable, etc); the
/// stderr slot always carries a human-readable reason in that case.
let private runGitExitCode (repoDir: string) (timeout: TimeSpan) (args: string list) : Async<int * string * string> =
  async {
    let argsText = String.concat " " args
    try
      let psi =
        ProcessStartInfo(
          "git",
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          UseShellExecute = false,
          WorkingDirectory = repoDir)
      for a in args do
        psi.ArgumentList.Add a
      use proc = new Process(StartInfo = psi)
      proc.Start() |> ignore
      // Drain both streams on background threads, exactly as
      // SessionManager.runBuildAsync does for `dotnet build` — a git process
      // whose stdout/stderr pipe fills without a reader would otherwise
      // deadlock against this process waiting on it.
      let stdoutTask = Task.Run(fun () -> proc.StandardOutput.ReadToEnd())
      let stderrTask = Task.Run(fun () -> proc.StandardError.ReadToEnd())
      let tcs = TaskCompletionSource<bool>()
      proc.EnableRaisingEvents <- true
      proc.Exited.Add(fun _ -> tcs.TrySetResult true |> ignore)
      do
        match proc.HasExited with
        | true -> tcs.TrySetResult true |> ignore
        | false -> ()
      let timeoutTask = Task.Delay(int timeout.TotalMilliseconds)
      let! completed = Task.WhenAny(tcs.Task, timeoutTask) |> Async.AwaitTask
      match Object.ReferenceEquals(completed, timeoutTask) with
      | true ->
        do (try proc.Kill(entireProcessTree = true) with _ -> ())
        return -1, "", sprintf "git %s timed out after %A in %s" argsText timeout repoDir
      | false ->
        let! _ = Task.WhenAll(stdoutTask, stderrTask) |> Async.AwaitTask
        return proc.ExitCode, stdoutTask.Result, stderrTask.Result
    with ex ->
      return -1, "", sprintf "failed to run git %s in %s: %s" argsText repoDir ex.Message
  }

/// Runs `git <args>` in `repoDir`, collapsing a nonzero exit (or a timeout /
/// start failure) into `Error` carrying stderr (falling back to stdout, then
/// a generic message, so the caller always gets a non-empty reason).
let private runGit (repoDir: string) (timeout: TimeSpan) (args: string list) : Async<Result<string, string>> =
  async {
    let! exitCode, stdout, stderr = runGitExitCode repoDir timeout args
    match exitCode with
    | 0 -> return Ok stdout
    | _ ->
      let reason =
        [ stderr; stdout ]
        |> List.tryFind (String.IsNullOrWhiteSpace >> not)
        |> Option.map trim
        |> Option.defaultValue (sprintf "git %s exited %d" (String.concat " " args) exitCode)
      return Error reason
  }

/// The absolute path of a per-repo git admin path (e.g. `rebase-merge`),
/// resolved via `git rev-parse --path-format=absolute --git-path` so it is
/// correct for a linked worktree too (whose `rebase-merge`/`rebase-apply`
/// live under `.git/worktrees/<name>/`, NOT a naive `<repoDir>/.git/...`
/// join).
let private gitAdminPath (repoDir: string) (relative: string) : Async<Result<string, string>> =
  runGit repoDir shortTimeout [ "rev-parse"; "--path-format=absolute"; "--git-path"; relative ]
  |> mapOk trim

let private isRebaseInProgress (repoDir: string) : Async<bool> =
  async {
    let! mergePath = gitAdminPath repoDir "rebase-merge"
    let! applyPath = gitAdminPath repoDir "rebase-apply"
    let exists =
      function
      | Ok (path: string) -> Directory.Exists path
      | Error _ -> false
    return exists mergePath || exists applyPath
  }

/// A rebase's error channel is `string list` — the exact shape of
/// `Cohort.CohortCommand.RebaseCompleted`'s payload — so the wiring slice can
/// pass `rebase`'s result straight through without adapting it. Two distinct
/// failure kinds share that one channel:
///  - `Conflict files`: a genuine merge conflict. `files` are the real
///    conflicting paths from `git diff --name-only --diff-filter=U`, read
///    while the rebase was still in progress, immediately before
///    `git rebase --abort` cleans it up.
///  - `InfraFailure reason`: NOT a conflict — a bad `onto` ref, git missing,
///    a dirty worktree that refused to even start rebasing, etc. It still
///    has to fit the `string list` channel, so it is encoded as a
///    single-element list whose element starts with a NUL byte
///    (`infraFailureMarker`). Git can never emit a NUL byte inside a real
///    path (NUL is git's own path separator/terminator in `-z` output), so
///    this encoding can never be produced by, or mistaken for, a real
///    conflicting file name. `classifyRebaseFailure` is the one place that
///    knows this encoding — callers should always go through it rather than
///    inspecting the raw list.
let private infraFailureMarker : string = string (char 0) + "cohort-git-infra-failure:"

type RebaseFailure =
  | Conflict of files: string list
  | InfraFailure of reason: string

/// The single, exhaustive classifier for a `rebase` error list — see
/// `infraFailureMarker`'s doc comment for why this encoding is unambiguous.
let classifyRebaseFailure (failure: string list) : RebaseFailure =
  match failure with
  | [ only ] when only.StartsWith(infraFailureMarker, StringComparison.Ordinal) ->
    InfraFailure(only.Substring(infraFailureMarker.Length))
  | files -> Conflict files

let private infraError (reason: string) : string list =
  [ infraFailureMarker + reason ]

/// `git rev-parse <reference>`, trimmed.
let revParse (repoDir: string) (reference: string) : Async<Result<string, string>> =
  runGit repoDir shortTimeout [ "rev-parse"; reference ]
  |> mapOk trim

/// The repo's current HEAD sha.
let currentHead (repoDir: string) : Async<Result<string, string>> =
  revParse repoDir "HEAD"

/// Whether `refs/heads/<branch>` exists.
let branchExists (repoDir: string) (branch: string) : Async<bool> =
  async {
    let! exitCode, _, _ =
      runGitExitCode repoDir shortTimeout [ "show-ref"; "--verify"; "--quiet"; sprintf "refs/heads/%s" branch ]
    return exitCode = 0
  }

/// The file paths that differ between `baseSha` and `headSha`
/// (`git diff --name-only <base>..<head>`).
let diffNames (repoDir: string) (baseSha: string) (headSha: string) : Async<Result<string list, string>> =
  runGit repoDir shortTimeout [ "diff"; "--name-only"; sprintf "%s..%s" baseSha headSha ]
  |> mapOk splitLines

/// Rebases the checked-out branch in `repoDir` onto `onto`.
///
/// - On success: `Ok <new HEAD sha>`.
/// - On a genuine conflict: `Error (Conflict files)` (via
///   `classifyRebaseFailure`) — the conflicting files are read BEFORE
///   `git rebase --abort` runs, and the abort always runs whenever a rebase
///   is actually left in progress, so the worktree is guaranteed clean
///   (never left mid-rebase) on every Error path.
/// - On an infra failure (bad `onto`, git missing, a dirty worktree that
///   never even started rebasing, etc.): `Error (InfraFailure reason)`. When
///   `git rebase` fails without ever entering a rebase (no
///   `rebase-merge`/`rebase-apply` admin dir created), there is nothing to
///   abort — the worktree was never touched.
let rebase (repoDir: string) (onto: string) : Async<Result<string, string list>> =
  async {
    let! rebaseResult = runGit repoDir rebaseTimeout [ "rebase"; onto ]
    match rebaseResult with
    | Ok _ ->
      let! head = revParse repoDir "HEAD"
      match head with
      | Ok sha -> return Ok sha
      | Error reason -> return Error(infraError reason)
    | Error rebaseReason ->
      let! inProgress = isRebaseInProgress repoDir
      match inProgress with
      | false ->
        // The rebase never started (e.g. an invalid `onto`, or the working
        // tree was already dirty and git refused before creating any
        // rebase-merge/rebase-apply admin dir) — nothing to abort or clean up.
        return Error(infraError rebaseReason)
      | true ->
        let! conflictResult = runGit repoDir shortTimeout [ "diff"; "--name-only"; "--diff-filter=U" ]
        let conflictFiles =
          match conflictResult with
          | Ok text -> splitLines text
          | Error _ -> []
        // Always abort once a rebase is genuinely in progress, regardless of
        // outcome below, so the worktree is never left mid-rebase.
        let! _abort = runGit repoDir shortTimeout [ "rebase"; "--abort" ]
        match conflictFiles with
        | [] ->
          return
            Error(
              infraError (
                sprintf "rebase onto %s left a rebase in progress with no conflicting files detected: %s" onto rebaseReason
              )
            )
        | files -> return Error files
  }

/// Fast-forwards `refs/heads/<branch>` to `toSha` without ever creating a
/// merge commit and without requiring `branch` to be checked out: resolves
/// the branch's current sha, verifies it is an ancestor of `toSha` via
/// `git merge-base --is-ancestor`, then moves the ref with
/// `git update-ref refs/heads/<branch> <toSha> <currentSha>` — a
/// compare-and-swap that fails closed if the branch moved concurrently or
/// the fast-forward isn't possible, rather than silently overwriting or
/// falling back to a merge.
let fastForwardBranch (repoDir: string) (branch: string) (toSha: string) : Async<Result<string, string>> =
  async {
    let! currentResult = revParse repoDir (sprintf "refs/heads/%s" branch)
    match currentResult with
    | Error reason -> return Error(sprintf "could not resolve branch '%s': %s" branch reason)
    | Ok currentSha ->
      let! ancestorExit, _, ancestorErr =
        runGitExitCode repoDir shortTimeout [ "merge-base"; "--is-ancestor"; currentSha; toSha ]
      match ancestorExit with
      | 0 ->
        let! updateResult =
          runGit repoDir shortTimeout [ "update-ref"; sprintf "refs/heads/%s" branch; toSha; currentSha ]
        match updateResult with
        | Ok _ -> return Ok toSha
        | Error reason -> return Error(sprintf "fast-forward of '%s' to %s failed: %s" branch toSha reason)
      | 1 ->
        return
          Error(sprintf "'%s' at %s is not an ancestor of %s — not a fast-forward" branch currentSha toSha)
      | code ->
        return Error(sprintf "git merge-base --is-ancestor exited %d: %s" code ancestorErr)
  }

/// Creates the per-cohort integration worktree: `git worktree add -b
/// <branch> <worktreePath> <baseRef>`.
let addWorktree (repoDir: string) (worktreePath: string) (branch: string) (baseRef: string) : Async<Result<unit, string>> =
  runGit repoDir worktreeTimeout [ "worktree"; "add"; "-b"; branch; worktreePath; baseRef ]
  |> mapOk ignore

/// Removes a worktree created by `addWorktree`.
let removeWorktree (repoDir: string) (worktreePath: string) : Async<Result<unit, string>> =
  runGit repoDir shortTimeout [ "worktree"; "remove"; "--force"; worktreePath ]
  |> mapOk ignore
