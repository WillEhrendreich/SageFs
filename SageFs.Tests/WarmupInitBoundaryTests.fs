module SageFs.Tests.WarmupInitBoundaryTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

let private warmupStartedAt =
  DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)

let private withTempDir run =
  let dir =
    Path.Combine(Path.GetTempPath(), $"sagefs-warmup-init-{Guid.NewGuid():N}")

  Directory.CreateDirectory(dir) |> ignore

  try
    run dir
  finally
    if Directory.Exists dir then
      Directory.Delete(dir, true)

[<Tests>]
let warmupInitBoundaryTests =
  testList "WarmupInitBoundary" [
    testCase "completeWarmup stops total timing at the namespace-open boundary" <| fun _ ->
      let ctx : WarmupContext =
        WarmupContext.completeWarmup
          warmupStartedAt
          3
          []
          []
          []
          25L
          80L
          120L

      ctx.StartedAt
      |> Expect.equal "warmup should keep the original start timestamp" warmupStartedAt

      ctx.PhaseTiming.ScanSourceFilesMs
      |> Expect.equal "scan timing should come from the first warmup milestone" 25L

      ctx.PhaseTiming.ScanAssembliesMs
      |> Expect.equal "assembly timing should stop at the assembly milestone" 55L

      ctx.PhaseTiming.OpenNamespacesMs
      |> Expect.equal "open timing should stop at the open milestone" 40L

      ctx.PhaseTiming.TotalMs
      |> Expect.equal
        "post-warmup startup work should not inflate warmup total timing"
        120L

      WarmupContext.completionDuration ctx
      |> Expect.equal
        "completion duration should stay anchored at the warmup boundary"
        (TimeSpan.FromMilliseconds 120.0)

    testCase "applyIfPresent reports missing startup profile without pretending it ran" <| fun _ ->
      let evalCalls = ResizeArray<string>()
      let logCalls = ResizeArray<string>()

      let outcome =
        StartupProfile.applyIfPresent
          @"C:\Code\Repos\SageFs\definitely-not-a-real-startup-profile-dir"
          evalCalls.Add
          logCalls.Add

      outcome
      |> Expect.equal
        "missing init script should stay distinct from a failed or loaded profile"
        StartupProfile.NotFound

      evalCalls
      |> Seq.toList
      |> Expect.isEmpty "missing init script should not evaluate anything"

      logCalls
      |> Seq.toList
      |> Expect.isEmpty "missing init script should not log a fake load"

    testCase "applyIfPresent preserves startup profile failures after warmup" <| fun _ ->
      withTempDir <| fun workingDir ->
        let scriptPath = Path.Combine(workingDir, ".SageFsrc")
        File.WriteAllText(scriptPath, "let warmupBoundary = 42")

        let outcome =
          StartupProfile.applyIfPresent
            workingDir
            (fun _ -> failwith "boom")
            ignore

        match outcome with
        | StartupProfile.Failed(path, message) ->
          path
          |> Expect.equal "failure should keep the init script path" scriptPath

          message
          |> Expect.stringContains "failure should preserve the evaluation error" "boom"

          StartupProfile.loadedPath outcome
          |> Expect.isNone "failed startup profiles should not surface as loaded"
        | other ->
          failtestf "expected Failed outcome, got %A" other

    testCase "loadedPath preserves the existing loaded-profile contract" <| fun _ ->
      let path = @"C:\Code\Repos\SageFs\.SageFsrc"

      StartupProfile.loadedPath (StartupProfile.Loaded path)
      |> Expect.equal "loaded startup profiles should still expose their path" (Some path)
  ]
