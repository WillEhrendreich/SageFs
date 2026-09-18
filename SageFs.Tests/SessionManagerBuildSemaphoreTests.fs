/// Roast-6 / multiagent-vision.md §3.4, §10 Phase 0 item 1: "N concurrent
/// warmups must not launch N unbounded `dotnet build`s" — the daemon-wide
/// build semaphore around `RunBuildAsync`. A live concurrency test would need
/// to spawn several real (multi-second) `dotnet build` processes, which does
/// not belong in the fast default suite (AGENTS.md: prefer the SageFs REPL
/// for live verification) — this asserts the semaphore's configured policy
/// and starting state with real numbers instead.
module SageFs.Tests.SessionManagerBuildSemaphoreTests

open System
open Expecto
open Expecto.Flip
open SageFs

[<Tests>]
let sessionManagerBuildSemaphoreTests = testList "SessionManager build semaphore" [

  testCase "WHY — the build concurrency limit is cores/4 (min 1) — vision §3.4's documented policy, checked against the real Environment.ProcessorCount on this machine" <| fun _ ->
    let expected = max 1 (Environment.ProcessorCount / 4)
    SessionBuild.buildConcurrencyLimit |> Expect.equal "cores/4, min 1" expected
    (SessionBuild.buildConcurrencyLimit, 1) |> Expect.isGreaterThanOrEqual "never zero — a single-core box still gets one build slot"

  testCase "WHY — the semaphore starts fully available — no build is in flight before any RunBuildAsync call" <| fun _ ->
    SessionBuild.availableBuildSlots () |> Expect.equal "all slots free at rest" SessionBuild.buildConcurrencyLimit

  testAsync "WHY — a build with no projects never touches the semaphore — the early-return path (`RunBuildAsync []`) has nothing to build, so it must not acquire (and must not need to release) a slot" {
    let before = SessionBuild.availableBuildSlots ()
    let! result = SessionBuild.runBuildAsync [] "/tmp"
    result |> Expect.equal "no projects short-circuits" (Ok "No projects to build")
    SessionBuild.availableBuildSlots () |> Expect.equal "slot count unchanged" before
  }
]
