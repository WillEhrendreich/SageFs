module SageFs.Tests.EventuallyTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Tests.Eventually

let private budget = TestTimeouts.patience
let private shortBudget = TestTimeouts.settle

[<Tests>]
let tests =
  testList "Eventually — the one wait primitive" [

    testTask "WHY — a read of None is not yet, and the fact ends the wait immediately" {
      let mutable calls = 0
      let probe () =
        task {
          calls <- calls + 1
          return if calls < 3 then Ok None else Ok(Some 42)
        }
      let! value = await budget (fun () -> "the answer") probe
      value |> Expect.equal "the fact, not the number of polls" 42
      calls |> Expect.equal "stopped as soon as the fact appeared" 3
    }

    testTask "WHY — a typed infrastructure transient is retried, never treated as a verdict" {
      let mutable calls = 0
      let probe () =
        task {
          calls <- calls + 1
          if calls < 3 then return Error(Transient.NotListening "connection refused (127.0.0.1:1)")
          else return Ok(Some "served")
        }
      let! value = await budget (fun () -> "the route") probe
      value |> Expect.equal "the served body" "served"
      calls |> Expect.equal "two transients then the fact" 3
    }

    testTask "WHY — an untyped exception is a verdict and propagates without retry" {
      let mutable calls = 0
      let probe () =
        task {
          calls <- calls + 1
          return raise (InvalidOperationException("the program did the wrong thing"))
        }
      let! outcome =
        task {
          try
            let! _ = await budget (fun () -> "never") probe
            return "no error"
          with :? InvalidOperationException -> return "verdict propagated"
        }
      outcome |> Expect.equal "a verdict is never retried" "verdict propagated"
      calls |> Expect.equal "called once" 1
    }

    testTask "WHY — an expired wait fails with a typed WaitTimedOut, distinct from an assertion" {
      let probe () = task { return Error(Transient.WarmingUp "still warming up") }
      let! outcome =
        task {
          try
            let! _ = await shortBudget (fun () -> "the settled session") probe
            return Choice1Of2 ()
          with WaitTimedOut e -> return Choice2Of2 e
        }
      match outcome with
      | Choice1Of2 () -> failtest "the wait must fail when the deadline expires"
      | Choice2Of2 e ->
        e.Description |> Expect.equal "names what was waited for" "the settled session"
        e.LastTransient |> Expect.isSome "keeps the last infrastructure reason"
        Expect.isTrue "polls at least once" (e.Observations > 0)
    }

    testTask "WHY — the fact still ends the wait under synthetic CPU contention" {
      let until = DateTime.UtcNow.Add TestTimeouts.pollTight
      let busy () =
        let mutable x = 1UL
        while DateTime.UtcNow < until do
          let iterations = 10 * 1000 in for _ in 1 .. iterations do x <- x * 6364136223846793005UL + 1442695040888963407UL
        if x = 0UL then failwith "unreachable"
      let threads = [ for _ in 1 .. Environment.ProcessorCount -> Thread(busy, IsBackground = true) ]
      for t in threads do t.Start ()
      try
        let mutable calls = 0
        let! value =
          await TestTimeouts.patience (fun () -> "the fact under load") (fun () -> task {
            calls <- calls + 1
            return if calls = 3 then Ok(Some "served") else Ok None })
        value |> Expect.equal "load did not change the answer" "served"
        calls |> Expect.equal "load did not change when the fact appeared" 3
      finally
        for t in threads do t.Join ()
    }

    testTask "WHY — awaitTrue ends on the predicate and needs no value" {
      let mutable calls = 0
      do! awaitTrue budget (fun () -> "the flag") (fun () -> task {
        calls <- calls + 1
        return calls = 2 })
      calls |> Expect.equal "ended on the second observation" 2
    }
  ]
