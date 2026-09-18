module SageFs.Tests.WorkerLiveTestEvalTests

/// Brief 3 (live-testing-asyoutype-plan.md) — the keystone: worker
/// `EvalLiveTestFile` (identity-preserving buffer eval + live merged
/// `GetTestDiscovery`).
///
/// Drives the REAL production `SageFs.Server.WorkerMain.mkLiveTestEvalSupport`
/// factory against the REAL shared FSI actor (`globalActorResult`, the same
/// pattern `StableIdentityEvalTests.fs`/`HotReloadingDiscoveryGateTests.fs`
/// use) — never a mock of the sealed `FsiEvaluationSession`.
///
/// HONEST FINDING (discovered empirically while writing these tests, not
/// assumed): the shared FSI session's dynamic-assembly discovery is
/// SESSION-CUMULATIVE — `LiveTestingHook.afterReload`'s scan of "the last
/// dynamic assembly" sees every `[<Tests>]` value the session has EVER
/// eval'd, and each FSI submission's reflected `Type.FullName` carries an
/// incrementing interaction wrapper (observed: `FSI_0004+SageFs+Tests+...`
/// then `FSI_0005+SageFs+Tests+...` for TWO submissions of the textually
/// identical `module` path). So two INDEPENDENT FSI submissions of "the same"
/// logical test do NOT converge on the same `TestId` — each gets its own,
/// content-independent-but-submission-dependent id. That means the
/// "override" tests below seed the compiled-baseline stub with the TestId a
/// real submission ACTUALLY produced (a calibration eval), rather than
/// assuming two separate evals would naturally agree — and the "new test"
/// assertions check for the marker's PRESENCE (`Array.exists`), never an
/// exact count, exactly like `HotReloadingDiscoveryGateTests.fs`'s own third
/// test already has to for the same session-accumulation reason. This is a
/// real gap in the pre-existing (not Brief-3-owned) FullName computation in
/// `LiveTestingExecutors.fs`/`ReflectionDiscovery.fs` — see this file's final
/// commit message and the handback report for the full writeup.
open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.AppState
open SageFs.Features.LiveTesting
open SageFs.Server.WorkerMain
open SageFs.Tests.TestInfrastructure

let private eval (args: Map<string, obj>) (code: string) =
  globalActorResult.Value.Actor.PostAndAsyncReply(fun rc -> Eval({ Code = code; Args = args }, CancellationToken.None, rc))

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

/// A dynamic run-test slot standing in for the worker's own
/// `latestDynamicRunTest` (SageFs.Host/WorkerMain.fs `run`) — the only piece
/// `mkLiveTestEvalSupport` doesn't own itself (it's shared with the file
/// watcher's hot-reload path in production), so these tests provide their own.
let private mkRunTestSlot () =
  let slot : (TestCase -> Async<TestResult>) option ref = ref None
  let set v = Interlocked.Exchange(slot, Some v) |> ignore
  let get () = Volatile.Read(&slot.contents)
  get, set

/// Unwrap an EvalLiveTestFile result, failing the test with the SageFsError's
/// description on Error. Explicitly typed: `SageFs.Features.LiveTesting`
/// contains several record types with a `FullName` field (TestCase,
/// TestTreemapEntry, ...), and an unannotated `Ok(tests, _)` destructure lets
/// F#'s ambiguous-field-name resolution silently pick the wrong one.
let private unwrap (label: string) (result: Result<TestCase array * ProviderDescription list, SageFsError>) : TestCase array =
  match result with
  | Ok(tests, _) -> tests
  | Error e -> failtestf "%s: %s" label (SageFsError.describe e)

let private hasFullNameContaining (fragment: string) (tc: TestCase) = tc.FullName.Contains fragment

