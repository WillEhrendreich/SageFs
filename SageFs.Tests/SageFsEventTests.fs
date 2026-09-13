module SageFs.Tests.SageFsEventTests

open System
open Expecto
open Expecto.Flip
open FsCheck
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
]
