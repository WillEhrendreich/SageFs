module SageFs.Tests.RingBufferTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.RingBuffer

// ── Helpers ──

let pushMany (items: 'T list) (buf: RingBuffer<'T>) =
  items |> List.fold (fun acc item -> push item acc) buf

// ── Tests ──

[<Tests>]
let ringBufferTests =
  testList "RingBuffer" [

    testList "create" [
      test "creates empty buffer with given capacity" {
        let buf = create 10
        buf |> count |> Expect.equal "count" 0
        buf |> capacity |> Expect.equal "capacity" 10
        buf |> isEmpty |> Expect.isTrue "should be empty"
        buf |> totalPushed |> Expect.equal "none pushed" 0L
      }

      test "rejects zero capacity" {
        Expect.throwsT<System.ArgumentException> "zero capacity"
          (fun () -> create 0 |> ignore)
      }

      test "rejects negative capacity" {
        Expect.throwsT<System.ArgumentException> "negative capacity"
          (fun () -> create -1 |> ignore)
      }
    ]

    testList "push and retrieve" [
      test "push single item retrievable as current" {
        let buf = create 5 |> push 42
        buf |> current |> Expect.equal "current" (Some 42)
        buf |> count |> Expect.equal "count" 1
      }

      test "push two items preserves order" {
        let buf = create 5 |> push 1 |> push 2
        buf |> current |> Expect.equal "current" (Some 2)
        buf |> previous |> Expect.equal "previous" (Some 1)
      }

      test "push fills to capacity" {
        let buf = create 3 |> pushMany [ 1; 2; 3 ]
        buf |> count |> Expect.equal "count" 3
        buf |> isFull |> Expect.isTrue "should be full"
        buf |> toList |> Expect.equal "order" [ 3; 2; 1 ]
      }

      test "push beyond capacity evicts oldest" {
        let buf = create 3 |> pushMany [ 1; 2; 3; 4 ]
        buf |> count |> Expect.equal "count still 3" 3
        buf |> toList |> Expect.equal "oldest evicted" [ 4; 3; 2 ]
        buf |> totalPushed |> Expect.equal "total pushed" 4L
        buf |> evictedCount |> Expect.equal "1 evicted" 1L
      }

      test "push wraps around correctly" {
        let buf = create 3 |> pushMany [ 1; 2; 3; 4; 5; 6; 7 ]
        buf |> toList |> Expect.equal "last 3 items" [ 7; 6; 5 ]
        buf |> totalPushed |> Expect.equal "total" 7L
        buf |> evictedCount |> Expect.equal "evicted" 4L
      }
    ]

    testList "tryGet" [
      test "age 0 returns most recent" {
        let buf = create 5 |> pushMany [ 10; 20; 30 ]
        buf |> tryGet 0 |> Expect.equal "age 0" (Some 30)
      }

      test "age 1 returns previous" {
        let buf = create 5 |> pushMany [ 10; 20; 30 ]
        buf |> tryGet 1 |> Expect.equal "age 1" (Some 20)
      }

      test "age 2 returns oldest" {
        let buf = create 5 |> pushMany [ 10; 20; 30 ]
        buf |> tryGet 2 |> Expect.equal "age 2" (Some 10)
      }

      test "age beyond count returns None" {
        let buf = create 5 |> pushMany [ 10; 20 ]
        buf |> tryGet 2 |> Expect.equal "beyond count" None
        buf |> tryGet 99 |> Expect.equal "way beyond" None
      }

      test "negative age returns None" {
        let buf = create 5 |> push 1
        buf |> tryGet -1 |> Expect.equal "negative" None
      }

      test "empty buffer returns None" {
        let buf: RingBuffer<int> = create 5
        buf |> current |> Expect.equal "empty current" None
        buf |> tryGet 0 |> Expect.equal "empty age 0" None
      }
    ]

    testList "iteration" [
      test "toSeq yields most recent first" {
        let buf = create 5 |> pushMany [ 1; 2; 3 ]
        buf |> toSeq |> Seq.toList |> Expect.equal "seq order" [ 3; 2; 1 ]
      }

      test "toList matches toSeq" {
        let buf = create 5 |> pushMany [ 1; 2; 3 ]
        buf |> toList |> Expect.equal "same as seq" (buf |> toSeq |> Seq.toList)
      }

      test "map transforms all items" {
        let buf = create 5 |> pushMany [ 1; 2; 3 ]
        buf |> map ((*) 10) |> Expect.equal "mapped" [ 30; 20; 10 ]
      }

      test "fold accumulates from most recent" {
        let buf = create 5 |> pushMany [ 1; 2; 3 ]
        buf |> fold (fun acc x -> acc @ [ x ]) []
        |> Expect.equal "fold order" [ 3; 2; 1 ]
      }

      test "iter visits all items" {
        let buf = create 5 |> pushMany [ 10; 20; 30 ]
        let mutable visited = []
        buf |> iter (fun x -> visited <- x :: visited)
        visited |> List.rev |> Expect.equal "iter order" [ 30; 20; 10 ]
      }
    ]

    testList "clear" [
      test "clear resets count and keeps capacity" {
        let buf = create 5 |> pushMany [ 1; 2; 3 ] |> clear
        buf |> count |> Expect.equal "count reset" 0
        buf |> capacity |> Expect.equal "capacity preserved" 5
        buf |> isEmpty |> Expect.isTrue "should be empty"
      }

      test "clear preserves totalPushed" {
        let buf = create 3 |> pushMany [ 1; 2; 3; 4 ] |> clear
        buf |> totalPushed |> Expect.equal "total preserved" 4L
      }
    ]

    testList "capacity 1 edge case" [
      test "capacity 1 holds exactly one item" {
        let buf = create 1 |> push 42
        buf |> current |> Expect.equal "current" (Some 42)
        buf |> count |> Expect.equal "count" 1
        buf |> isFull |> Expect.isTrue "full"
      }

      test "capacity 1 evicts on second push" {
        let buf = create 1 |> push 42 |> push 99
        buf |> current |> Expect.equal "newest" (Some 99)
        buf |> count |> Expect.equal "still 1" 1
        buf |> previous |> Expect.equal "no previous" None
      }
    ]

    testList "property tests" [
      testProperty "count never exceeds capacity" (fun (cap: PositiveInt) (items: int list) ->
        let c = min cap.Get 100
        let buf = items |> List.fold (fun b i -> push i b) (create c)
        count buf <= capacity buf
      )

      testProperty "totalPushed equals number of pushes" (fun (items: int list) ->
        let c = max 1 (items.Length / 2 + 1)
        let buf = items |> List.fold (fun b i -> push i b) (create c)
        totalPushed buf = int64 items.Length
      )

      testProperty "evictedCount = totalPushed - count" (fun (items: int list) ->
        let c = max 1 (items.Length / 3 + 1)
        let buf = items |> List.fold (fun b i -> push i b) (create c)
        evictedCount buf = totalPushed buf - int64 (count buf)
      )

      testProperty "toList has exactly count elements" (fun (cap: PositiveInt) (items: int list) ->
        let c = min cap.Get 100
        let buf = items |> List.fold (fun b i -> push i b) (create c)
        (toList buf).Length = count buf
      )

      testProperty "most recent items are preserved" (fun (items: int list) ->
        match items with
        | [] -> true
        | _ ->
          let c = max 1 (items.Length / 2)
          let buf = items |> List.fold (fun b i -> push i b) (create c)
          let expected = items |> List.rev |> List.truncate c
          toList buf = expected
      )

      testProperty "tryGet age < count always returns Some" (fun (cap: PositiveInt) (items: NonEmptyArray<int>) ->
        let c = min cap.Get 50
        let buf = items.Get |> Array.fold (fun b i -> push i b) (create c)
        let age = (abs (items.Get.[0]) % count buf)
        (tryGet age buf).IsSome
      )
    ]
  ]