[<Tests>]
let workerLiveTestEvalTests =
  testSequenced <| testList "WorkerMain.mkLiveTestEvalSupport (EvalLiveTestFile keystone)" [

    testTask "WHY — evaling an unsaved buffer that redefines a test sharing an existing TestId overrides it (no duplicate) and a subsequent run of that TestId reflects the edit" {
      do! referenceExpecto ()
      let marker = System.Guid.NewGuid().ToString("N")
      let modulePath = sprintf "SageFs.Tests.LiveEvalKeystone.Override_%s" marker
      let filePath = sprintf "/fake/%s.fs" marker
      let source (shouldPass: bool) =
        let assertionCode =
          match shouldPass with
          | true -> "Expect.equal 1 1 \"old body — passes\""
          | false -> "Expect.equal 1 2 \"new body — fails\""
        sprintf
          "module %s\nopen Expecto\n[<Tests>]\nlet overrideProbe = testList \"probe\" [ testCase \"assertion\" (fun () -> %s) ]"
          modulePath assertionCode

      // Calibration eval: discover the REAL TestId a submission of this
      // exact module path actually produces, so the "compiled baseline"
      // stub below carries a genuinely FSI-derived id rather than a guessed
      // one (see the file-level HONEST FINDING comment — two independent
      // submissions do NOT naturally converge on the same TestId).
      let _, calibrateEvalFile = mkLiveTestEvalSupport globalActorResult.Value.Actor [||] [] (snd (mkRunTestSlot ()))
      let! calibrationResult = calibrateEvalFile filePath (source true) |> Async.StartAsTask
      let calibrationTests : TestCase array = unwrap "calibration eval failed" calibrationResult
      let discoveredProbe =
        match calibrationTests |> Array.tryFind (hasFullNameContaining "overrideProbe") with
        | Some p -> p
        | None -> failtest "calibration eval must discover the probe test"

      // A stand-in "compiled" entry carrying that SAME TestId but a
      // distinguishing DisplayName/Labels, so the assertions below can tell
      // whether it survived (bug: duplicate) or was replaced (correct:
      // override) once the REAL edited eval runs.
      let compiledStub : TestCase =
        { discoveredProbe with DisplayName = "COMPILED-STUB"; Labels = [ "compiled-stub" ] }

      let getRunTest, setRunTest = mkRunTestSlot ()
      let getDiscovery, evalFile = mkLiveTestEvalSupport globalActorResult.Value.Actor [| compiledStub |] [] setRunTest

      let! editedResult = evalFile filePath (source false) |> Async.StartAsTask
      let mergedTests : TestCase array = unwrap "edited eval failed" editedResult

      let sameId = mergedTests |> Array.filter (fun t -> t.Id = compiledStub.Id)
      sameId.Length
      |> Expect.equal "exactly one entry for this TestId after the merge — override, not duplicate" 1
      sameId.[0].DisplayName
      |> Expect.notEqual "the compiled-baseline stub must be OVERRIDDEN by the dynamic (FSI) entry, not survive" "COMPILED-STUB"

      let liveTests : TestCase array = getDiscovery () |> fst
      liveTests
      |> Array.filter (fun t -> t.Id = compiledStub.Id)
      |> Array.length
      |> Expect.equal "GetTestDiscovery's live composition agrees: exactly one entry for the overridden TestId" 1

      match getRunTest () with
      | None -> failtest "a successful eval must set the dynamic run-test slot"
      | Some runTest ->
        let! result = runTest sameId.[0] |> Async.StartAsTask
        match result with
        | TestResult.Failed _ -> ()
        | other -> failtestf "running the merged TestId must execute the EDITED (now-failing) assertion, got %A" other
    }

    testTask "WHY — evaling a buffer that adds a brand-new [<Tests>] value makes it discoverable, because Brief 2's force flag makes eval-time discovery see it even though it detours no existing method" {
      do! referenceExpecto ()
      let marker = System.Guid.NewGuid().ToString("N")
      let modulePath = sprintf "SageFs.Tests.LiveEvalKeystone.New_%s" marker
      let filePath = sprintf "/fake/%s.fs" marker
      let source =
        sprintf
          "module %s\nopen Expecto\n[<Tests>]\nlet newProbe = testList \"probe\" [ testCase \"case\" (fun () -> ()) ]"
          modulePath

      let getDiscovery, evalFile = mkLiveTestEvalSupport globalActorResult.Value.Actor [||] [] (snd (mkRunTestSlot ()))
      let! result = evalFile filePath source |> Async.StartAsTask
      let tests : TestCase array = unwrap "eval failed" result

      // NOTE: not an exact count — the shared FSI session's dynamic-assembly
      // scan is session-cumulative (see the file-level HONEST FINDING), so
      // other tests' probes may legitimately also be present. Presence of
      // THIS marker, undestroyed by the merge, is the correctness bar.
      tests
      |> Array.exists (hasFullNameContaining "newProbe")
      |> Expect.isTrue "the newly eval'd test must be discoverable after a forced rediscovery"
      let liveTests : TestCase array = getDiscovery () |> fst
      liveTests
      |> Array.exists (hasFullNameContaining "newProbe")
      |> Expect.isTrue "GetTestDiscovery's live view must reflect the new test too"
    }

    testTask "WHY — an eval that fails to compile must leave the dynamic discovery and run-test slots exactly as they were (fail-closed, Invariant 4) — a broken keystroke must never wipe or falsely flip prior results" {
      do! referenceExpecto ()
      let marker = System.Guid.NewGuid().ToString("N")
      let modulePath = sprintf "SageFs.Tests.LiveEvalKeystone.FailClosed_%s" marker
      let filePath = sprintf "/fake/%s.fs" marker
      let goodSource =
        sprintf
          "module %s\nopen Expecto\n[<Tests>]\nlet stableProbe = testList \"probe\" [ testCase \"case\" (fun () -> ()) ]"
          modulePath
      let brokenSource =
        sprintf
          "module %s\nopen Expecto\n[<Tests>]\nlet stableProbe = this is not $$$ valid fsharp at all ???"
          modulePath

      let getRunTest, setRunTest = mkRunTestSlot ()
      let getDiscovery, evalFile = mkLiveTestEvalSupport globalActorResult.Value.Actor [||] [] setRunTest

      let! goodResult = evalFile filePath goodSource |> Async.StartAsTask
      let goodTests : TestCase array = unwrap "good eval unexpectedly failed" goodResult
      goodTests
      |> Array.exists (hasFullNameContaining "stableProbe")
      |> Expect.isTrue "the good eval must discover the probe"
      let goodRunTest = getRunTest ()
      goodRunTest |> Expect.isSome "the good eval must set the dynamic run-test slot"

      let! brokenResult = evalFile filePath brokenSource |> Async.StartAsTask
      match brokenResult with
      | Ok(tests: TestCase array, _) -> failtestf "a buffer with a syntax error must not report success, got %A" tests
      | Error _ -> ()

      let liveTests : TestCase array = getDiscovery () |> fst
      liveTests
      |> Expect.equal "discovery after a failed eval must be UNCHANGED (fail-closed, last-good retained)" goodTests
      // Function values don't support structural equality — compare by
      // reference: the SAME closure instance must still be installed.
      obj.ReferenceEquals(getRunTest (), goodRunTest)
      |> Expect.isTrue "the dynamic run-test slot must be the SAME closure, unchanged, after a failed eval"
    }
  ]
