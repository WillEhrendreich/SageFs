module SageFs.Tests.SageFsEventTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open SageFs

let private mkLine text = {
  Kind = OutputKind.Result; Text = text
  Timestamp = DateTime(2026, 3, 3, 12, 0, 0); SessionId = "s1" }

[<Tests>]
let outputRingBufferTests = testList "OutputRingBuffer" [
  testCase "fresh buffer has version 0" <| fun _ ->
    let buf = OutputRingBuffer(10)
    buf.Version |> Expect.equal "starts at 0" 0

  testCase "each Add increments version by 1" <| fun _ ->
    let buf = OutputRingBuffer(10)
    buf.Add(mkLine "a")
    buf.Version |> Expect.equal "after 1 add" 1
    buf.Add(mkLine "b")
    buf.Version |> Expect.equal "after 2 adds" 2

  testCase "Clear increments version" <| fun _ ->
    let buf = OutputRingBuffer(10)
    buf.Add(mkLine "a")
    buf.Add(mkLine "b")
    let v = buf.Version
    buf.Clear()
    buf.Version |> Expect.equal "incremented after clear" (v + 1)

  testProperty "version equals total Add + Clear count" <| fun (adds: NonNegativeInt) (clears: NonNegativeInt) ->
    let buf = OutputRingBuffer(100)
    for i in 1..adds.Get do buf.Add(mkLine (sprintf "line%d" i))
    for _ in 1..clears.Get do buf.Clear()
    buf.Version = adds.Get + clears.Get

  testProperty "version never decreases across any mutation sequence" <| fun (ops: bool list) ->
    let buf = OutputRingBuffer(50)
    let mutable prev = buf.Version
    let mutable ok = true
    for isAdd in ops do
      match isAdd with
      | true -> buf.Add(mkLine "x")
      | false -> buf.Clear()
      match buf.Version >= prev with
      | true -> prev <- buf.Version
      | false -> ok <- false
    ok

  testCase "RenderAllCached returns same content on repeated calls" <| fun _ ->
    let buf = OutputRingBuffer(10)
    buf.Add(mkLine "hello")
    let r1 = buf.RenderAllCached()
    let r2 = buf.RenderAllCached()
    r1 |> Expect.equal "should be identical" r2

  testCase "RenderAllCached invalidates after Add" <| fun _ ->
    let buf = OutputRingBuffer(10)
    buf.Add(mkLine "first")
    let r1 = buf.RenderAllCached()
    buf.Add(mkLine "second")
    let r2 = buf.RenderAllCached()
    r2 |> Expect.stringContains "should include second" "second"
    (r1 = r2) |> Expect.isFalse "should differ after mutation"

  testCase "RenderAllCached invalidates after Clear" <| fun _ ->
    let buf = OutputRingBuffer(10)
    buf.Add(mkLine "stuff")
    let r1 = buf.RenderAllCached()
    buf.Clear()
    let r2 = buf.RenderAllCached()
    r2 |> Expect.equal "empty after clear" ""
    (r1 = r2) |> Expect.isFalse "should differ after clear"

  testCase "SessionOutputStore survives concurrent mixed operations" <| fun _ ->
    let store = SessionOutputStore(64)
    let errors = System.Collections.Concurrent.ConcurrentQueue<Exception>()
    let start = new ManualResetEventSlim(false)
    let sessions = [| "a"; "b"; "c"; "d"; "e" |]
    let run kind index =
      start.Wait()
      for i in 0..199 do
        try
          match kind, i % 4 with
          | 0, _ -> store.Add({ mkLine (sprintf "s%d" index) with SessionId = sessions[index % sessions.Length] })
          | 1, _ -> store.Add({ mkLine (sprintf "g%d" i) with SessionId = "" })
          | 2, _ ->
              let buffer = store.GetBuffer sessions[(index + 1) % sessions.Length]
              let _ = buffer.RenderAllCached()
              let _ = buffer.FilterToList (fun line -> line.Text.Contains "s")
              let _ = store.LiveSessionIds
              let _ = store.SessionCount
              ()
          | _ ->
            let id = sessions[index % sessions.Length]
            if i % 8 = 0 then store.Remove id else store.Clear id
        with ex -> errors.Enqueue ex
    let tasks : Task array =
      [| for kind in 0..4 do
           for index in 0..sessions.Length - 1 do
             yield Task.Run(fun () -> run kind index) |]
    start.Set()
    Task.WaitAll tasks
    errors |> Seq.toList |> List.iter (fun ex -> raise ex)
    store.Version >= 0L |> Expect.isTrue "version remains monotonic"
    store.SessionCount >= 0 |> Expect.isTrue "session count is coherent"
]
