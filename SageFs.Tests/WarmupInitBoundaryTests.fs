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
      let boundaryMs = FixtureDurations.warmupBoundaryMs
      let ctx : WarmupContext =
        WarmupContext.completeWarmup
          warmupStartedAt
          3
          []
          []
          []
          25L
          80L
          boundaryMs

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
        boundaryMs

      WarmupContext.completionDuration ctx
      |> Expect.equal
        "completion duration should stay anchored at the warmup boundary"
        (TimeSpan.FromMilliseconds (float boundaryMs))

    testCase "applyIfPresent reports missing startup profile without pretending it ran" <| fun _ ->
      let evalCalls = ResizeArray<string>()
      let logCalls = ResizeArray<string>()

      let outcome =
        StartupProfile.applyIfPresent
          [ @"C:\Code\Repos\SageFs\definitely-not-a-real-startup-profile-dir" ]
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
            [ workingDir ]
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

let private writeInit (dir: string) (fileName: string) =
  let path =
    match fileName with
    | "init.fsx" -> Path.Combine(dir, ".SageFs", "init.fsx")
    | other -> Path.Combine(dir, other)
  Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
  File.WriteAllText(path, "()")
  path

/// What a session in `site/` with `Dev/Dev.fsproj` as its primary project searches.
let private siteAndProject (site: string) =
  let project = Path.Combine(site, "Dev")
  Directory.CreateDirectory project |> ignore
  project, StartupProfile.searchDirectories site (Some project)

[<Tests>]
let startupProfileLocationTests =
  testList "Where the startup profile is read from" [
    testCase "WHY — a session's init.fsx is read from its working directory, where its config.fsx is, not only from the project folder the worker moves into" <| fun _ ->
      withTempDir <| fun site ->
        let _, dirs = siteAndProject site
        let expected = writeInit site "init.fsx"
        StartupProfile.discoverInitScript dirs
        |> Expect.equal "site/.SageFs/init.fsx is found" (Some expected)

    testCase "WHY — an init.fsx kept in the project folder still loads, because that was the only place looked before" <| fun _ ->
      withTempDir <| fun site ->
        let project, dirs = siteAndProject site
        let expected = writeInit project "init.fsx"
        StartupProfile.discoverInitScript dirs
        |> Expect.equal "site/Dev/.SageFs/init.fsx is found" (Some expected)

    testCase "WHY — when both folders have one, the working directory's wins and only it runs" <| fun _ ->
      withTempDir <| fun site ->
        let project, dirs = siteAndProject site
        let expected = writeInit site "init.fsx"
        writeInit project "init.fsx" |> ignore
        let evaluated = ResizeArray<string>()
        StartupProfile.applyIfPresent dirs evaluated.Add ignore
        |> Expect.equal "the working directory's script loads" (StartupProfile.Loaded expected)
        evaluated |> Seq.length |> Expect.equal "one script is evaluated" 1

    testCase "WHY — the folder decides before the file name, so the working directory's .SageFsrc beats the project's init.fsx" <| fun _ ->
      withTempDir <| fun site ->
        let project, dirs = siteAndProject site
        let expected = writeInit site ".SageFsrc"
        writeInit project "init.fsx" |> ignore
        StartupProfile.discoverInitScript dirs
        |> Expect.equal "site/.SageFsrc is found" (Some expected)

    testCase "a bare session, or one whose project sits in its working directory, searches that one folder" <| fun _ ->
      StartupProfile.searchDirectories "/work/site" None
      |> Expect.equal "no project" [ "/work/site" ]
      StartupProfile.searchDirectories "/work/site" (Some "/work/site")
      |> Expect.equal "project in the working directory" [ "/work/site" ]

    testProperty "the working directory is always searched first, and the project folder after it only when it differs" <| fun (sessionDir: string) (projectDir: string option) ->
      let dirs = StartupProfile.searchDirectories sessionDir projectDir
      List.head dirs = sessionDir
      && dirs = List.distinct dirs
      && (match projectDir with
          | Some dir when dir <> sessionDir -> dirs = [ sessionDir; dir ]
          | Some _
          | None -> dirs = [ sessionDir ])
  ]
