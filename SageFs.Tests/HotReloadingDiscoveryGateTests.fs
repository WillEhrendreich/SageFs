module SageFs.Tests.HotReloadingDiscoveryGateTests

/// Brief 2 (live-testing-asyoutype-plan.md) — un-gate eval-time test discovery.
///
/// Root cause: SageFs.Core/Middleware/HotReloading.fs's live-testing hook only
/// runs LiveTestingHook.afterReload when a Harmony method-detour fired
/// (`updatedMethods` non-empty) or this is the session's very first eval
/// (`needsInitialScan`). A brand-new `[<Tests>]` value defined after warmup —
/// which detours no existing method and isn't the first eval — is silently
/// never scanned, so it can never be discovered. These tests drive the REAL
/// production middleware (via the shared `globalActorResult` FSI actor, the
/// same pattern StableIdentityEvalTests.fs uses) through that exact gate and
/// prove: (a) today a fresh test binding is invisible without a force flag,
/// (b) the flag widens the scan to discover it, and (c) the flag never
/// itself turns on Harmony's reload/detour behavior.
open System.Threading
open Expecto
open Expecto.Flip
open SageFs.AppState
open SageFs.Features.LiveTesting
open SageFs.Tests.TestInfrastructure

let private eval (args: Map<string, obj>) (code: string) =
  globalActorResult.Value.Actor.PostAndAsyncReply(fun rc -> Eval({ Code = code; Args = args }, CancellationToken.None, rc))

let private hookResultOf (response: EvalResponse) : LiveTestHookResultDto =
  match Map.tryFind "liveTestHookResult" response.Metadata with
  | Some (:? LiveTestHookResultDto as dto) -> dto
  | _ -> failtestf "response carried no liveTestHookResult metadata: %A" response.Metadata

/// Ensures the shared FSI session references Expecto so `[<Tests>]` /
/// `testList` / `testCase` resolve for subsequently eval'd probe modules.
let private referenceExpecto () =
  task {
    let expectoAsm = typeof<Expecto.TestCode>.Assembly.Location
    let! r = eval Map.empty (sprintf "#r @\"%s\"" expectoAsm) |> Async.StartAsTask
    match r.EvaluationResult with
    | Error ex -> failtestf "could not reference Expecto in the shared FSI session: %s" ex.Message
    | Ok _ -> ()
  }

/// A uniquely-named `[<Tests>]` binding at the FSI submission's top level, so
/// each test case probes a binding the session has never seen before —
/// guaranteeing `updatedMethods` can't accidentally be non-empty from a
/// same-named prior definition. Deliberately NOT nested in an explicit
/// `module ... =` block: FSI compiles a nested module's generated class as
/// non-public (measured: `IsPublic=false`), which `Assembly.GetExportedTypes()`
/// then excludes — a top-level submission binding is what real "as-you-type"
/// buffer edits eval as, and it compiles publicly under FSI's own generated
/// interaction type.
let private probeModule (marker: string) =
  sprintf
    "open Expecto\n[<Tests>]\nlet probeTests_%s = testList \"probe\" [ testCase \"case_%s\" (fun () -> ()) ]"
    marker marker

[<Tests>]
let discoveryGateTests =
  testSequenced <| testList "HotReloading eval-time discovery gate" [
    testTask "WHY — a freshly FSI-defined [<Tests>] value is invisible without the force flag because the scan is gated on Harmony method-detours or the session's very first eval" {
      do! referenceExpecto ()
      let marker = System.Guid.NewGuid().ToString("N")
      let! response = eval Map.empty (probeModule marker) |> Async.StartAsTask
      match response.EvaluationResult with
      | Error ex -> failtestf "probe module did not compile: %s" ex.Message
      | Ok _ -> ()
      let hookResult = hookResultOf response
      hookResult.DiscoveredTests
      |> Expect.isEmpty "a brand-new test binding with no method-detour and no first-scan must not be discovered without forcing"
    }

    testTask "WHY — the liveTestRediscover force flag makes a freshly FSI-defined [<Tests>] value discoverable because the eval-time gate must widen on demand" {
      do! referenceExpecto ()
      let marker = System.Guid.NewGuid().ToString("N")
      let! response = eval (Map.ofList [ "liveTestRediscover", box true ]) (probeModule marker) |> Async.StartAsTask
      match response.EvaluationResult with
      | Error ex -> failtestf "probe module did not compile: %s" ex.Message
      | Ok _ -> ()
      let hookResult = hookResultOf response
      hookResult.DiscoveredTests
      |> Array.exists (fun tc -> tc.DisplayName = sprintf "case_%s" marker)
      |> Expect.isTrue "the forced scan must discover the freshly defined test"
    }

    testTask "WHY — the force flag only widens the discovery scan because forcing on an ordinary expression eval must never fabricate a discovered test" {
      // NOTE: the shared FSI session's dynamic assembly accumulates every
      // prior submission (its own real [<Tests>] probes included), so this
      // asserts the "unrelated" binding itself was never turned into a
      // fabricated test — not that the whole discovered set is empty, which
      // would be false whenever an earlier test case in this shared session
      // already defined a real one.
      let marker = System.Guid.NewGuid().ToString("N")
      let code = sprintf "let unrelated_%s = 1 + 1" marker
      let! response = eval (Map.ofList [ "liveTestRediscover", box true ]) code |> Async.StartAsTask
      match response.EvaluationResult with
      | Error ex -> failtestf "expression eval failed: %s" ex.Message
      | Ok _ -> ()
      let hookResult = hookResultOf response
      hookResult.DiscoveredTests
      |> Array.exists (fun tc -> tc.FullName.Contains marker)
      |> Expect.isFalse "forcing a scan on a plain expression eval must never fabricate a discovered test from it — the flag widens WHEN it scans, not WHAT it fabricates"
    }
  ]
