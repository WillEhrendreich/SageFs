/// The outcome, against a real FSI host: an eval that recurses far, recurses forever, loops, or sits in a native call.
/// Found live on 0.6.940: `f 1000000` (a plain non-tail recursion) killed the host with a trace and no word about a stack,
/// cancelling a loop ended the call while the host kept running it, and the next eval then waited behind a run nobody was
/// waiting for, with the session reading Ready and hard_reset_fsi_session refused because it read Evaluating.
module SageFs.Tests.EvalHangHostTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.AppState
open SageFs.ActorCreation
open SageFs.WorkerProtocol
open SageFs.Tests
open SageFs.Tests.TestInfrastructure

let private recursionDefinition = "let rec depth n = if n = 0 then 0 else 1 + depth (n - 1);;"

/// A plain non-tail recursion nothing stops: it ends at the end of the stack. Defined, then called, in two evals.
let private recursionWithoutEnd = "let rec forever (n: int) : int = 1 + forever (n + 1);;"

let private callsItForever = "forever 0;;"

/// A wait with no end: cancelling interrupts it.
let private sleeps = "(new System.Threading.ManualResetEvent(false)).WaitOne() |> ignore;;"

/// A loop with nothing in it: the runtime has no point to stop it at.
/// (A native sleep is not a fixture: the interrupt's signal ends `sleep` early on some runs, so whether it stops varies.)
let private spins = "while true do ();;"

let private startSession () : Task<ActorResult> =
  let args = { mkCommonActorArgs TestInfrastructure.quietLogger false ignore SageFs.Args.ProjectLoadConfig.empty with FsiKind = SessionKinds.Isolated }
  createActor args

let private eval (result: ActorResult) (code: string) : Task<EvalResponse> =
  result.Actor.PostAndAsyncReply(fun reply -> Eval({ Code = code; Args = Map.empty }, CancellationToken.None, reply))
  |> Started.asTask

let private evalWith (result: ActorResult) (token: CancellationToken) (code: string) : Task<EvalResponse> =
  result.Actor.PostAndAsyncReply(fun reply -> Eval({ Code = code; Args = Map.empty }, token, reply))
  |> Started.asTask

/// A legitimate long eval: it waits on an event nobody sets for a few seconds, then returns the value it defines.
let private slowButLegitimate =
  "let aDefined = ((new System.Threading.ManualResetEvent(false)).WaitOne(8000) |> ignore; 42);;"

let private cancel (result: ActorResult) : Task<bool> =
  result.Actor.PostAndAsyncReply(fun reply -> CancelEval reply) |> Started.asTask

let private hardReset (result: ActorResult) =
  result.Actor.PostAndAsyncReply(fun reply -> HardResetSession(false, reply)) |> Started.asTask

