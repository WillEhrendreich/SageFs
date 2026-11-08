/// A submission of several statements through the dashboard (the Evaluate box, eval-file, and the live bindings' Try, Reset and Revert).
/// The worker runs the first `;;` interaction of the text it is given and answers Ok, so passing the whole text on drops every later
/// statement without a word. The shared evaluation runs the statements one at a time, in order, and a failure says which statement it was
/// and what it was.
module SageFs.Tests.EvalInOrderTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs

/// The worker as observed in the browser journey: given text with several `;;`, it runs the first interaction and answers Ok. A statement
/// that says `boom` fails.
type FakeWorker() =
  let ran = ResizeArray<string>()
  member _.Ran = List.ofSeq ran
  member _.Eval(text: string) : Task<Result<string, string>> =
    task {
      let first = text.Split([| ";;" |], StringSplitOptions.None).[0].Trim()
      match first.Contains "boom" with
      | true -> return Error "boom is not defined"
      | false ->
        ran.Add first
        return Ok(sprintf "ran %s" first)
    }

/// A worker whose every statement waits for the test to let it finish, and which records the order statements ENTERED it.
type Turnstile() =
  let entered = ResizeArray<string>()
  let permits = new SemaphoreSlim(0)
  member _.Entered : string list = lock entered (fun () -> List.ofSeq entered)
  member _.LetThrough(count: int) : unit = permits.Release count |> ignore
  member _.Eval(text: string) : Task<Result<string, string>> =
    task {
      lock entered (fun () -> entered.Add(text.Trim().TrimEnd(';')))
      do! permits.WaitAsync()
      return Ok text
    }

/// What the Evaluate box did before: the whole text, once.
let wholeTextOnce (worker: FakeWorker) (statements: string list) : Task<Result<string, string>> =
  worker.Eval(String.concat "\n" statements)

