/// Proves the actor-dispatch seam Island F built (demo-actors-plan.md §1.2):
/// a `WireStep`/`ScenarioPlan` naming the `"dashboard"` client resolves,
/// through `CellAgent.actorIdOfString`, to exactly the `ActorId` the wrapped
/// Dashboard actor (`Actors.Dashboard.toLiveActor`) reports as its own `Id`
/// — i.e. the seam actually routes a dashboard step to the Dashboard actor,
/// not merely "the types compile." No cell/Xvfb/Chromium/daemon is spawned:
/// `toLiveActor` only touches its `Handle`'s Playwright objects lazily,
/// inside the async functions the seam exposes, so a `Handle` whose fields
/// are never dereferenced here is a legitimate way to inspect its `Id` and
/// prove the wiring without a live browser.
module SageFs.Demos.Tests.CellAgentTests

open Expecto
open Expecto.Flip
open SageFs.Demos.Domain
open SageFs.Demos.CellAgent
open SageFs.Demos.Actors

[<Tests>]
let tests =
  testList "CellAgent actor dispatch" [

    testCase "actorIdOfString maps every Wire client/actor token to its ActorId" <| fun _ ->
      actorIdOfString "dashboard" |> Expect.equal "dashboard maps to ActorId.Dashboard" (Some ActorId.Dashboard)
      actorIdOfString "vscode" |> Expect.equal "vscode maps to ActorId.VsCode" (Some ActorId.VsCode)
      actorIdOfString "neovim" |> Expect.equal "neovim maps to ActorId.Neovim" (Some ActorId.Neovim)
      actorIdOfString "app" |> Expect.equal "app maps to ActorId.App" (Some ActorId.App)
      actorIdOfString "agent" |> Expect.equal "agent maps to ActorId.Agent" (Some ActorId.Agent)

    testCase "actorIdOfString never silently defaults an unknown token to Dashboard" <| fun _ ->
      actorIdOfString "not-a-real-actor" |> Expect.equal "unknown tokens fail loud (None), never a guess" None

    testCase "a dashboard-targeted step's resolved ActorId matches the Dashboard actor's own reported Id — the dispatch seam actually routes to it" <| fun _ ->
      // The Handle's Playwright fields are never touched merely by wrapping
      // it — `toLiveActor` only closes over them inside `ResolveRect`/
      // `Observe`/`Close`, none of which this test calls — so a `Handle`
      // built from `Unchecked.defaultof` is a safe, honest way to inspect
      // the wrapped `LiveActor`'s `Id` alone.
      let dummyHandle: Dashboard.Handle =
        { Playwright = Unchecked.defaultof<_>
          Context = Unchecked.defaultof<_>
          Page = Unchecked.defaultof<_> }

      let liveActor = Dashboard.toLiveActor dummyHandle
      let resolvedTarget = actorIdOfString "dashboard"

      resolvedTarget |> Expect.equal "the plan Client 'dashboard' resolves to a real ActorId" (Some liveActor.Id)
      liveActor.Id |> Expect.equal "the wrapped Dashboard actor reports ActorId.Dashboard" ActorId.Dashboard
  ]
