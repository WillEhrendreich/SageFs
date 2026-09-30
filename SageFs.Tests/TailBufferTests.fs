module SageFs.Tests.TailBufferTests

open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open SageFs

let private create<'T> (capacity: int) : TailBuffer<'T> =
  match TailBuffer<'T>.TryCreate capacity with
  | Ok buf -> buf
  | Error err -> failtestf "capacity %d should be accepted, got %A" capacity err

let private pushAll (items: 'T list) (buf: TailBuffer<'T>) : TailBuffer<'T> =
  items |> List.iter buf.Push
  buf

/// The reference model: the last `capacity` items, oldest first.
let private lastN (n: int) (items: 'T list) : 'T list =
  items |> List.skip (max 0 (items.Length - n))

[<Tests>]
let tailBufferTests =
  testList "TailBuffer bounded tail" [

    testCase "WHY — TryCreate — a zero capacity is refused with a named error, because a ring that can hold nothing has no tail to report" <| fun _ ->
      TailBuffer<int>.TryCreate 0
      |> Expect.equal "zero refused" (Error (TailCapacityError.NotPositive 0))

    testCase "WHY — TryCreate — a negative capacity is refused with a named error instead of a crash inside Array.zeroCreate" <| fun _ ->
      TailBuffer<int>.TryCreate -3
      |> Expect.equal "negative refused" (Error (TailCapacityError.NotPositive -3))

    testProperty "WHY — TryCreate — every non-positive capacity is refused, none reaches the array allocation" (fun (n: int) ->
      // Implication as plain boolean logic: a positive n is vacuously fine.
      n > 0 || TailBuffer<int>.TryCreate n = Error (TailCapacityError.NotPositive n))

    testCase "WHY — Snapshot — an empty buffer has an empty tail, so the failure text can tell 'no stderr' from 'some stderr'" <| fun _ ->
      let buf = create<string> 4
      buf.Snapshot() |> Expect.equal "empty" [||]
      buf.Count |> Expect.equal "count" 0

    testCase "WHY — Snapshot — before the ring wraps it returns everything pushed, oldest first" <| fun _ ->
      let buf = create<int> 5 |> pushAll [ 1; 2; 3 ]
      buf.Snapshot() |> Expect.equal "in push order" [| 1; 2; 3 |]

    testCase "WHY — Snapshot — after the ring wraps the oldest lines are gone and the order is still oldest first" <| fun _ ->
      let buf = create<int> 3 |> pushAll [ 1; 2; 3; 4; 5 ]
      buf.Snapshot() |> Expect.equal "last three" [| 3; 4; 5 |]

    testCase "WHY — Snapshot — the returned array is a copy, so a caller holding it cannot see later pushes or corrupt the ring" <| fun _ ->
      let buf = create<int> 3 |> pushAll [ 1; 2 ]
      let taken = buf.Snapshot()
      buf.Push 3
      taken.[0] <- 99
      taken |> Expect.equal "unchanged length" [| 99; 2 |]
      buf.Snapshot() |> Expect.equal "ring untouched" [| 1; 2; 3 |]

    testCase "WHY — SnapshotLast — asking for more than is held returns what is held, not a padded or failing result" <| fun _ ->
      let buf = create<int> 10 |> pushAll [ 1; 2; 3 ]
      buf.SnapshotLast 50 |> Expect.equal "all three" [| 1; 2; 3 |]

    testCase "WHY — SnapshotLast — the last N of a wrapped ring are the newest N, oldest first (the failure text wants the end of stderr)" <| fun _ ->
      let buf = create<int> 4 |> pushAll [ 1; 2; 3; 4; 5; 6 ]
      buf.SnapshotLast 2 |> Expect.equal "newest two" [| 5; 6 |]

    testCase "WHY — SnapshotLast — a non-positive count is an empty snapshot rather than an exception" <| fun _ ->
      let buf = create<int> 4 |> pushAll [ 1; 2 ]
      buf.SnapshotLast 0 |> Expect.equal "zero" [||]
      buf.SnapshotLast -1 |> Expect.equal "negative" [||]

    testProperty "WHY — Count and Snapshot — never exceed the capacity, however many items are pushed (the unbounded-queue bug)" (fun (cap: int) (items: int list) ->
      let capacity = abs (cap % 50) + 1
      let buf = create<int> capacity |> pushAll items
      buf.Count <= capacity
      && buf.Snapshot().Length <= capacity
      && buf.Capacity = capacity)

    testProperty "WHY — Snapshot — after any push sequence equals the last `capacity` items in order" (fun (cap: int) (items: int list) ->
      let capacity = abs (cap % 50) + 1
      let buf = create<int> capacity |> pushAll items
      buf.Snapshot() |> Array.toList = lastN capacity items)

    testProperty "WHY — SnapshotLast — equals the last min(n, held) items in order for any n" (fun (cap: int) (n: int) (items: int list) ->
      let capacity = abs (cap % 50) + 1
      let buf = create<int> capacity |> pushAll items
      let held = lastN capacity items
      let expected = match n <= 0 with | true -> [] | false -> lastN n held
      buf.SnapshotLast n |> Array.toList = expected)

    testCase "WHY — Push — the stderr reader thread pushes while a fault handler snapshots, and neither may tear or throw" <| fun _ ->
      let capacity = 64
      let buf = create<int> capacity
      let writers =
        [| for w in 0 .. 3 ->
             Task.Run(fun () -> for i in 0 .. 4999 do buf.Push(w * 10000 + i)) |]
      let reader =
        Task.Run(fun () ->
          for _ in 0 .. 999 do
            let snap = buf.Snapshot()
            match snap.Length <= capacity with
            | true -> ()
            | false -> failtestf "snapshot of %d exceeded capacity %d" snap.Length capacity)
      Task.WaitAll(Array.append writers [| reader |])
      buf.Count |> Expect.equal "full after 20000 pushes" capacity
      buf.Snapshot().Length |> Expect.equal "snapshot is full" capacity
  ]

let private linesOf (from: int) (upTo: int) : string list =
  [ for i in from .. upTo -> sprintf "line %d" i ]

[<Tests>]
let stderrTailTests =
  testList "StderrTail worker stderr capture" [

    testCase "WHY — StderrTail — a chatty worker leaves the daemon holding a bounded tail, not every line it ever wrote" <| fun _ ->
      let tail = StderrTail.create ()
      linesOf 1 100000 |> List.iter tail.Push
      let held = tail.Snapshot()
      held.Length |> Expect.equal "bounded to the named capacity" StderrTail.capacity
      held |> Array.last |> Expect.equal "newest line kept" "line 100000"
      held |> Array.head |> Expect.equal "oldest kept line" (sprintf "line %d" (100000 - StderrTail.capacity + 1))

    testCase "WHY — StderrTail.summary — no stderr yields empty text, so the failure branches keep their 'no stderr' wording" <| fun _ ->
      StderrTail.summary (StderrTail.create ())
      |> Expect.equal "empty" ""

    testCase "WHY — StderrTail.summary — a short stderr is reported whole, in order, newline-joined" <| fun _ ->
      let tail = StderrTail.create ()
      linesOf 1 3 |> List.iter tail.Push
      StderrTail.summary tail
      |> Expect.equal "three lines" "line 1\nline 2\nline 3"

    testCase "WHY — StderrTail.summary — a long stderr reports its END, because the crash reason is the last thing a dying worker writes" <| fun _ ->
      let tail = StderrTail.create ()
      linesOf 1 500 |> List.iter tail.Push
      let lines = (StderrTail.summary tail).Split('\n')
      lines.Length |> Expect.equal "summary is the named number of lines" StderrTail.summaryLineCount
      lines |> Array.last |> Expect.equal "ends at the newest line" "line 500"
      lines |> Array.head |> Expect.equal "starts summaryLineCount back" (sprintf "line %d" (500 - StderrTail.summaryLineCount + 1))
  ]
