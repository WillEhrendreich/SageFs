/// A workspace scan asks git many questions, and every one of them is a wait on another process. The scan awaits each
/// answer, so a thread is never held while git thinks: not the MCP request thread that asked for the plan, and not a
/// pool thread. The proof is a choreography, not a clock: a git whose every call is answered only when the test says so.
/// A scan that blocked a thread on a call would never hand control back, and the test would never get to answer it.
module SageFs.Tests.HygieneAsyncScanTests

open System
open System.Threading.Channels
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.HygieneGather

/// One git call the scan has started and is waiting on: what it asked, and the way to answer it.
type PendingCall =
  { Args: string list
    Answer: TaskCompletionSource<GitResult> }

/// A `Git` that answers nothing until it is told to. Calls arrive in a channel, in the order the scan made them.
type GatedGit() =
  let arrivals = Channel.CreateUnbounded<PendingCall>()

  member _.Git : Git =
    fun _ args ->
      let call = { Args = args; Answer = TaskCompletionSource<GitResult>(TaskCreationOptions.RunContinuationsAsynchronously) }
      arrivals.Writer.TryWrite call |> ignore
      call.Answer.Task

  member _.Next() : Task<PendingCall> = arrivals.Reader.ReadAsync().AsTask()

let repo = "/nonexistent/hygiene-async/repo"

let worktreeList : string =
  String.Join(
    "\n",
    [ "worktree " + repo; "HEAD aaa"; "branch refs/heads/master"; ""
      "worktree " + repo + "/.claude/worktrees/w1"; "HEAD bbb"; "branch refs/heads/worktree-agent-w1"; "" ])

/// What git says to each question a scan of one unmerged-looking, patch-equivalent worktree asks.
let answerFor (args: string list) : GitResult =
  match args with
  | "worktree" :: _ -> GitResult.Output worktreeList
  | "rev-parse" :: _ -> GitResult.Output "aaa"
  | "for-each-ref" :: _ -> GitResult.Output "worktree-agent-w1\t0\n"
  | "merge-base" :: _ -> GitResult.Exit(1, "")
  | "cherry" :: _ -> GitResult.Output ""
  | "status" :: _ -> GitResult.Output ""
  | other -> GitResult.Unavailable(sprintf "the choreography has no answer for git %s" (String.Join(" ", other)))

let scanWith (git: Git) : Scan =
  { Now = DateTime.UtcNow
    Loc =
      { Repo = repo
        GateDir = repo + "-gate"
        DataDir = repo + "-data"
        HostCacheDir = repo + "-hosts"
        TempDir = repo + "-tmp"
        Processes = ProcessScope.WholeMachine }
    Git = git
    Processes = []
    Live = LiveFacts.none
    IsAlive = fun _ _ -> false }

/// `work`, or a failure naming what did not happen in time, so a scan that never hands control back fails the test
/// instead of hanging it.
let awaitWithin (what: string) (work: Task<'a>) : Task<'a> =
  task {
    let! first = Task.WhenAny(work :> Task, Task.Delay TestTimeouts.patienceBrief)
    match obj.ReferenceEquals(first, work) with
    | true -> return! work
    | false -> return failtestf "%s did not happen within %A" what TestTimeouts.patienceBrief
  }

/// Start a scan against a gated git and answer its calls one at a time. At every call the scan has not finished and
/// has handed control back to the test. Returns the questions in the order they were asked, and what the scan found.
let drive (start: Scan -> Task<'a>) : Task<string list list * 'a> =
  task {
    let gated = GatedGit()
    let! scanning =
      awaitWithin
        "the scan handing control back while git is outstanding"
        (Task.Run<Task<'a>>(fun () -> Task.FromResult(start (scanWith gated.Git))))
    let asked = ResizeArray<string list>()
    let mutable finished = false
    while not finished do
      let next = gated.Next()
      let! winner = Task.WhenAny(scanning :> Task, next :> Task, Task.Delay TestTimeouts.patienceBrief)
      match obj.ReferenceEquals(winner, next), obj.ReferenceEquals(winner, scanning) with
      | true, _ ->
        let! call = next
        scanning.IsCompleted |> Expect.isFalse "the scan is waiting on git, so it has not finished"
        asked.Add call.Args
        call.Answer.SetResult(answerFor call.Args)
      | _, true -> finished <- true
      | _ -> failtestf "neither a git call nor the end of the scan came within %A; asked so far: %A" TestTimeouts.patienceBrief (List.ofSeq asked)
    let! found = scanning
    return List.ofSeq asked, found
  }

let verbs (asked: string list list) : string list = asked |> List.map List.head

[<Tests>]
let asyncScanTests =
  testList "Workspace hygiene scan: git is awaited, never waited on" [

    testTask "WHY — a gather over several git calls hands its thread back at every one, because each is an await and not a block" {
      let! asked, leftovers = drive (fun scan -> gather scan None)
      verbs asked
      |> Expect.equal
        "the worktree list, the branch list, then each candidate in turn (the first one also asks for the base branch)"
        [ "worktree"; "for-each-ref"; "rev-parse"; "merge-base"; "cherry"; "status"; "merge-base"; "cherry" ]
      leftovers |> List.isEmpty |> Expect.isFalse "the scan found the worktree and its branch"
    }

    testTask "WHY — the snapshot a tool reply is built from comes out of the same awaited scan, and says what a scan that answers at once says" {
      let! driven = drive (fun scan -> HygieneService.takeFrom scan)
      let snapshot : HygieneService.Snapshot = snd driven
      let! answeredAtOnce = HygieneService.takeFrom (scanWith (fun _ args -> Task.FromResult(answerFor args)))
      let immediate : HygieneService.Snapshot = answeredAtOnce
      snapshot.Leftovers |> List.map Leftover.target
      |> Expect.equal "the same targets as when git answers at once" (immediate.Leftovers |> List.map Leftover.target)
      snapshot.Plan.Steps |> List.length
      |> Expect.equal "the same number of plan steps" (immediate.Plan.Steps |> List.length)
    }
  ]
