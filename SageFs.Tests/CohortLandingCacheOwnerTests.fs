/// Roast-6 #7a: the daemon's landing-verification test-result cache used to
/// be a bare `ref`, read-modify-written from the `RunTests` performer
/// (`DaemonMode.fs`) — safe only because v1's landing queue is strictly
/// serial. `Features.CohortOwner.LandingCacheOwner` gives it a single
/// mailbox owner instead, so correctness no longer rests on that invariant.
/// These tests prove (1) the cache actually caches — a second `Verify` for
/// the same session/input never re-invokes `runMisses` — and (2) concurrent
/// `Verify` calls for DIFFERENT tests never lose a write to each other: the
/// exact race a shared `ref`'s read-modify-write is vulnerable to, and a
/// single mailbox owner structurally cannot have.
module SageFs.Tests.CohortLandingCacheOwnerTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.LiveTesting

let private silentLogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

[<Tests>]
let landingCacheOwnerTests =
  testList "CohortOwner.LandingCacheOwner" [

    // `LandingCache.verify` (Features/LandingCache.fs) always CALLS
    // `runMisses` — even with an empty list, when everything is a cache
    // hit — so "never re-run" is proven by WHICH test ids actually reach
    // `runMisses`, not by whether it was invoked at all.
    testTask "a cached test is never handed to runMisses again on a second Verify for the same session and input hash" {
      use owner = CohortOwner.LandingCacheOwner.start silentLogger
      let executed = ResizeArray<TestId>()
      let inputHashOf (_: TestId) = Some "hash-v1"
      let runMisses (toRun: TestId list) =
        async {
          executed.AddRange toRun
          return Ok([]: TestId list)  // everything passes
        }

      let! first = owner.Verify inputHashOf "session-a" [ TestId.TestId "t1" ] runMisses |> Async.AwaitTask
      first |> Expect.equal "first call runs and passes" (Ok [])
      executed |> List.ofSeq |> Expect.equal "the first call actually ran t1" [ TestId.TestId "t1" ]

      let! second = owner.Verify inputHashOf "session-a" [ TestId.TestId "t1" ] runMisses |> Async.AwaitTask
      second |> Expect.equal "second call is served from the cache" (Ok [])
      executed
      |> List.ofSeq
      |> Expect.equal "t1 was never handed to runMisses again — the cache hit skipped it" [ TestId.TestId "t1" ]
    }

    testTask "a changed input hash is a cache MISS even for the same test id and session" {
      use owner = CohortOwner.LandingCacheOwner.start silentLogger
      let executed = ResizeArray<TestId>()
      let runMisses (toRun: TestId list) =
        async {
          executed.AddRange toRun
          return Ok([]: TestId list)
        }

      let! _ = owner.Verify (fun _ -> Some "hash-A") "session-a" [ TestId.TestId "t1" ] runMisses |> Async.AwaitTask
      let! _ = owner.Verify (fun _ -> Some "hash-B") "session-a" [ TestId.TestId "t1" ] runMisses |> Async.AwaitTask

      executed
      |> List.ofSeq
      |> Expect.equal "a different content hash always re-runs — a source change can never be masked by a stale cache hit" [ TestId.TestId "t1"; TestId.TestId "t1" ]
    }

    testTask "two concurrent Verify calls for DIFFERENT tests never lose a write to each other (the race a shared ref is vulnerable to)" {
      use owner = CohortOwner.LandingCacheOwner.start silentLogger
      let inputHashOf (_: TestId) = Some "hash-v1"
      // Both runs overlap in wall-clock time — this is the exact interleaving
      // that would race a naive `cache.Value <- newCache` read-modify-write:
      // both reads happen before either write commits, so whichever commits
      // last silently erases the other's cached entry.
      let slowRunMisses (delayMs: int) (toRun: TestId list) =
        async {
          do! Async.Sleep delayMs
          return Ok(toRun)  // report exactly what ran, so the re-verify below can prove neither entry was lost
        }

      let! results =
        Async.Parallel [
          owner.Verify inputHashOf "session-a" [ TestId.TestId "t1" ] (slowRunMisses 40) |> Async.AwaitTask
          owner.Verify inputHashOf "session-b" [ TestId.TestId "t2" ] (slowRunMisses 10) |> Async.AwaitTask
        ]
      results |> Array.iter (fun r -> (match r with Ok _ -> () | Error e -> failtestf "unexpected error: %s" e))

      // Re-verify both, this time recording exactly which test ids reach
      // `runMisses` — this only comes back empty for BOTH if the entries
      // the concurrent calls above wrote are still in the cache, i.e.
      // neither write was lost to the other.
      let executed = System.Collections.Concurrent.ConcurrentBag<TestId>()
      let recordingRunMisses (toRun: TestId list) =
        async {
          toRun |> List.iter executed.Add
          return Ok([]: TestId list)
        }
      let! _ = owner.Verify inputHashOf "session-a" [ TestId.TestId "t1" ] recordingRunMisses |> Async.AwaitTask
      let! _ = owner.Verify inputHashOf "session-b" [ TestId.TestId "t2" ] recordingRunMisses |> Async.AwaitTask
      executed
      |> List.ofSeq
      |> Expect.equal "neither session-a's nor session-b's cached entry was lost to the concurrent write" []
    }

    testTask "an untrusted test (no input hash) is always run and never cached" {
      use owner = CohortOwner.LandingCacheOwner.start silentLogger
      let executed = ResizeArray<TestId>()
      let untrustedInputHashOf (_: TestId) : string option = None
      let runMisses (toRun: TestId list) =
        async {
          executed.AddRange toRun
          return Ok([]: TestId list)
        }
      let! _ = owner.Verify untrustedInputHashOf "session-a" [ TestId.TestId "t1" ] runMisses |> Async.AwaitTask
      let! _ = owner.Verify untrustedInputHashOf "session-a" [ TestId.TestId "t1" ] runMisses |> Async.AwaitTask

      executed
      |> List.ofSeq
      |> Expect.equal "an untrustworthy hash is never cached, so it runs every time" [ TestId.TestId "t1"; TestId.TestId "t1" ]
    }

    testTask "a runMisses failure fails closed: nothing is cached and the error propagates" {
      use owner = CohortOwner.LandingCacheOwner.start silentLogger
      let inputHashOf (_: TestId) = Some "hash-v1"
      let failingRunMisses (_: TestId list) : Async<Result<TestId list, string>> =
        async { return Error "session cannot be trusted" }
      let! result = owner.Verify inputHashOf "session-a" [ TestId.TestId "t1" ] failingRunMisses |> Async.AwaitTask
      result |> Expect.equal "the error propagates" (Error "session cannot be trusted")

      let executed = ResizeArray<TestId>()
      let succeedingRunMisses (toRun: TestId list) =
        async {
          executed.AddRange toRun
          return Ok([]: TestId list)
        }
      let! second = owner.Verify inputHashOf "session-a" [ TestId.TestId "t1" ] succeedingRunMisses |> Async.AwaitTask
      second |> Expect.equal "a later call still runs — nothing was cached from the failed attempt" (Ok [])
      executed
      |> List.ofSeq
      |> Expect.equal "t1 genuinely ran again (fail-closed: a failed verify caches nothing, so it is NOT a cache hit)" [ TestId.TestId "t1" ]
    }
  ]
