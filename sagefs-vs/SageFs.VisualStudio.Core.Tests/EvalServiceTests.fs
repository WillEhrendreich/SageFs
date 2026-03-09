module SageFs.VisualStudio.Core.Tests.EvalServiceTests

open Xunit
open FsUnit.Xunit
open SageFs.VisualStudio.Core

// -- EvalCancellation ---------------------------------------------------------

[<Fact>]
let ``EvalCancellation StartNew returns fresh non-cancelled token`` () =
  let ecs = new EvalCancellation()
  let token = ecs.StartNew()
  token.IsCancellationRequested |> should equal false

[<Fact>]
let ``EvalCancellation StartNew second call cancels first token`` () =
  let ecs = new EvalCancellation()
  let t1 = ecs.StartNew()
  let _t2 = ecs.StartNew()
  t1.IsCancellationRequested |> should equal true

[<Fact>]
let ``EvalCancellation StartNew second call returns non-cancelled token`` () =
  let ecs = new EvalCancellation()
  let _t1 = ecs.StartNew()
  let t2 = ecs.StartNew()
  t2.IsCancellationRequested |> should equal false

[<Fact>]
let ``EvalCancellation Cancel cancels the current token`` () =
  let ecs = new EvalCancellation()
  let token = ecs.StartNew()
  ecs.Cancel()
  token.IsCancellationRequested |> should equal true

[<Fact>]
let ``EvalCancellation Cancel before StartNew does not throw`` () =
  let ecs = new EvalCancellation()
  // should not throw
  (fun () -> ecs.Cancel()) |> should not' (throw typeof<System.Exception>)

[<Fact>]
let ``EvalCancellation IsEvaluating is true after StartNew`` () =
  let ecs = new EvalCancellation()
  let _ = ecs.StartNew()
  ecs.IsEvaluating |> should equal true

[<Fact>]
let ``EvalCancellation IsEvaluating is false after Cancel`` () =
  let ecs = new EvalCancellation()
  let _ = ecs.StartNew()
  ecs.Cancel()
  ecs.IsEvaluating |> should equal false

[<Fact>]
let ``EvalCancellation Done sets IsEvaluating to false`` () =
  let ecs = new EvalCancellation()
  let _ = ecs.StartNew()
  ecs.Done()
  ecs.IsEvaluating |> should equal false

[<Fact>]
let ``EvalCancellation multiple StartNew each cancels previous`` () =
  let ecs = new EvalCancellation()
  let tokens = [ for _ in 1..5 -> ecs.StartNew() ]
  tokens |> List.take 4 |> List.iter (fun t ->
    t.IsCancellationRequested |> should equal true)
  (List.last tokens).IsCancellationRequested |> should equal false

[<Fact>]
let ``EvalCancellation IsEvaluating is false initially`` () =
  let ecs = new EvalCancellation()
  ecs.IsEvaluating |> should equal false

[<Fact>]
let ``EvalCancellation Dispose does not throw`` () =
  let ecs = new EvalCancellation()
  let _ = ecs.StartNew()
  (fun () -> (ecs :> System.IDisposable).Dispose()) |> should not' (throw typeof<System.Exception>)