[<Tests>]
let tests =
  testList "a submission of several statements" [

    testTask "the twin that hands the whole text to the worker runs only the first statement and answers Ok, which is the defect" {
      let worker = FakeWorker()
      let! answer = wholeTextOnce worker [ "let a = 1;;"; "let b = 2;;"; "let c = 3;;" ]
      answer |> Expect.isOk "it says Ok"
      worker.Ran |> Expect.equal "and only the first ran" [ "let a = 1" ]
    }

    testTask "WHY — every statement runs, in order, each as its own interaction" {
      let worker = FakeWorker()
      let! outcome = EvalSequence.evalInOrder worker.Eval [ "let a = 1;;"; "let b = 2;;"; "let c = 3;;" ]
      worker.Ran |> Expect.equal "all three, in order" [ "let a = 1"; "let b = 2"; "let c = 3" ]
      outcome |> Expect.equal "with their answers" (EvalSequence.SequenceOutcome.Ran [ "ran let a = 1"; "ran let b = 2"; "ran let c = 3" ])
    }

    testTask "WHY — a statement that fails stops the rest and is named: which one, what it was, and what it said" {
      let worker = FakeWorker()
      let! outcome = EvalSequence.evalInOrder worker.Eval [ "let a = 1;;"; "let b = boom;;"; "let c = 3" ]
      worker.Ran |> Expect.equal "the one before ran and the one after did not" [ "let a = 1" ]
      match outcome with
      | EvalSequence.SequenceOutcome.StoppedAt(position, total, statement, reason, before) ->
        position |> Expect.equal "the second" 2
        total |> Expect.equal "of three" 3
        statement |> Expect.equal "what it was" "let b = boom;;"
        reason |> Expect.equal "what it said" "boom is not defined"
        before |> Expect.equal "what ran before it" [ "ran let a = 1" ]
        EvalSequence.describeStop position total statement reason
        |> Expect.equal "said in words" "Statement 2 of 3 failed: let b = boom;;\nThe error: boom is not defined\nThe 1 statement after it did not run."
      | other -> failtestf "expected a stop, got %A" other
    }

    testTask "a statement without its `;;` gets one, because the terminator is what makes it an interaction" {
      let seen = ResizeArray<string>()
      let record (text: string) : Task<Result<string, string>> =
        task {
          seen.Add text
          return Ok text
        }
      let! _ = EvalSequence.evalInOrder record [ "let a = 1;;"; "let b = 2" ]
      List.ofSeq seen |> Expect.equal "both end with the terminator" [ "let a = 1;;"; "let b = 2;;" ]
    }

    testCase "the words for a stop say how many did not run, and say nothing when the last one failed" <| fun _ ->
      (EvalSequence.describeStop 1 3 "x;;" "bad").Contains "The 2 statements after it did not run." |> Expect.isTrue "two"
      (EvalSequence.describeStop 3 3 "x;;" "bad").Contains "did not run" |> Expect.isFalse "the last one has nothing after it"

    testList "a submission to a session" [

      testTask "WHY — a paste of thirty statements pulls the live bindings once, after the last, not thirty times" {
        let worker = FakeWorker()
        let pulls = ref 0
        let statements = [ for i in 1 .. 30 -> sprintf "let s%d = %d;;" i i ]
        let! outcome = EvalSequence.submit (SubmissionGates()) "s1" (fun () -> pulls.Value <- pulls.Value + 1) worker.Eval statements
        outcome |> EvalSequence.ranAnything |> Expect.isTrue "every statement ran"
        pulls.Value |> Expect.equal "one pull for the whole paste" 1
      }

      testTask "a submission that stops partway still pulls once, because the statements before the failure changed the values" {
        let worker = FakeWorker()
        let pulls = ref 0
        let! _ = EvalSequence.submit (SubmissionGates()) "s1" (fun () -> pulls.Value <- pulls.Value + 1) worker.Eval [ "let a = 1;;"; "let b = 2;;"; "let c = boom;;"; "let d = 4;;" ]
        pulls.Value |> Expect.equal "once" 1
      }

      testTask "a submission whose first statement fails pulls nothing, because no value changed" {
        let worker = FakeWorker()
        let pulls = ref 0
        let! _ = EvalSequence.submit (SubmissionGates()) "s1" (fun () -> pulls.Value <- pulls.Value + 1) worker.Eval [ "let a = boom;;"; "let b = 2;;" ]
        pulls.Value |> Expect.equal "none" 0
      }

      testTask "WHY — two submissions to one session do not interleave: the second starts when the first has finished all of its statements" {
        let gates = SubmissionGates()
        let turnstile = Turnstile()
        let submitting (statements: string list) : Task<EvalSequence.SequenceOutcome> =
          Started.asTask (async { return! EvalSequence.submit gates "s1" ignore turnstile.Eval statements |> Async.AwaitTask })
        let first = submitting [ "a1;;"; "a2;;"; "a3;;" ]
        let second = submitting [ "b1;;"; "b2;;"; "b3;;" ]
        turnstile.LetThrough 6
        let! _ = Task.WhenAll([ first; second ]).WaitAsync TestTimeouts.patience
        turnstile.Entered |> Expect.equal "all of the first, then all of the second" [ "a1"; "a2"; "a3"; "b1"; "b2"; "b3" ]
      }

      testTask "a submission to one session never waits for another session's" {
        let gates = SubmissionGates()
        let stuck = Turnstile()
        let held = Started.asTask (async { return! EvalSequence.submit gates "s1" ignore stuck.Eval [ "a1;;" ] |> Async.AwaitTask })
        let free = FakeWorker()
        let! _ = (EvalSequence.submit gates "s2" ignore free.Eval [ "b1;;" ]).WaitAsync TestTimeouts.patience
        free.Ran |> Expect.equal "the other session's statement ran while the first was held" [ "b1" ]
        stuck.LetThrough 1
        let! _ = held.WaitAsync TestTimeouts.patience
        ()
      }
    ]

    testTask "WHY — for every length up to six and every statement that fails, exactly the ones before it ran and none after" {
      for count in 1..6 do
        for failAt in 0..5 do
          let statements = List.init count (fun i -> match i = failAt with | true -> sprintf "let s%d = boom;;" i | false -> sprintf "let s%d = %d;;" i i)
          let worker = FakeWorker()
          let! outcome = EvalSequence.evalInOrder worker.Eval statements
          let expectedRan = statements |> List.truncate (min failAt count) |> List.map (fun s -> s.TrimEnd(';'))
          worker.Ran |> Expect.equal (sprintf "%d statements, #%d fails: the ones before it" count failAt) expectedRan
          match outcome with
          | EvalSequence.SequenceOutcome.Ran _ -> (failAt >= count) |> Expect.isTrue "nothing failed"
          | EvalSequence.SequenceOutcome.StoppedAt(position, _, _, _, _) -> position |> Expect.equal "the failing one is named" (failAt + 1)
    }
  ]
