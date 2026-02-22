module SageFs.Tests.ElmLoopResilienceTests

open Expecto
open Expecto.Flip
open SageFs

let waitFor (condition: unit -> bool) (timeoutMs: int) =
  let sw = System.Diagnostics.Stopwatch.StartNew()
  while not (condition ()) && sw.ElapsedMilliseconds < int64 timeoutMs do
    System.Threading.Thread.Sleep 10
  condition ()

let wait () =
  System.Threading.Thread.Sleep 500

[<Tests>]
let elmLoopResilienceTests =
  testList "ElmLoop resilience" [

    testCase "Update throws: model stays, no effect, loop recovers" <| fun _ ->
      let effCount = ref 0
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model ->
          if msg = 2 then failwith "Update boom!"
          model + 1, [1]
        Render = fun model -> [model * 10]
        ExecuteEffect = fun _ _ ->
          async { System.Threading.Interlocked.Increment effCount |> ignore }
        OnModelChanged = fun _ _ -> ()
      }
      let rt = ElmLoop.start prog 0

      rt.Dispatch 1  // model=1, effect fires
      waitFor (fun () -> effCount.Value >= 1) 2000 |> ignore
      effCount.Value |> Expect.equal "effect fired on d1" 1
      rt.GetModel() |> Expect.equal "model is 1" 1

      rt.Dispatch 2  // Update throws, model stays 1, no effect
      wait ()
      effCount.Value |> Expect.equal "no new effect on d2" 1
      rt.GetModel() |> Expect.equal "model still 1" 1

      rt.Dispatch 3  // recovers, model=2
      waitFor (fun () -> effCount.Value >= 2) 2000 |> ignore
      effCount.Value |> Expect.equal "effect fires on d3" 2
      rt.GetModel() |> Expect.equal "model is 2" 2

    testCase "Render throws: previous regions preserved, effects fire" <| fun _ ->
      let effCount = ref 0
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model -> model + 1, [1]
        Render = fun model ->
          if model = 2 then failwith "Render boom!"
          [model * 10]
        ExecuteEffect = fun _ _ ->
          async { System.Threading.Interlocked.Increment effCount |> ignore }
        OnModelChanged = fun _ _ -> ()
      }
      let rt = ElmLoop.start prog 0

      rt.Dispatch 1  // model=1, regions=[10]
      waitFor (fun () -> effCount.Value >= 1) 2000 |> ignore
      rt.GetRegions() |> Expect.equal "regions from d1" [10]

      rt.Dispatch 2  // model=2, Render throws, regions stay [10]
      waitFor (fun () -> effCount.Value >= 2) 2000 |> ignore
      effCount.Value |> Expect.equal "effect still fires" 2
      rt.GetRegions() |> Expect.equal "regions preserved" [10]

      rt.Dispatch 3  // model=3, Render succeeds with [30]
      waitFor (fun () -> effCount.Value >= 3) 2000 |> ignore
      rt.GetRegions() |> Expect.equal "regions recover" [30]

    testCase "OnModelChanged throws: effects still fire" <| fun _ ->
      let effCount = ref 0
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model -> model + 1, [1]
        Render = fun model -> [model]
        ExecuteEffect = fun _ _ ->
          async { System.Threading.Interlocked.Increment effCount |> ignore }
        OnModelChanged = fun model _ ->
          if model = 2 then failwith "OnModelChanged boom!"
      }
      let rt = ElmLoop.start prog 0

      rt.Dispatch 1
      waitFor (fun () -> effCount.Value >= 1) 2000 |> ignore
      effCount.Value |> Expect.equal "effect on d1" 1

      rt.Dispatch 2  // OnModelChanged throws, but effect should still fire
      waitFor (fun () -> effCount.Value >= 2) 2000 |> ignore
      effCount.Value |> Expect.equal "effect on d2 despite throw" 2
      rt.GetModel() |> Expect.equal "model updated" 2

      rt.Dispatch 3  // recovers
      waitFor (fun () -> effCount.Value >= 3) 2000 |> ignore
      effCount.Value |> Expect.equal "effect on d3" 3

    testCase "Effect throws: loop still works for subsequent dispatches" <| fun _ ->
      let effCount = ref 0
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model -> model + 1, [msg]
        Render = fun model -> [model]
        ExecuteEffect = fun _ eff ->
          async {
            if eff = 2 then failwith "Effect boom!"
            System.Threading.Interlocked.Increment effCount |> ignore
          }
        OnModelChanged = fun _ _ -> ()
      }
      let rt = ElmLoop.start prog 0

      rt.Dispatch 1  // effect=1, succeeds
      waitFor (fun () -> effCount.Value >= 1) 2000 |> ignore
      effCount.Value |> Expect.equal "effect 1 ran" 1

      rt.Dispatch 2  // effect=2, throws
      wait ()
      effCount.Value |> Expect.equal "effect 2 failed" 1

      rt.Dispatch 3  // effect=3, succeeds
      waitFor (fun () -> effCount.Value >= 2) 2000 |> ignore
      effCount.Value |> Expect.equal "effect 3 ran" 2

    testCase "Initial Render throws: starts with empty regions" <| fun _ ->
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model -> model + 1, []
        Render = fun model ->
          if model = 0 then failwith "Initial Render boom!"
          [model * 10]
        ExecuteEffect = fun _ _ -> async { () }
        OnModelChanged = fun _ _ -> ()
      }
      let rt = ElmLoop.start prog 0

      rt.GetRegions() |> Expect.equal "empty regions on failed init" []
      rt.GetModel() |> Expect.equal "model still 0" 0

      rt.Dispatch 1  // Render succeeds now
      wait ()
      rt.GetRegions() |> Expect.equal "regions recover" [10]

    testCase "Initial OnModelChanged throws: loop still works" <| fun _ ->
      let effCount = ref 0
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model -> model + 1, [1]
        Render = fun model -> [model]
        ExecuteEffect = fun _ _ ->
          async { System.Threading.Interlocked.Increment effCount |> ignore }
        OnModelChanged = fun model _ ->
          if model = 0 then failwith "Initial OnModelChanged boom!"
      }
      let rt = ElmLoop.start prog 0

      rt.GetModel() |> Expect.equal "model is 0" 0
      rt.GetRegions() |> Expect.equal "regions rendered despite throw" [0]

      rt.Dispatch 1
      waitFor (fun () -> effCount.Value >= 1) 2000 |> ignore
      effCount.Value |> Expect.equal "effect fires" 1
      rt.GetModel() |> Expect.equal "model updated" 1

    testCase "Multiple failures in sequence: loop survives all" <| fun _ ->
      let effCount = ref 0
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model ->
          if msg = 3 then failwith "Update boom on 3!"
          model + 1, [msg]
        Render = fun model ->
          if model = 2 then failwith "Render boom on 2!"
          [model]
        ExecuteEffect = fun _ eff ->
          async {
            if eff = 5 then failwith "Effect boom on 5!"
            System.Threading.Interlocked.Increment effCount |> ignore
          }
        OnModelChanged = fun model _ ->
          if model = 4 then failwith "OnModelChanged boom on 4!"
      }
      let rt = ElmLoop.start prog 0

      rt.Dispatch 1  // all good, model=1
      waitFor (fun () -> effCount.Value >= 1) 2000 |> ignore
      rt.GetModel() |> Expect.equal "d1 model" 1
      effCount.Value |> Expect.equal "d1 effects" 1

      rt.Dispatch 2  // model=2, Render throws, regions preserved
      waitFor (fun () -> effCount.Value >= 2) 2000 |> ignore
      rt.GetModel() |> Expect.equal "d2 model" 2
      rt.GetRegions() |> Expect.equal "d2 regions preserved" [1]

      rt.Dispatch 3  // Update throws, model stays 2
      wait ()
      rt.GetModel() |> Expect.equal "d3 model unchanged" 2

      rt.Dispatch 4  // model=3, OnModelChanged doesn't throw (model=3, not 4)
      waitFor (fun () -> effCount.Value >= 3) 2000 |> ignore
      rt.GetModel() |> Expect.equal "d4 model" 3

      rt.Dispatch 5  // model=4, OnModelChanged throws; effect=5 throws
      wait ()
      rt.GetModel() |> Expect.equal "d5 model" 4

      rt.Dispatch 6  // model=5, all good
      waitFor (fun () -> effCount.Value >= 4) 2000 |> ignore
      rt.GetModel() |> Expect.equal "d6 model" 5
      // effects: d1(1)+d2(2)+d4(4)+d5(5 fails)+d6(6) = 4 successes
      effCount.Value |> Expect.equal "total effects" 4
  ]
