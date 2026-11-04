module SageFs.Tests.ChangeCoalescerTests

// The daemon's model-change hook queued one thread-pool work item per change, and each ran every subscriber
// to completion on its own thread. A change that arrived while an earlier one was still projecting every file
// of a big session started a second projection beside the first. The handlers keep their state in `ref` cells
// written with `<-`, so two passes interleaved; a burst of N output lines meant N passes.
//
// A `Coalescer` has at most one delivery in flight and keeps only the newest value that arrives behind it.

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs

// ── The rules, checked over every interleaving of up to ten posts and finishes ──

/// One state of the transition model: what is in flight, and what has been started so far.
type private Sim =
  { State: CoalesceState<int>
    InFlight: int
    MaxInFlight: int
    Started: int list
    Posted: int }

let private begun (sim: Sim) (state': CoalesceState<int>, step: CoalesceStep<int>) : Sim =
  match step with
  | CoalesceStep.Wait -> { sim with State = state' }
  | CoalesceStep.StartDelivery value ->
    let inFlight = sim.InFlight + 1
    { sim with
        State = state'
        InFlight = inFlight
        MaxInFlight = max sim.MaxInFlight inFlight
        Started = value :: sim.Started }

/// A post, or the end of the delivery in flight (ignored when none is).
let private advance (sim: Sim) (isPost: bool) : Sim =
  match isPost, sim.State with
  | true, state ->
    let posted = sim.Posted + 1
    begun { sim with Posted = posted } (Coalesce.post posted state)
  | false, CoalesceState.Idle -> sim
  | false, state -> begun { sim with InFlight = sim.InFlight - 1 } (Coalesce.finished state)

let rec private drained (sim: Sim) : Sim =
  match sim.State with
  | CoalesceState.Idle -> sim
  | _ -> drained (advance sim false)

let rec private sequencesOf (length: int) : bool list list =
  match length with
  | 0 -> [ [] ]
  | n -> [ for rest in sequencesOf (n - 1) do
             yield true :: rest
             yield false :: rest ]

let private initial : Sim =
  { State = CoalesceState.Idle; InFlight = 0; MaxInFlight = 0; Started = []; Posted = 0 }

// ── The class, on real threads ──

let private onThePool (work: unit -> unit) : unit =
  ThreadPool.QueueUserWorkItem(fun _ -> work ()) |> ignore

[<Tests>]
let tests =
  testList "change coalescer" [

    test "WHY — whatever the order of posts and finishes, one delivery is in flight at a time and the newest post is delivered" {
      for length in 1 .. 10 do
        for ops in sequencesOf length do
          let final = ops |> List.fold advance initial |> drained
          let started = List.rev final.Started
          let shown = ops |> List.map (fun p -> if p then "post" else "finish") |> String.concat ", "
          Expect.isTrue (sprintf "one in flight at a time for [%s]" shown) (final.MaxInFlight <= 1)
          started |> Expect.equal (sprintf "deliveries only move forward for [%s]" shown) (List.sort started)
          match final.Posted with
          | 0 -> started |> Expect.isEmpty "nothing posted, nothing delivered"
          | posted -> List.last started |> Expect.equal (sprintf "the newest post is delivered for [%s]" shown) posted
    }

    test "posts that arrive while a delivery is in flight are delivered once, as the newest" {
      let delivered = List<int>()
      let pending = Queue<unit -> unit>()
      let holder = ref Unchecked.defaultof<Coalescer<int>>
      holder.Value <-
        Coalescer<int>(
          (fun value ->
            delivered.Add value
            // A burst lands while the first delivery is running.
            if value = 1 then [ 2; 3; 4 ] |> List.iter holder.Value.Post),
          pending.Enqueue)
      holder.Value.Post 1
      while pending.Count > 0 do (pending.Dequeue ()) ()
      List.ofSeq delivered |> Expect.equal "the first, then only the newest of the burst" [ 1; 4 ]
    }

    test "posts that arrive one after another each get their own delivery" {
      let delivered = List<int>()
      let pending = Queue<unit -> unit>()
      let coalescer = Coalescer<int>(delivered.Add, pending.Enqueue)
      for value in [ 1; 2; 3 ] do
        coalescer.Post value
        while pending.Count > 0 do (pending.Dequeue ()) ()
      List.ofSeq delivered |> Expect.equal "all three" [ 1; 2; 3 ]
    }

    test "WHY — a delivery that throws does not strand the value waiting behind it" {
      let delivered = List<int>()
      let pending = Queue<unit -> unit>()
      let holder = ref Unchecked.defaultof<Coalescer<int>>
      holder.Value <-
        Coalescer<int>(
          (fun value ->
            delivered.Add value
            if value = 1 then
              holder.Value.Post 2
              failwith "the first delivery fails"),
          pending.Enqueue)
      holder.Value.Post 1
      let first = pending.Dequeue ()
      Expect.throws "the failure still surfaces where it always did" first
      while pending.Count > 0 do (pending.Dequeue ()) ()
      List.ofSeq delivered |> Expect.equal "the waiting value got its own delivery" [ 1; 2 ]
    }

    testTask "WHY — a burst that lands during a slow delivery on real threads is delivered once, newest, and never beside it" {
      let delivered = List<int>()
      let running = [| 0 |]
      let overlapped = [| false |]
      let slowStarted = TaskCompletionSource()
      let letItFinish = new ManualResetEventSlim(false)
      let last = TaskCompletionSource()
      let burst = 50
      let coalescer =
        Coalescer<int>(
          (fun value ->
            if Interlocked.Increment(&running.[0]) > 1 then overlapped.[0] <- true
            lock delivered (fun () -> delivered.Add value)
            if value = 1 then
              slowStarted.SetResult ()
              letItFinish.Wait ()
            Interlocked.Decrement(&running.[0]) |> ignore
            if value = burst then last.SetResult ()),
          onThePool)
      coalescer.Post 1
      do! slowStarted.Task.WaitAsync TestTimeouts.patienceInProcess
      for value in 2 .. burst do coalescer.Post value
      letItFinish.Set ()
      do! last.Task.WaitAsync TestTimeouts.patienceInProcess
      overlapped.[0] |> Expect.isFalse "no two deliveries ran at once"
      lock delivered (fun () -> List.ofSeq delivered) |> Expect.equal "the slow one, then only the newest" [ 1; burst ]
    }
  ]
