module SageFs.Tests.WorkerReaperTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.WorkerProtocol

// The reaping book is a pure reducer: a stop starts pids leaving, a fate arrives per pid, and a caller parked on
// "workers gone" is answered exactly once, when the last pid has left. The mailbox that owns the book is covered in
// SessionManagerStopOffMailboxTests.fs; these pin the reducer on its own.

let private session = SessionId.newId ()

let private fateOfIndex (survives: bool list) (i: int) : WorkerReaper.Fate =
  match List.tryItem i survives with
  | Some true -> WorkerReaper.Fate.Outlived "still running"
  | Some false | None -> WorkerReaper.Fate.Left

[<Tests>]
let workerReaperTests =
  testList "WorkerReaper book" [

    testCase "WHY — a session that was never reaped has nothing to wait for" <| fun _ ->
      WorkerReaper.standing session WorkerReaper.empty<int>
      |> Expect.equal "all gone" (WorkerReaper.Standing.Done WorkerReaper.Settled.AllGone)

    testCase "WHY — a started pid is exiting until its fate arrives, and is watched the whole time" <| fun _ ->
      let book = WorkerReaper.empty<int> |> WorkerReaper.start session [ 100; 101 ]
      WorkerReaper.standing session book |> Expect.equal "both exiting" (WorkerReaper.Standing.StillExiting [ 100; 101 ])
      WorkerReaper.watchedPids book |> List.sort |> Expect.equal "both pids are on the books" [ 100; 101 ]

    testCase "WHY — a waiter is parked while a pid exits and answered once, in arrival order, when the last one leaves" <| fun _ ->
      let book = WorkerReaper.empty<string> |> WorkerReaper.start session [ 100; 101 ]
      let book, first = WorkerReaper.await session "first" book
      let book, second = WorkerReaper.await session "second" book
      first |> Expect.equal "the first waits" (WorkerReaper.Standing.StillExiting [ 100; 101 ])
      second |> Expect.equal "the second waits" (WorkerReaper.Standing.StillExiting [ 100; 101 ])
      let book, answers = WorkerReaper.finish session 100 WorkerReaper.Fate.Left book
      answers |> Expect.isEmpty "one pid is still exiting, so nobody is answered"
      let book, answers = WorkerReaper.finish session 101 WorkerReaper.Fate.Left book
      answers |> Expect.equal "both are answered, in the order they asked" [ "first", WorkerReaper.Settled.AllGone; "second", WorkerReaper.Settled.AllGone ]
      WorkerReaper.watchedPids book |> Expect.isEmpty "nothing is on the books once everything left"

    testCase "WHY — a duplicate report for a pid cannot settle the session early" <| fun _ ->
      let book = WorkerReaper.empty<string> |> WorkerReaper.start session [ 100; 101 ]
      let book, _ = WorkerReaper.await session "caller" book
      let book, _ = WorkerReaper.finish session 100 WorkerReaper.Fate.Left book
      let book, answers = WorkerReaper.finish session 100 WorkerReaper.Fate.Left book
      answers |> Expect.isEmpty "pid 100 was already reported, 101 is still exiting"
      WorkerReaper.standing session book |> Expect.equal "101 still exiting" (WorkerReaper.Standing.StillExiting [ 101 ])

    testCase "WHY — a worker that outlived its kill is a named survivor that stays on the books after the waiters are told" <| fun _ ->
      let book = WorkerReaper.empty<string> |> WorkerReaper.start session [ 100; 101 ]
      let book, _ = WorkerReaper.await session "caller" book
      let book, _ = WorkerReaper.finish session 100 (WorkerReaper.Fate.Outlived "still running") book
      let book, answers = WorkerReaper.finish session 101 WorkerReaper.Fate.Left book
      answers |> Expect.equal "the caller is told who survived" [ "caller", WorkerReaper.Settled.Survivors [ 100 ] ]
      WorkerReaper.watchedPids book |> Expect.equal "the survivor is still watched" [ 100 ]
      let _, later = WorkerReaper.await session "late" book
      later |> Expect.equal "a later caller hears the same, at once" (WorkerReaper.Standing.Done (WorkerReaper.Settled.Survivors [ 100 ]))

    testCase "WHY — a second stop of the same session adds to what is exiting" <| fun _ ->
      let book = WorkerReaper.empty<int> |> WorkerReaper.start session [ 100 ] |> WorkerReaper.start session [ 101 ]
      WorkerReaper.standing session book |> Expect.equal "both exiting" (WorkerReaper.Standing.StillExiting [ 100; 101 ])

    testCase "WHY — two workers with the same pid (a fake runtime, a reused handle) are both waited for" <| fun _ ->
      let book = WorkerReaper.empty<int> |> WorkerReaper.start session [ 100; 100 ]
      let book, _ = WorkerReaper.finish session 100 WorkerReaper.Fate.Left book
      WorkerReaper.standing session book |> Expect.equal "one still exiting" (WorkerReaper.Standing.StillExiting [ 100 ])

    testProperty "WHY — however fates arrive, every waiter is answered exactly once, after the last pid, with the survivors named" <| fun (pidCount: byte) (survives: bool list) (waiterCount: byte) (seed: int) ->
      let pids = [ 1 .. int (pidCount % 6uy) + 1 ]
      let waiters = [ 1 .. int (waiterCount % 4uy) ]
      let order = pids |> List.sortBy (fun pid -> (seed * 31 + pid * 17) % 97)
      let parked = waiters |> List.fold (fun book w -> fst (WorkerReaper.await session w book)) (WorkerReaper.empty<int> |> WorkerReaper.start session pids)
      let fates = pids |> List.mapi (fun i pid -> pid, fateOfIndex survives i) |> Map.ofList
      let _, answers =
        order
        |> List.fold
          (fun (book, answered) pid ->
            let book', now = WorkerReaper.finish session pid fates.[pid] book
            book', answered @ now)
          (parked, [])
      let expectedSurvivors = pids |> List.filter (fun pid -> match fates.[pid] with WorkerReaper.Fate.Outlived _ -> true | WorkerReaper.Fate.Left -> false) |> List.sort
      let settledOk =
        match expectedSurvivors with
        | [] -> answers |> List.forall (fun (_, s) -> s = WorkerReaper.Settled.AllGone)
        | _ -> answers |> List.forall (fun (_, s) -> match s with WorkerReaper.Settled.Survivors named -> List.sort named = expectedSurvivors | _ -> false)
      (List.sort (List.map fst answers) = waiters) && settledOk

    testCase "WHY — the stuck-mailbox message names the bound, the setting that changes it, and says the mailbox is the suspect" <| fun _ ->
      let message = WorkerReaper.mailboxStuckMessage Timeouts.stopSessionMailboxTimeout
      message |> Expect.stringContains "names the bound" (sprintf "within %.0fs" Timeouts.stopSessionMailboxTimeout.TotalSeconds)
      message |> Expect.stringContains "says the mailbox may be stuck" "session mailbox may be stuck"
      message |> Expect.stringContains "names the setting" "SAGEFS_STOP_SESSION_TIMEOUT_SECONDS"

    testCase "WHY — the stopped message says the session is stopped and its worker is still exiting, never that the worker is gone" <| fun _ ->
      let message = WorkerReaper.stoppedMessage "abc123"
      message |> Expect.stringContains "names the session" "'abc123'"
      message |> Expect.stringContains "says the worker is exiting" "worker process is exiting"

    testCase "WHY — what purge says depends on whether the worker is gone, survived, or unknown" <| fun _ ->
      WorkerReaper.purgedMessage "s" WorkerReaper.Settled.AllGone |> Expect.stringContains "gone" "worker gone"
      WorkerReaper.purgedMessage "s" (WorkerReaper.Settled.Survivors [ 42 ]) |> Expect.stringContains "survivor pid" "worker pid 42"
      WorkerReaper.purgedMessage "s" (WorkerReaper.Settled.Unknown "boom") |> Expect.stringContains "unknown" "unknown: boom"
  ]
