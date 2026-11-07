/// A submission of several statements through the dashboard (the Evaluate box, eval-file, and the live bindings' Try, Reset and Revert).
/// The worker runs the first `;;` interaction of the text it is given and answers Ok, so passing the whole text on drops every later
/// statement without a word. The shared evaluation runs the statements one at a time, in order, and a failure says which statement it was
/// and what it was.
module SageFs.Tests.EvalInOrderTests

open System
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
