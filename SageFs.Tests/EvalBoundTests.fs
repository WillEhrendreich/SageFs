/// An eval caller gets an answer in a bounded time, and a host that ended because of the eval says why.
/// Found live on 0.6.940: `send_fsharp_code` had no timeout of its own (the worker call's ten minutes was the only bound), a
/// stack overflow reported the last 40 lines of a trace with the word "Stack overflow" cut off the top, and cancelling an eval
/// the host could not stop left the session Evaluating with no way out. The decision is `EvalWait.next`; the caller's loop is
/// `EvalWait.run`; the whole thing against a real host is in EvalHangHostTests, the interleavings in EvalBoundSimTests.
module SageFs.Tests.EvalBoundTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs

let private secs (n: float) = TimeSpan.FromSeconds n
let private millis (n: float) = TimeSpan.FromMilliseconds n

let private cap = secs 600.
let private defaultLimit = secs 120.

[<Tests>]
let tests =
  testList "the bound on an eval call" [

    testList "timeout_seconds" [
      testProperty "whatever is asked, the limit is positive and never above the cap" <| fun (asked: int) ->
        let span = EvalLimit.ofRequest defaultLimit cap asked |> EvalLimit.span
        span > TimeSpan.Zero && span <= cap

      testCase "nothing asked, or a figure that is not positive, is the product's default" <| fun _ ->
        for asked in [ 0; -1; Int32.MinValue ] do
          EvalLimit.ofRequest defaultLimit cap asked |> Expect.equal (sprintf "asked %d" asked) (EvalLimit.Default defaultLimit)

      testCase "a figure within the cap is taken as asked" <| fun _ ->
        EvalLimit.ofRequest defaultLimit cap 30 |> Expect.equal "30 s" (EvalLimit.Asked(secs 30.))
        EvalLimit.ofRequest defaultLimit cap 600 |> Expect.equal "the cap itself" (EvalLimit.Asked cap)

      testCase "a figure past the cap is cut to it, and the answer can say so" <| fun _ ->
        EvalLimit.ofRequest defaultLimit cap 9999 |> Expect.equal "cut" (EvalLimit.Capped(9999, cap))

      testCase "the product's default is 120 seconds and may not exceed what a worker call is cut at" <| fun _ ->
        Timeouts.evalCall |> Expect.equal "FsiMcp's default" (secs 120.)
        Expect.isTrue "the default fits under the cap" (Timeouts.evalCall <= Timeouts.evalCallCap)
        Expect.isTrue "the cap is the worker's own bound" (Timeouts.evalCallCap = Timeouts.workerHttpRequest)
    ]

    testList "the decision" [
      let limit = secs 120.
      let answerWait = secs 30.

      testCase "before the limit the caller keeps waiting for what is left" <| fun _ ->
        EvalWait.next limit answerWait (secs 5.) EvalWaitStage.Running
        |> Expect.equal "115 s left" (EvalWaitStep.KeepWaiting(secs 115.))

      testCase "at the limit the eval is asked to stop" <| fun _ ->
        EvalWait.next limit answerWait limit EvalWaitStage.Running |> Expect.equal "ask" EvalWaitStep.AskCancel

      testCase "after the ask the caller waits out the answer bound, then answers without the eval" <| fun _ ->
        let asked = EvalWaitStage.CancelAsked limit
        EvalWait.next limit answerWait (secs 130.) asked |> Expect.equal "20 s left" (EvalWaitStep.KeepWaiting(secs 20.))
        EvalWait.next limit answerWait (secs 150.) asked |> Expect.equal "give up" EvalWaitStep.GiveUp

      testProperty "the caller is never told to keep waiting past the limit plus the answer bound" <| fun (elapsedMs: uint16) ->
        let elapsed = millis (float elapsedMs * 10.)
        let asked = EvalWaitStage.CancelAsked limit
        match EvalWait.next limit answerWait elapsed asked with
        | EvalWaitStep.KeepWaiting remaining -> elapsed + remaining <= limit + answerWait
        | EvalWaitStep.GiveUp -> elapsed >= limit + answerWait
        | EvalWaitStep.AskCancel -> false
    ]

    testList "the wait" [
      testTask "an eval that answers in time is passed through and never cancelled" {
        let cancelled = ref 0
        let! waited = EvalWait.run (secs 5.) (secs 5.) (fun () -> Task.FromResult 7) (fun () -> cancelled.Value <- cancelled.Value + 1; Task.CompletedTask)
        waited |> Expect.equal "finished" (EvalWait.Waited.Finished 7)
        cancelled.Value |> Expect.equal "no cancel" 0
      }

      testTask "an eval that stops when cancelled answers after the cancel, which is named" {
        let call = TaskCompletionSource<int>()
        let! waited = EvalWait.run (millis 100.) (secs 5.) (fun () -> call.Task) (fun () -> call.TrySetResult 42 |> ignore; Task.CompletedTask)
        waited |> Expect.equal "finished after cancel" (EvalWait.Waited.FinishedAfterCancel 42)
      }

      testTask "an eval that never answers still gets the caller an answer, after one cancel" {
        let call = TaskCompletionSource<int>()
        let cancelled = ref 0
        let clock = Diagnostics.Stopwatch.StartNew()
        let! waited = EvalWait.run (millis 100.) (millis 200.) (fun () -> call.Task) (fun () -> cancelled.Value <- cancelled.Value + 1; Task.CompletedTask)
        waited |> Expect.equal "gave up" EvalWait.Waited.GaveUp
        cancelled.Value |> Expect.equal "asked once" 1
        Expect.isTrue "not before the limit and the answer bound" (clock.Elapsed >= millis 290.)
      }

      testTask "a call with no time left is never started" {
        let started = ref 0
        let! waited = EvalWait.run TimeSpan.Zero (secs 5.) (fun () -> started.Value <- started.Value + 1; Task.FromResult 1) (fun () -> Task.CompletedTask)
        waited |> Expect.equal "not started" EvalWait.Waited.NotStarted
        started.Value |> Expect.equal "the eval never began" 0
      }

      testTask "reply puts the cancelled-at-limit note before an answer that came after the cancel" {
        let call = TaskCompletionSource<Result<int, string>>()
        let! answer, note = EvalWait.reply (secs 30.) (millis 50.) (secs 5.) id (fun () -> call.Task) (fun () -> call.TrySetResult(Result.Ok 1) |> ignore; Task.CompletedTask)
        answer |> Expect.equal "the eval's own answer" (Result.Ok 1)
        note |> Expect.stringContains "the figure the caller asked for, not the time left" "timeout_seconds (30 s)"
      }

      testTask "reply turns a silent eval into a refusal that says what to do, and a spent budget into one that says it was not run" {
        let silent = TaskCompletionSource<Result<int, string>>()
        let! gaveUp, _ = EvalWait.reply (secs 30.) (millis 50.) (millis 50.) id (fun () -> silent.Task) (fun () -> Task.CompletedTask)
        match gaveUp with
        | Result.Error text -> text |> Expect.stringContains "the way out" "hard_reset_fsi_session"
        | Result.Ok _ -> failtest "a silent eval has no answer"
        let! unstarted, _ = EvalWait.reply (secs 30.) TimeSpan.Zero (millis 50.) id (fun () -> silent.Task) (fun () -> Task.CompletedTask)
        match unstarted with
        | Result.Error text -> text |> Expect.stringContains "that it did not run" "Not run"
        | Result.Ok _ -> failtest "a spent budget runs nothing"
      }

      testTask "a cancel that throws costs the caller nothing" {
        let call = TaskCompletionSource<int>()
        let! waited = EvalWait.run (millis 50.) (millis 100.) (fun () -> call.Task) (fun () -> failwith "the worker is gone")
        waited |> Expect.equal "still answered" EvalWait.Waited.GaveUp
      }
    ]

    testList "what the caller reads" [
      testCase "cancelled at the limit: the figure, the state and the way to ask for longer" <| fun _ ->
        let text = EvalBoundMessage.cancelledAtLimit (secs 30.)
        text |> Expect.stringContains "the figure that applied" "timeout_seconds (30 s)"
        text |> Expect.stringContains "what became of the eval" "was cancelled"
        text |> Expect.stringContains "what is intact" "definitions from before it are intact"
        text |> Expect.stringContains "how to ask for longer" "up to 600"

      testCase "an eval that did not stop: what may still run and what to do" <| fun _ ->
        let text = EvalBoundMessage.didNotStop (secs 30.) (secs 20.)
        text |> Expect.stringContains "the limit" "30 s"
        text |> Expect.stringContains "that it did not stop" "had not answered"
        text |> Expect.stringContains "the way out" "hard_reset_fsi_session"

      testCase "a statement left unrun by the call's budget says so and says what to do" <| fun _ ->
        let text = EvalBoundMessage.budgetSpent (secs 30.)
        text |> Expect.stringContains "that it did not run" "Not run"
        text |> Expect.stringContains "what to do" "call of its own"
    ]

    testList "a host that ended because of the eval" [
      let report = [| "Stack overflow."; "Repeated 261771 times:"; "--------------------------------"; "   at FSI_0004.f(Int32)"; "--------------------------------" |]

      testCase "a stack overflow says it was the stack, what was lost, and what to change" <| fun _ ->
        let crash = HostCrash.ofStackOverflow report
        crash.Exit |> Expect.equal "named as an overflow" OverflowedStack
        let text = HostCrash.describe crash
        text |> Expect.stringContains "the cause" "stack overflow"
        text |> Expect.stringContains "that the state is gone" "definitions are gone"
        text |> Expect.stringContains "the recursing function, from the runtime's own report" "FSI_0004.f(Int32)"
        text |> Expect.stringContains "how often it repeated" "261771"
        text |> Expect.stringContains "the fix" "tail-recursive"

      testCase "the overflow report is bounded to its first lines, not the frames after" <| fun _ ->
        let long = Array.append report (Array.create 500 "   at some.frame()")
        (HostCrash.ofStackOverflow long).Output.Split('\n').Length
        |> Expect.equal "the lines kept" HostCrash.stackOverflowReportLines

      testCase "the runtime's overflow line is recognised and an ordinary line is not" <| fun _ ->
        HostCrash.isStackOverflowLine "Stack overflow." |> Expect.isTrue "the runtime's line"
        HostCrash.isStackOverflowLine "Unhandled exception. System.StackOverflowException" |> Expect.isFalse "an exception text is not the runtime's report"
        HostCrash.isStackOverflowLine "printfn Stack overflow" |> Expect.isFalse "not at the start of the line"
        HostCrash.isStackOverflowLine null |> Expect.isFalse "null"

      testCase "a host stopped after a cancel says the eval did not stop, what was lost, and for how long" <| fun _ ->
        let text = HostCrash.describe (HostCrash.ofTail (StoppedAfterCancel(secs 10.)) [| "last line" |])
        text |> Expect.stringContains "the cause" "did not stop within 10 seconds of being cancelled"
        text |> Expect.stringContains "that the state is gone" "definitions are gone"
        text |> Expect.stringContains "the host's last output" "last line"

      testCase "the output tail keeps the overflow report apart from the trace that follows it" <| fun _ ->
        let tail = FsiHostClient.HostOutputTail()
        tail.Add "some earlier output"
        for line in report do tail.Add line
        for _ in 1 .. 200 do tail.Add "   at System.Reflection.MethodBaseInvoker.InvokeWithFewArgs()"
        tail.StackOverflowReport() |> Expect.equal "the first lines of the report" (Array.truncate HostCrash.stackOverflowReportLines report)
        tail.Lines().Length |> Expect.equal "the tail is still bounded" HostCrash.maxOutputLines

      testCase "an output with no overflow has no report" <| fun _ ->
        let tail = FsiHostClient.HostOutputTail()
        tail.Add "Unhandled exception. System.Exception: boom"
        tail.StackOverflowReport() |> Expect.equal "none" [||]

      testCase "the host's eval thread has 256 MB of stack, a fraction of which a deep recursion touches" <| fun _ ->
        SageFs.FsiHost.FsiProtocol.HostLimits.EvalStackBytes |> Expect.equal "256 MB" (256 * 1024 * 1024)
    ]
  ]
