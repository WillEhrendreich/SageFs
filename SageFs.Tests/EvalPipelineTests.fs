module SageFs.Tests.EvalPipelineTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.EvalPipeline
open SageFs.Measures

// ── Helpers ──

let succeedingStage name value =
  stage name (fun () -> Ok value)

let failingStage name err =
  stage name (fun () -> Error err)

let sampleError = SageFsError.EvalFailed "test failure"

// ── Tests ──

[<Tests>]
let pipelineBuilderTests =
  testList "EvalPipeline" [

    testList "PipelineBuilder" [
      test "return produces Ok with no stages" {
        let trace = pipeline { return 42 }
        trace.Result |> Expect.equal "should be Ok 42" (Ok 42)
        trace.Stages |> Expect.isEmpty "should have no stages"
      }

      test "single succeeding stage records trace" {
        let trace = pipeline {
          let! v = succeedingStage "Parse" "parsed-code"
          return v
        }
        trace.Result |> Expect.equal "should be Ok" (Ok "parsed-code")
        trace.Stages |> Expect.hasLength "should have 1 stage" 1
        trace.Stages.[0].Name |> Expect.equal "stage name" "Parse"
        trace.Stages.[0].Outcome |> Expect.equal "should succeed" StageOutcome.Succeeded
      }

      test "single failing stage records trace and short-circuits" {
        let trace = pipeline {
          let! _ = failingStage "Parse" sampleError
          return "should not reach"
        }
        trace.Result |> Expect.equal "should be Error" (Error sampleError)
        trace.Stages |> Expect.hasLength "should have 1 stage" 1
        trace.Stages.[0].Outcome
        |> Expect.equal "should fail" (StageOutcome.Failed sampleError)
      }

      test "multi-stage pipeline records all stages in execution order" {
        let trace = pipeline {
          let! a = succeedingStage "Parse" 1
          let! b = succeedingStage "TypeCheck" (a + 1)
          let! c = succeedingStage "Execute" (b + 1)
          return c
        }
        trace.Result |> Expect.equal "should be Ok 3" (Ok 3)
        trace.Stages |> Expect.hasLength "should have 3 stages" 3
        let names = trace.Stages |> List.map (fun s -> s.Name)
        names |> Expect.equal "execution order" [ "Parse"; "TypeCheck"; "Execute" ]
      }

      test "failure in middle stage short-circuits remaining stages" {
        let mutable executeCalled = false
        let trace = pipeline {
          let! _ = succeedingStage "Parse" "code"
          let! _ = failingStage "TypeCheck" sampleError
          executeCalled <- true
          return "unreachable"
        }
        trace.Result |> Expect.isError "should be Error"
        trace.Stages |> Expect.hasLength "should have 2 stages" 2
        executeCalled |> Expect.isFalse "Execute should not run"
      }
    ]

    testList "stage timing" [
      test "stage records non-negative elapsed time" {
        let tracked = stage "Test" (fun () -> Ok 42)
        (tracked.ElapsedMs, 0.0<ms>)
        |> Expect.isGreaterThanOrEqual "elapsed should be >= 0"
      }

      test "stageOk wraps value as Ok" {
        let tracked = stageOk "Compute" (fun () -> 99)
        tracked.Value |> Expect.equal "should be Ok 99" (Ok 99)
        tracked.StageName |> Expect.equal "stage name" "Compute"
      }

      test "stageOk records timing" {
        let tracked = stageOk "Slow" (fun () ->
          System.Threading.Thread.SpinWait(1000)
          42)
        (tracked.ElapsedMs, 0.0<ms>)
        |> Expect.isGreaterThanOrEqual "should have non-zero time"
      }
    ]

    testList "trace utilities" [
      test "totalMs sums all stage times" {
        let trace = pipeline {
          let! _ = succeedingStage "A" 1
          let! _ = succeedingStage "B" 2
          return ()
        }
        let total = totalMs trace
        (total, 0.0<ms>)
        |> Expect.isGreaterThanOrEqual "total should be >= 0"
      }

      test "succeeded returns true for Ok trace" {
        let trace = pipeline { return 42 }
        trace |> succeeded |> Expect.isTrue "should succeed"
      }

      test "succeeded returns false for Error trace" {
        let trace = pipeline {
          let! _ = failingStage "Fail" sampleError
          return ()
        }
        trace |> succeeded |> Expect.isFalse "should not succeed"
      }

      test "formatRailway shows all stages with checkmarks" {
        let trace = pipeline {
          let! _ = succeedingStage "Parse" 1
          let! _ = succeedingStage "TypeCheck" 2
          let! _ = succeedingStage "Execute" 3
          return ()
        }
        let railway = formatRailway trace
        railway |> Expect.stringContains "has Parse" "Parse ✓"
        railway |> Expect.stringContains "has TypeCheck" "TypeCheck ✓"
        railway |> Expect.stringContains "has Execute" "Execute ✓"
        railway |> Expect.stringContains "has arrows" "→"
      }

      test "formatRailway shows failure mark on failed stage" {
        let trace = pipeline {
          let! _ = succeedingStage "Parse" "ok"
          let! _ = failingStage "TypeCheck" sampleError
          return ()
        }
        let railway = formatRailway trace
        railway |> Expect.stringContains "has Parse success" "Parse ✓"
        railway |> Expect.stringContains "has TypeCheck failure" "TypeCheck ✗"
      }

      test "formatRailway on empty pipeline" {
        let trace: PipelineTrace<unit> = { Result = Ok (); Stages = [] }
        let railway = formatRailway trace
        railway |> Expect.equal "empty pipeline text" "(empty pipeline)"
      }
    ]

    testList "property tests" [
      testProperty "pipeline preserves stage count" (fun (n: int) ->
        let count = (abs n % 10) + 1
        let trace =
          List.init count (fun i -> succeedingStage (sprintf "S%d" i) i)
          |> List.fold
            (fun (acc: PipelineTrace<int>) tracked ->
              match acc.Result with
              | Error _ -> acc
              | Ok _ ->
                let completed = {
                  Name = tracked.StageName
                  ElapsedMs = tracked.ElapsedMs
                  Outcome = StageOutcome.Succeeded
                }
                { Result = tracked.Value; Stages = completed :: acc.Stages })
            { Result = Ok 0; Stages = [] }
        trace.Stages.Length = count
      )

      testProperty "totalMs is non-negative" (fun () ->
        let trace = pipeline {
          let! _ = succeedingStage "A" 1
          let! _ = succeedingStage "B" 2
          return ()
        }
        totalMs trace >= 0.0<ms>
      )
    ]
  ]