let private within (bound: TimeSpan) (pending: Task<'a>) : Task<'a> =
  task {
    let! first = Task.WhenAny(pending :> Task, Task.Delay bound)
    match obj.ReferenceEquals(first, pending) with
    | true -> return! pending
    | false -> return failtestf "did not complete within %A" bound
  }

/// Wait until the session reads Evaluating, so a cancel meets a run and not a queue.
let private untilEvaluating (result: ActorResult) : Task<unit> =
  task {
    let deadline = DateTime.UtcNow + TestTimeouts.patience
    while result.GetSessionStatus() <> SessionStatus.Evaluating && DateTime.UtcNow < deadline do
      do! Task.Delay TestTimeouts.pollQuick
    result.GetSessionStatus() |> Expect.equal "the eval is running" SessionStatus.Evaluating
  }

let private crashOf (result: ActorResult) : HostCrash =
  match result.GetSessionStatus() with
  | SessionStatus.HostCrashed crash -> crash
  | other -> failtestf "expected the session to report a dead host, got %A" other

/// The bound a cancelled eval that never confirms is held to: the grace, the time to report the death, and the usual patience.
let private escalationBound = Timeouts.evalCancelAnswer + TestTimeouts.patience

[<Tests>]
let tests =
  Integration.hostList "an isolated session whose eval runs away" [

    testTask "a recursion 4 million frames deep returns, where 8 MB of stack ended the host near 520,000" {
      let! session = startSession ()
      let! defined = within TestTimeouts.patience (eval session recursionDefinition)
      defined.EvaluationResult |> Result.isOk |> Expect.isTrue "the definition is accepted"
      let! deep = within escalationBound (eval session "depth 4000000;;")
      match deep.EvaluationResult with
      | Result.Ok text -> text |> Expect.stringContains "the depth came back" "4000000"
      | Result.Error error -> failtestf "a deep recursion must return, got %A" error
      session.GetSessionStatus() |> Expect.equal "the session never left Ready" SessionStatus.Ready
    }

    testTask "a stack overflow ends the host with a report that says so, and a hard reset brings the session back" {
      let! session = startSession ()
      let! defined = within TestTimeouts.patience (eval session recursionWithoutEnd)
      defined.EvaluationResult |> Result.isOk |> Expect.isTrue "the definition is accepted"
      let! overflowed = within escalationBound (eval session callsItForever)
      overflowed.EvaluationResult |> Result.isError |> Expect.isTrue "the eval did not return a value"
      let crash = crashOf session
      crash.Exit |> Expect.equal "the host's end is named as an overflow, not an exit code" OverflowedStack
      crash.Output |> Expect.stringContains "the runtime's own report is kept" "Stack overflow"
      let described = HostCrash.describe crash
      described |> Expect.stringContains "the text says it was the stack" "stack overflow"
      described |> Expect.stringContains "the text says what was lost" "definitions are gone"
      described |> Expect.stringContains "the text says what to change" "tail-recursive"
      session.GetSessionState() |> Expect.equal "reset is offered" SessionState.Faulted

      let! reset = within escalationBound (hardReset session)
      reset |> Result.isOk |> Expect.isTrue "the hard reset succeeds"
      session.GetSessionStatus() |> Expect.equal "Ready again" SessionStatus.Ready
      let! again = within TestTimeouts.patience (eval session "1 + 1;;")
      again.EvaluationResult |> Result.isOk |> Expect.isTrue "the fresh host evaluates"
    }

    testTask "agent B queued behind agent A's long eval times out: B is cancelled before it ran, A finishes, and A's definitions survive" {
      let! session = startSession ()
      let a = evalWith session CancellationToken.None slowButLegitimate
      do! untilEvaluating session
      let timeoutOfB = new CancellationTokenSource()
      let b = evalWith session timeoutOfB.Token "let bDefined = 1;;"
      // B reaches the host's queue behind A before its timeout fires.
      do! Task.Delay TestTimeouts.startSettle
      timeoutOfB.Cancel()
      let! aResult = within escalationBound a
      aResult.EvaluationResult |> Result.isOk |> Expect.isTrue "A's eval was not touched by B's cancel"
      let! bResult = within escalationBound b
      match bResult.EvaluationResult with
      | Result.Error error -> error.Message |> Expect.stringContains "B is told it was queued and never ran" "before it ran"
      | Result.Ok _ -> failtest "B was cancelled while queued and must not have run"
      session.GetSessionStatus() |> Expect.equal "the host was not killed" SessionStatus.Ready
      let! survived = within TestTimeouts.patience (eval session "aDefined;;")
      match survived.EvaluationResult with
      | Result.Ok text -> text |> Expect.stringContains "A's definition is still in the session" "42"
      | Result.Error error -> failtestf "A's definition was lost: %A" error
    }

    testTask "cancelling a wait stops it in the host, the session is Ready, and the next eval runs" {
      let! session = startSession ()
      let running = eval session sleeps
      do! untilEvaluating session
      let! cancelled = within TestTimeouts.patience (cancel session)
      cancelled |> Expect.isTrue "a running eval was found and cancelled"
      let! stopped = within escalationBound running
      stopped.EvaluationResult |> Result.isError |> Expect.isTrue "the cancelled eval reports that it did not finish"
      session.GetSessionStatus() |> Expect.equal "Ready, with nothing still running in the host" SessionStatus.Ready
      let! next = within TestTimeouts.patience (eval session "1 + 1;;")
      next.EvaluationResult |> Result.isOk |> Expect.isTrue "the next eval is not queued behind the run"
    }

    testTask "a loop with nothing in it does not stop on cancel: the host is stopped after the grace and the session says what was lost" {
      let! session = startSession ()
      let running = eval session spins
      do! untilEvaluating session
      let! cancelled = within TestTimeouts.patience (cancel session)
      cancelled |> Expect.isTrue "a running eval was found and cancelled"
      let! answered = within escalationBound running
      answered.EvaluationResult |> Result.isError |> Expect.isTrue "the eval was answered, it did not hang"
      let crash = crashOf session
      match crash.Exit with
      | StoppedAfterCancel waited -> waited |> Expect.equal "the grace that applied" Timeouts.evalCancelGrace
      | other -> failtestf "expected the host to be stopped after the cancel, got %A" other
      HostCrash.describe crash |> Expect.stringContains "the text says what was lost" "definitions are gone"

      let! reset = within escalationBound (hardReset session)
      reset |> Result.isOk |> Expect.isTrue "the hard reset succeeds"
      let! again = within TestTimeouts.patience (eval session "1 + 1;;")
      again.EvaluationResult |> Result.isOk |> Expect.isTrue "the fresh host evaluates"
    }
  ]
